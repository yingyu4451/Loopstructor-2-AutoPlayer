using System.Diagnostics;
using System.Collections.Concurrent;
using System.IO.Pipes;
using System.Text;
using Loopstructor.AutoPlayer.Core;
using Loopstructor.AutoPlayer.Manager.Models;
using Loopstructor.AutoPlayer.Manager.Services;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace Loopstructor.AutoPlayer.Tests;

public sealed class PipeControlClientTests
{
    [Fact]
    public async Task MissingEndpoint_UsesShortConnectDeadlineWithoutClaimingExecution()
    {
        PipeControlClient client = new(
            timeout: TimeSpan.FromSeconds(3),
            connectTimeout: TimeSpan.FromMilliseconds(150));
        ActivationSession session = CreateSession("Loopstructor.AutoPlayer.Missing." + Guid.NewGuid().ToString("N"));
        Stopwatch stopwatch = Stopwatch.StartNew();

        PipeCallResult result = await client.StatusAsync(session);

        stopwatch.Stop();
        Assert.False(result.TransportSuccess);
        Assert.False(result.RequestMayHaveExecuted);
        Assert.Contains("控制管道", result.Error, StringComparison.Ordinal);
        Assert.True(stopwatch.Elapsed < TimeSpan.FromSeconds(2), $"连接失败耗时过长：{stopwatch.Elapsed}。");
    }

    [Fact]
    public async Task HealthCheck_IsNotBlockedByLongControlCommand()
    {
        string pipeName = "Loopstructor.AutoPlayer.Concurrent." + Guid.NewGuid().ToString("N");
        ActivationSession session = CreateSession(pipeName);
        string processPipeName = Protocol.GetControlPipeName(
            pipeName,
            session.ActivationMode,
            session.ProcessId!.Value);
        using NamedPipeServerStream firstServer = CreateServer(processPipeName);
        using NamedPipeServerStream secondServer = CreateServer(processPipeName);
        TaskCompletionSource commandStarted = new(TaskCreationOptions.RunContinuationsAsynchronously);
        Task firstWorker = ServeOnceAsync(firstServer, commandStarted);
        Task secondWorker = ServeOnceAsync(secondServer, commandStarted);
        PipeControlClient client = new(
            timeout: TimeSpan.FromSeconds(3),
            connectTimeout: TimeSpan.FromSeconds(1));

        Task<PipeCallResult> command = client.SendCheatAsync(
            session,
            CheatCommands.QueryCatalog,
            arguments: null);
        await commandStarted.Task.WaitAsync(TimeSpan.FromSeconds(2));
        Stopwatch stopwatch = Stopwatch.StartNew();

        PipeCallResult health = await client.StatusAsync(session);

        stopwatch.Stop();
        Assert.True(health.TransportSuccess, health.Error);
        Assert.True(stopwatch.Elapsed < TimeSpan.FromSeconds(1), $"健康检查被长命令阻塞：{stopwatch.Elapsed}。");
        Assert.True((await command).TransportSuccess);
        await Task.WhenAll(firstWorker, secondWorker);
    }

    [Fact]
    public async Task HealthCheck_ResponseLossReturnsAtSingleAttemptDeadline()
    {
        string basePipeName = "Loopstructor.AutoPlayer.HealthTimeout." + Guid.NewGuid().ToString("N");
        ActivationSession session = CreateSession(basePipeName);
        string pipeName = Protocol.GetControlPipeName(
            basePipeName,
            session.ActivationMode,
            session.ProcessId!.Value);
        using NamedPipeServerStream server = CreateServer(pipeName);
        Task worker = ServeWithoutResponseAsync(server);
        PipeControlClient client = new(
            timeout: TimeSpan.FromMilliseconds(180),
            connectTimeout: TimeSpan.FromMilliseconds(180),
            pendingTimeout: TimeSpan.FromSeconds(3));
        Stopwatch stopwatch = Stopwatch.StartNew();

        PipeCallResult result = await client.StatusAsync(session);

        stopwatch.Stop();
        Assert.False(result.TransportSuccess);
        Assert.True(result.RequestMayHaveExecuted);
        Assert.True(stopwatch.Elapsed < TimeSpan.FromSeconds(1), $"健康检查错误地进入 pending 恢复循环：{stopwatch.Elapsed}。");
        await worker;
    }

    [Fact]
    public async Task ResidentSession_WithoutTargetProcessFailsBeforeConnecting()
    {
        ActivationSession session = CreateSession("Loopstructor.AutoPlayer.Unbound." + Guid.NewGuid().ToString("N"));
        session.ProcessId = null;
        PipeControlClient client = new(
            timeout: TimeSpan.FromSeconds(3),
            connectTimeout: TimeSpan.FromSeconds(1));

        PipeCallResult result = await client.StatusAsync(session);

        Assert.False(result.TransportSuccess);
        Assert.False(result.RequestMayHaveExecuted);
        Assert.Contains("尚未绑定有效", result.Error, StringComparison.Ordinal);
    }

    [Fact]
    public async Task UnscopedProbe_UsesBaseEndpointForReadOnlyLegacyDiagnosis()
    {
        string basePipeName = "Loopstructor.AutoPlayer.Unscoped." + Guid.NewGuid().ToString("N");
        ActivationSession session = CreateSession(basePipeName);
        using NamedPipeServerStream server = CreateServer(basePipeName);
        TaskCompletionSource requestStarted = new(TaskCreationOptions.RunContinuationsAsynchronously);
        Task worker = ServeOnceAsync(server, requestStarted);
        PipeControlClient client = new(
            timeout: TimeSpan.FromSeconds(3),
            connectTimeout: TimeSpan.FromSeconds(1));

        PipeCallResult result = await client.ProbeUnscopedHelloAsync(session);

        Assert.True(result.TransportSuccess, result.Error);
        Assert.True(result.UsedLegacyEndpoint);
        Assert.Equal(basePipeName, result.Endpoint);
        await worker;
    }

    [Fact]
    public async Task PendingCommand_PollsSameRequestIdUntilCachedResultIsAvailable()
    {
        string basePipeName = "Loopstructor.AutoPlayer.Pending." + Guid.NewGuid().ToString("N");
        ActivationSession session = CreateSession(basePipeName);
        string pipeName = Protocol.GetControlPipeName(
            basePipeName,
            session.ActivationMode,
            session.ProcessId!.Value);
        using NamedPipeServerStream firstServer = CreateServer(pipeName);
        using NamedPipeServerStream secondServer = CreateServer(pipeName);
        ConcurrentQueue<string> requestIds = new();
        int[] sequence = { 0 };
        Task firstWorker = ServePendingSequenceAsync(firstServer, requestIds, sequence);
        Task secondWorker = ServePendingSequenceAsync(secondServer, requestIds, sequence);
        PipeControlClient client = new(
            timeout: TimeSpan.FromSeconds(1),
            connectTimeout: TimeSpan.FromSeconds(1),
            pendingTimeout: TimeSpan.FromSeconds(3));

        PipeCallResult result = await client.SendCheatAsync(
            session,
            CheatCommands.SpawnEnemy,
            new JObject { ["enemyId"] = "CommonMonster" });

        Assert.True(result.TransportSuccess, result.Error);
        Assert.True(result.Response!.Success, result.Response.Message);
        Assert.Equal(2, requestIds.Count);
        Assert.Single(requestIds.Distinct(StringComparer.Ordinal));
        await Task.WhenAll(firstWorker, secondWorker);
    }

    [Fact]
    public async Task PendingMutation_ReportsUnknownOutcomeAtOverallDeadline()
    {
        string basePipeName = "Loopstructor.AutoPlayer.PendingTimeout." + Guid.NewGuid().ToString("N");
        ActivationSession session = CreateSession(basePipeName);
        string pipeName = Protocol.GetControlPipeName(
            basePipeName,
            session.ActivationMode,
            session.ProcessId!.Value);
        using NamedPipeServerStream server = CreateServer(pipeName);
        Task worker = ServePendingOnceAsync(server);
        PipeControlClient client = new(
            timeout: TimeSpan.FromMilliseconds(300),
            connectTimeout: TimeSpan.FromMilliseconds(100),
            pendingTimeout: TimeSpan.FromMilliseconds(450));

        PipeCallResult result = await client.SendCheatAsync(
            session,
            CheatCommands.SpawnEnemy,
            new JObject { ["enemyId"] = "CommonMonster" });

        Assert.False(result.TransportSuccess);
        Assert.True(result.RequestMayHaveExecuted);
        Assert.Contains("同一请求 ID", result.Error, StringComparison.Ordinal);
        await worker;
    }

    private static NamedPipeServerStream CreateServer(string pipeName) => new(
        pipeName,
        PipeDirection.InOut,
        2,
        PipeTransmissionMode.Byte,
        PipeOptions.Asynchronous);

    private static async Task ServeOnceAsync(
        NamedPipeServerStream server,
        TaskCompletionSource commandStarted)
    {
        await server.WaitForConnectionAsync();
        using StreamReader reader = new(server, Encoding.UTF8, false, 4096, leaveOpen: true);
        await using StreamWriter writer = new(server, new UTF8Encoding(false), 4096, leaveOpen: true)
        {
            AutoFlush = true
        };
        ControlRequest request = JsonConvert.DeserializeObject<ControlRequest>((await reader.ReadLineAsync())!)!;
        Assert.Equal(Environment.ProcessId, request.TargetGameProcessId);
        Assert.Equal("0123456789abcdef0123456789abcdef", request.TargetProcessInstanceId);
        if (string.Equals(request.Command, CheatCommands.QueryCatalog, StringComparison.OrdinalIgnoreCase))
        {
            commandStarted.TrySetResult();
            await Task.Delay(700);
        }

        ControlResponse response = new()
        {
            Id = request.Id,
            Success = true,
            Message = "ok",
            Status = new AutoPlayerStatus()
        };
        await writer.WriteLineAsync(JsonConvert.SerializeObject(response));
    }

    private static async Task ServePendingSequenceAsync(
        NamedPipeServerStream server,
        ConcurrentQueue<string> requestIds,
        int[] sequence)
    {
        await server.WaitForConnectionAsync();
        using StreamReader reader = new(server, Encoding.UTF8, false, 4096, leaveOpen: true);
        await using StreamWriter writer = new(server, new UTF8Encoding(false), 4096, leaveOpen: true)
        {
            AutoFlush = true
        };
        ControlRequest request = JsonConvert.DeserializeObject<ControlRequest>((await reader.ReadLineAsync())!)!;
        requestIds.Enqueue(request.Id);
        int current = Interlocked.Increment(ref sequence[0]);
        ControlResponse response = new()
        {
            Id = request.Id,
            Success = current > 1,
            Message = current > 1 ? "completed" : "pending",
            Data = current > 1 ? null : new JObject { ["pending"] = true, ["requestId"] = request.Id }
        };
        await writer.WriteLineAsync(JsonConvert.SerializeObject(response));
    }

    private static async Task ServePendingOnceAsync(NamedPipeServerStream server)
    {
        await server.WaitForConnectionAsync();
        using StreamReader reader = new(server, Encoding.UTF8, false, 4096, leaveOpen: true);
        await using StreamWriter writer = new(server, new UTF8Encoding(false), 4096, leaveOpen: true)
        {
            AutoFlush = true
        };
        ControlRequest request = JsonConvert.DeserializeObject<ControlRequest>((await reader.ReadLineAsync())!)!;
        ControlResponse response = new()
        {
            Id = request.Id,
            Message = "pending",
            Data = new JObject { ["pending"] = true, ["requestId"] = request.Id }
        };
        await writer.WriteLineAsync(JsonConvert.SerializeObject(response));
    }

    private static async Task ServeWithoutResponseAsync(NamedPipeServerStream server)
    {
        await server.WaitForConnectionAsync();
        using StreamReader reader = new(server, Encoding.UTF8, false, 4096, leaveOpen: true);
        _ = await reader.ReadLineAsync();
        await Task.Delay(500);
    }

    private static ActivationSession CreateSession(string pipeName) => new()
    {
        TicketPath = string.Empty,
        GameRoot = Path.GetTempPath(),
        EnvironmentVariables = new Dictionary<string, string>(),
        IsPersistent = true,
        ActivationMode = AutoPlayerActivationMode.ResidentPlayer,
        ProcessId = Environment.ProcessId,
        ProcessInstanceId = "0123456789abcdef0123456789abcdef",
        Ticket = new LaunchTicket
        {
            Protocol = Protocol.CurrentVersion,
            ExpiresUtc = DateTime.MaxValue,
            GameRootSha256 = new string('a', 64),
            PipeName = pipeName,
            Token = new string('b', 64),
            ProfileRoot = Path.GetTempPath(),
            ArtifactRoot = Path.GetTempPath(),
            ExpectedAssemblySha256 = new string('c', 64),
            CheatModeAllowed = true
        }
    };
}
