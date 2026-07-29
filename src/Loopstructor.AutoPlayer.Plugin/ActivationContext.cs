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
        string source)
    {
        PipeName = pipeName;
        Token = token;
        ProfileRoot = profileRoot;
        ArtifactRoot = artifactRoot;
        ExpectedAssemblySha256 = expectedAssemblySha256;
        Source = source;
    }

    public string PipeName { get; }
    public string Token { get; }
    public string ProfileRoot { get; }
    public string ArtifactRoot { get; }
    public string ExpectedAssemblySha256 { get; }
    public string Source { get; }

    public static bool TryLoad(string gameRoot, out ActivationContext? context, out string reason)
    {
        context = null;
        reason = "No one-time automation activation was supplied.";
        if (string.Equals(Environment.GetEnvironmentVariable(Protocol.EnabledEnvironmentVariable), "1", StringComparison.Ordinal))
        {
            return TryCreate(
                Environment.GetEnvironmentVariable(Protocol.PipeEnvironmentVariable),
                Environment.GetEnvironmentVariable(Protocol.TokenEnvironmentVariable),
                Environment.GetEnvironmentVariable(Protocol.ProfileEnvironmentVariable),
                Environment.GetEnvironmentVariable(Protocol.ArtifactEnvironmentVariable),
                Environment.GetEnvironmentVariable(Protocol.ExpectedAssemblySha256EnvironmentVariable),
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
            reason = "The launch ticket was already consumed or could not be claimed: " + exception.Message;
            return false;
        }

        try
        {
            LaunchTicket? ticket = JsonConvert.DeserializeObject<LaunchTicket>(File.ReadAllText(claimedTicketPath));
            if (ticket == null || ticket.Protocol != Protocol.CurrentVersion)
            {
                reason = "The launch ticket protocol is invalid.";
                return false;
            }

            DateTime now = DateTime.UtcNow;
            if (ticket.ExpiresUtc <= now || ticket.ExpiresUtc > now.AddMinutes(10))
            {
                reason = "The launch ticket is expired or has an unsafe lifetime.";
                return false;
            }

            if (!string.Equals(ticket.GameRootSha256, Protocol.HashGameRoot(gameRoot), StringComparison.OrdinalIgnoreCase))
            {
                reason = "The launch ticket belongs to a different game installation.";
                return false;
            }

            return TryCreate(
                ticket.PipeName,
                ticket.Token,
                ticket.ProfileRoot,
                ticket.ArtifactRoot,
                ticket.ExpectedAssemblySha256,
                "ticket",
                out context,
                out reason);
        }
        catch (Exception exception)
        {
            reason = "Could not consume the launch ticket: " + exception.Message;
            return false;
        }
        finally
        {
            TryDelete(claimedTicketPath);
        }
    }

    private static bool TryCreate(
        string? pipeName,
        string? token,
        string? profileRoot,
        string? artifactRoot,
        string? expectedAssemblySha256,
        string source,
        out ActivationContext? context,
        out string reason)
    {
        context = null;
        if (string.IsNullOrWhiteSpace(pipeName) || pipeName.Length > 180 || pipeName.IndexOfAny(new[] { '\\', '/', ':', '\0' }) >= 0)
        {
            reason = "The automation pipe name is invalid.";
            return false;
        }

        if (string.IsNullOrWhiteSpace(token) || token.Length < 32 || token.Length > 256)
        {
            reason = "The automation session token is invalid.";
            return false;
        }

        if (string.IsNullOrWhiteSpace(profileRoot) || !Path.IsPathRooted(profileRoot))
        {
            reason = "The automation profile root is invalid.";
            return false;
        }

        if (string.IsNullOrWhiteSpace(artifactRoot) || !Path.IsPathRooted(artifactRoot))
        {
            reason = "The automation artifact root is invalid.";
            return false;
        }

        string expectedHash = (expectedAssemblySha256 ?? string.Empty).Trim();
        if (expectedHash.Length != 64 || !IsHex(expectedHash))
        {
            reason = "The expected Assembly-CSharp fingerprint is invalid.";
            return false;
        }

        string fullProfile = Path.GetFullPath(profileRoot);
        string allowedRoot = Path.GetFullPath(Path.Combine(Protocol.DataRoot, "profiles"))
            .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;
        if (!fullProfile.StartsWith(allowedRoot, StringComparison.OrdinalIgnoreCase))
        {
            reason = "The automation profile must be inside the tool's profile directory.";
            return false;
        }

        string fullArtifact = Path.GetFullPath(artifactRoot);
        string allowedArtifactRoot = Path.GetFullPath(Path.Combine(Protocol.DataRoot, "artifacts"))
            .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;
        if (!fullArtifact.StartsWith(allowedArtifactRoot, StringComparison.OrdinalIgnoreCase))
        {
            reason = "The automation artifact root must be inside the tool's artifact directory.";
            return false;
        }

        Directory.CreateDirectory(fullProfile);
        Directory.CreateDirectory(fullArtifact);
        context = new ActivationContext(pipeName, token, fullProfile, fullArtifact, expectedHash.ToLowerInvariant(), source);
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
