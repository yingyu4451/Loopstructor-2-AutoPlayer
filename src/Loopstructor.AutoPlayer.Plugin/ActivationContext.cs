using System;
using System.IO;
using Loopstructor.AutoPlayer.Core;
using Newtonsoft.Json;

namespace Loopstructor.AutoPlayer.Plugin;

internal sealed class ActivationContext
{
    private ActivationContext(
        string pipeName,
        string token,
        string profileRoot,
        string artifactRoot,
        string expectedAssemblySha256,
        bool cheatModeAllowed,
        AutoPlayerActivationMode activationMode,
        string source)
    {
        PipeName = pipeName;
        Token = token;
        ProfileRoot = profileRoot;
        ArtifactRoot = artifactRoot;
        ExpectedAssemblySha256 = expectedAssemblySha256;
        CheatModeAllowed = cheatModeAllowed;
        ActivationMode = activationMode;
        Source = source;
        ProcessInstanceId = Guid.NewGuid().ToString("N");
    }

    public string PipeName { get; }
    public string Token { get; }
    public string ProfileRoot { get; }
    public string ArtifactRoot { get; }
    public string ExpectedAssemblySha256 { get; }
    public bool CheatModeAllowed { get; }
    public AutoPlayerActivationMode ActivationMode { get; }
    public bool IsPlayerMode => ActivationMode == AutoPlayerActivationMode.ResidentPlayer;
    public string Source { get; }
    public string ProcessInstanceId { get; }
    public bool CheatProfileTainted => CheatProfileTaintMarker.IsTainted(ProfileRoot);

    public bool TryMarkCheatProfileTainted(string requestId, string command, out string error) =>
        CheatProfileTaintMarker.TryMark(ProfileRoot, requestId, command, out error);

    public static bool TryLoad(string gameRoot, out ActivationContext? context, out string reason)
    {
        context = null;
        reason = "未提供一次性自动游玩激活信息。";
        if (string.Equals(Environment.GetEnvironmentVariable(Protocol.EnabledEnvironmentVariable), "1", StringComparison.Ordinal))
        {
            return TryCreate(
                gameRoot,
                Environment.GetEnvironmentVariable(Protocol.PipeEnvironmentVariable),
                Environment.GetEnvironmentVariable(Protocol.TokenEnvironmentVariable),
                Environment.GetEnvironmentVariable(Protocol.ProfileEnvironmentVariable),
                Environment.GetEnvironmentVariable(Protocol.ArtifactEnvironmentVariable),
                Environment.GetEnvironmentVariable(Protocol.ExpectedAssemblySha256EnvironmentVariable),
                string.Equals(
                    Environment.GetEnvironmentVariable(Protocol.CheatModeAllowedEnvironmentVariable),
                    "1",
                    StringComparison.Ordinal),
                AutoPlayerActivationMode.IsolatedQa,
                "environment",
                out context,
                out reason);
        }

        bool installedFound;
        if (TryLoadInstalled(gameRoot, out context, out reason, out installedFound))
        {
            return true;
        }

        if (installedFound)
        {
            return false;
        }

        string ticketPath = Protocol.GetTicketPath(gameRoot);
        if (!File.Exists(ticketPath))
        {
            return false;
        }

        string claimedTicketPath = ticketPath + ".claim-" + Guid.NewGuid().ToString("N");
        try
        {
            File.Move(ticketPath, claimedTicketPath);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            reason = "启动票据已被使用或无法领取：" + exception.Message;
            return false;
        }

        try
        {
            LaunchTicket? ticket = JsonConvert.DeserializeObject<LaunchTicket>(File.ReadAllText(claimedTicketPath));
            if (ticket == null || ticket.Protocol != Protocol.CurrentVersion)
            {
                reason = "启动票据的协议无效。";
                return false;
            }

            DateTime now = DateTime.UtcNow;
            if (ticket.ExpiresUtc <= now || ticket.ExpiresUtc > now.AddMinutes(10))
            {
                reason = "启动票据已过期或有效期不安全。";
                return false;
            }

            if (!string.Equals(ticket.GameRootSha256, Protocol.HashGameRoot(gameRoot), StringComparison.OrdinalIgnoreCase))
            {
                reason = "启动票据属于另一个游戏安装目录。";
                return false;
            }

            return TryCreate(
                gameRoot,
                ticket.PipeName,
                ticket.Token,
                ticket.ProfileRoot,
                ticket.ArtifactRoot,
                ticket.ExpectedAssemblySha256,
                ticket.CheatModeAllowed,
                AutoPlayerActivationMode.IsolatedQa,
                "ticket",
                out context,
                out reason);
        }
        catch (Exception exception)
        {
            reason = "无法读取启动票据：" + exception.Message;
            return false;
        }
        finally
        {
            TryDelete(claimedTicketPath);
        }
    }

    private static bool TryCreate(
        string gameRoot,
        string? pipeName,
        string? token,
        string? profileRoot,
        string? artifactRoot,
        string? expectedAssemblySha256,
        bool cheatModeAllowed,
        AutoPlayerActivationMode activationMode,
        string source,
        out ActivationContext? context,
        out string reason)
    {
        context = null;
        if (string.IsNullOrWhiteSpace(pipeName) || pipeName.Length > 180 || pipeName.IndexOfAny(new[] { '\\', '/', ':', '\0' }) >= 0)
        {
            reason = "自动游玩通信管道名称无效。";
            return false;
        }

        if (string.IsNullOrWhiteSpace(token) || token.Length < 32 || token.Length > 256)
        {
            reason = "自动游玩会话令牌无效。";
            return false;
        }

        if (string.IsNullOrWhiteSpace(profileRoot) || !Path.IsPathRooted(profileRoot))
        {
            reason = "自动游玩存档根目录无效。";
            return false;
        }

        if (string.IsNullOrWhiteSpace(artifactRoot) || !Path.IsPathRooted(artifactRoot))
        {
            reason = "自动游玩测试产物根目录无效。";
            return false;
        }

        string expectedHash = (expectedAssemblySha256 ?? string.Empty).Trim();
        if (expectedHash.Length != 64 || !IsHex(expectedHash))
        {
            reason = "预期的 Assembly-CSharp 指纹无效。";
            return false;
        }

        string fullProfile = Path.GetFullPath(profileRoot);
        string allowedRoot = Path.GetFullPath(Path.Combine(Protocol.DataRoot, "profiles"))
            .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;
        if (!fullProfile.StartsWith(allowedRoot, StringComparison.OrdinalIgnoreCase))
        {
            reason = "自动游玩存档必须位于本工具的存档目录内。";
            return false;
        }

        string fullArtifact = Path.GetFullPath(artifactRoot);
        string allowedArtifactRoot = Path.GetFullPath(Path.Combine(Protocol.DataRoot, "artifacts"))
            .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;
        if (!fullArtifact.StartsWith(allowedArtifactRoot, StringComparison.OrdinalIgnoreCase))
        {
            reason = "自动游玩测试产物必须位于本工具的测试产物目录内。";
            return false;
        }

        Directory.CreateDirectory(fullProfile);
        Directory.CreateDirectory(fullArtifact);
        context = new ActivationContext(
            pipeName,
            token,
            fullProfile,
            fullArtifact,
            expectedHash.ToLowerInvariant(),
            cheatModeAllowed,
            activationMode,
            source);
        reason = string.Empty;
        return true;
    }

    private static bool TryLoadInstalled(
        string gameRoot,
        out ActivationContext? context,
        out string reason,
        out bool found)
    {
        context = null;
        string path = InstalledRegistrationPath(gameRoot);
        found = File.Exists(path);
        if (!found)
        {
            reason = "未找到已安装插件的玩家模式本机控制注册。";
            return false;
        }

        try
        {
            InstalledControlRegistration? registration =
                JsonConvert.DeserializeObject<InstalledControlRegistration>(File.ReadAllText(path));
            if (registration == null || registration.Protocol != Protocol.CurrentVersion)
            {
                reason = "玩家模式本机控制注册的协议无效。";
                return false;
            }

            if (!string.Equals(
                    registration.GameRootSha256,
                    Protocol.HashGameRoot(gameRoot),
                    StringComparison.OrdinalIgnoreCase))
            {
                reason = "玩家模式本机控制注册属于另一个游戏安装目录。";
                return false;
            }

            return TryCreate(
                gameRoot,
                registration.PipeName,
                registration.Token,
                registration.ProfileRoot,
                registration.ArtifactRoot,
                registration.ExpectedAssemblySha256,
                registration.CheatModeAllowed,
                AutoPlayerActivationMode.ResidentPlayer,
                "玩家模式本机注册",
                out context,
                out reason);
        }
        catch (Exception exception)
        {
            reason = "无法读取玩家模式本机控制注册：" + exception.Message;
            return false;
        }
    }

    private static string InstalledRegistrationPath(string gameRoot)
    {
        string id = Protocol.HashGameRoot(gameRoot).Substring(0, 16);
        return Path.Combine(Protocol.DataRoot, "control", "installed-" + id + ".json");
    }

    private static bool IsHex(string value)
    {
        for (int index = 0; index < value.Length; index++)
        {
            char character = value[index];
            bool valid = character >= '0' && character <= '9' ||
                         character >= 'a' && character <= 'f' ||
                         character >= 'A' && character <= 'F';
            if (!valid) return false;
        }

        return true;
    }

    private static void TryDelete(string path)
    {
        try { File.Delete(path); } catch { }
    }

    private sealed class InstalledControlRegistration
    {
        public int Protocol { get; set; }
        public string GameRootSha256 { get; set; } = string.Empty;
        public string PipeName { get; set; } = string.Empty;
        public string Token { get; set; } = string.Empty;
        public string ProfileRoot { get; set; } = string.Empty;
        public string ArtifactRoot { get; set; } = string.Empty;
        public string ExpectedAssemblySha256 { get; set; } = string.Empty;
        public bool CheatModeAllowed { get; set; }
    }
}
