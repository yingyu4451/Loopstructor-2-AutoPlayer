using System.Net;
using System.Text;
using Loopstructor.AutoPlayer.Manager.Models;
using Loopstructor.AutoPlayer.Manager.Services;
using Newtonsoft.Json;

namespace Loopstructor.AutoPlayer.Tests;

public sealed class EditorBridgeRegistryTests
{
    [Fact]
    public void ListInstances_HidesCredentialsAndRejectsStaleOrMalformedEntries()
    {
        using TemporaryDirectory temporary = new();
        string instancesRoot = Path.Combine(temporary.Root, "editor-instances");
        Directory.CreateDirectory(instancesRoot);
        WriteInstance(instancesRoot, "editor-4321.json", DateTime.UtcNow, "secret-token-value-secret-token-value");
        WriteInstance(instancesRoot, "editor-stale.json", DateTime.UtcNow.AddSeconds(-30), "stale-token-value-stale-token-value");
        File.WriteAllText(Path.Combine(instancesRoot, "broken.json"), "{", Encoding.UTF8);
        EditorBridgeRegistry registry = new(instancesRoot, TimeSpan.FromSeconds(10));

        IReadOnlyList<EditorBridgeInstance> instances = registry.ListInstances();
        string json = JsonConvert.SerializeObject(instances);

        EditorBridgeInstance instance = Assert.Single(instances);
        Assert.Equal("editor-4321", instance.InstanceId);
        Assert.Equal("editor-edit", instance.Mode);
        Assert.DoesNotContain("token", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("pipe", json, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task ConnectAsync_UsesThePrivateBearerTokenOnlyAgainstLoopback()
    {
        TrustedEditorBridgeInstance trusted = CreateTrusted();
        RecordingHandler handler = new();
        EditorBridgeClient client = new(new HttpClient(handler));

        EditorBridgeConnectionResult result = await client.ConnectAsync(trusted);

        Assert.True(result.Success, result.Message);
        Assert.Equal("editor-edit", result.Mode);
        Assert.False(result.RuntimeReady);
        Assert.Equal(new Uri("http://127.0.0.1:39001/api/status"), handler.RequestUri);
        Assert.Equal("Bearer", handler.AuthorizationScheme);
        Assert.Equal(trusted.Token, handler.AuthorizationParameter);
    }

    [Fact]
    public async Task SendCheatAsync_PostsAnAuthenticatedCommandToLoopback()
    {
        TrustedEditorBridgeInstance trusted = CreateTrusted();
        RecordingHandler handler = new();
        EditorBridgeClient client = new(new HttpClient(handler));

        var result = await client.SendCheatAsync(
            trusted,
            "cheat.queryState",
            new Newtonsoft.Json.Linq.JObject());

        Assert.True(result.TransportSuccess, result.Error);
        Assert.True(result.Response?.Success);
        Assert.Equal(HttpMethod.Post, handler.Method);
        Assert.Equal(new Uri("http://127.0.0.1:39001/api/command"), handler.RequestUri);
        Assert.Equal("Bearer", handler.AuthorizationScheme);
        Assert.Equal(trusted.Token, handler.AuthorizationParameter);
        Assert.Contains("cheat.queryState", handler.RequestBody, StringComparison.Ordinal);
        Assert.DoesNotContain(trusted.Token, handler.RequestBody, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ConnectAsync_RejectsAStatusIdentityThatDiffersFromTheRegistry()
    {
        TrustedEditorBridgeInstance trusted = CreateTrusted(processId: 9876);
        EditorBridgeClient client = new(new HttpClient(new RecordingHandler()));

        EditorBridgeConnectionResult result = await client.ConnectAsync(trusted);

        Assert.False(result.Success);
        Assert.Contains("状态身份", result.Message, StringComparison.Ordinal);
    }

    private static void WriteInstance(string root, string fileName, DateTime seenAt, string token)
    {
        File.WriteAllText(Path.Combine(root, fileName), JsonConvert.SerializeObject(new
        {
            schemaVersion = 1,
            protocolVersion = 1,
            instanceId = Path.GetFileNameWithoutExtension(fileName),
            kind = "editor",
            processId = 4321,
            displayName = "Unity Editor · Loopstructor2",
            projectPath = "D:\\Unity Project\\Loopstructor2",
            unityExecutablePath = "D:\\Unity Editor\\2022.3.62f3c1\\Editor\\Unity.exe",
            unityVersion = "2022.3.62f3c1",
            gameVersion = "1.390",
            sceneName = "StartGameScene",
            mode = "editor-edit",
            runtimeReady = false,
            port = 39001,
            token,
            assemblySha256 = new string('a', 64),
            artifactRoot = Path.Combine(temporaryDataRoot, "artifacts", "editor"),
            lastSeenAt = seenAt.ToString("O")
        }), Encoding.UTF8);
    }

    private static readonly string temporaryDataRoot = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "LoopstructorAutoPlayer");

    private static TrustedEditorBridgeInstance CreateTrusted(int processId = 4321) => new()
    {
        InstanceId = "editor-4321",
        ProcessId = processId,
        ProjectPath = "D:\\Unity Project\\Loopstructor2",
        UnityExecutablePath = "D:\\Unity Editor\\2022.3.62f3c1\\Editor\\Unity.exe",
        Mode = "editor-edit",
        RuntimeReady = false,
        Port = 39001,
        ProtocolVersion = "1",
        Token = "private-token-private-token-private-token",
        AssemblySha256 = new string('a', 64),
        ArtifactRoot = Path.Combine(temporaryDataRoot, "artifacts", "editor"),
        LastSeenAt = DateTime.UtcNow
    };

    private sealed class RecordingHandler : HttpMessageHandler
    {
        public Uri? RequestUri { get; private set; }
        public string? AuthorizationScheme { get; private set; }
        public string? AuthorizationParameter { get; private set; }
        public HttpMethod? Method { get; private set; }
        public string RequestBody { get; private set; } = string.Empty;

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            RequestUri = request.RequestUri;
            AuthorizationScheme = request.Headers.Authorization?.Scheme;
            AuthorizationParameter = request.Headers.Authorization?.Parameter;
            Method = request.Method;
            RequestBody = request.Content == null
                ? string.Empty
                : await request.Content.ReadAsStringAsync(cancellationToken);
            HttpResponseMessage response = new(HttpStatusCode.OK)
            {
                RequestMessage = request,
                Content = new StringContent(
                    request.RequestUri?.AbsolutePath == "/api/command"
                        ? "{\"success\":true,\"message\":\"ok\",\"data\":{\"enabled\":true}}"
                        : "{\"success\":true,\"schemaVersion\":1,\"protocolVersion\":1,\"instanceId\":\"editor-4321\",\"processId\":4321,\"projectPath\":\"D:\\\\Unity Project\\\\Loopstructor2\",\"assemblySha256\":\"aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa\",\"mode\":\"editor-edit\",\"runtimeReady\":false,\"message\":\"Editor 已连接\"}",
                    Encoding.UTF8,
                    "application/json")
            };
            return response;
        }
    }

    private sealed class TemporaryDirectory : IDisposable
    {
        public TemporaryDirectory()
        {
            Root = Path.Combine(Path.GetTempPath(), "Loopstructor.EditorRegistry.Tests", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Root);
        }

        public string Root { get; }

        public void Dispose()
        {
            if (Directory.Exists(Root)) Directory.Delete(Root, recursive: true);
        }
    }
}
