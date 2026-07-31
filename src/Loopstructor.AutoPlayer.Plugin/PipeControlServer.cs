using System;
using System.Collections.Concurrent;
using System.IO;
using System.IO.Pipes;
using System.Text;
using System.Threading;
using Loopstructor.AutoPlayer.Core;
using Newtonsoft.Json;

namespace Loopstructor.AutoPlayer.Plugin;

internal sealed class PipeControlServer : IDisposable
{
    private const int MaxRequestCharacters = 65536;

    private readonly AutoPlayController _controller;
    private readonly ActivationContext _activation;
    private readonly ConcurrentQueue<QueuedControl> _requests = new();
    private readonly Thread _thread;
    private volatile bool _stopping;
    private NamedPipeServerStream? _activeServer;

    public PipeControlServer(AutoPlayController controller, ActivationContext activation)
    {
        _controller = controller;
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

        QueuedControl request = new(command, input.Options);
        _requests.Enqueue(request);
        bool completed = request.Done.Wait(TimeSpan.FromSeconds(8));
        if (!completed && Interlocked.CompareExchange(ref request.State, 2, 0) == 1)
        {
            completed = request.Done.Wait(TimeSpan.FromSeconds(2));
        }

        return new ControlResponse
        {
            Id = id,
            Success = completed && request.Success,
            Message = completed ? request.Message : "游戏主线程未能及时处理控制命令。",
            Status = request.Status
        };
    }

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
        public QueuedControl(string command, AutomationRunOptions? options)
        {
            Command = command;
            Options = options;
        }

        public string Command { get; }
        public AutomationRunOptions? Options { get; }
        public ManualResetEventSlim Done { get; } = new(false);
        public int State;
        public bool Success { get; set; }
        public string Message { get; set; } = string.Empty;
        public AutoPlayerStatus? Status { get; set; }
    }
}
