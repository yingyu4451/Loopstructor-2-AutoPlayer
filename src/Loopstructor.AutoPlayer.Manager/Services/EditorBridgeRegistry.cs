using Loopstructor.AutoPlayer.Core;
using Loopstructor.AutoPlayer.Manager.Models;
using Newtonsoft.Json.Linq;

namespace Loopstructor.AutoPlayer.Manager.Services;

public sealed class EditorBridgeRegistry
{
    public const int CurrentSchemaVersion = 1;
    public const int CurrentProtocolVersion = 1;

    private readonly string _instancesRoot;
    private readonly TimeSpan _maximumAge;
    private readonly string _dataRoot;

    public EditorBridgeRegistry(string? instancesRoot = null, TimeSpan? maximumAge = null, string? dataRoot = null)
    {
        _dataRoot = Path.GetFullPath(dataRoot ?? Protocol.DataRoot);
        _instancesRoot = Path.GetFullPath(instancesRoot ?? Path.Combine(_dataRoot, "editor-instances"));
        _maximumAge = maximumAge ?? TimeSpan.FromSeconds(10);
    }

    public IReadOnlyList<EditorBridgeInstance> ListInstances() => ReadTrustedInstances()
        .OrderByDescending(instance => instance.LastSeenAt)
        .Select(instance => instance.ToPublic())
        .ToArray();

    internal bool TryGetTrusted(string instanceId, out TrustedEditorBridgeInstance instance, out string error)
    {
        instance = ReadTrustedInstances().FirstOrDefault(candidate =>
            string.Equals(candidate.InstanceId, instanceId, StringComparison.Ordinal))!;
        error = instance == null ? "Unity Editor 实例不存在、心跳已过期或登记无效。" : string.Empty;
        return instance != null;
    }

    private IReadOnlyList<TrustedEditorBridgeInstance> ReadTrustedInstances()
    {
        if (!Directory.Exists(_instancesRoot)) return Array.Empty<TrustedEditorBridgeInstance>();
        List<TrustedEditorBridgeInstance> result = new();
        foreach (string file in Directory.EnumerateFiles(_instancesRoot, "*.json", SearchOption.TopDirectoryOnly))
        {
            TrustedEditorBridgeInstance? instance = TryRead(file);
            if (instance != null && result.All(existing => existing.InstanceId != instance.InstanceId)) result.Add(instance);
        }
        return result;
    }

    private TrustedEditorBridgeInstance? TryRead(string path)
    {
        try
        {
            FileInfo file = new(path);
            if (!file.Exists || file.Attributes.HasFlag(FileAttributes.ReparsePoint) || file.Length is <= 0 or > 64 * 1024) return null;
            JObject json = JObject.Parse(File.ReadAllText(path));
            if (json.Value<int?>("schemaVersion") != CurrentSchemaVersion
                || json.Value<int?>("protocolVersion") != CurrentProtocolVersion) return null;

            DateTime seen = json.Value<DateTime?>("lastSeenAt")?.ToUniversalTime() ?? default;
            DateTime now = DateTime.UtcNow;
            if (seen == default || seen < now - _maximumAge || seen > now.AddSeconds(5)) return null;
            string instanceId = Required(json, "instanceId");
            string kind = Required(json, "kind");
            string mode = Required(json, "mode");
            string token = Required(json, "token");
            string projectPath = FullPath(json, "projectPath");
            string unityExecutablePath = FullPath(json, "unityExecutablePath");
            string artifactRoot = FullPath(json, "artifactRoot");
            string assemblyHash = Required(json, "assemblySha256").ToLowerInvariant();
            int processId = json.Value<int?>("processId") ?? 0;
            int port = json.Value<int?>("port") ?? 0;
            if (!instanceId.StartsWith("editor-", StringComparison.Ordinal)
                || instanceId.Length > 96
                || !string.Equals(Path.GetFileNameWithoutExtension(path), instanceId, StringComparison.Ordinal)
                || !string.Equals(kind, "editor", StringComparison.Ordinal)
                || mode is not ("editor-edit" or "editor-play")
                || token.Length is < 32 or > 256
                || processId <= 0
                || port is <= 0 or > 65535
                || assemblyHash.Length != 64
                || !assemblyHash.All(Uri.IsHexDigit)
                || assemblyHash.All(character => character == '0')
                || !IsOwned(artifactRoot, "artifacts")) return null;

            return new TrustedEditorBridgeInstance
            {
                InstanceId = instanceId,
                Kind = "editor",
                ProcessId = processId,
                DisplayName = json.Value<string>("displayName") ?? "Unity Editor",
                ProjectPath = projectPath,
                UnityExecutablePath = unityExecutablePath,
                UnityVersion = json.Value<string>("unityVersion") ?? string.Empty,
                GameVersion = json.Value<string>("gameVersion") ?? string.Empty,
                SceneName = json.Value<string>("sceneName") ?? string.Empty,
                Mode = mode,
                RuntimeReady = json.Value<bool?>("runtimeReady") == true,
                Port = port,
                ProtocolVersion = CurrentProtocolVersion.ToString(),
                Token = token,
                AssemblySha256 = assemblyHash,
                ArtifactRoot = artifactRoot,
                LastSeenAt = seen
            };
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or Newtonsoft.Json.JsonException or ArgumentException)
        {
            return null;
        }
    }

    private bool IsOwned(string candidate, string child)
    {
        string root = Path.Combine(_dataRoot, child);
        string relative = Path.GetRelativePath(root, candidate);
        return !Path.IsPathRooted(relative) && relative != ".." && !relative.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal);
    }

    private static string Required(JObject json, string name) =>
        string.IsNullOrWhiteSpace(json.Value<string>(name))
            ? throw new InvalidDataException("Editor Bridge 登记缺少字段：" + name)
            : json.Value<string>(name)!.Trim();

    private static string FullPath(JObject json, string name)
    {
        string value = Required(json, name);
        if (!Path.IsPathRooted(value)) throw new InvalidDataException("Editor Bridge 登记路径不是绝对路径：" + name);
        return Path.GetFullPath(value);
    }
}
