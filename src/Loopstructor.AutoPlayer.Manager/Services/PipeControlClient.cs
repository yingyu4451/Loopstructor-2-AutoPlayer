using System.IO.Pipes;
using System.Text;
using Loopstructor.AutoPlayer.Core;
using Loopstructor.AutoPlayer.Manager.Models;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Newtonsoft.Json.Serialization;

namespace Loopstructor.AutoPlayer.Manager.Services;

public sealed class PipeControlClient
{
    public const string LegacyPipeName = "Loopstructor.Skyspine.AutoPlayer.v1";

    private static readonly JsonSerializerSettings JsonSettings = new()
    {
        ContractResolver = new CamelCasePropertyNamesContractResolver()
    };

    private readonly SemaphoreSlim _commandGate = new(1, 1);
    private readonly TimeSpan _timeout;
    private readonly TimeSpan _connectTimeout;
    private readonly TimeSpan _pendingTimeout;

    public PipeControlClient(
        TimeSpan? timeout = null,
        TimeSpan? connectTimeout = null,
        TimeSpan? pendingTimeout = null)
    {
        _timeout = timeout ?? TimeSpan.FromSeconds(12);
        _connectTimeout = connectTimeout ?? TimeSpan.FromSeconds(2);
        _pendingTimeout = pendingTimeout ?? TimeSpan.FromSeconds(30);
        if (_timeout <= TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(timeout));
        if (_connectTimeout <= TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(connectTimeout));
        if (_pendingTimeout <= TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(pendingTimeout));
    }

    public Task<PipeCallResult> HelloAsync(ActivationSession session, CancellationToken cancellationToken = default) =>
        SendAsync(session, "hello", null, cancellationToken);

    public Task<PipeCallResult> StatusAsync(ActivationSession session, CancellationToken cancellationToken = default) =>
        SendAsync(session, "status", null, cancellationToken);

    public Task<PipeCallResult> StartAsync(
        ActivationSession session,
        AutomationRunOptions options,
        CancellationToken cancellationToken = default) =>
        SendAsync(session, "start", options, cancellationToken);

    public Task<PipeCallResult> PauseAsync(ActivationSession session, CancellationToken cancellationToken = default) =>
        SendAsync(session, "pause", null, cancellationToken);

    public Task<PipeCallResult> ResumeAsync(ActivationSession session, CancellationToken cancellationToken = default) =>
        SendAsync(session, "resume", null, cancellationToken);

    public Task<PipeCallResult> StopAsync(ActivationSession session, CancellationToken cancellationToken = default) =>
        SendAsync(session, "stop", null, cancellationToken);

    public Task<PipeCallResult> QueryAutomationSetupAsync(
        ActivationSession session,
        CancellationToken cancellationToken = default) =>
        SendAsync(session, "queryAutomationSetup", null, cancellationToken);

    public async Task<PipeCallResult> SendCheatAsync(
        ActivationSession session,
        string command,
        JObject? arguments,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(session);
        ControlRequest request = CreateRequest(session, command, null, arguments);
        if (!TryResolveEndpoint(session, out string endpoint, out string endpointError))
        {
            return Failure(
                session.Ticket.PipeName,
                legacy: false,
                message: endpointError,
                requestMayHaveExecuted: false);
        }

        return await SendUntilCompleteAsync(
            endpoint,
            request,
            usedLegacyEndpoint: false,
            cancellationToken);
    }

    public async Task<PipeCallResult> ProbeLegacyStatusAsync(CancellationToken cancellationToken = default)
    {
        ControlRequest request = new()
        {
            Id = Guid.NewGuid().ToString("N"),
            Command = "status"
        };
        return await SendCoreAsync(LegacyPipeName, request, usedLegacyEndpoint: true, cancellationToken);
    }

    public async Task<PipeCallResult> ProbeUnscopedHelloAsync(
        ActivationSession session,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(session);
        ControlRequest request = CreateRequest(session, "hello", null, null);
        return await SendCoreAsync(
            session.Ticket.PipeName,
            request,
            usedLegacyEndpoint: true,
            cancellationToken);
    }

    public async Task<PipeCallResult> SendAsync(
        ActivationSession session,
        string command,
        AutomationRunOptions? options,
        CancellationToken cancellationToken = default)
    {
        return await SendAsync(session, command, options, null, cancellationToken);
    }

    private async Task<PipeCallResult> SendAsync(
        ActivationSession session,
        string command,
        AutomationRunOptions? options,
        JObject? arguments,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(session);
        ControlRequest request = CreateRequest(session, command, options, arguments);
        if (!TryResolveEndpoint(session, out string endpoint, out string endpointError))
        {
            return Failure(
                session.Ticket.PipeName,
                legacy: false,
                message: endpointError,
                requestMayHaveExecuted: false);
        }

        return await SendUntilCompleteAsync(
            endpoint,
            request,
            usedLegacyEndpoint: false,
            cancellationToken);
    }

    private static ControlRequest CreateRequest(
        ActivationSession session,
        string command,
        AutomationRunOptions? options,
        JObject? arguments) => new()
    {
        Id = Guid.NewGuid().ToString("N"),
        Token = session.Ticket.Token,
        TargetGameProcessId = session.ProcessId ?? 0,
        TargetProcessInstanceId = session.ProcessInstanceId,
        Command = command,
        Options = options,
        Arguments = arguments
    };

    internal static bool TryResolveEndpoint(
        ActivationSession session,
        out string endpoint,
        out string error)
    {
        ArgumentNullException.ThrowIfNull(session);
        try
        {
            endpoint = Protocol.GetControlPipeName(
                session.Ticket.PipeName,
                session.ActivationMode,
                session.ProcessId ?? 0);
            error = string.Empty;
            return true;
        }
        catch (ArgumentException exception)
        {
            endpoint = session.Ticket.PipeName;
            error = "尚未绑定有效的 Skyspine 游戏进程，控制命令未发送。详细信息：" + exception.Message;
            return false;
        }
    }

    private async Task<PipeCallResult> SendUntilCompleteAsync(
        string pipeName,
        ControlRequest request,
        bool usedLegacyEndpoint,
        CancellationToken cancellationToken)
    {
        if (IsHealthCommand(request.Command))
        {
            return await SendCoreAsync(
                pipeName,
                request,
                usedLegacyEndpoint,
                cancellationToken);
        }

        bool serialize = !IsHealthCommand(request.Command);
        bool gateEntered = false;
        if (serialize)
        {
            using CancellationTokenSource gateTimeout =
                CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            gateTimeout.CancelAfter(_timeout);
            try
            {
                await _commandGate.WaitAsync(gateTimeout.Token);
                gateEntered = true;
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                return Failure(
                    pipeName,
                    usedLegacyEndpoint,
                    "等待前一条 AutoPlayer 控制命令完成超时。",
                    requestMayHaveExecuted: false);
            }
        }

        try
        {
            using CancellationTokenSource pendingDeadline =
                CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            pendingDeadline.CancelAfter(_pendingTimeout);
            bool requestMayHaveExecuted = false;
            try
            {
                while (true)
                {
                    PipeCallResult result = await SendCoreAsync(
                        pipeName,
                        request,
                        usedLegacyEndpoint,
                        pendingDeadline.Token);
                    requestMayHaveExecuted |= result.RequestMayHaveExecuted;
                    if (result.TransportSuccess && !IsPendingResponse(result.Response))
                    {
                        return result;
                    }

                    if (!result.TransportSuccess && !requestMayHaveExecuted)
                    {
                        return result;
                    }

                    await Task.Delay(TimeSpan.FromMilliseconds(150), pendingDeadline.Token);
                }
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                return Failure(
                    pipeName,
                    usedLegacyEndpoint,
                    "等待插件返回同一请求 ID 的最终结果超时；命令可能仍在游戏中执行。",
                    requestMayHaveExecuted);
            }
        }
        finally
        {
            if (gateEntered) _commandGate.Release();
        }
    }

    private async Task<PipeCallResult> SendCoreAsync(
        string pipeName,
        ControlRequest request,
        bool usedLegacyEndpoint,
        CancellationToken cancellationToken)
    {
        bool requestWritten = false;
        using NamedPipeClientStream pipe = new(
            ".",
            pipeName,
            PipeDirection.InOut,
            PipeOptions.Asynchronous);
        try
        {
            using (CancellationTokenSource connectTimeout =
                   CancellationTokenSource.CreateLinkedTokenSource(cancellationToken))
            {
                connectTimeout.CancelAfter(_connectTimeout);
                await pipe.ConnectAsync(connectTimeout.Token);
            }

            using CancellationTokenSource responseTimeout =
                CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            responseTimeout.CancelAfter(_timeout);
            await using StreamWriter writer = new(pipe, new UTF8Encoding(false), leaveOpen: true)
            {
                AutoFlush = true
            };
            using StreamReader reader = new(pipe, Encoding.UTF8, detectEncodingFromByteOrderMarks: false, leaveOpen: true);
            string payload = JsonConvert.SerializeObject(request, JsonSettings);
            requestWritten = true;
            await writer.WriteLineAsync(payload.AsMemory(), responseTimeout.Token);
            string? responseLine = await reader.ReadLineAsync(responseTimeout.Token);
            if (string.IsNullOrWhiteSpace(responseLine))
            {
                return Failure(pipeName, usedLegacyEndpoint, "插件已关闭管道，但未返回响应。", requestWritten);
            }

            ControlResponse? response = JsonConvert.DeserializeObject<ControlResponse>(responseLine);
            if (response == null)
            {
                return Failure(pipeName, usedLegacyEndpoint, "插件响应为空或格式无效。", requestWritten);
            }

            if (!string.Equals(response.Id, request.Id, StringComparison.Ordinal))
            {
                return Failure(pipeName, usedLegacyEndpoint, "插件响应标识与请求不匹配。", requestWritten);
            }

            return new PipeCallResult
            {
                TransportSuccess = true,
                RequestMayHaveExecuted = requestWritten,
                UsedLegacyEndpoint = usedLegacyEndpoint,
                Endpoint = pipeName,
                Response = response
            };
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return Failure(
                pipeName,
                usedLegacyEndpoint,
                requestWritten
                    ? "等待 AutoPlayer 插件响应超时。"
                    : "未找到 AutoPlayer 插件控制管道（连接超时）。",
                requestWritten);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException)
        {
            return Failure(
                pipeName,
                usedLegacyEndpoint,
                "插件管道通信失败。详细信息：" + exception.Message,
                requestWritten);
        }
    }

    private static bool IsPendingResponse(ControlResponse? response) =>
        response?.Data?["pending"]?.Value<bool>() == true;

    private static bool IsHealthCommand(string? command) =>
        string.Equals(command, "hello", StringComparison.OrdinalIgnoreCase)
        || string.Equals(command, "status", StringComparison.OrdinalIgnoreCase)
        || string.Equals(command, "ping", StringComparison.OrdinalIgnoreCase);

    private static PipeCallResult Failure(
        string endpoint,
        bool legacy,
        string message,
        bool requestMayHaveExecuted = false) => new()
    {
        Endpoint = endpoint,
        UsedLegacyEndpoint = legacy,
        RequestMayHaveExecuted = requestMayHaveExecuted,
        Error = message
    };
}
