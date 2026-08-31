using Newtonsoft.Json.Linq;

namespace Loopstructor.AutoPlayer.Host;

internal static class DesktopHostProtocol
{
    public const int CurrentVersion = 1;
}

internal sealed class DesktopHostRequest
{
    public string Id { get; set; } = string.Empty;
    public string Method { get; set; } = string.Empty;
    public JObject? Params { get; set; }
}

internal sealed class DesktopHostResponse
{
    public string Id { get; set; } = string.Empty;
    public bool Success { get; set; }
    public JToken? Result { get; set; }
    public string Error { get; set; } = string.Empty;
}

internal sealed class DesktopHostEvent
{
    public string Event { get; set; } = string.Empty;
    public JToken? Payload { get; set; }
}

internal sealed record HostLogEntry(DateTime TimestampUtc, string Level, string Message);
