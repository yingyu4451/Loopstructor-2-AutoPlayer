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

    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly TimeSpan _timeout;

    public PipeControlClient(TimeSpan? timeout = null)
    {
        _timeout = timeout ?? TimeSpan.FromSeconds(12);
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

    public async Task<PipeCallResult> SendCheatAsync(
        ActivationSession session,
        string command,
        JObject? arguments,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(session);
        ControlRequest request = CreateRequest(session, command, null, arguments);
        PipeCallResult first = await SendCoreAsync(
            session.Ticket.PipeName,
            request,
            usedLegacyEndpoint: false,
            cancellationToken);
        if (first.TransportSuccess
            || !first.RequestMayHaveExecuted
            || !CheatCommands.IsMutationCommand(command))
        {
            return first;
        }

        // A response can be lost after the Unity main thread has already
        // performed the write. Retry once with the same request id; the plugin
        // caches completed ids and will return the result without re-executing.
        return await SendCoreAsync(
            session.Ticket.PipeName,
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
        return await SendCoreAsync(session.Ticket.PipeName, request, usedLegacyEndpoint: false, cancellationToken);
    }

    private static ControlRequest CreateRequest(
        ActivationSession session,
        string command,
        AutomationRunOptions? options,
        JObject? arguments) => new()
    {
        Id = Guid.NewGuid().ToString("N"),
        Token = session.Ticket.Token,
        Command = command,
        Options = options,
        Arguments = arguments
    };

    private async Task<PipeCallResult> SendCoreAsync(
        string pipeName,
        ControlRequest request,
        bool usedLegacyEndpoint,
        CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            bool requestWritten = false;
            using CancellationTokenSource timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(_timeout);
            using NamedPipeClientStream pipe = new(
                ".",
                pipeName,
                PipeDirection.InOut,
                PipeOptions.Asynchronous);
            try
            {
                await pipe.ConnectAsync(timeout.Token);
                await using StreamWriter writer = new(pipe, new UTF8Encoding(false), leaveOpen: true)
                {
                    AutoFlush = true
                };
                using StreamReader reader = new(pipe, Encoding.UTF8, detectEncodingFromByteOrderMarks: false, leaveOpen: true);
                string payload = JsonConvert.SerializeObject(request, JsonSettings);
                await writer.WriteLineAsync(payload.AsMemory(), timeout.Token);
                requestWritten = true;
                string? responseLine = await reader.ReadLineAsync(timeout.Token);
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
                return Failure(pipeName, usedLegacyEndpoint, "等待 AutoPlayer 插件响应超时。", requestWritten);
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
        finally
        {
            _gate.Release();
        }
    }

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
