using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Pipes;
using System.Text;
using System.Threading;
using Loopstructor.AutoPlayer.Core;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace Loopstructor.AutoPlayer.Plugin;

internal sealed class PipeControlServer : IDisposable
{
    private const int MaxRequestCharacters = 65536;
    private const int MaxCachedResponses = 256;
    private const int ListenerWorkerCount = 4;
    private static readonly TimeSpan QueueWaitTimeout = TimeSpan.FromSeconds(8);
    private static readonly TimeSpan ConnectionReadTimeout = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan ConnectionWriteTimeout = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan ListenerStartTimeout = TimeSpan.FromSeconds(5);

    private readonly AutoPlayController _controller;
    private readonly CheatController _cheatController;
    private readonly ActivationContext _activation;
    private readonly int _gameProcessId;
    private readonly string _pipeName;
    private readonly ConcurrentQueue<QueuedControl> _requests = new();
    private readonly object _requestSync = new();
    private readonly Dictionary<string, QueuedControl> _pendingRequests = new(StringComparer.Ordinal);
    private readonly Dictionary<string, CachedControlResponse> _responseCache = new(StringComparer.Ordinal);
    private readonly Queue<string> _responseCacheOrder = new();
    private readonly object _serverSync = new();
    private readonly HashSet<NamedPipeServerStream> _activeServers = new();
    private readonly ManualResetEvent _shutdown = new(false);
    private readonly ManualResetEventSlim _listenerReady = new(false);
    private readonly Thread[] _workers;
    private Exception? _lastListenerError;
    private volatile bool _stopping;
    private int _startState;
    private int _disposeState;

    public PipeControlServer(
        AutoPlayController controller,
        CheatController cheatController,
        ActivationContext activation)
    {
        _controller = controller;
        _cheatController = cheatController;
        _activation = activation;
        using (Process process = Process.GetCurrentProcess())
        {
            _gameProcessId = process.Id;
        }
        _pipeName = Protocol.GetControlPipeName(
            activation.PipeName,
            activation.ActivationMode,
            _gameProcessId);
        _workers = new Thread[ListenerWorkerCount];
        for (int index = 0; index < _workers.Length; index++)
        {
            _workers[index] = new Thread(RunWorker)
            {
                IsBackground = true,
                Name = "LoopstructorAutoPlayerControl-" + (index + 1)
            };
        }
    }

    public void Start()
    {
        if (Interlocked.Exchange(ref _startState, 1) != 0)
        {
            throw new InvalidOperationException("本机控制通道已经启动。");
        }

        foreach (Thread worker in _workers) worker.Start();
        if (_listenerReady.Wait(ListenerStartTimeout)) return;

        string detail = Volatile.Read(ref _lastListenerError)?.Message ?? "监听器未在限定时间内就绪。";
        Dispose();
        throw new IOException("无法创建 AutoPlayer 本机控制管道：" + detail);
    }

    public void Pump()
    {
        while (_requests.TryDequeue(out QueuedControl? request))
        {
            if (Interlocked.CompareExchange(ref request.State, 1, 0) != 0) continue;
            try
            {
                string message;
                if (CheatCommands.IsCheatCommand(request.Command))
                {
                    CheatExecutionResult result = _cheatController.Execute(
                        request.Id,
                        request.Command,
                        request.Arguments);
                    request.Success = result.Success;
                    request.Data = result.Data;
                    message = result.Message;
                }
                else
                {
                    switch (request.Command.ToLowerInvariant())
                    {
                        case "start":
                            request.Success = _controller.Start(request.Options, out message);
                            break;
                        case "pause":
                            request.Success = _controller.Pause(out message);
                            break;
                        case "resume":
                            request.Success = _controller.Resume(out message);
                            break;
                        case "stop":
                            request.Success = _controller.Stop(out message);
                            break;
                        case "queryautomationsetup":
                            request.Success = AutomationSetupRuntimeReader.TryQuery(out JObject setup, out message);
                            request.Data = setup;
                            break;
                        default:
                            request.Success = false;
                            message = "未知的控制命令：" + request.Command;
                            break;
                    }
                }

                request.Message = message;
                request.Status = _controller.Snapshot();
            }
            catch (Exception exception)
            {
                request.Success = false;
                request.Message = "处理控制命令时发生异常：" + exception.Message;
            }
            finally
            {
                _cheatController.NotifyManagerCommandCompleted();
                CompleteRequest(request, BuildCompletedResponse(request));
            }
        }
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposeState, 1) != 0) return;
        _stopping = true;
        _shutdown.Set();

        QueuedControl[] pending;
        lock (_requestSync)
        {
            pending = new QueuedControl[_pendingRequests.Count];
            _pendingRequests.Values.CopyTo(pending, 0);
        }

        foreach (QueuedControl request in pending)
        {
            if (Interlocked.CompareExchange(ref request.State, 2, 0) == 0)
            {
                CompleteRequest(request, BuildCanceledBeforeExecutionResponse(request.Id));
            }
        }

        NamedPipeServerStream[] servers;
        lock (_serverSync)
        {
            servers = new NamedPipeServerStream[_activeServers.Count];
            _activeServers.CopyTo(servers);
        }

        foreach (NamedPipeServerStream server in servers)
        {
            try { server.Dispose(); } catch { }
        }

        foreach (Thread worker in _workers)
        {
            if (worker.IsAlive) worker.Join(1500);
        }
    }

    private void RunWorker()
    {
        while (!_stopping)
        {
            NamedPipeServerStream? server = null;
            try
            {
                server = new NamedPipeServerStream(
                    _pipeName,
                    PipeDirection.InOut,
                    ListenerWorkerCount,
                    PipeTransmissionMode.Byte,
                    PipeOptions.Asynchronous);
                if (!RegisterActiveServer(server)) return;
                server.WaitForConnection();
                if (_stopping) return;
                ProcessConnection(server);
            }
            catch (ObjectDisposedException) when (_stopping)
            {
                return;
            }
            catch (IOException exception) when (!_stopping)
            {
                Volatile.Write(ref _lastListenerError, exception);
                Thread.Sleep(250);
            }
            catch (Exception exception)
            {
                Volatile.Write(ref _lastListenerError, exception);
                if (!_stopping) Thread.Sleep(1000);
            }
            finally
            {
                if (server != null)
                {
                    lock (_serverSync) _activeServers.Remove(server);
                    try { server.Dispose(); } catch { }
                }
            }
        }
    }

    private bool RegisterActiveServer(NamedPipeServerStream server)
    {
        lock (_serverSync)
        {
            if (_stopping) return false;
            _activeServers.Add(server);
            _listenerReady.Set();
            return true;
        }
    }

    private void ProcessConnection(Stream server)
    {
        using StreamReader reader = new(server, Encoding.UTF8, false, 4096, true);
        using StreamWriter writer = new(server, new UTF8Encoding(false), 4096, true) { AutoFlush = true };
        Task<string?> readTask = reader.ReadLineAsync();
        if (!readTask.Wait(ConnectionReadTimeout))
        {
            // A connected client that never sends a line must not own a listener forever.
            try { server.Dispose(); } catch { }
            try { readTask.Wait(TimeSpan.FromMilliseconds(250)); } catch { }
            return;
        }

        string? line = readTask.GetAwaiter().GetResult();
        if (_stopping || line == null) return;
        ControlResponse response = line.Length > MaxRequestCharacters
            ? Error(string.Empty, "控制请求过大。")
            : ProcessLine(line);
        Task writeTask = WriteResponseAsync(writer, JsonConvert.SerializeObject(response));
        if (!writeTask.Wait(ConnectionWriteTimeout))
        {
            // A client that stops reading must not own a listener forever.
            try { server.Dispose(); } catch { }
            try { writeTask.Wait(TimeSpan.FromMilliseconds(250)); } catch { }
        }
    }

    private ControlResponse ProcessLine(string line)
    {
        ControlRequest? input;
        try
        {
            input = JsonConvert.DeserializeObject<ControlRequest>(line);
        }
        catch (Exception exception)
        {
            return Error(string.Empty, "控制请求 JSON 无效：" + exception.Message);
        }

        if (input == null) return Error(string.Empty, "控制请求为空。");
        string id;
        if (string.IsNullOrWhiteSpace(input.Id))
        {
            id = Guid.NewGuid().ToString("N");
        }
        else if (!Protocol.IsValidRequestId(input.Id))
        {
            return Error(string.Empty, "控制请求标识无效。");
        }
        else
        {
            id = input.Id;
        }
        if (!TokensEqual(input.Token, _activation.Token)) return Error(id, "控制令牌无效。");
        string command = string.IsNullOrWhiteSpace(input.Command) ? "status" : input.Command.Trim();
        bool targetPidMissing = input.TargetGameProcessId <= 0;
        bool targetPidMismatch = !targetPidMissing && input.TargetGameProcessId != _gameProcessId;
        if ((_activation.IsPlayerMode && targetPidMissing) || targetPidMismatch)
        {
            return Error(id, $"控制请求目标 PID {input.TargetGameProcessId} 与当前游戏 PID {_gameProcessId} 不一致。");
        }
        if (!string.Equals(command, "hello", StringComparison.OrdinalIgnoreCase)
            && !string.Equals(
                input.TargetProcessInstanceId,
                _activation.ProcessInstanceId,
                StringComparison.Ordinal))
        {
            return Error(id, "控制请求的游戏进程实例标识无效，请重新握手。");
        }
        _cheatController.NotifyManagerHeartbeat();

        if (string.Equals(command, "hello", StringComparison.OrdinalIgnoreCase))
        {
            return new ControlResponse
            {
                Id = id,
                Success = true,
                Message = "hello",
                Hello = _controller.Hello(),
                Status = _controller.Snapshot()
            };
        }

        if (string.Equals(command, "status", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(command, "ping", StringComparison.OrdinalIgnoreCase))
        {
            return new ControlResponse
            {
                Id = id,
                Success = true,
                Message = string.Equals(command, "ping", StringComparison.OrdinalIgnoreCase) ? "pong" : "ok",
                Status = _controller.Snapshot()
            };
        }

        string fingerprint = BuildRequestFingerprint(command, input.Options, input.Arguments);
        QueuedControl request;
        bool enqueue = false;
        lock (_requestSync)
        {
            if (_responseCache.TryGetValue(id, out CachedControlResponse? cached))
            {
                return string.Equals(cached.Fingerprint, fingerprint, StringComparison.Ordinal)
                    ? cached.Response
                    : Error(id, "请求标识已用于另一条控制命令，拒绝重复执行。");
            }

            if (_pendingRequests.TryGetValue(id, out QueuedControl? pending))
            {
                if (!string.Equals(pending.Fingerprint, fingerprint, StringComparison.Ordinal))
                {
                    return Error(id, "请求标识正在执行另一条控制命令，拒绝重复执行。");
                }

                if (Volatile.Read(ref pending.State) == 1)
                {
                    return BuildInProgressResponse(id);
                }

                request = pending;
            }
            else
            {
                request = new QueuedControl(id, fingerprint, command, input.Options, input.Arguments);
                _pendingRequests.Add(id, request);
                enqueue = true;
            }
        }

        if (enqueue) _requests.Enqueue(request);
        return WaitForResponse(request);
    }

    private ControlResponse WaitForResponse(QueuedControl request)
    {
        int waitResult = WaitHandle.WaitAny(
            new WaitHandle[] { request.Done.WaitHandle, _shutdown },
            QueueWaitTimeout);
        if (waitResult == 0) return CompletedResponse(request);
        if (waitResult == 1) return BuildShutdownResponse(request.Id);

        int previousState = Interlocked.CompareExchange(ref request.State, 2, 0);
        if (previousState == 0)
        {
            ControlResponse canceled = BuildCanceledBeforeExecutionResponse(request.Id);
            CompleteRequest(request, canceled);
            return canceled;
        }

        if (previousState == 1)
        {
            return BuildInProgressResponse(request.Id);
        }

        return request.Done.Wait(TimeSpan.FromMilliseconds(100))
            ? CompletedResponse(request)
            : BuildCanceledBeforeExecutionResponse(request.Id);
    }

    private void CompleteRequest(QueuedControl request, ControlResponse response)
    {
        if (Interlocked.CompareExchange(ref request.CompletionState, 1, 0) != 0) return;
        request.Response = response;
        lock (_requestSync)
        {
            if (_pendingRequests.TryGetValue(request.Id, out QueuedControl? pending)
                && ReferenceEquals(pending, request))
            {
                _pendingRequests.Remove(request.Id);
            }

            CacheResponseUnderLock(request.Id, request.Fingerprint, response);
        }

        request.Done.Set();
    }

    private void CacheResponseUnderLock(string id, string fingerprint, ControlResponse response)
    {
        _responseCache[id] = new CachedControlResponse(fingerprint, response);
        _responseCacheOrder.Enqueue(id);
        while (_responseCacheOrder.Count > MaxCachedResponses)
        {
            string expiredId = _responseCacheOrder.Dequeue();
            _responseCache.Remove(expiredId);
        }
    }

    private static ControlResponse CompletedResponse(QueuedControl request) =>
        request.Response ?? Error(request.Id, "控制命令已结束，但没有生成响应。");

    private static ControlResponse BuildCompletedResponse(QueuedControl request) => new()
    {
        Id = request.Id,
        Success = request.Success,
        Message = request.Message,
        Status = request.Status,
        Data = request.Data
    };

    private static ControlResponse BuildCanceledBeforeExecutionResponse(string id) => new()
    {
        Id = id,
        Success = false,
        Message = "游戏主线程未能及时处理控制命令；该请求已在执行前取消。"
    };

    private static ControlResponse BuildShutdownResponse(string id) => new()
    {
        Id = id,
        Success = false,
        Message = "游戏正在退出，已开始的控制命令未能返回确定结果。"
    };

    private static ControlResponse BuildInProgressResponse(string id) => new()
    {
        Id = id,
        Success = false,
        Message = "控制命令仍在游戏主线程执行；监听通道已释放，该请求不会重复执行。",
        Data = new JObject
        {
            ["pending"] = true,
            ["requestId"] = id
        }
    };

    private static string BuildRequestFingerprint(
        string command,
        AutomationRunOptions? options,
        JObject? arguments) =>
        command.ToLowerInvariant()
        + "\n" + JsonConvert.SerializeObject(options)
        + "\n" + JsonConvert.SerializeObject(arguments);

    private static ControlResponse Error(string id, string message) => new()
    {
        Id = id,
        Success = false,
        Message = message
    };

    private static async Task WriteResponseAsync(StreamWriter writer, string payload)
    {
        await writer.WriteLineAsync(payload);
        await writer.FlushAsync();
    }

    private static bool TokensEqual(string? first, string second)
    {
        if (first == null || first.Length != second.Length) return false;
        int difference = 0;
        for (int index = 0; index < first.Length; index++) difference |= first[index] ^ second[index];
        return difference == 0;
    }

    private sealed class QueuedControl
    {
        public QueuedControl(
            string id,
            string fingerprint,
            string command,
            AutomationRunOptions? options,
            Newtonsoft.Json.Linq.JObject? arguments)
        {
            Id = id;
            Fingerprint = fingerprint;
            Command = command;
            Options = options;
            Arguments = arguments;
        }

        public string Id { get; }
        public string Fingerprint { get; }
        public string Command { get; }
        public AutomationRunOptions? Options { get; }
        public Newtonsoft.Json.Linq.JObject? Arguments { get; }
        public ManualResetEventSlim Done { get; } = new(false);
        public int State;
        public int CompletionState;
        public bool Success { get; set; }
        public string Message { get; set; } = string.Empty;
        public AutoPlayerStatus? Status { get; set; }
        public Newtonsoft.Json.Linq.JObject? Data { get; set; }
        public ControlResponse? Response { get; set; }
    }

    private sealed class CachedControlResponse
    {
        public CachedControlResponse(string fingerprint, ControlResponse response)
        {
            Fingerprint = fingerprint;
            Response = response;
        }

        public string Fingerprint { get; }
        public ControlResponse Response { get; }
    }
}
