using Newtonsoft.Json.Linq;

namespace Loopstructor.AutoPlayer.Plugin;

internal sealed class CheatExecutionResult
{
    public bool Success { get; private set; }
    public bool Mutated { get; private set; }
    public string Message { get; private set; } = string.Empty;
    public string ErrorCode { get; private set; } = string.Empty;
    public JObject Data { get; private set; } = new();

    public static CheatExecutionResult Ok(string message, JObject? data = null) => new()
    {
        Success = true,
        Message = message,
        Data = data ?? new JObject()
    };

    public static CheatExecutionResult Changed(string message, JObject? data = null) => new()
    {
        Success = true,
        Mutated = true,
        Message = message,
        Data = data ?? new JObject()
    };

    public static CheatExecutionResult Partial(string message, JObject? data = null) => new()
    {
        Mutated = true,
        Message = message,
        ErrorCode = "PARTIAL_CHANGE",
        Data = data ?? new JObject()
    };

    public static CheatExecutionResult Fail(string message, string errorCode = "CHEAT_COMMAND_FAILED") => new()
    {
        Message = message,
        ErrorCode = errorCode,
        Data = new JObject { ["errorCode"] = errorCode }
    };
}
