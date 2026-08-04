using Loopstructor.AutoPlayer.Core;
using Loopstructor.AutoPlayer.Manager.Models;
using Loopstructor.AutoPlayer.Manager.Services;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace Loopstructor.AutoPlayer.Tests;

public sealed class InstalledControlSessionStoreTests
{
    [Fact]
    public void Ensure_PreservesLocalCredentialAndOnlyChangesProfileWhenRequested()
    {
        using TemporaryDirectory temporary = new();
        string dataRoot = Path.Combine(temporary.Root, "data");
        string gameRoot = Path.Combine(temporary.Root, "Skyspine");
        Directory.CreateDirectory(gameRoot);
        GameInstallValidation game = ValidGame(gameRoot);
        InstalledControlSessionStore store = new(dataRoot);

        ActivationSession first = store.Ensure(game, "player-one", selectProfile: true);
        ActivationSession attached = store.Ensure(game, "player-two", selectProfile: false);
        ActivationSession changed = store.Ensure(game, "player-two", selectProfile: true);

        Assert.True(first.IsPersistent);
        Assert.Equal(AutoPlayerActivationMode.ResidentPlayer, first.ActivationMode);
        Assert.Empty(first.EnvironmentVariables);
        Assert.Equal(first.Ticket.PipeName, attached.Ticket.PipeName);
        Assert.Equal(first.Ticket.Token, attached.Ticket.Token);
        Assert.Equal(first.Ticket.ProfileRoot, attached.Ticket.ProfileRoot);
        Assert.Equal(first.Ticket.PipeName, changed.Ticket.PipeName);
        Assert.Equal(first.Ticket.Token, changed.Ticket.Token);
        Assert.NotEqual(first.Ticket.ProfileRoot, changed.Ticket.ProfileRoot);
        Assert.EndsWith("player-two", changed.Ticket.ProfileRoot, StringComparison.OrdinalIgnoreCase);
        Assert.True(Directory.Exists(changed.Ticket.ProfileRoot));
        Assert.True(Directory.Exists(changed.Ticket.ArtifactRoot));
    }

    [Fact]
    public void TryLoad_RejectsRegistrationWhoseAssemblyFingerprintWasChanged()
    {
        using TemporaryDirectory temporary = new();
        string dataRoot = Path.Combine(temporary.Root, "data");
        string gameRoot = Path.Combine(temporary.Root, "Skyspine");
        Directory.CreateDirectory(gameRoot);
        GameInstallValidation game = ValidGame(gameRoot);
        InstalledControlSessionStore store = new(dataRoot);
        store.Ensure(game, "player-default", selectProfile: true);

        string path = InstalledControlSessionStore.RegistrationPath(dataRoot, gameRoot);
        JObject registration = JObject.Parse(File.ReadAllText(path));
        registration["expectedAssemblySha256"] = new string('b', 64);
        File.WriteAllText(path, registration.ToString(Formatting.Indented));

        bool loaded = store.TryLoad(game, out ActivationSession? session, out string error);

        Assert.False(loaded);
        Assert.Null(session);
        Assert.Contains("指纹已过期", error, StringComparison.Ordinal);
    }

    [Fact]
    public void Ensure_UpgradesV1RegistrationWithoutRotatingLocalCredential()
    {
        using TemporaryDirectory temporary = new();
        string dataRoot = Path.Combine(temporary.Root, "data");
        string gameRoot = Path.Combine(temporary.Root, "Skyspine");
        Directory.CreateDirectory(gameRoot);
        GameInstallValidation game = ValidGame(gameRoot);
        InstalledControlSessionStore store = new(dataRoot);
        ActivationSession original = store.Ensure(game, "player-default", selectProfile: true);
        string path = InstalledControlSessionStore.RegistrationPath(dataRoot, gameRoot);
        JObject registration = JObject.Parse(File.ReadAllText(path));
        registration["protocol"] = 1;
        File.WriteAllText(path, registration.ToString(Formatting.Indented));

        ActivationSession upgraded = store.Ensure(game, "player-default", selectProfile: false);
        JObject persisted = JObject.Parse(File.ReadAllText(path));

        Assert.Equal(original.Ticket.PipeName, upgraded.Ticket.PipeName);
        Assert.Equal(original.Ticket.Token, upgraded.Ticket.Token);
        Assert.Equal(Protocol.CurrentVersion, persisted.Value<int>("protocol"));
    }

    [Fact]
    public void Delete_RemovesOnlyTheSelectedGameRegistration()
    {
        using TemporaryDirectory temporary = new();
        string dataRoot = Path.Combine(temporary.Root, "data");
        string firstRoot = Path.Combine(temporary.Root, "First");
        string secondRoot = Path.Combine(temporary.Root, "Second");
        Directory.CreateDirectory(firstRoot);
        Directory.CreateDirectory(secondRoot);
        InstalledControlSessionStore store = new(dataRoot);
        store.Ensure(ValidGame(firstRoot), "player-default", selectProfile: true);
        store.Ensure(ValidGame(secondRoot), "player-default", selectProfile: true);

        store.Delete(firstRoot);

        Assert.False(File.Exists(InstalledControlSessionStore.RegistrationPath(dataRoot, firstRoot)));
        Assert.True(File.Exists(InstalledControlSessionStore.RegistrationPath(dataRoot, secondRoot)));
    }

    private static GameInstallValidation ValidGame(string gameRoot) => new()
    {
        GameRoot = gameRoot,
        ExecutablePath = Path.Combine(gameRoot, "Loopstructor 2_ Skyspine.exe"),
        AssemblySha256 = new string('a', 64)
    };

    private sealed class TemporaryDirectory : IDisposable
    {
        public TemporaryDirectory()
        {
            Root = Path.Combine(Path.GetTempPath(), "Loopstructor.AutoPlayer.Tests", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Root);
        }

        public string Root { get; }

        public void Dispose()
        {
            if (Directory.Exists(Root)) Directory.Delete(Root, recursive: true);
        }
    }
}
