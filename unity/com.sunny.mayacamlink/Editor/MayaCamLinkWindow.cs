using System.Collections.Generic;
using System.Linq;
using UnityEditor;
using UnityEngine;

namespace MayaCamLink.Editor
{
    public class MayaCamLinkWindow : EditorWindow
    {
        static readonly Color Green = new Color(0.35f, 0.85f, 0.45f);
        static readonly Color Yellow = new Color(0.95f, 0.75f, 0.25f);
        static readonly Color Gray = new Color(0.5f, 0.5f, 0.5f);
        static readonly Color Red = new Color(0.9f, 0.4f, 0.4f);

        double _lastRepaint;
        bool _showSettings;
        GUIStyle _big, _dot, _chip;

        [MenuItem("Window/Maya Cam Link")]
        public static void Open()
        {
            var w = GetWindow<MayaCamLinkWindow>();
            w.titleContent = new GUIContent("Maya Cam Link", EditorGUIUtility.IconContent("Camera Icon").image);
            w.minSize = new Vector2(300, 220);
        }

        void OnEnable() => EditorApplication.update += Tick;
        void OnDisable() => EditorApplication.update -= Tick;

        void Tick()
        {
            if (EditorApplication.timeSinceStartup - _lastRepaint > 0.25)
            {
                _lastRepaint = EditorApplication.timeSinceStartup;
                Repaint();
            }
        }

        /// <summary>manifest がローカルパス参照の時: 共有相手の PC でパッケージが見つからないので警告</summary>
        void PackageSourceGUI()
        {
            if (!MayaCamLinkPackageSource.ShowWarning) return;
            bool embed = false, git = false, hide = false;
            using (new EditorGUILayout.VerticalScope(EditorStyles.helpBox))
            {
                using (new EditorGUILayout.HorizontalScope())
                {
                    GUILayout.Label(EditorGUIUtility.IconContent("console.warnicon.sml"), GUILayout.Width(18));
                    GUILayout.Label("パッケージがローカルパスで参照されています。\n" +
                                    "このプロジェクトを Git で共有すると、相手の PC では見つかりません。",
                        EditorStyles.wordWrappedMiniLabel);
                    hide = GUILayout.Button(new GUIContent("×", "このプロジェクトでは表示しない"),
                        EditorStyles.miniButton, GUILayout.Width(20));
                }
                EditorGUILayout.LabelField(MayaCamLinkPackageSource.Reference, EditorStyles.miniLabel);
                using (new EditorGUI.DisabledScope(MayaCamLinkPackageSource.Busy))
                using (new EditorGUILayout.HorizontalScope())
                {
                    embed = GUILayout.Button(new GUIContent("プロジェクトに埋め込む",
                        "Packages/ にコピーしてプロジェクトと一緒にコミット (相手は何もしなくて良い)"));
                    git = GUILayout.Button(new GUIContent(MayaCamLinkPackageSource.Busy ? "切り替え中…" : "GitHub から参照",
                        "GitHub の URL 参照に切り替え (相手の PC に Git が必要)"));
                }
            }
            if (hide) MayaCamLinkPackageSource.ShowWarning = false;
            if (embed &&
                EditorUtility.DisplayDialog("Maya Cam Link",
                    "パッケージを Packages/com.sunny.mayacamlink にコピーし、manifest.json の参照を外します。\n" +
                    "以後はプロジェクトと一緒にコミットしてください。", "埋め込む", "キャンセル"))
                MayaCamLinkPackageSource.Embed();
            if (git) MayaCamLinkPackageSource.SwitchToGit();
            if (hide || embed || git) GUIUtility.ExitGUI();
        }

        void Styles()
        {
            if (_big != null) return;
            _big = new GUIStyle(EditorStyles.boldLabel) { fontSize = 15 };
            _dot = new GUIStyle(EditorStyles.label) { fontSize = 20, alignment = TextAnchor.MiddleCenter };
            _chip = new GUIStyle(EditorStyles.miniButton) { fontSize = 11, fixedHeight = 24 };
        }

        void OnGUI()
        {
            Styles();
            bool running = MayaCamLinkServer.IsRunning;
            bool linked = running && MayaCamLinkServer.ClientCount > 0;
            bool recent = EditorApplication.timeSinceStartup - MayaCamLinkProcessor.LastPacketTime < 1.0;

            // ---------- ステータス ----------
            using (new EditorGUILayout.HorizontalScope(EditorStyles.helpBox, GUILayout.Height(44)))
            {
                var old = GUI.contentColor;
                GUI.contentColor = linked ? (recent ? Green : Yellow) : running ? Gray : Red;
                GUILayout.Label("●", _dot, GUILayout.Width(28), GUILayout.Height(38));
                GUI.contentColor = old;
                using (new EditorGUILayout.VerticalScope())
                {
                    GUILayout.Label(linked ? MayaCamLinkProcessor.LastCamera : running ? "待機中" : "停止", _big);
                    GUILayout.Label(linked
                            ? $"Maya {MayaCamLinkProcessor.MayaVersion}   F {MayaCamLinkProcessor.LastFrame:0.#}   {MayaCamLinkProcessor.LastFps:0.##} fps"
                            : $":{MayaCamLinkSettings.Port}",
                        EditorStyles.miniLabel);
                }
                GUILayout.FlexibleSpace();
                var bg = GUI.backgroundColor;
                GUI.backgroundColor = running ? Red : Green;
                if (GUILayout.Button(new GUIContent(running ? "■" : "▶", running ? "サーバー停止" : "サーバー開始"),
                        GUILayout.Width(44), GUILayout.Height(36)))
                {
                    if (running) MayaCamLinkServer.Stop(); else MayaCamLinkServer.Start();
                }
                GUI.backgroundColor = bg;
            }
            if (!string.IsNullOrEmpty(MayaCamLinkServer.LastError))
                EditorGUILayout.HelpBox(MayaCamLinkServer.LastError, MessageType.Error);
            PackageSourceGUI();

            // ---------- 同期トグル ----------
            using (new EditorGUILayout.HorizontalScope())
            {
                MayaCamLinkSettings.ApplyLive = Chip("◉ ライブ", "Maya の状態をリアルタイム反映", MayaCamLinkSettings.ApplyLive);
                MayaCamLinkSettings.SyncTime = Chip("◷ 時間", "Timeline の再生ヘッドを Maya に同期", MayaCamLinkSettings.SyncTime);
                MayaCamLinkSettings.AcceptBake = Chip("◆ キー", "Maya のキーを Timeline に取り込む", MayaCamLinkSettings.AcceptBake);
            }

            // ---------- ツール ----------
            using (new EditorGUILayout.HorizontalScope())
            {
                using (new EditorGUI.DisabledScope(!MayaCamLinkProcessor.TargetDirector))
                    if (GUILayout.Button(new GUIContent("▤ Timeline", "追加先の Timeline を選択して開く"), _chip))
                        OpenTimeline(MayaCamLinkProcessor.TargetDirector);
                if (GUILayout.Button(new GUIContent("↩ 解除", "ライブ上書きを解除して Timeline に戻す"), _chip))
                    MayaCamLinkProcessor.ClearAllLiveOverrides();
                if (GUILayout.Button(new GUIContent("◎ セットアップ", "メインカメラ / Brain / ポストプロセスを設定"), _chip))
                {
                    var cam = MayaCamLinkProcessor.EnsureBrain();
                    Selection.activeGameObject = cam.gameObject;
                }
                if (GUILayout.Button(new GUIContent("⇩ JSON", "JSON ファイルを読み込む"), _chip))
                {
                    var path = EditorUtility.OpenFilePanel("Maya カメラ JSON", "", "json");
                    if (!string.IsNullOrEmpty(path)) MayaCamLinkProcessor.ImportJsonFile(path);
                }
            }

            // ---------- 初回: Clip の作成待ち ----------
            PendingGUI();

            // ---------- キーの出力先 ----------
            EditorGUILayout.Space(2);
            using (new EditorGUILayout.VerticalScope(EditorStyles.helpBox))
            {
                // AnimationClip
                EditorGUILayout.LabelField("AnimationClip (.anim)", EditorStyles.boldLabel);
                ClipNameField();
                FolderField("Clip 保存先", "カメラの .anim を保存するフォルダ",
                    MayaCamLinkSettings.ClipFolder, v => MayaCamLinkSettings.ClipFolder = v);

                EditorGUILayout.Space(4);

                // Timeline (別行)
                MayaCamLinkSettings.UseTimeline = EditorGUILayout.ToggleLeft(
                    new GUIContent("Timeline に追加",
                        "指定した Timeline にカメラのトラックを追加し、再生ヘッドを Maya と同期\nOFF: AnimationClip だけ作成"),
                    MayaCamLinkSettings.UseTimeline, EditorStyles.boldLabel);
                using (new EditorGUI.DisabledScope(!MayaCamLinkSettings.UseTimeline))
                    TimelineTargetField();
            }

            // ---------- 情報 ----------
            if (MayaCamLinkProcessor.LastBakeInfo != "-")
                EditorGUILayout.LabelField("◆ " + MayaCamLinkProcessor.LastBakeInfo, EditorStyles.wordWrappedMiniLabel);
            if (MayaCamLinkProcessor.LastResW > 0)
                EditorGUILayout.LabelField(
                    $"▭ {MayaCamLinkProcessor.LastResW}×{MayaCamLinkProcessor.LastResH}  ← Game ビューを同じ比率に",
                    EditorStyles.miniLabel);

            // ---------- 設定 ----------
            _showSettings = EditorGUILayout.Foldout(_showSettings, "≡ 設定", true);
            if (_showSettings)
            {
                EditorGUI.indentLevel++;
                using (new EditorGUI.DisabledScope(running))
                {
                    MayaCamLinkSettings.Port = EditorGUILayout.IntField("ポート", MayaCamLinkSettings.Port);
                    MayaCamLinkSettings.AllowRemote = EditorGUILayout.Toggle(
                        new GUIContent("外部接続を許可", "別PCの Maya から接続 (0.0.0.0)"), MayaCamLinkSettings.AllowRemote);
                }
                MayaCamLinkSettings.AutoStart = EditorGUILayout.Toggle("自動開始", MayaCamLinkSettings.AutoStart);
                MayaCamLinkSettings.AutoCreateRig = EditorGUILayout.Toggle("Rig 自動作成", MayaCamLinkSettings.AutoCreateRig);
                MayaCamLinkSettings.OutputFolder = EditorGUILayout.TextField("出力フォルダ", MayaCamLinkSettings.OutputFolder);
                EditorGUI.indentLevel--;
            }
        }

        static string ToAbsolute(string assetPath)
        {
            var root = System.IO.Path.GetDirectoryName(Application.dataPath).Replace('\\', '/');
            var abs = root + "/" + assetPath;
            return System.IO.Directory.Exists(abs) ? abs : Application.dataPath;
        }

        static readonly Dictionary<string, string> _pendingNames = new Dictionary<string, string>();

        /// <summary>初めて受信したカメラ: 名前を決めて「作成」(以降は自動で上書き)</summary>
        void PendingGUI()
        {
            var pending = MayaCamLinkProcessor.Pending;
            if (pending.Count == 0) return;
            string dismiss = null, create = null;
            foreach (var cam in pending.Keys.ToList())
            {
                var p = pending[cam];
                var bg = GUI.backgroundColor;
                GUI.backgroundColor = new Color(1f, 0.82f, 0.35f);
                using (new EditorGUILayout.VerticalScope(EditorStyles.helpBox))
                {
                    GUI.backgroundColor = bg;
                    using (new EditorGUILayout.HorizontalScope())
                    {
                        GUILayout.Label($"◆ {cam}   {p.frames.Length} f   未作成", EditorStyles.boldLabel);
                        GUILayout.FlexibleSpace();
                        if (GUILayout.Button(new GUIContent("×", "今は作成しない (次に受信すると再表示)"), GUILayout.Width(22)))
                            dismiss = cam;
                    }
                    using (new EditorGUILayout.HorizontalScope())
                    {
                        EditorGUILayout.LabelField(new GUIContent("Clip 名", "このカメラの AnimationClip の名前 (作成後は自動で上書き)"),
                            GUILayout.Width(96));
                        if (!_pendingNames.TryGetValue(cam, out var name)) name = MayaCamClipBuilder.ClipFileName(cam);
                        _pendingNames[cam] = EditorGUILayout.TextField(name);
                        GUILayout.Label(".anim", EditorStyles.miniLabel, GUILayout.Width(34));
                        GUI.backgroundColor = Green;
                        if (GUILayout.Button(new GUIContent("作成", "Clip を作成し、以降は Maya の変更を自動で反映"),
                                GUILayout.Width(52)))
                            create = cam;
                        GUI.backgroundColor = bg;
                    }
                }
            }
            // レイアウトが崩れないよう、描画が終わってから実行
            if (dismiss != null)
            {
                MayaCamLinkProcessor.DismissPending(dismiss);
                _pendingNames.Remove(dismiss);
                GUIUtility.ExitGUI();
            }
            if (create != null)
            {
                CreatePending(create, _pendingNames[create]);
                GUIUtility.ExitGUI();
            }
        }

        static void CreatePending(string cam, string name)
        {
            var path = MayaCamLinkProcessor.NewClipPath(cam, name);
            if (AssetDatabase.LoadAssetAtPath<AnimationClip>(path) != null &&
                !EditorUtility.DisplayDialog("Maya Cam Link",
                    $"{System.IO.Path.GetFileName(path)} は既にあります。上書きしますか？", "上書き", "キャンセル"))
                return;
            MayaCamLinkProcessor.CreatePendingClip(cam, name);
            _pendingNames.Remove(cam);
            GUI.FocusControl(null);
        }

        /// <summary>Clip 初期名 ({scene} / {cam} を置き換え) + 実際のファイル名のプレビュー</summary>
        static void ClipNameField()
        {
            using (new EditorGUILayout.HorizontalScope())
            {
                EditorGUILayout.LabelField(new GUIContent("Clip 初期名",
                    "新しいカメラの Clip 名の初期値 ({scene} = シーン名、{cam} = Maya のカメラ名)\n作成済みのカメラは、作成した Clip に上書きされます"),
                    GUILayout.Width(96));
                var cur = MayaCamLinkSettings.ClipName;
                var edited = EditorGUILayout.DelayedTextField(cur);
                if (edited != cur) MayaCamLinkSettings.ClipName = edited;
                if (GUILayout.Button(new GUIContent("既定", MayaCamLinkSettings.DefaultClipName), GUILayout.Width(52)))
                {
                    MayaCamLinkSettings.ClipName = MayaCamLinkSettings.DefaultClipName;
                    GUI.FocusControl(null);
                }
            }
            string cam = MayaCamLinkProcessor.LastCamera != "-" ? MayaCamLinkProcessor.LastCamera : "camera1";
            using (new EditorGUILayout.HorizontalScope())
            {
                GUILayout.Space(100);
                GUILayout.Label("→ " + MayaCamClipBuilder.ClipFileName(cam) + ".anim", EditorStyles.miniLabel);
            }
        }

        /// <summary>追加先 Timeline の指定: 欄 + 選択中を使う + 新規作成</summary>
        static void TimelineTargetField()
        {
            using (new EditorGUILayout.HorizontalScope())
            {
                EditorGUILayout.LabelField(new GUIContent("追加先", "カメラのトラックを追加し、再生ヘッドを同期する Timeline"),
                    GUILayout.Width(96));
                var cur = MayaCamLinkProcessor.TargetDirector;
                var picked = (UnityEngine.Playables.PlayableDirector)EditorGUILayout.ObjectField(
                    cur, typeof(UnityEngine.Playables.PlayableDirector), true);
                if (picked != cur) MayaCamLinkProcessor.TargetDirector = picked;
                if (GUILayout.Button(new GUIContent("選択中", "Hierarchy で選択中 / Timeline ウィンドウで開いている Timeline を使う"),
                        GUILayout.Width(52)))
                {
                    var sel = MayaCamLinkProcessor.PickSelectedDirector();
                    if (sel) MayaCamLinkProcessor.TargetDirector = sel;
                    else EditorUtility.DisplayDialog("Maya Cam Link",
                        "Timeline (PlayableDirector) を Hierarchy で選択するか、Timeline ウィンドウで開いてください。", "OK");
                }
                if (GUILayout.Button(new GUIContent("新規…", "新しい Timeline を作成して追加先にする"), GUILayout.Width(52)))
                {
                    var folder = MayaCamLinkProcessor.EnsureFolder(MayaCamLinkSettings.TimelineFolder);
                    var path = EditorUtility.SaveFilePanelInProject("Timeline を作成",
                        MayaCamLinkProcessor.SceneName() + "_MayaCamLink", "playable", "", folder);
                    if (!string.IsNullOrEmpty(path))
                    {
                        MayaCamLinkSettings.TimelineFolder = System.IO.Path.GetDirectoryName(path).Replace('\\', '/');
                        OpenTimeline(MayaCamLinkProcessor.CreateTimeline(path));
                    }
                }
            }

            var dir = MayaCamLinkProcessor.TargetDirector;
            if (!dir)
                EditorGUILayout.HelpBox("Timeline を指定してください (未指定の間は AnimationClip だけ作成)", MessageType.None);
            else if (!(dir.playableAsset is UnityEngine.Timeline.TimelineAsset))
                EditorGUILayout.HelpBox("この PlayableDirector に Timeline アセットがありません", MessageType.Warning);
        }

        static void OpenTimeline(UnityEngine.Playables.PlayableDirector dir)
        {
            if (!dir) return;
            Selection.activeGameObject = dir.gameObject;
            EditorApplication.ExecuteMenuItem("Window/Sequencing/Timeline");
        }

        /// <summary>フォルダを Project ウィンドウで表示 (Shift+クリック: エクスプローラー)</summary>
        static void RevealFolder(string assetFolder)
        {
            assetFolder = MayaCamLinkProcessor.EnsureFolder(assetFolder);
            if (Event.current != null && Event.current.shift)
            {
                EditorUtility.RevealInFinder(ToAbsolute(assetFolder));
                return;
            }
            var folderObj = AssetDatabase.LoadAssetAtPath<Object>(assetFolder);
            if (!folderObj) return;
            EditorUtility.FocusProjectWindow();
            // 直下の最新ファイルを選択すると、そのフォルダの中身が開く
            string latest = null;
            var latestTime = System.DateTime.MinValue;
            foreach (var guid in AssetDatabase.FindAssets("", new[] { assetFolder }))
            {
                var p = AssetDatabase.GUIDToAssetPath(guid);
                if (AssetDatabase.IsValidFolder(p)) continue;
                if (System.IO.Path.GetDirectoryName(p).Replace('\\', '/') != assetFolder) continue;
                var time = System.IO.File.GetLastWriteTimeUtc(p);
                if (time > latestTime) { latestTime = time; latest = p; }
            }
            if (latest != null)
            {
                var obj = AssetDatabase.LoadAssetAtPath<Object>(latest);
                Selection.activeObject = obj;
                EditorGUIUtility.PingObject(obj);
            }
            else
            {
                AssetDatabase.OpenAsset(folderObj);  // 空のフォルダはそのまま開く
            }
        }

        /// <summary>ラベル + パス入力 + 選択ボタン + 表示ボタン の 1 行</summary>
        static void FolderField(string label, string tip, string current, System.Action<string> setter)
        {
            using (new EditorGUILayout.HorizontalScope())
            {
                EditorGUILayout.LabelField(new GUIContent(label, tip), GUILayout.Width(96));
                var edited = EditorGUILayout.DelayedTextField(current);
                if (edited != current) SetFolder(edited, setter);
                if (GUILayout.Button("選択…", GUILayout.Width(52)))
                {
                    var abs = EditorUtility.OpenFolderPanel(label + " (Assets 内)", ToAbsolute(current), "");
                    if (!string.IsNullOrEmpty(abs)) SetFolder(abs, setter);
                }
                if (GUILayout.Button(new GUIContent("表示", "Project ウィンドウでフォルダを表示\nShift+クリック: エクスプローラーで開く"),
                        GUILayout.Width(52)))
                    RevealFolder(current);
            }
        }

        /// <summary>絶対パス / Assets 相対パスを受け取り、Assets 内なら設定</summary>
        static void SetFolder(string path, System.Action<string> setter)
        {
            path = (path ?? "").Trim().Replace('\\', '/').TrimEnd('/');
            var data = Application.dataPath.Replace('\\', '/');   // .../Project/Assets
            if (path.StartsWith(data, System.StringComparison.OrdinalIgnoreCase))
                path = "Assets" + path.Substring(data.Length);
            if (path == "Assets" || path.StartsWith("Assets/"))
                setter(path);
            else
                EditorUtility.DisplayDialog("Maya Cam Link", "プロジェクトの Assets フォルダ内を指定してください。", "OK");
        }

        bool Chip(string label, string tip, bool on)
        {
            var bg = GUI.backgroundColor;
            if (on) GUI.backgroundColor = new Color(0.45f, 0.75f, 1f);
            bool r = GUILayout.Toggle(on, new GUIContent(label, tip), _chip);
            GUI.backgroundColor = bg;
            return r;
        }
    }

    [CustomEditor(typeof(MayaCameraRig))]
    public class MayaCameraRigEditor : UnityEditor.Editor
    {
        public override void OnInspectorGUI()
        {
            var rig = (MayaCameraRig)target;
            if (rig.HasLiveOverride)
                EditorGUILayout.HelpBox("● Maya ライブ中", MessageType.Info);
            EditorGUILayout.LabelField("垂直 FOV", $"{rig.VerticalFov:0.###}°");
            DrawDefaultInspector();
            if (rig.HasLiveOverride && GUILayout.Button("↩ Timeline に戻す"))
                MayaCamLinkProcessor.ClearAllLiveOverrides();
        }
    }
}
