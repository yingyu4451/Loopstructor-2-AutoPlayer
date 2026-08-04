using System.Security.Cryptography;
using Loopstructor.AutoPlayer.Core;
using Loopstructor.AutoPlayer.Manager.Models;
using Newtonsoft.Json;
using Newtonsoft.Json.Serialization;

namespace Loopstructor.AutoPlayer.Manager.Services;

/// <summary>
/// Owns the per-installation local credential used by the resident player-mode plugin.
/// The credential never grants trust by itself: MainForm still validates the game PID,
/// executable path, assembly fingerprint and runtime contract returned by the plugin.
/// </summary>
public sealed class InstalledControlSessionStore
{
    private static readonly JsonSerializerSettings JsonSettings = new()
    {
        ContractResolver = new CamelCasePropertyNamesContractResolver(),
        Formatting = Formatting.Indented
    };

    private readonly string _dataRoot;

    public InstalledControlSessionStore(string? dataRoot = null)
    {
        _dataRoot = Path.GetFullPath(dataRoot ?? Protocol.DataRoot);
    }

    public ActivationSession Ensure(
        GameInstallValidation game,
        string profileName,
        bool selectProfile)
    {
        ArgumentNullException.ThrowIfNull(game);
        if (!game.IsValid || game.AssemblySha256.Length != 64 || game.AssemblySha256.Any(character => !Uri.IsHexDigit(character)))
        {
            throw new InvalidOperationException("创建玩家控制注册前必须验证 Skyspine 游戏和程序集指纹。");
        }

        string gameRoot = Path.GetFullPath(game.GameRoot);
        string rootHash = Protocol.HashGameRoot(gameRoot);
        string gameId = rootHash[..16];
        string path = RegistrationPath(_dataRoot, gameRoot);
        InstalledControlRegistration? existing = TryRead(path);

        bool reusableIdentity = existing != null
                                && existing.Protocol == Protocol.CurrentVersion
                                && string.Equals(existing.GameRootSha256, rootHash, StringComparison.OrdinalIgnoreCase)
                                && IsPipeName(existing.PipeName)
                                && IsToken(existing.Token);
        string pipeName = reusableIdentity
            ? existing!.PipeName
            : "Loopstructor.AutoPlayer.Player." + gameId;
        string token = reusableIdentity
            ? existing!.Token
            : Convert.ToHexString(RandomNumberGenerator.GetBytes(32)).ToLowerInvariant();

        string defaultStateRoot = Path.Combine(_dataRoot, "profiles", gameId, SanitizeSegment(profileName, "player-default"));
        string stateRoot = reusableIdentity && !selectProfile && IsOwnedDirectory(existing!.ProfileRoot, "profiles")
            ? Path.GetFullPath(existing.ProfileRoot)
            : Path.GetFullPath(defaultStateRoot);
        string defaultArtifactRoot = Path.Combine(_dataRoot, "artifacts", gameId, "player");
        string artifactRoot = reusableIdentity && IsOwnedDirectory(existing!.ArtifactRoot, "artifacts")
            ? Path.GetFullPath(existing.ArtifactRoot)
            : Path.GetFullPath(defaultArtifactRoot);

        Directory.CreateDirectory(stateRoot);
        Directory.CreateDirectory(artifactRoot);
        InstalledControlRegistration registration = new()
        {
            Protocol = Protocol.CurrentVersion,
            UpdatedAtUtc = DateTime.UtcNow,
            GameRootSha256 = rootHash,
            PipeName = pipeName,
            Token = token,
            ProfileRoot = stateRoot,
            ArtifactRoot = artifactRoot,
            ExpectedAssemblySha256 = game.AssemblySha256.ToLowerInvariant(),
            CheatModeAllowed = true
        };
        AtomicFile.WriteAllText(path, JsonConvert.SerializeObject(registration, JsonSettings));
        return CreateSession(gameRoot, registration);
    }

    public bool TryLoad(GameInstallValidation game, out ActivationSession? session, out string error)
    {
        ArgumentNullException.ThrowIfNull(game);
        session = null;
        string path = RegistrationPath(_dataRoot, game.GameRoot);
        InstalledControlRegistration? registration = TryRead(path);
        if (registration == null)
        {
            error = "尚未创建玩家模式本机控制注册。";
            return false;
        }

        if (!Validate(game, registration, out error)) return false;
        session = CreateSession(Path.GetFullPath(game.GameRoot), registration);
        return true;
    }

    public void Delete(string gameRoot)
    {
        string path = RegistrationPath(_dataRoot, gameRoot);
        try
        {
            if (File.Exists(path)) File.Delete(path);
        }
        catch
        {
            // Uninstalling the plugin is authoritative. A stale local credential
            // cannot activate anything once the plugin assembly has been removed.
        }
    }

    internal static string RegistrationPath(string dataRoot, string gameRoot)
    {
        string id = Protocol.HashGameRoot(gameRoot)[..16];
        return Path.Combine(Path.GetFullPath(dataRoot), "control", "installed-" + id + ".json");
    }

    private ActivationSession CreateSession(string gameRoot, InstalledControlRegistration registration) => new()
    {
        GameRoot = Path.GetFullPath(gameRoot),
        TicketPath = string.Empty,
        IsPersistent = true,
        ActivationMode = AutoPlayerActivationMode.ResidentPlayer,
        EnvironmentVariables = new Dictionary<string, string>(),
        Ticket = new LaunchTicket
        {
            Protocol = registration.Protocol,
            ExpiresUtc = DateTime.MaxValue,
            GameRootSha256 = registration.GameRootSha256,
            PipeName = registration.PipeName,
            Token = registration.Token,
            ProfileRoot = registration.ProfileRoot,
            ArtifactRoot = registration.ArtifactRoot,
            ExpectedAssemblySha256 = registration.ExpectedAssemblySha256,
            CheatModeAllowed = registration.CheatModeAllowed
        }
    };

    private bool Validate(
        GameInstallValidation game,
        InstalledControlRegistration registration,
        out string error)
    {
        if (registration.Protocol != Protocol.CurrentVersion)
        {
            error = "玩家模式本机控制注册的协议版本不兼容。";
            return false;
        }

        if (!string.Equals(registration.GameRootSha256, Protocol.HashGameRoot(game.GameRoot), StringComparison.OrdinalIgnoreCase))
        {
            error = "玩家模式本机控制注册属于另一个游戏目录。";
            return false;
        }

        if (!string.Equals(registration.ExpectedAssemblySha256, game.AssemblySha256, StringComparison.OrdinalIgnoreCase))
        {
            error = "玩家模式本机控制注册的程序集指纹已过期。";
            return false;
        }

        if (!IsPipeName(registration.PipeName) || !IsToken(registration.Token))
        {
            error = "玩家模式本机控制凭据无效。";
            return false;
        }

        if (!IsOwnedDirectory(registration.ProfileRoot, "profiles")
            || !IsOwnedDirectory(registration.ArtifactRoot, "artifacts"))
        {
            error = "玩家模式控制状态目录超出 AutoPlayer 本机数据目录。";
            return false;
        }

        error = string.Empty;
        return true;
    }

    private bool IsOwnedDirectory(string? path, string child)
    {
        if (string.IsNullOrWhiteSpace(path) || !Path.IsPathRooted(path)) return false;
        try
        {
            string full = Path.GetFullPath(path);
            string allowed = Path.GetFullPath(Path.Combine(_dataRoot, child))
                .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;
            return full.StartsWith(allowed, StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            return false;
        }
    }

    private static InstalledControlRegistration? TryRead(string path)
    {
        try
        {
            return File.Exists(path)
                ? JsonConvert.DeserializeObject<InstalledControlRegistration>(File.ReadAllText(path))
                : null;
        }
        catch
        {
            return null;
        }
    }

    private static bool IsPipeName(string? value) =>
        !string.IsNullOrWhiteSpace(value)
        && value.Length <= 180
        && value.IndexOfAny(new[] { '\\', '/', ':', '\0' }) < 0;

    private static bool IsToken(string? value) => value is { Length: >= 32 and <= 256 };

    private static string SanitizeSegment(string? value, string fallback)
    {
        string result = string.IsNullOrWhiteSpace(value) ? fallback : value.Trim();
        foreach (char invalid in Path.GetInvalidFileNameChars()) result = result.Replace(invalid, '_');
        result = result.Trim('.', ' ');
        if (result.Length > 48) result = result[..48];
        return string.IsNullOrWhiteSpace(result) ? fallback : result;
    }

    private sealed class InstalledControlRegistration
    {
        public int Protocol { get; set; }
        public DateTime UpdatedAtUtc { get; set; }
        public string GameRootSha256 { get; set; } = string.Empty;
        public string PipeName { get; set; } = string.Empty;
        public string Token { get; set; } = string.Empty;
        public string ProfileRoot { get; set; } = string.Empty;
        public string ArtifactRoot { get; set; } = string.Empty;
        public string ExpectedAssemblySha256 { get; set; } = string.Empty;
        public bool CheatModeAllowed { get; set; }
    }
}
