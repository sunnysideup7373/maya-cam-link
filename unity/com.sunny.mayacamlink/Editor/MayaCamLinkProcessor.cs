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
                var dir = FindDirector(false);
                if (dir && dir.playableAsset)
                {
                    double t = Math.Max(0.0, (p.frame - p.start) / Math.Max(1f, p.fps));
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
            var dir = FindDirector(false);
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
            var dir = FindDirector(false);
            if (dir && dir.playableAsset && !EditorApplication.isPlaying) dir.Evaluate();
            RepaintAll();
        }

        public static void RepaintAll()
        {
            EditorApplication.QueuePlayerLoopUpdate();
            SceneView.RepaintAll();
            UnityEditorInternal.InternalEditorUtility.RepaintAllViews();
        }

        // ------------------------------------------------------------------
        // Bake
        // ------------------------------------------------------------------
        public static void HandleBake(MayaCamBakePacket p)
        {
            if (p?.frames == null || p.frames.Length == 0) return;
            LastResW = p.resW;
            LastResH = p.resH;
            var rig = FindRig(p.camera, true);
            if (!rig) return;
            var dir = FindDirector(true);
            var clip = MayaCamClipBuilder.Build(p, rig, dir);
            LastBakeInfo = $"{p.camera}  {p.frames.Length} f  @{p.fps:0.###}fps  → {AssetDatabase.GetAssetPath(clip)}   ({DateTime.Now:HH:mm:ss})";

            // ライブ状態は引き続き有効 (Maya の現在フレームの値)。再評価してから上書き
            if (dir && dir.playableAsset) dir.Evaluate();
            RepaintAll();
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

        public static PlayableDirector FindDirector(bool create)
        {
            string name = MayaCamLinkSettings.DirectorName;
            var go = GameObject.Find(name);
            PlayableDirector dir = go ? go.GetComponent<PlayableDirector>() : null;
            if (!dir && !create) return null;

            if (!go)
            {
                go = new GameObject(name);
                Undo.RegisterCreatedObjectUndo(go, "Create Maya Cam Link Timeline");
            }
            if (!dir)
            {
                dir = go.AddComponent<PlayableDirector>();
                dir.playOnAwake = true;
            }
            if (!(dir.playableAsset is TimelineAsset))
            {
                string folder = EnsureFolder(MayaCamLinkSettings.OutputFolder);
                string path = $"{folder}/{SceneName()}_MayaCamLink.playable";
                var tl = AssetDatabase.LoadAssetAtPath<TimelineAsset>(path);
                if (!tl)
                {
                    tl = ScriptableObject.CreateInstance<TimelineAsset>();
                    AssetDatabase.CreateAsset(tl, path);
                }
                dir.playableAsset = tl;
                EditorSceneManager.MarkSceneDirty(go.scene);
            }
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
