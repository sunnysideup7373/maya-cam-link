using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using UnityEditor;
using UnityEngine;

namespace MayaCamLink.Editor
{
    /// <summary>
    /// TCP サーバー (バックグラウンドスレッド)。Maya がクライアントとして接続し、1 行 1 JSON で送信。
    /// 受信データはキューに入れ、MayaCamLinkProcessor がメインスレッドで処理する。
    /// </summary>
    [InitializeOnLoad]
    public static class MayaCamLinkServer
    {
        static TcpListener _listener;
        static Thread _acceptThread;
        static volatile bool _running;
        static readonly ConcurrentQueue<string> _queue = new ConcurrentQueue<string>();
        static readonly List<TcpClient> _clients = new List<TcpClient>();
        static readonly object _lock = new object();

        public static bool IsRunning => _running;
        public static string LastError { get; private set; }

        public static int ClientCount
        {
            get { lock (_lock) return _clients.Count; }
        }

        static MayaCamLinkServer()
        {
            AssemblyReloadEvents.beforeAssemblyReload += Stop;
            EditorApplication.quitting += Stop;
            if (MayaCamLinkSettings.AutoStart)
                EditorApplication.delayCall += Start;
        }

        public static void Start()
        {
            if (_running) return;
            try
            {
                var addr = MayaCamLinkSettings.AllowRemote ? IPAddress.Any : IPAddress.Loopback;
                _listener = new TcpListener(addr, MayaCamLinkSettings.Port);
                _listener.Start();
                LastError = null;
            }
            catch (Exception e)
            {
                LastError = e.Message;
                Debug.LogError($"[MayaCamLink] ポートを開けません {MayaCamLinkSettings.Port}: {e.Message}");
                _listener = null;
                return;
            }
            _running = true;
            _acceptThread = new Thread(AcceptLoop) { IsBackground = true, Name = "MayaCamLink Accept" };
            _acceptThread.Start();
        }

        public static void Stop()
        {
            _running = false;
            try { _listener?.Stop(); } catch { /* ignore */ }
            _listener = null;
            lock (_lock)
            {
                foreach (var c in _clients)
                    try { c.Close(); } catch { /* ignore */ }
                _clients.Clear();
            }
        }

        public static bool TryDequeue(out string line) => _queue.TryDequeue(out line);

        static void AcceptLoop()
        {
            while (_running)
            {
                try
                {
                    var c = _listener.AcceptTcpClient();
                    c.NoDelay = true;
                    lock (_lock) _clients.Add(c);
                    new Thread(() => ClientLoop(c)) { IsBackground = true, Name = "MayaCamLink Client" }.Start();
                }
                catch
                {
                    if (!_running) break;
                    Thread.Sleep(100);
                }
            }
        }

        static void ClientLoop(TcpClient c)
        {
            try
            {
                using (var reader = new StreamReader(c.GetStream(), new UTF8Encoding(false)))
                {
                    string line;
                    while (_running && (line = reader.ReadLine()) != null)
                    {
                        if (line.Length > 0) _queue.Enqueue(line);
                    }
                }
            }
            catch { /* 切断 */ }
            finally
            {
                lock (_lock) _clients.Remove(c);
                try { c.Close(); } catch { /* ignore */ }
                _queue.Enqueue("{\"type\":\"_disconnect\"}");
            }
        }
    }

    /// <summary>EditorPrefs の設定</summary>
    public static class MayaCamLinkSettings
    {
        const string P = "MayaCamLink.";

        public static int Port
        {
            get => EditorPrefs.GetInt(P + "Port", 7788);
            set => EditorPrefs.SetInt(P + "Port", value);
        }
        public static bool AutoStart
        {
            get => EditorPrefs.GetBool(P + "AutoStart", true);
            set => EditorPrefs.SetBool(P + "AutoStart", value);
        }
        public static bool AllowRemote
        {
            get => EditorPrefs.GetBool(P + "AllowRemote", false);
            set => EditorPrefs.SetBool(P + "AllowRemote", value);
        }
        public static bool ApplyLive
        {
            get => EditorPrefs.GetBool(P + "ApplyLive", true);
            set => EditorPrefs.SetBool(P + "ApplyLive", value);
        }
        public static bool SyncTime
        {
            get => EditorPrefs.GetBool(P + "SyncTime", true);
            set => EditorPrefs.SetBool(P + "SyncTime", value);
        }
        public static bool AcceptBake
        {
            get => EditorPrefs.GetBool(P + "AcceptBake", true);
            set => EditorPrefs.SetBool(P + "AcceptBake", value);
        }
        /// <summary>true: 指定した Timeline にカメラのトラックを追加し、再生ヘッドを同期する</summary>
        public static bool UseTimeline
        {
            get => EditorPrefs.GetBool(P + "UseTimeline", true);
            set => EditorPrefs.SetBool(P + "UseTimeline", value);
        }
        /// <summary>追加先 Timeline (PlayableDirector) の GlobalObjectId</summary>
        public static string TargetDirectorId
        {
            get => EditorPrefs.GetString(P + "TargetDirectorId", "");
            set => EditorPrefs.SetString(P + "TargetDirectorId", value);
        }
        /// <summary>追加先 Timeline の「シーンパス|階層パス」(ID で見つからない時の予備)</summary>
        public static string TargetDirectorPath
        {
            get => EditorPrefs.GetString(P + "TargetDirectorPath", "");
            set => EditorPrefs.SetString(P + "TargetDirectorPath", value);
        }
        public const string DefaultClipName = "{scene}_{cam}";
        /// <summary>AnimationClip のファイル名。{scene} = シーン名, {cam} = Maya のカメラ名</summary>
        public static string ClipName
        {
            get
            {
                var v = EditorPrefs.GetString(P + "ClipName", DefaultClipName);
                return string.IsNullOrWhiteSpace(v) ? DefaultClipName : v;
            }
            set => EditorPrefs.SetString(P + "ClipName", value);
        }
        public static bool AutoCreateRig
        {
            get => EditorPrefs.GetBool(P + "AutoCreateRig", true);
            set => EditorPrefs.SetBool(P + "AutoCreateRig", value);
        }
        public static string OutputFolder
        {
            get => EditorPrefs.GetString(P + "OutputFolder", "Assets/MayaCamLink");
            set => EditorPrefs.SetString(P + "OutputFolder", value);
        }
        /// <summary>カメラの AnimationClip (.anim) の保存先。空なら 出力フォルダ/Clips</summary>
        public static string ClipFolder
        {
            get
            {
                var v = EditorPrefs.GetString(P + "ClipFolder", "");
                return string.IsNullOrWhiteSpace(v) ? OutputFolder + "/Clips" : v;
            }
            set => EditorPrefs.SetString(P + "ClipFolder", value);
        }
        /// <summary>「新規」で作る Timeline (.playable) の既定の保存先。空なら 出力フォルダ</summary>
        public static string TimelineFolder
        {
            get
            {
                var v = EditorPrefs.GetString(P + "TimelineFolder", "");
                return string.IsNullOrWhiteSpace(v) ? OutputFolder : v;
            }
            set => EditorPrefs.SetString(P + "TimelineFolder", value);
        }
    }
}
