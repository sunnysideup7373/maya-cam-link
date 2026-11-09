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
                if (GUILayout.Button(new GUIContent("▤ Timeline", "Timeline を選択して開く"), _chip))
                {
                    var dir = MayaCamLinkProcessor.FindDirector(false);
                    if (dir)
                    {
                        Selection.activeGameObject = dir.gameObject;
                        EditorApplication.ExecuteMenuItem("Window/Sequencing/Timeline");
                    }
                }
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

            // ---------- キーの出力先 ----------
            EditorGUILayout.Space(2);
            using (new EditorGUILayout.VerticalScope(EditorStyles.helpBox))
            {
                // AnimationClip
                EditorGUILayout.LabelField("AnimationClip (.anim)", EditorStyles.boldLabel);
                FolderField("Clip 保存先", "カメラの .anim を保存するフォルダ",
                    MayaCamLinkSettings.ClipFolder, v => MayaCamLinkSettings.ClipFolder = v);

                EditorGUILayout.Space(4);

                // Timeline (別行)
                MayaCamLinkSettings.CreateTimeline = EditorGUILayout.ToggleLeft(
                    new GUIContent("Timeline を作成", "OFF: キー受信時に AnimationClip だけ作成"),
                    MayaCamLinkSettings.CreateTimeline, EditorStyles.boldLabel);
                using (new EditorGUI.DisabledScope(!MayaCamLinkSettings.CreateTimeline))
                    FolderField("Timeline 保存先", "Timeline (.playable) を保存するフォルダ",
                        MayaCamLinkSettings.TimelineFolder, v => MayaCamLinkSettings.TimelineFolder = v);
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
                MayaCamLinkSettings.DirectorName = EditorGUILayout.TextField("Timeline 名", MayaCamLinkSettings.DirectorName);
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

        /// <summary>ラベル + パス入力 + 選択ボタン の 1 行</summary>
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
