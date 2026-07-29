using System.Security.Cryptography;
using Loopstructor.AutoPlayer.Core;
using Loopstructor.AutoPlayer.Manager.Models;
using Newtonsoft.Json;
using Newtonsoft.Json.Serialization;

namespace Loopstructor.AutoPlayer.Manager.Services;

public sealed class ActivationSessionFactory
{
    private static readonly JsonSerializerSettings JsonSettings = new()
    {
        ContractResolver = new CamelCasePropertyNamesContractResolver(),
        Formatting = Formatting.Indented
    };

    public ActivationSession Create(GameInstallValidation game, string profileName)
    {
        ArgumentNullException.ThrowIfNull(game);
        if (!game.IsValid || game.AssemblySha256.Length != 64)
        {
            throw new InvalidOperationException("A validated Skyspine build fingerprint is required.");
        }

        string gameId = Protocol.HashGameRoot(game.GameRoot)[..16];
        string safeProfile = SanitizeSegment(profileName, "qa-default");
        string sessionId = DateTime.UtcNow.ToString("yyyyMMdd-HHmmss") + "-" + RandomHex(4);
        string profileRoot = Path.Combine(Protocol.DataRoot, "profiles", gameId, safeProfile);
        string artifactRoot = Path.Combine(Protocol.DataRoot, "artifacts", gameId, sessionId);
        Directory.CreateDirectory(profileRoot);
        Directory.CreateDirectory(artifactRoot);

        LaunchTicket ticket = new()
        {
            Protocol = Protocol.CurrentVersion,
            ExpiresUtc = DateTime.UtcNow.AddMinutes(5),
            GameRootSha256 = Protocol.HashGameRoot(game.GameRoot),
            PipeName = "Loopstructor.AutoPlayer." + RandomHex(8),
            Token = RandomHex(32),
            ProfileRoot = profileRoot,
            ArtifactRoot = artifactRoot,
            ExpectedAssemblySha256 = game.AssemblySha256.ToLowerInvariant()
        };

        string ticketPath = Protocol.GetTicketPath(game.GameRoot);
        AtomicFile.WriteAllText(ticketPath, JsonConvert.SerializeObject(ticket, JsonSettings));
        Dictionary<string, string> environment = new(StringComparer.Ordinal)
        {
            [Protocol.EnabledEnvironmentVariable] = "1",
            [Protocol.PipeEnvironmentVariable] = ticket.PipeName,
            [Protocol.TokenEnvironmentVariable] = ticket.Token,
            [Protocol.ProfileEnvironmentVariable] = ticket.ProfileRoot,
            [Protocol.ArtifactEnvironmentVariable] = ticket.ArtifactRoot,
            [Protocol.ExpectedAssemblySha256EnvironmentVariable] = ticket.ExpectedAssemblySha256
        };

        return new ActivationSession
        {
            Ticket = ticket,
            TicketPath = ticketPath,
            GameRoot = Path.GetFullPath(game.GameRoot),
            EnvironmentVariables = environment
        };
    }

    private static string RandomHex(int byteCount) =>
        Convert.ToHexString(RandomNumberGenerator.GetBytes(byteCount)).ToLowerInvariant();

    private static string SanitizeSegment(string? value, string fallback)
    {
        string result = string.IsNullOrWhiteSpace(value) ? fallback : value.Trim();
        foreach (char invalid in Path.GetInvalidFileNameChars())
        {
            result = result.Replace(invalid, '_');
        }

        result = result.Trim('.', ' ');
        if (result.Length > 48)
        {
            result = result[..48];
        }

        return string.IsNullOrWhiteSpace(result) ? fallback : result;
    }
}
