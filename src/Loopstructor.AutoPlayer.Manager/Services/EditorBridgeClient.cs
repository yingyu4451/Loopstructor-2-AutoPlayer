using System.Net.Http.Headers;
using System.Text;
using Loopstructor.AutoPlayer.Core;
using Loopstructor.AutoPlayer.Manager.Models;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace Loopstructor.AutoPlayer.Manager.Services;

public sealed class EditorBridgeClient
{
    private readonly HttpClient _client;

    public EditorBridgeClient(HttpClient? client = null)
    {
        _client = client ?? new HttpClient();
    }

    internal async Task<EditorBridgeConnectionResult> ConnectAsync(
        TrustedEditorBridgeInstance instance,
        CancellationToken cancellationToken = default)
    {
        using CancellationTokenSource timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(3));
        Uri endpoint = new($"http://127.0.0.1:{instance.Port}/api/status");
        using HttpRequestMessage request = new(HttpMethod.Get, endpoint);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", instance.Token);
        try
        {
            using HttpResponseMessage response = await _client.SendAsync(request, timeout.Token);
            if (response.RequestMessage?.RequestUri != endpoint)
                return new EditorBridgeConnectionResult { Message = "Unity Editor Bridge 响应离开了预期回环端点。" };
            string content = await response.Content.ReadAsStringAsync(timeout.Token);
            JObject payload = JObject.Parse(content);
            if (!response.IsSuccessStatusCode || payload.Value<bool?>("success") != true)
            {
                return new EditorBridgeConnectionResult
                {
                    Message = payload.Value<string>("message") ?? payload.Value<string>("error") ?? "Unity Editor Bridge 拒绝连接。"
                };
            }
            string mode = payload.Value<string>("mode") ?? string.Empty;
            if (payload.Value<int?>("schemaVersion") != EditorBridgeRegistry.CurrentSchemaVersion
                || payload.Value<int?>("protocolVersion") != EditorBridgeRegistry.CurrentProtocolVersion
                || !string.Equals(payload.Value<string>("instanceId"), instance.InstanceId, StringComparison.Ordinal)
                || payload.Value<int?>("processId") != instance.ProcessId
                || !SamePath(payload.Value<string>("projectPath"), instance.ProjectPath)
                || !string.Equals(payload.Value<string>("assemblySha256"), instance.AssemblySha256, StringComparison.OrdinalIgnoreCase)
                || mode is not ("editor-edit" or "editor-play")
                || payload.Value<bool?>("runtimeReady") == true && mode != "editor-play")
            {
                return new EditorBridgeConnectionResult { Message = "Unity Editor Bridge 状态身份与实例登记不一致。" };
            }
            return new EditorBridgeConnectionResult
            {
                Success = true,
                InstanceId = instance.InstanceId,
                ProcessId = payload.Value<int?>("processId") ?? 0,
                Mode = mode,
                RuntimeReady = payload.Value<bool?>("runtimeReady") == true,
                SceneName = payload.Value<string>("sceneName") ?? instance.SceneName,
                AssemblySha256 = payload.Value<string>("assemblySha256") ?? string.Empty,
                Message = payload.Value<string>("message") ?? "Unity Editor 已连接。"
            };
        }
        catch (Exception exception) when (exception is HttpRequestException or TaskCanceledException or Newtonsoft.Json.JsonException)
        {
            return new EditorBridgeConnectionResult { Message = "无法连接 Unity Editor Bridge。详细信息：" + exception.Message };
        }
    }

    private static bool SamePath(string? left, string right)
    {
        if (string.IsNullOrWhiteSpace(left)) return false;
        try
        {
            return string.Equals(
                Path.TrimEndingDirectorySeparator(Path.GetFullPath(left)),
                Path.TrimEndingDirectorySeparator(Path.GetFullPath(right)),
                StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception exception) when (exception is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return false;
        }
    }

    internal async Task<PipeCallResult> SendCheatAsync(
        TrustedEditorBridgeInstance instance,
        string command,
        JObject? arguments,
        CancellationToken cancellationToken = default)
    {
        const string endpoint = "editor-http";
        using CancellationTokenSource timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(8));
        Uri endpointUri = new($"http://127.0.0.1:{instance.Port}/api/command");
        using HttpRequestMessage request = new(
            HttpMethod.Post,
            endpointUri);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", instance.Token);
        request.Content = new StringContent(
            new JObject
            {
                ["command"] = command,
                ["arguments"] = arguments ?? new JObject()
            }.ToString(Formatting.None),
            Encoding.UTF8,
            "application/json");
        try
        {
            using HttpResponseMessage response = await _client.SendAsync(request, timeout.Token);
            if (response.RequestMessage?.RequestUri != endpointUri)
            {
                return new PipeCallResult
                {
                    Endpoint = endpoint,
                    Error = "Unity Editor Bridge 命令响应离开了预期回环端点。"
                };
            }
            string content = await response.Content.ReadAsStringAsync(timeout.Token);
            JObject payload = JObject.Parse(content);
            if (!response.IsSuccessStatusCode)
            {
                return new PipeCallResult
                {
                    RequestMayHaveExecuted = true,
                    Endpoint = endpoint,
                    Error = payload.Value<string>("message") ?? payload.Value<string>("error") ?? "Unity Editor Bridge 拒绝命令。"
                };
            }

            ControlResponse result = payload.ToObject<ControlResponse>()
                                     ?? throw new JsonException("Unity Editor Bridge 命令响应为空。");
            return new PipeCallResult
            {
                TransportSuccess = true,
                RequestMayHaveExecuted = result.Success,
                Endpoint = endpoint,
                Response = result
            };
        }
        catch (Exception exception) when (exception is HttpRequestException or TaskCanceledException or Newtonsoft.Json.JsonException)
        {
            return new PipeCallResult
            {
                RequestMayHaveExecuted = true,
                Endpoint = endpoint,
                Error = "Unity Editor Bridge 命令连接失败。详细信息：" + exception.Message
            };
        }
    }
}
