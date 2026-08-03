using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
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

    private readonly AutoPlayController _controller;
    private readonly CheatController _cheatController;
    private readonly ActivationContext _activation;
    private readonly ConcurrentQueue<QueuedControl> _requests = new();
    private readonly Dictionary<string, CachedControlResponse> _responseCache = new(StringComparer.Ordinal);
    private readonly Queue<string> _responseCacheOrder = new();
    private readonly Thread _thread;
    private volatile bool _stopping;
    private NamedPipeServerStream? _activeServer;

    public PipeControlServer(
        AutoPlayController controller,
        CheatController cheatController,
        ActivationContext activation)
    {
        _controller = controller;
        _cheatController = cheatController;
        _activation = activation;
        _thread = new Thread(Run)
        {
            IsBackground = true,
            Name = "LoopstructorAutoPlayerControl"
        };
    }

    public void Start() => _thread.Start();

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
                request.Done.Set();
            }
        }
    }

    public void Dispose()
    {
        _stopping = true;
        try { _activeServer?.Dispose(); } catch { }
        if (_thread.IsAlive) _thread.Join(1500);
    }

    private void Run()
    {
        while (!_stopping)
        {
            try
            {
                using NamedPipeServerStream server = new(
                    _activation.PipeName,
                    PipeDirection.InOut,
                    1,
                    PipeTransmissionMode.Byte,
                    PipeOptions.None);
                _activeServer = server;
                server.WaitForConnection();
                if (_stopping) return;
                ProcessConnection(server);
            }
            catch (ObjectDisposedException) when (_stopping)
            {
                return;
            }
            catch (IOException) when (!_stopping)
            {
                Thread.Sleep(250);
            }
            catch
            {
                if (!_stopping) Thread.Sleep(1000);
            }
            finally
            {
                _activeServer = null;
            }
        }
    }

    private void ProcessConnection(Stream server)
    {
        using StreamReader reader = new(server, Encoding.UTF8, false, 4096, true);
        using StreamWriter writer = new(server, new UTF8Encoding(false), 4096, true) { AutoFlush = true };
        string? line;
        while (!_stopping && (line = reader.ReadLine()) != null)
        {
            ControlResponse response;
            if (line.Length > MaxRequestCharacters)
            {
                response = Error(string.Empty, "控制请求过大。");
            }
            else
            {
                response = ProcessLine(line);
            }

            writer.WriteLine(JsonConvert.SerializeObject(response));
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
        string id = string.IsNullOrWhiteSpace(input.Id) ? Guid.NewGuid().ToString("N") : input.Id;
        if (!TokensEqual(input.Token, _activation.Token)) return Error(id, "控制令牌无效。");
        _cheatController.NotifyManagerHeartbeat();

        string command = string.IsNullOrWhiteSpace(input.Command) ? "status" : input.Command.Trim();
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
        if (_responseCache.TryGetValue(id, out CachedControlResponse? cached))
        {
            return string.Equals(cached.Fingerprint, fingerprint, StringComparison.Ordinal)
                ? cached.Response
                : Error(id, "请求标识已用于另一条控制命令，拒绝重复执行。");
        }

        QueuedControl request = new(id, command, input.Options, input.Arguments);
        _requests.Enqueue(request);
        bool completed = request.Done.Wait(TimeSpan.FromSeconds(8));
        bool canceledBeforeExecution = false;
        if (!completed)
        {
            int previousState = Interlocked.CompareExchange(ref request.State, 2, 0);
            if (previousState == 0)
            {
                canceledBeforeExecution = true;
            }
            else if (previousState == 1)
            {
                while (!_stopping && !(completed = request.Done.Wait(TimeSpan.FromMilliseconds(250))))
                {
                }
            }
        }

        ControlResponse response = new()
        {
            Id = id,
            Success = completed && request.Success,
            Message = completed
                ? request.Message
                : canceledBeforeExecution
                    ? "游戏主线程未能及时处理控制命令；该请求已在执行前取消。"
                    : "游戏正在退出，控制命令未能返回结果。",
            Status = request.Status,
            Data = request.Data
        };
        CacheResponse(id, fingerprint, response);
        return response;
    }

    private void CacheResponse(string id, string fingerprint, ControlResponse response)
    {
        _responseCache[id] = new CachedControlResponse(fingerprint, response);
        _responseCacheOrder.Enqueue(id);
        while (_responseCacheOrder.Count > MaxCachedResponses)
        {
            string expiredId = _responseCacheOrder.Dequeue();
            _responseCache.Remove(expiredId);
        }
    }

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
            string command,
            AutomationRunOptions? options,
            Newtonsoft.Json.Linq.JObject? arguments)
        {
            Id = id;
            Command = command;
            Options = options;
            Arguments = arguments;
        }

        public string Id { get; }
        public string Command { get; }
        public AutomationRunOptions? Options { get; }
        public Newtonsoft.Json.Linq.JObject? Arguments { get; }
        public ManualResetEventSlim Done { get; } = new(false);
        public int State;
        public bool Success { get; set; }
        public string Message { get; set; } = string.Empty;
        public AutoPlayerStatus? Status { get; set; }
        public Newtonsoft.Json.Linq.JObject? Data { get; set; }
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
