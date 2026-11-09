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
        /// <summary>false: キーを受け取っても AnimationClip だけ作成し、Timeline は作らない</summary>
        public static bool CreateTimeline
        {
            get => EditorPrefs.GetBool(P + "CreateTimeline", true);
            set => EditorPrefs.SetBool(P + "CreateTimeline", value);
        }
        public static bool AutoCreateRig
        {
            get => EditorPrefs.GetBool(P + "AutoCreateRig", true);
            set => EditorPrefs.SetBool(P + "AutoCreateRig", value);
        }
        public static string DirectorName
        {
            get => EditorPrefs.GetString(P + "DirectorName", "MayaCamLink_Timeline");
            set => EditorPrefs.SetString(P + "DirectorName", value);
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
        /// <summary>Timeline (.playable) の保存先。空なら 出力フォルダ</summary>
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
