using System;
using System.Diagnostics;
using System.IO;
using System.Text.RegularExpressions;
using UnityEditor;
using UnityEditor.PackageManager;
using UnityEditor.PackageManager.Requests;
using UnityEngine;
using Debug = UnityEngine.Debug;
using PackageInfo = UnityEditor.PackageManager.PackageInfo;

namespace MayaCamLink.Editor
{
    /// <summary>
    /// 「Install package from disk」で入れると manifest.json にローカルパス (file:...) が書かれ、
    /// プロジェクトを Git で共有した相手の PC ではパッケージが見つからない。
    /// それを検出して Git URL 参照か、プロジェクトへの埋め込みに切り替える。
    /// </summary>
    public static class MayaCamLinkPackageSource
    {
        public const string PackageName = "com.sunny.mayacamlink";
        public const string GitUrl = "https://github.com/sunnysideup7373/maya-cam-link.git?path=/unity/com.sunny.mayacamlink";

        // 注意: Unity の中では "<プロジェクト>/Packages/<パッケージ名>" へのパスはパッケージの実体の場所に
        // 読み替えられる (絶対パスでも)。埋め込み先の存在確認やコピーは System.IO で直接行わない。
        static string ProjectRoot => Path.GetFullPath(Path.GetDirectoryName(Application.dataPath));
        static string ManifestPath => Path.Combine(ProjectRoot, "Packages", "manifest.json");
        static string EmbedPath => Path.Combine(ProjectRoot, "Packages", PackageName);
        static string HideKey => "MayaCamLink.HideSourceWarning." + Application.dataPath;

        static double _checkedAt = -10;
        static bool _local;
        static string _reference;
        static AddRequest _add;

        public static bool Busy => _add != null;

        static PackageInfo Info => PackageInfo.FindForAssembly(typeof(MayaCamLinkPackageSource).Assembly);

        /// <summary>manifest.json に書かれている参照</summary>
        public static string Reference { get { Check(); return _reference; } }

        /// <summary>プロジェクトの外を指すローカルパス参照か (共有相手の PC では解決できない)</summary>
        public static bool IsLocalReference { get { Check(); return _local; } }

        public static bool ShowWarning
        {
            get => IsLocalReference && !EditorPrefs.GetBool(HideKey, false);
            set => EditorPrefs.SetBool(HideKey, !value);
        }

        static void Check()
        {
            if (EditorApplication.timeSinceStartup - _checkedAt < 2) return;
            _checkedAt = EditorApplication.timeSinceStartup;
            _local = false;
            _reference = null;
            var info = Info;
            if (info == null) return;
            _reference = info.packageId.StartsWith(PackageName + "@") ? info.packageId.Substring(PackageName.Length + 1) : info.packageId;
            if (info.source != PackageSource.Local || string.IsNullOrEmpty(info.resolvedPath)) return;
            // プロジェクトのフォルダの中を相対パスで指している場合は一緒に共有されるので OK
            string root = ProjectRoot.TrimEnd('\\', '/') + Path.DirectorySeparatorChar;
            _local = !Path.GetFullPath(info.resolvedPath).StartsWith(root, StringComparison.OrdinalIgnoreCase);
        }

        static void Invalidate() => _checkedAt = -10;

        // ------------------------------------------------------------------
        /// <summary>GitHub の URL 参照に切り替える (相手の PC に Git が必要)</summary>
        public static void SwitchToGit()
        {
            if (Busy) return;
            _add = Client.Add(GitUrl);
            EditorApplication.update += WaitAdd;
        }

        static void WaitAdd()
        {
            if (_add == null || !_add.IsCompleted) return;
            EditorApplication.update -= WaitAdd;
            if (_add.Status == StatusCode.Success)
                Debug.Log($"[Maya Cam Link] Git URL 参照に切り替えました: {GitUrl}");
            else
                EditorUtility.DisplayDialog("Maya Cam Link",
                    "Git URL への切り替えに失敗しました。\n" + _add.Error?.message +
                    "\n\nGit がインストールされ、PATH が通っているか確認してください。", "OK");
            _add = null;
            Invalidate();
        }

        /// <summary>パッケージを Packages/ にコピーし、プロジェクトと一緒にコミットできるようにする</summary>
        public static bool Embed()
        {
            var info = Info;
            if (info == null || string.IsNullOrEmpty(info.resolvedPath))
            {
                EditorUtility.DisplayDialog("Maya Cam Link", "パッケージの場所が見つかりません。", "OK");
                return false;
            }
            if (info.source == PackageSource.Embedded) return true;
            string src = Path.GetFullPath(info.resolvedPath).TrimEnd('\\', '/');

            try
            {
                // Unity のパス読み替えを避けるため、コピーは外部プロセスで行う
                string err = CopyExternally(src, EmbedPath);
                if (err != null) throw new Exception(err);

                // manifest から参照を外す (埋め込みパッケージは manifest に書かない)
                var text = File.ReadAllText(ManifestPath);
                text = Regex.Replace(text,
                    "\\s*\"" + Regex.Escape(PackageName) + "\"\\s*:\\s*\"[^\"]*\"\\s*,?", "");
                text = Regex.Replace(text, ",(\\s*})", "$1");
                File.WriteAllText(ManifestPath, text);
            }
            catch (Exception e)
            {
                EditorUtility.DisplayDialog("Maya Cam Link", "埋め込みに失敗しました。\n" + e.Message, "OK");
                return false;
            }
            Debug.Log($"[Maya Cam Link] パッケージを {EmbedPath} に埋め込みました。プロジェクトと一緒にコミットしてください。");
            Invalidate();
            Client.Resolve();
            return true;
        }

        /// <summary>src フォルダの中身を dst にコピー (.git などドットで始まるものは除く)。失敗時はメッセージ</summary>
        static string CopyExternally(string src, string dst)
        {
            ProcessStartInfo psi;
            if (Application.platform == RuntimePlatform.WindowsEditor)
                psi = new ProcessStartInfo("robocopy",
                    $"\"{src}\" \"{dst}\" /E /XD .* /XF .* /NFL /NDL /NJH /NJS /NP");
            else
                psi = new ProcessStartInfo("/bin/sh",
                    $"-c \"mkdir -p '{dst}' && cd '{src}' && tar --exclude='.*' -cf - . | (cd '{dst}' && tar -xf -)\"");
            psi.UseShellExecute = false;
            psi.CreateNoWindow = true;
            psi.RedirectStandardOutput = true;
            psi.RedirectStandardError = true;
            using (var p = Process.Start(psi))
            {
                string output = p.StandardOutput.ReadToEnd() + p.StandardError.ReadToEnd();
                p.WaitForExit();
                // robocopy は 0–7 が成功
                bool ok = Application.platform == RuntimePlatform.WindowsEditor ? p.ExitCode < 8 : p.ExitCode == 0;
                return ok ? null : $"コピーに失敗しました (code {p.ExitCode})\n{output}";
            }
        }
    }
}
