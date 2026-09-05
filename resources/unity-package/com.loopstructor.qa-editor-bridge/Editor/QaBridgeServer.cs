using System;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using Loopstructor.AutoPlayer.EditorBridge.Runtime;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using UnityEditor;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace Loopstructor.QA.EditorBridge
{
    internal sealed class QaBridgeServer : IDisposable
    {
        private const int SchemaVersion = 1;
        private const int ProtocolVersion = 1;
        private HttpListener m_listener;
        private Thread m_listenerThread;
        private string m_token;
        private string m_instanceId;
        private string m_projectRoot;
        private string m_artifactRoot;
        private string m_rendezvousPath;
        private string m_runtimeMessage = "进入 Play Mode 后启用运行控制。";
        private int m_port;
        private bool m_disposed;
        private double m_nextHeartbeat;

        /// <summary>启动回环监听与实例心跳。</summary>
        public void Start()
        {
            if (m_listener != null || m_disposed) return;
            m_projectRoot = Directory.GetParent(Application.dataPath).FullName;
            m_instanceId = "editor-" + Process.GetCurrentProcess().Id;
            m_token = CreateRandomHex(32);
            string projectId = HashText(m_projectRoot).Substring(0, 16);
            string dataRoot = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "LoopstructorAutoPlayer");
            m_artifactRoot = Path.Combine(dataRoot, "artifacts", "editor", projectId);
            Directory.CreateDirectory(m_artifactRoot);
            StartListener();
            RegisterEvents();
            RefreshRegistration();
            m_listenerThread = new Thread(ListenLoop)
            {
                IsBackground = true,
                Name = "Loopstructor 2 QA Editor Bridge"
            };
            m_listenerThread.Start();
        }

        /// <summary>在 Play Mode 中启动现有 AutoPlayer 运行模块。</summary>
        public void StartRuntime()
        {
            if (m_disposed || !EditorApplication.isPlaying || UnityEditorCheatBridge.IsRunning) return;
            try
            {
                UnityEditorCheatBridge.TryStart(m_artifactRoot, out m_runtimeMessage);
            }
            catch (Exception exception)
            {
                m_runtimeMessage = "Play Mode 运行模块启动失败：" + exception.Message;
            }
            RefreshRegistration();
        }

        /// <summary>停止 Play Mode 运行模块。</summary>
        public void StopRuntime()
        {
            try
            {
                UnityEditorCheatBridge.Stop();
                m_runtimeMessage = "进入 Play Mode 后启用运行控制。";
            }
            catch (Exception exception)
            {
                m_runtimeMessage = "停止 Play Mode 运行模块失败：" + exception.Message;
            }
            RefreshRegistration();
        }

        /// <summary>刷新原子实例登记文件。</summary>
        public void RefreshRegistration()
        {
            if (m_disposed || string.IsNullOrEmpty(m_instanceId)) return;
            string instancesRoot = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "LoopstructorAutoPlayer",
                "editor-instances");
            Directory.CreateDirectory(instancesRoot);
            m_rendezvousPath = Path.Combine(instancesRoot, m_instanceId + ".json");
            string assemblyPath = Path.Combine(m_projectRoot, "Library", "ScriptAssemblies", "Assembly-CSharp.dll");
            JObject payload = new JObject
            {
                ["schemaVersion"] = SchemaVersion,
                ["protocolVersion"] = ProtocolVersion,
                ["instanceId"] = m_instanceId,
                ["kind"] = "editor",
                ["processId"] = Process.GetCurrentProcess().Id,
                ["displayName"] = "Unity Editor · " + Path.GetFileName(m_projectRoot),
                ["projectPath"] = m_projectRoot,
                ["unityExecutablePath"] = Process.GetCurrentProcess().MainModule.FileName,
                ["unityVersion"] = Application.unityVersion,
                ["gameVersion"] = Application.version,
                ["sceneName"] = SceneManager.GetActiveScene().name,
                ["mode"] = CurrentMode,
                ["runtimeReady"] = UnityEditorCheatBridge.IsRunning,
                ["compiling"] = EditorApplication.isCompiling,
                ["port"] = m_port,
                ["token"] = m_token,
                ["assemblySha256"] = HashFile(assemblyPath),
                ["artifactRoot"] = m_artifactRoot,
                ["lastSeenAt"] = DateTime.UtcNow.ToString("O")
            };
            AtomicWrite(m_rendezvousPath, payload.ToString(Formatting.None));
        }

        /// <summary>释放监听器、运行模块与登记文件。</summary>
        public void Dispose()
        {
            if (m_disposed) return;
            m_disposed = true;
            UnregisterEvents();
            try { UnityEditorCheatBridge.Stop(); } catch { }
            try { m_listener?.Stop(); } catch { }
            try { m_listener?.Close(); } catch { }
            if (m_listenerThread != null && m_listenerThread.IsAlive) m_listenerThread.Join(1500);
            try { if (!string.IsNullOrEmpty(m_rendezvousPath)) File.Delete(m_rendezvousPath); } catch { }
        }

        /// <summary>在可用端口启动 HttpListener；端口发生竞争时重试。</summary>
        private void StartListener()
        {
            Exception lastError = null;
            for (int attempt = 0; attempt < 8; attempt++)
            {
                int candidatePort = ReservePort();
                HttpListener candidate = new HttpListener();
                candidate.Prefixes.Add("http://127.0.0.1:" + candidatePort + "/");
                try
                {
                    candidate.Start();
                    m_port = candidatePort;
                    m_listener = candidate;
                    return;
                }
                catch (Exception exception)
                {
                    lastError = exception;
                    candidate.Close();
                }
            }
            throw new InvalidOperationException("无法为 Unity Editor Bridge 分配回环端口。", lastError);
        }

        /// <summary>集中注册低频心跳。</summary>
        private void RegisterEvents() => EditorApplication.update += Heartbeat;

        /// <summary>集中解除低频心跳。</summary>
        private void UnregisterEvents() => EditorApplication.update -= Heartbeat;

        /// <summary>每 2 秒刷新实例状态。</summary>
        private void Heartbeat()
        {
            if (EditorApplication.timeSinceStartup < m_nextHeartbeat) return;
            m_nextHeartbeat = EditorApplication.timeSinceStartup + 2d;
            RefreshRegistration();
        }

        /// <summary>在后台线程接受本机请求。</summary>
        private void ListenLoop()
        {
            while (!m_disposed && m_listener != null && m_listener.IsListening)
            {
                try
                {
                    HttpListenerContext context = m_listener.GetContext();
                    ThreadPool.QueueUserWorkItem(_ => Handle(context));
                }
                catch (HttpListenerException)
                {
                    if (!m_disposed) Thread.Sleep(100);
                }
                catch (ObjectDisposedException)
                {
                    return;
                }
            }
        }

        /// <summary>认证并路由 Bridge 请求。</summary>
        private void Handle(HttpListenerContext context)
        {
            try
            {
                if (!IsAuthorized(context.Request))
                {
                    WriteJson(context.Response, 401, new JObject { ["success"] = false, ["error"] = "unauthorized" });
                    return;
                }
                string path = context.Request.Url?.AbsolutePath ?? string.Empty;
                if (context.Request.HttpMethod == "GET" && path == "/api/status")
                {
                    WriteJson(context.Response, 200, QaMainThreadDispatcher.Invoke(BuildStatus));
                    return;
                }
                if (context.Request.HttpMethod == "GET" && path == "/api/catalog")
                {
                    WriteJson(context.Response, 200, QaMainThreadDispatcher.Invoke(QaCatalogReader.Read));
                    return;
                }
                if (context.Request.HttpMethod == "POST" && path == "/api/command")
                {
                    JObject request = JObject.Parse(ReadBody(context.Request));
                    string command = request.Value<string>("command") ?? string.Empty;
                    JObject arguments = request["arguments"] as JObject ?? new JObject();
                    WriteJson(
                        context.Response,
                        200,
                        QaMainThreadDispatcher.Invoke(() => JToken.FromObject(UnityEditorCheatBridge.Execute(command, arguments))));
                    return;
                }
                WriteJson(context.Response, 404, new JObject { ["success"] = false, ["error"] = "route_not_found" });
            }
            catch (Exception exception)
            {
                WriteJson(context.Response, 500, new JObject { ["success"] = false, ["error"] = exception.Message });
            }
        }

        /// <summary>构造主线程状态响应。</summary>
        private JToken BuildStatus() => new JObject
        {
            ["success"] = true,
            ["schemaVersion"] = SchemaVersion,
            ["protocolVersion"] = ProtocolVersion,
            ["instanceId"] = m_instanceId,
            ["processId"] = Process.GetCurrentProcess().Id,
            ["projectPath"] = m_projectRoot,
            ["unityVersion"] = Application.unityVersion,
            ["gameVersion"] = Application.version,
            ["sceneName"] = SceneManager.GetActiveScene().name,
            ["mode"] = CurrentMode,
            ["runtimeReady"] = UnityEditorCheatBridge.IsRunning,
            ["compiling"] = EditorApplication.isCompiling,
            ["assemblySha256"] = HashFile(Path.Combine(m_projectRoot, "Library", "ScriptAssemblies", "Assembly-CSharp.dll")),
            ["message"] = m_runtimeMessage
        };

        /// <summary>以固定时间比较 Bearer Token。</summary>
        private bool IsAuthorized(HttpListenerRequest request)
        {
            string value = request.Headers["Authorization"] ?? string.Empty;
            string expected = "Bearer " + m_token;
            if (value.Length != expected.Length) return false;
            int difference = 0;
            for (int index = 0; index < value.Length; index++) difference |= value[index] ^ expected[index];
            return difference == 0;
        }

        /// <summary>读取有大小上限的 UTF-8 请求体。</summary>
        private static string ReadBody(HttpListenerRequest request)
        {
            if (request.ContentLength64 < 0 || request.ContentLength64 > 64 * 1024)
                throw new InvalidDataException("Editor Bridge 请求体大小无效。");
            using (StreamReader reader = new StreamReader(request.InputStream, request.ContentEncoding ?? Encoding.UTF8))
                return reader.ReadToEnd();
        }

        /// <summary>写入 JSON 响应并关闭输出流。</summary>
        private static void WriteJson(HttpListenerResponse response, int statusCode, JToken payload)
        {
            byte[] bytes = Encoding.UTF8.GetBytes(payload.ToString(Formatting.None));
            response.StatusCode = statusCode;
            response.ContentType = "application/json; charset=utf-8";
            response.ContentLength64 = bytes.Length;
            response.KeepAlive = false;
            response.OutputStream.Write(bytes, 0, bytes.Length);
            response.OutputStream.Close();
        }

        /// <summary>取得一个临时可用回环端口。</summary>
        private static int ReservePort()
        {
            TcpListener listener = new TcpListener(IPAddress.Loopback, 0);
            listener.Start();
            int port = ((IPEndPoint)listener.LocalEndpoint).Port;
            listener.Stop();
            return port;
        }

        /// <summary>生成指定字节数的密码学随机十六进制文本。</summary>
        private static string CreateRandomHex(int bytes)
        {
            byte[] value = new byte[bytes];
            using (RandomNumberGenerator random = RandomNumberGenerator.Create()) random.GetBytes(value);
            StringBuilder result = new StringBuilder(value.Length * 2);
            for (int index = 0; index < value.Length; index++) result.Append(value[index].ToString("x2"));
            return result.ToString();
        }

        /// <summary>计算文件 SHA-256；文件不可用时返回 64 个零。</summary>
        private static string HashFile(string path)
        {
            try
            {
                using (FileStream stream = File.Open(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
                using (SHA256 sha = SHA256.Create())
                    return BytesToHex(sha.ComputeHash(stream));
            }
            catch
            {
                return new string('0', 64);
            }
        }

        /// <summary>计算稳定文本 SHA-256。</summary>
        private static string HashText(string value)
        {
            using (SHA256 sha = SHA256.Create()) return BytesToHex(sha.ComputeHash(Encoding.UTF8.GetBytes(value)));
        }

        /// <summary>把字节转换为小写十六进制。</summary>
        private static string BytesToHex(byte[] bytes)
        {
            StringBuilder result = new StringBuilder(bytes.Length * 2);
            for (int index = 0; index < bytes.Length; index++) result.Append(bytes[index].ToString("x2"));
            return result.ToString();
        }

        /// <summary>原子替换实例登记文件。</summary>
        private static void AtomicWrite(string path, string content)
        {
            string temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
            File.WriteAllText(temporary, content, new UTF8Encoding(false));
            try
            {
                if (File.Exists(path)) File.Replace(temporary, path, null);
                else File.Move(temporary, path);
            }
            finally
            {
                try { File.Delete(temporary); } catch { }
            }
        }

        private static string CurrentMode => EditorApplication.isPlaying ? "editor-play" : "editor-edit";
    }
}
