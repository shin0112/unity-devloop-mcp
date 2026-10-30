using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using UnityEditor;
using UnityEditor.Compilation;
using UnityEditor.SceneManagement;
using UnityEngine;

namespace Shin0112.UnityDevloopMcp.Editor
{
    [InitializeOnLoad]
    public static class UnityDevloopMcpBridge
    {
        public const string PortPrefKey = "Shin0112.UnityDevloopMcp.Port";
        public const string EnabledPrefKey = "Shin0112.UnityDevloopMcp.Enabled";
        public const int DefaultPort = 17890;

        private const int MaxLogEntries = 500;
        private const int MaxStackTraceLength = 2000;
        private const int RequestTimeoutMs = 30000;
        private const int PortSearchCount = 10;
        private const int MaxHeaderLength = 16384;

        private sealed class PendingRequest
        {
            public string Route;
            public string Body;
            public string Response;
            public readonly ManualResetEventSlim Done = new ManualResetEventSlim(false);
        }

        private struct LogEntry
        {
            public string Time;
            public string Type;
            public string Message;
            public string StackTrace;
        }

        [System.Serializable]
        private sealed class ConsoleRequest
        {
            public int limit = 100;
            public string types;
            public bool clear;
        }

        [System.Serializable]
        private sealed class MenuRequest
        {
            public string path;
        }

        private static readonly ConcurrentQueue<PendingRequest> Pending = new ConcurrentQueue<PendingRequest>();
        private static readonly Queue<LogEntry> Logs = new Queue<LogEntry>();
        private static readonly object LogLock = new object();

        private static TcpListener _listener;
        private static Thread _listenerThread;
        private static volatile bool _running;
        private static string _projectPath;

        public static bool IsRunning => _listener != null && _running;
        public static int Port => EditorPrefs.GetInt(PortPrefKey, DefaultPort);
        public static int ActivePort { get; private set; }

        static UnityDevloopMcpBridge()
        {
            _projectPath = Directory.GetParent(Application.dataPath).FullName;

            Application.logMessageReceivedThreaded += HandleLog;
            CompilationPipeline.assemblyCompilationFinished += HandleCompilationFinished;
            EditorApplication.update += ProcessPending;
            AssemblyReloadEvents.beforeAssemblyReload += Stop;
            EditorApplication.quitting += Stop;

            if (EditorPrefs.GetBool(EnabledPrefKey, true)) Start();
        }

        // -----------------------------------------------------
        // Lifecycle
        // -----------------------------------------------------
        public static void Start()
        {
            if (IsRunning) return;

            int basePort = Port;
            for (int offset = 0; offset < PortSearchCount; offset++)
            {
                int port = basePort + offset;
                TcpListener listener = new TcpListener(IPAddress.Loopback, port) { ExclusiveAddressUse = true };
                try
                {
                    listener.Start();
                }
                catch (SocketException)
                {
                    continue;
                }

                _listener = listener;
                ActivePort = port;
                break;
            }

            if (_listener == null)
            {
                Debug.LogWarning($"[UnityDevloopMcp] Failed to start: ports {basePort}-{basePort + PortSearchCount - 1} are all in use.");
                return;
            }

            if (ActivePort != basePort)
                Debug.LogWarning($"[UnityDevloopMcp] Port {basePort} is in use, listening on {ActivePort} instead. The MCP server finds it automatically.");

            _running = true;
            TcpListener activeListener = _listener;
            _listenerThread = new Thread(() => ListenLoop(activeListener)) { IsBackground = true, Name = "UnityDevloopMcpBridge" };
            _listenerThread.Start();
        }

        public static void Stop()
        {
            _running = false;
            if (_listener == null) return;

            try
            {
                _listener.Stop();
            }
            catch (System.Exception)
            {
            }

            _listener = null;
            _listenerThread = null;
            ActivePort = 0;
        }

        // -----------------------------------------------------
        // Listener thread
        // -----------------------------------------------------
        private static void ListenLoop(TcpListener listener)
        {
            while (_running)
            {
                TcpClient client;
                try
                {
                    client = listener.AcceptTcpClient();
                }
                catch (System.Exception)
                {
                    return;
                }

                ThreadPool.QueueUserWorkItem(_ => HandleClient(client));
            }
        }

        private static void HandleClient(TcpClient client)
        {
            using (client)
            {
                NetworkStream stream;
                string route;
                string body;
                try
                {
                    stream = client.GetStream();
                    if (!TryReadRequest(stream, out route, out body)) return;
                }
                catch (System.Exception)
                {
                    return;
                }

                string response;
                try
                {
                    response = route == "/ping" ? BuildPing() : DispatchOnMainThread(route, body);
                }
                catch (System.Exception exception)
                {
                    response = Error(exception.Message);
                }

                try
                {
                    WriteResponse(stream, response);
                }
                catch (System.Exception)
                {
                }
            }
        }

        private static string DispatchOnMainThread(string route, string body)
        {
            PendingRequest request = new PendingRequest { Route = route, Body = body };
            Pending.Enqueue(request);
            return request.Done.Wait(RequestTimeoutMs)
                ? request.Response
                : Error("Timed out waiting for the editor main thread (editor busy or unfocused with throttled updates).");
        }

        private static bool TryReadRequest(NetworkStream stream, out string route, out string body)
        {
            route = null;
            body = string.Empty;

            List<byte> header = new List<byte>(512);
            int matched = 0;
            while (matched < 4)
            {
                int value = stream.ReadByte();
                if (value < 0 || header.Count >= MaxHeaderLength) return false;

                header.Add((byte)value);
                bool expectCr = matched % 2 == 0;
                if (expectCr ? value == '\r' : value == '\n') matched++;
                else matched = value == '\r' ? 1 : 0;
            }

            string[] lines = Encoding.ASCII.GetString(header.ToArray()).Split(new[] { "\r\n" }, System.StringSplitOptions.None);
            string[] requestLine = lines[0].Split(' ');
            if (requestLine.Length < 2) return false;

            route = requestLine[1];
            int queryIndex = route.IndexOf('?');
            if (queryIndex >= 0) route = route.Substring(0, queryIndex);

            int contentLength = 0;
            for (int i = 1; i < lines.Length; i++)
            {
                int separator = lines[i].IndexOf(':');
                if (separator <= 0) continue;
                if (!lines[i].Substring(0, separator).Trim().Equals("Content-Length", System.StringComparison.OrdinalIgnoreCase)) continue;

                int.TryParse(lines[i].Substring(separator + 1).Trim(), out contentLength);
            }

            if (contentLength <= 0) return true;

            byte[] buffer = new byte[contentLength];
            int read = 0;
            while (read < contentLength)
            {
                int count = stream.Read(buffer, read, contentLength - read);
                if (count <= 0) return false;
                read += count;
            }

            body = Encoding.UTF8.GetString(buffer);
            return true;
        }

        private static void WriteResponse(NetworkStream stream, string response)
        {
            byte[] content = Encoding.UTF8.GetBytes(response);
            byte[] header = Encoding.ASCII.GetBytes(
                "HTTP/1.1 200 OK\r\n" +
                "Content-Type: application/json; charset=utf-8\r\n" +
                $"Content-Length: {content.Length}\r\n" +
                "Connection: close\r\n\r\n");

            stream.Write(header, 0, header.Length);
            stream.Write(content, 0, content.Length);
            stream.Flush();
        }

        private static string BuildPing()
        {
            StringBuilder json = new StringBuilder("{\"ok\":true");
            AppendField(json, "projectPath", _projectPath);
            json.Append(",\"port\":").Append(ActivePort);
            json.Append(",\"pid\":").Append(System.Diagnostics.Process.GetCurrentProcess().Id);
            json.Append('}');
            return json.ToString();
        }

        // -----------------------------------------------------
        // Main thread
        // -----------------------------------------------------
        private static void ProcessPending()
        {
            while (Pending.TryDequeue(out PendingRequest request))
            {
                try
                {
                    request.Response = Dispatch(request.Route, request.Body);
                }
                catch (System.Exception exception)
                {
                    request.Response = Error(exception.Message);
                }
                finally
                {
                    request.Done.Set();
                }
            }
        }

        private static string Dispatch(string route, string body)
        {
            switch (route)
            {
                case "/status":
                    return BuildStatus();
                case "/console":
                    return BuildConsole(ParseOrDefault<ConsoleRequest>(body));
                case "/menu":
                    return ExecuteMenu(ParseOrDefault<MenuRequest>(body));
                case "/refresh":
                    AssetDatabase.Refresh();
                    return "{\"ok\":true}";
                default:
                    return Error($"Unknown route '{route}'.");
            }
        }

        // -----------------------------------------------------
        // Commands
        // -----------------------------------------------------
        private static string BuildStatus()
        {
            StringBuilder json = new StringBuilder("{\"ok\":true");
            AppendField(json, "projectPath", _projectPath);
            json.Append(",\"port\":").Append(ActivePort);
            AppendField(json, "unityVersion", Application.unityVersion);
            AppendField(json, "activeScene", EditorSceneManager.GetActiveScene().path);
            AppendField(json, "buildTarget", EditorUserBuildSettings.activeBuildTarget.ToString());
            json.Append(",\"isCompiling\":").Append(EditorApplication.isCompiling ? "true" : "false");
            json.Append(",\"isPlaying\":").Append(EditorApplication.isPlaying ? "true" : "false");
            json.Append(",\"isPaused\":").Append(EditorApplication.isPaused ? "true" : "false");
            json.Append('}');
            return json.ToString();
        }

        private static string BuildConsole(ConsoleRequest request)
        {
            HashSet<string> types = null;
            if (!string.IsNullOrWhiteSpace(request.types))
            {
                types = new HashSet<string>();
                foreach (string type in request.types.Split(',')) types.Add(type.Trim().ToLowerInvariant());
            }

            List<LogEntry> selected = new List<LogEntry>();
            lock (LogLock)
            {
                foreach (LogEntry entry in Logs)
                {
                    if (types == null || types.Contains(entry.Type)) selected.Add(entry);
                }

                if (request.clear) Logs.Clear();
            }

            int limit = request.limit > 0 ? request.limit : 100;
            int start = Mathf.Max(0, selected.Count - limit);

            StringBuilder json = new StringBuilder("{\"ok\":true,\"total\":").Append(selected.Count).Append(",\"entries\":[");
            for (int i = start; i < selected.Count; i++)
            {
                if (i > start) json.Append(',');
                LogEntry entry = selected[i];
                json.Append('{');
                json.Append("\"time\":\"").Append(Escape(entry.Time)).Append('"');
                AppendField(json, "type", entry.Type);
                AppendField(json, "message", entry.Message);
                if (!string.IsNullOrEmpty(entry.StackTrace)) AppendField(json, "stackTrace", entry.StackTrace);
                json.Append('}');
            }

            json.Append("]}");
            return json.ToString();
        }

        private static string ExecuteMenu(MenuRequest request)
        {
            if (string.IsNullOrWhiteSpace(request.path)) return Error("Menu path is empty.");

            bool executed = EditorApplication.ExecuteMenuItem(request.path);
            return executed ? "{\"ok\":true}" : Error($"Menu item not found or disabled: '{request.path}'.");
        }

        // -----------------------------------------------------
        // Log capture
        // -----------------------------------------------------
        private static void HandleLog(string message, string stackTrace, LogType type)
        {
            bool keepStack = type == LogType.Error || type == LogType.Exception || type == LogType.Assert;
            AddLog(ToTypeName(type), message, keepStack ? stackTrace : null);
        }

        private static void HandleCompilationFinished(string assemblyPath, CompilerMessage[] messages)
        {
            foreach (CompilerMessage message in messages)
            {
                string type = message.type == CompilerMessageType.Error ? "compile-error" : "compile-warning";
                AddLog(type, message.message, null);
            }
        }

        private static void AddLog(string type, string message, string stackTrace)
        {
            if (stackTrace != null && stackTrace.Length > MaxStackTraceLength) stackTrace = stackTrace.Substring(0, MaxStackTraceLength);

            lock (LogLock)
            {
                Logs.Enqueue(new LogEntry
                {
                    Time = System.DateTime.Now.ToString("HH:mm:ss.fff"),
                    Type = type,
                    Message = message,
                    StackTrace = stackTrace,
                });

                while (Logs.Count > MaxLogEntries) Logs.Dequeue();
            }
        }

        private static string ToTypeName(LogType type)
        {
            switch (type)
            {
                case LogType.Warning:
                    return "warning";
                case LogType.Error:
                    return "error";
                case LogType.Exception:
                    return "exception";
                case LogType.Assert:
                    return "assert";
                default:
                    return "log";
            }
        }

        // -----------------------------------------------------
        // JSON helpers
        // -----------------------------------------------------
        private static T ParseOrDefault<T>(string body) where T : new()
        {
            if (string.IsNullOrWhiteSpace(body)) return new T();

            try
            {
                return JsonUtility.FromJson<T>(body) ?? new T();
            }
            catch (System.Exception)
            {
                return new T();
            }
        }

        private static string Error(string message)
        {
            return "{\"ok\":false,\"error\":\"" + Escape(message) + "\"}";
        }

        private static void AppendField(StringBuilder json, string name, string value)
        {
            json.Append(",\"").Append(name).Append("\":\"").Append(Escape(value)).Append('"');
        }

        private static string Escape(string value)
        {
            if (string.IsNullOrEmpty(value)) return string.Empty;

            StringBuilder escaped = new StringBuilder(value.Length + 16);
            foreach (char c in value)
            {
                switch (c)
                {
                    case '"': escaped.Append("\\\""); break;
                    case '\\': escaped.Append("\\\\"); break;
                    case '\n': escaped.Append("\\n"); break;
                    case '\r': escaped.Append("\\r"); break;
                    case '\t': escaped.Append("\\t"); break;
                    default:
                        if (c < 0x20) escaped.Append("\\u").Append(((int)c).ToString("x4"));
                        else escaped.Append(c);
                        break;
                }
            }

            return escaped.ToString();
        }
    }
}
