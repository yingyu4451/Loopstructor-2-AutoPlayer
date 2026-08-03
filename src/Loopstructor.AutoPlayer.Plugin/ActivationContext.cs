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
        string source)
    {
        PipeName = pipeName;
        Token = token;
        ProfileRoot = profileRoot;
        ArtifactRoot = artifactRoot;
        ExpectedAssemblySha256 = expectedAssemblySha256;
        CheatModeAllowed = cheatModeAllowed;
        Source = source;
    }

    public string PipeName { get; }
    public string Token { get; }
    public string ProfileRoot { get; }
    public string ArtifactRoot { get; }
    public string ExpectedAssemblySha256 { get; }
    public bool CheatModeAllowed { get; }
    public string Source { get; }

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
                "environment",
                out context,
                out reason);
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

        if (cheatModeAllowed)
        {
            string gameId = Protocol.HashGameRoot(gameRoot)[..16];
            string cheatRoot = Path.GetFullPath(Path.Combine(Protocol.DataRoot, "profiles", gameId, "cheat"));
            string? parent = Directory.GetParent(fullProfile)?.FullName;
            if (!string.Equals(parent, cheatRoot, StringComparison.OrdinalIgnoreCase))
            {
                reason = "作弊调试会话必须使用当前游戏专属的一次性隔离档。";
                return false;
            }
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
            source);
        reason = string.Empty;
        return true;
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
}
