using System;
using System.Collections.Generic;
using System.IO;
using Unity.Cinemachine;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEditor.Timeline;
using UnityEngine;
using UnityEngine.Playables;
using UnityEngine.Timeline;
using System.Linq;
#if MCL_SRP
using UnityEngine.Rendering;
#endif
#if MCL_URP
using UnityEngine.Rendering.Universal;
#endif
#if MCL_HDRP
using HD = UnityEngine.Rendering.HighDefinition;
#elif MCL_URP
using URP = UnityEngine.Rendering.Universal;
#endif
using Object = UnityEngine.Object;

namespace MayaCamLink.Editor
{
    /// <summary>
    /// Maya から届いたデータをメインスレッドで処理:
    ///   live → カメラをリアルタイムに上書き (+ Timeline の再生ヘッドを同期)
    ///   bake → AnimationClip を生成して Timeline に配置
    /// </summary>
    [InitializeOnLoad]
    public static class MayaCamLinkProcessor
    {
        public static string LastCamera { get; private set; } = "-";
        public static float LastFrame { get; private set; }
        public static float LastFps { get; private set; } = 24f;
        public static string LastBakeInfo { get; private set; } = "-";
        public static int LastResW { get; private set; }
        public static int LastResH { get; private set; }
        public static double LastPacketTime { get; private set; }
        public static string MayaVersion { get; private set; } = "-";

        static double _syncedDirectorTime = double.NaN;
        static readonly Dictionary<string, MayaCamLivePacket> _lives = new Dictionary<string, MayaCamLivePacket>();

        static MayaCamLinkProcessor()
        {
            EditorApplication.update += Update;
            EditorApplication.playModeStateChanged += _ => ClearAllLiveOverrides();
            // 保存後・再コンパイル前に追加先 Timeline の ID を更新 (未保存時の ID は復元できないため)
            EditorSceneManager.sceneSaved += _ => RefreshStoredTarget();
            AssemblyReloadEvents.beforeAssemblyReload += RefreshStoredTarget;
            EditorSceneManager.activeSceneChangedInEditMode += (_, __) => { _target = null; _lastResolve = -10; };
        }

        static void Update()
        {
            _lives.Clear();
            int guard = 0;
            while (guard++ < 2000 && MayaCamLinkServer.TryDequeue(out var line))
            {
                MayaCamPacketHeader h;
                try { h = JsonUtility.FromJson<MayaCamPacketHeader>(line); }
                catch (Exception e) { Debug.LogWarning($"[MayaCamLink] JSON 解析エラー: {e.Message}"); continue; }
                if (h == null) continue;
                LastPacketTime = EditorApplication.timeSinceStartup;

                switch (h.type)
                {
                    case "live":
                        // 1 回の update では同じカメラの最新データだけ処理
                        _lives[h.camera ?? ""] = JsonUtility.FromJson<MayaCamLivePacket>(line);
                        break;
                    case "bake":
                        if (EditorApplication.isPlayingOrWillChangePlaymode) break;
                        if (!MayaCamLinkSettings.AcceptBake) break;
                        try { HandleBake(JsonUtility.FromJson<MayaCamBakePacket>(line)); }
                        catch (Exception e) { Debug.LogException(e); }
                        break;
                    case "hello":
                        MayaVersion = ExtractString(line, "mayaVersion") ?? "?";
                        Debug.Log($"[MayaCamLink] Maya {MayaVersion} 接続");
                        break;
                    case "_disconnect":
                        if (MayaCamLinkServer.ClientCount == 0) ClearAllLiveOverrides();
                        break;
                }
            }

            if (!EditorApplication.isPlayingOrWillChangePlaymode)
            {
                foreach (var p in _lives.Values)
                {
                    try { HandleLive(p); }
                    catch (Exception e) { Debug.LogException(e); }
                }
                ReleaseOverrideIfUserScrubbed();
            }
        }

        // ------------------------------------------------------------------
        // Live
        // ------------------------------------------------------------------
        static void HandleLive(MayaCamLivePacket p)
        {
            if (p?.state == null) return;
            LastCamera = p.camera;
            LastFrame = p.frame;
            LastFps = p.fps;
            if (!MayaCamLinkSettings.ApplyLive) return;

            var rig = FindRig(p.camera, MayaCamLinkSettings.AutoCreateRig);
            if (!rig) return;
            rig.frameRate = p.fps;
#if MCL_URP17
            // Maya Bokeh を選んだ Rig がある時だけ Renderer Feature を追加
            if (!_featureChecked && rig.dofRenderer == MayaCameraRig.DofRenderer.MayaBokeh) EnsureBokehFeature();
#endif

            if (MayaCamLinkSettings.SyncTime && p.syncTime != 0)
            {
                var dir = SyncDirector;
                if (dir && dir.playableAsset)
                {
                    // Timeline 上でクリップを移動・トリムしていても、そのクリップの位置に合わせる
                    double t = Math.Max(0.0, ClipOffset(dir, rig, p.camera) + (p.frame - p.start) / Math.Max(1f, p.fps));
                    if (Math.Abs(dir.time - t) > 1e-6)
                    {
                        dir.time = t;
                        dir.Evaluate();
                        TimelineEditor.Refresh(RefreshReason.WindowNeedsRedraw);
                    }
                    _syncedDirectorTime = dir.time;
                }
            }

            rig.SetLiveState(p.state);
            RepaintAll();
        }

        /// <summary>Unity 側で Timeline が操作されたら Timeline に戻す</summary>
        static void ReleaseOverrideIfUserScrubbed()
        {
            if (double.IsNaN(_syncedDirectorTime)) return;
            var dir = SyncDirector;
            if (!dir) return;
            if (Math.Abs(dir.time - _syncedDirectorTime) > 0.5 / Math.Max(1f, LastFps))
            {
                ClearAllLiveOverrides();
            }
        }

        public static void ClearAllLiveOverrides()
        {
            _syncedDirectorTime = double.NaN;
            foreach (var r in AllRigs())
                r.ClearLiveOverride();
            var dir = SyncDirector;
            if (dir && dir.playableAsset && !EditorApplication.isPlaying) dir.Evaluate();
            RepaintAll();
        }

        /// <summary>Maya の時間 0 (再生開始フレーム) が Timeline 上のどの時刻になるか</summary>
        static double ClipOffset(PlayableDirector dir, MayaCameraRig rig, string camera)
        {
            if (!(dir.playableAsset is TimelineAsset tl)) return 0;
            var anim = rig ? rig.GetComponent<Animator>() : null;
            var track = MayaCamClipBuilder.FindCameraTrack(dir, tl, anim, camera);
            var tc = track != null ? track.GetClips().FirstOrDefault() : null;
            return tc != null ? tc.start - tc.clipIn : 0;
        }

        public static void RepaintAll()
        {
            UpdateBrainsInBackground();
            EditorApplication.QueuePlayerLoopUpdate();
            SceneView.RepaintAll();
            UnityEditorInternal.InternalEditorUtility.RepaintAllViews();
        }

        /// <summary>
        /// Unity が非アクティブ (Maya を操作中) の間は player loop が止まり、
        /// CinemachineBrain が Main Camera を動かさないため Game ビューが更新されない。ここで手動で更新する。
        /// </summary>
        static void UpdateBrainsInBackground()
        {
            if (UnityEditorInternal.InternalEditorUtility.isApplicationActive) return;
            for (int i = 0; i < CinemachineBrain.ActiveBrainCount; i++)
            {
                var b = CinemachineBrain.GetActiveBrain(i);
                if (b) b.ManualUpdate();
            }
        }

        // ------------------------------------------------------------------
        // Bake
        // ------------------------------------------------------------------
        // 初回受信で Clip がまだ無いカメラ (ウィンドウで名前を決めて「作成」するまで保留)
        static readonly Dictionary<string, MayaCamBakePacket> _pending = new Dictionary<string, MayaCamBakePacket>();
        public static IReadOnlyDictionary<string, MayaCamBakePacket> Pending => _pending;

        public static void HandleBake(MayaCamBakePacket p)
        {
            if (p?.frames == null || p.frames.Length == 0) return;
            LastResW = p.resW;
            LastResH = p.resH;
            var rig = FindRig(p.camera, true);
            if (!rig) return;

            string path = rig.bakedClip ? AssetDatabase.GetAssetPath(rig.bakedClip) : null;
            if (string.IsNullOrEmpty(path))
            {
                _pending[p.camera] = p;
                LastBakeInfo = $"{p.camera}  {p.frames.Length} f  → 未作成   ({DateTime.Now:HH:mm:ss})";
                RepaintAll();
                return;
            }
            _pending.Remove(p.camera);
            Bake(p, rig, path);
        }

        /// <summary>保留中のカメラの Clip を作成。以降はこの Clip に自動で上書きする</summary>
        public static AnimationClip CreatePendingClip(string camera, string fileName)
        {
            if (!_pending.TryGetValue(camera, out var p)) return null;
            var rig = FindRig(camera, true);
            if (!rig) return null;
            _pending.Remove(camera);
            var clip = Bake(p, rig, NewClipPath(camera, fileName));
            Undo.RecordObject(rig, "Maya Cam Link Clip");
            rig.bakedClip = clip;
            EditorUtility.SetDirty(rig);
            EditorSceneManager.MarkSceneDirty(rig.gameObject.scene);
            return clip;
        }

        public static void DismissPending(string camera) => _pending.Remove(camera);

        /// <summary>新しい Clip の保存パス (Clip 保存先 / 名前.anim)</summary>
        public static string NewClipPath(string camera, string fileName) =>
            $"{MayaCamLinkSettings.ClipFolder.Replace('\\', '/').TrimEnd('/')}/{MayaCamClipBuilder.SanitizeFileName(fileName, camera)}.anim";

        static AnimationClip Bake(MayaCamBakePacket p, MayaCameraRig rig, string path)
        {
            // 「Timeline に追加」が OFF / 追加先が未指定の時は AnimationClip だけ
            var dir = SyncDirector;
            if (dir && !(dir.playableAsset is TimelineAsset))
            {
                Debug.LogWarning($"[MayaCamLink] {dir.name} に Timeline アセットがないため、AnimationClip だけ更新しました。");
                dir = null;
            }
            var clip = MayaCamClipBuilder.Build(p, rig, dir, path);
            LastBakeInfo = $"{p.camera}  {p.frames.Length} f  @{p.fps:0.###}fps  → {AssetDatabase.GetAssetPath(clip)}   ({DateTime.Now:HH:mm:ss})";

            // ライブ状態は引き続き有効 (Maya の現在フレームの値)。再評価してから上書き
            if (dir && dir.playableAsset) dir.Evaluate();
            RepaintAll();
            return clip;
        }

        public static void ImportJsonFile(string path)
        {
            var json = File.ReadAllText(path);
            var p = JsonUtility.FromJson<MayaCamBakePacket>(json);
            if (p == null || p.frames == null || p.frames.Length == 0)
            {
                EditorUtility.DisplayDialog("Maya Cam Link", "カメラのフレームデータがありません。", "OK");
                return;
            }
            HandleBake(p);
        }

        // ------------------------------------------------------------------
        // Scene objects
        // ------------------------------------------------------------------
        public static MayaCameraRig FindRig(string mayaName, bool create)
        {
            foreach (var r in AllRigs())
                if (r.mayaCameraName == mayaName) return r;
            return create ? CreateRig(mayaName) : null;
        }

        public static MayaCameraRig[] AllRigs()
        {
#if UNITY_6000_4_OR_NEWER
            return Object.FindObjectsByType<MayaCameraRig>(FindObjectsInactive.Include);
#else
            return Object.FindObjectsByType<MayaCameraRig>(FindObjectsInactive.Include, FindObjectsSortMode.None);
#endif
        }

        public static MayaCameraRig CreateRig(string mayaName)
        {
            string safe = Sanitize(mayaName);
            var root = new GameObject("MayaCam_" + safe);
            Undo.RegisterCreatedObjectUndo(root, "Create Maya Camera Rig");
            root.AddComponent<Animator>();

            var camGo = new GameObject("CM_" + safe);
            camGo.transform.SetParent(root.transform, false);
            var cm = camGo.AddComponent<CinemachineCamera>();

#if MCL_SRP
            var vol = root.AddComponent<Volume>();
            vol.isGlobal = true;
            vol.priority = 100;
            vol.sharedProfile = GetOrCreateProfile(safe);
#endif
            var rig = root.AddComponent<MayaCameraRig>();
            rig.mayaCameraName = mayaName;
            rig.cmCamera = cm;
#if MCL_SRP
            rig.volume = vol;
#endif
            rig.Apply(true);
            EnsureBrain();
            EditorSceneManager.MarkSceneDirty(root.scene);
            Debug.Log($"[MayaCamLink] カメラ Rig 作成: {root.name}");
            return rig;
        }

#if MCL_SRP
        static VolumeProfile GetOrCreateProfile(string safeName)
        {
            string folder = EnsureFolder(MayaCamLinkSettings.OutputFolder + "/Profiles");
            string path = $"{folder}/{SceneName()}_{safeName}_DOF.asset";
            var prof = AssetDatabase.LoadAssetAtPath<VolumeProfile>(path);
            if (prof) return prof;
            prof = ScriptableObject.CreateInstance<VolumeProfile>();
#if MCL_HDRP
            prof.Add<HD.DepthOfField>(true);
#elif MCL_URP
            prof.Add<URP.DepthOfField>(true);
#endif
            AssetDatabase.CreateAsset(prof, path);
            // VolumeComponent はサブアセットとして保存する必要がある
            foreach (var c in prof.components)
            {
                c.hideFlags = HideFlags.HideInInspector | HideFlags.HideInHierarchy;
                AssetDatabase.AddObjectToAsset(c, prof);
            }
            AssetDatabase.SaveAssets();
            return prof;
        }
#endif

#if MCL_URP17
        static bool _featureChecked;
        const string BokehShaderPath = "Packages/com.sunny.mayacamlink/Runtime/Shaders/MayaCamBokeh.shader";

        /// <summary>使用中の全 URP Renderer に MayaCamBokehFeature を追加</summary>
        public static void EnsureBokehFeature()
        {
            _featureChecked = true;
            var assets = new HashSet<UniversalRenderPipelineAsset>();
            if (GraphicsSettings.defaultRenderPipeline is UniversalRenderPipelineAsset d) assets.Add(d);
            for (int i = 0; i < QualitySettings.count; i++)
                if (QualitySettings.GetRenderPipelineAssetAt(i) is UniversalRenderPipelineAsset q) assets.Add(q);

            var shader = AssetDatabase.LoadAssetAtPath<Shader>(BokehShaderPath);
            bool changed = false;
            foreach (var a in assets)
            {
                var list = new SerializedObject(a).FindProperty("m_RendererDataList");
                if (list == null) continue;
                for (int i = 0; i < list.arraySize; i++)
                {
                    var rd = list.GetArrayElementAtIndex(i).objectReferenceValue as ScriptableRendererData;
                    if (!rd || rd.rendererFeatures.Exists(f => f is MayaCamBokehFeature)) continue;

                    var feat = ScriptableObject.CreateInstance<MayaCamBokehFeature>();
                    feat.name = "MayaCamBokeh";
                    feat.shader = shader;
                    AssetDatabase.AddObjectToAsset(feat, rd);
                    AssetDatabase.TryGetGUIDAndLocalFileIdentifier(feat, out _, out long localId);

                    var rso = new SerializedObject(rd);
                    var feats = rso.FindProperty("m_RendererFeatures");
                    var map = rso.FindProperty("m_RendererFeatureMap");
                    feats.arraySize++;
                    feats.GetArrayElementAtIndex(feats.arraySize - 1).objectReferenceValue = feat;
                    map.arraySize++;
                    map.GetArrayElementAtIndex(map.arraySize - 1).longValue = localId;
                    rso.ApplyModifiedProperties();
                    rd.SetDirty();
                    EditorUtility.SetDirty(rd);
                    changed = true;
                    Debug.Log($"[MayaCamLink] Renderer に Maya Cam Bokeh を追加: {rd.name}");
                }
            }
            if (changed) AssetDatabase.SaveAssets();
        }
#endif

        /// <summary>メインカメラに CinemachineBrain を付け、物理カメラ / ポストプロセスを有効にする</summary>
        public static Camera EnsureBrain()
        {
            var cam = Camera.main;
            if (!cam) cam = Object.FindAnyObjectByType<Camera>();
            if (!cam)
            {
                var go = new GameObject("Main Camera") { tag = "MainCamera" };
                Undo.RegisterCreatedObjectUndo(go, "Create Main Camera");
                cam = go.AddComponent<Camera>();
            }
            var brain = cam.GetComponent<CinemachineBrain>();
            if (!brain) brain = Undo.AddComponent<CinemachineBrain>(cam.gameObject);

            Undo.RecordObject(brain, "Maya Cam Link Brain");
            brain.LensModeOverride.Enabled = true;
            brain.LensModeOverride.DefaultMode = LensSettings.OverrideModes.Physical;

            Undo.RecordObject(cam, "Maya Cam Link Camera");
            cam.usePhysicalProperties = true;
#if MCL_URP
            var data = cam.GetUniversalAdditionalCameraData();
            if (data)
            {
                Undo.RecordObject(data, "Maya Cam Link Camera");
                data.renderPostProcessing = true;
            }
#endif
            return cam;
        }

        // ------------------------------------------------------------------
        // 追加先 Timeline (ユーザーが指定した PlayableDirector)
        // ------------------------------------------------------------------
        static PlayableDirector _target;
        static double _lastResolve = -10;

        /// <summary>指定された Timeline。シーンを開き直しても GlobalObjectId から復元する</summary>
        public static PlayableDirector TargetDirector
        {
            get
            {
                if (_target) return _target;
                string id = MayaCamLinkSettings.TargetDirectorId;
                string path = MayaCamLinkSettings.TargetDirectorPath;
                if (string.IsNullOrEmpty(id) && string.IsNullOrEmpty(path)) return null;
                // 見つからない時に毎フレーム探さないよう間引く
                double now = EditorApplication.timeSinceStartup;
                if (now - _lastResolve < 1.0) return null;
                _lastResolve = now;
                if (GlobalObjectId.TryParse(id, out var gid))
                    _target = GlobalObjectId.GlobalObjectIdentifierToObjectSlow(gid) as PlayableDirector;
                // 未保存のシーン / オブジェクトは ID で戻せないので、シーン + 階層パスで探す
                if (!_target && !string.IsNullOrEmpty(path))
                    _target = FindDirectorByPath(path);
                return _target;
            }
            set
            {
                _target = value;
                _lastResolve = -10;
                StoreTarget();
            }
        }

        static void StoreTarget()
        {
            MayaCamLinkSettings.TargetDirectorId = _target ? GlobalObjectId.GetGlobalObjectIdSlow(_target).ToString() : "";
            MayaCamLinkSettings.TargetDirectorPath = _target ? DirectorPath(_target) : "";
        }

        static string DirectorPath(PlayableDirector d) =>
            d.gameObject.scene.path + "|" + AnimationUtility.CalculateTransformPath(d.transform, null);

        static PlayableDirector FindDirectorByPath(string path)
        {
            foreach (var d in Resources.FindObjectsOfTypeAll<PlayableDirector>())
            {
                if (!d.gameObject.scene.IsValid() || EditorUtility.IsPersistent(d)) continue;
                if (DirectorPath(d) == path) return d;
            }
            return null;
        }

        static void RefreshStoredTarget()
        {
            if (_target) StoreTarget();
        }

        /// <summary>同期・追加に使う Timeline (「Timeline に追加」が OFF なら null)</summary>
        public static PlayableDirector SyncDirector =>
            MayaCamLinkSettings.UseTimeline ? TargetDirector : null;

        /// <summary>Timeline ウィンドウで開いている / Hierarchy で選択中の Timeline</summary>
        public static PlayableDirector PickSelectedDirector()
        {
            var go = Selection.activeGameObject;
            var sel = go ? go.GetComponent<PlayableDirector>() : null;
            if (sel) return sel;
            return TimelineEditor.inspectedDirector;
        }

        /// <summary>新しい Timeline を作成して追加先に設定</summary>
        public static PlayableDirector CreateTimeline(string assetPath)
        {
            var tl = AssetDatabase.LoadAssetAtPath<TimelineAsset>(assetPath);
            if (!tl)
            {
                tl = ScriptableObject.CreateInstance<TimelineAsset>();
                tl.editorSettings.frameRate = Math.Max(1f, LastFps);
                AssetDatabase.CreateAsset(tl, assetPath);
                AssetDatabase.SaveAssets();
            }
            var go = new GameObject(Path.GetFileNameWithoutExtension(assetPath));
            Undo.RegisterCreatedObjectUndo(go, "Create Maya Cam Link Timeline");
            var dir = go.AddComponent<PlayableDirector>();
            dir.playOnAwake = true;
            dir.playableAsset = tl;
            EditorSceneManager.MarkSceneDirty(go.scene);
            TargetDirector = dir;
            return dir;
        }

        // ------------------------------------------------------------------
        public static string Sanitize(string s)
        {
            if (string.IsNullOrEmpty(s)) return "Camera";
            foreach (var c in Path.GetInvalidFileNameChars()) s = s.Replace(c, '_');
            return s.Replace(':', '_').Replace('|', '_').Replace(' ', '_');
        }

        public static string SceneName()
        {
            var n = EditorSceneManager.GetActiveScene().name;
            return string.IsNullOrEmpty(n) ? "Untitled" : Sanitize(n);
        }

        public static string EnsureFolder(string path)
        {
            path = path.Replace('\\', '/').TrimEnd('/');
            if (AssetDatabase.IsValidFolder(path)) return path;
            var parts = path.Split('/');
            string cur = parts[0]; // "Assets"
            for (int i = 1; i < parts.Length; i++)
            {
                string next = cur + "/" + parts[i];
                if (!AssetDatabase.IsValidFolder(next)) AssetDatabase.CreateFolder(cur, parts[i]);
                cur = next;
            }
            return path;
        }

        static string ExtractString(string json, string key)
        {
            string k = "\"" + key + "\":\"";
            int i = json.IndexOf(k, StringComparison.Ordinal);
            if (i < 0) return null;
            i += k.Length;
            int j = json.IndexOf('"', i);
            return j > i ? json.Substring(i, j - i) : null;
        }
    }
}
