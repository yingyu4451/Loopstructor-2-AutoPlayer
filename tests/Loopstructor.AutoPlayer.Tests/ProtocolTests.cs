using Loopstructor.AutoPlayer.Core;
using Newtonsoft.Json;

namespace Loopstructor.AutoPlayer.Tests;

public sealed class ProtocolTests
{
    [Fact]
    public void BridgeHello_GameProcessIdIsBackwardCompatibleWithoutProtocolChange()
    {
        BridgeHello legacy = JsonConvert.DeserializeObject<BridgeHello>("{\"ProtocolVersion\":1,\"PluginVersion\":\"0.1.0\"}")!;
        string current = JsonConvert.SerializeObject(new BridgeHello
        {
            ProtocolVersion = Protocol.CurrentVersion,
            GameProcessId = 4321
        });

        Assert.Equal(1, Protocol.CurrentVersion);
        Assert.Equal(0, legacy.GameProcessId);
        Assert.Contains("\"GameProcessId\":4321", current, StringComparison.Ordinal);
    }

    [Fact]
    public void HashGameRoot_IsStableAcrossCaseAndTrailingSeparators()
    {
        string root = Path.Combine(Path.GetTempPath(), "LoopstructorHashTest", "Game");
        string variant = root.ToLowerInvariant() + Path.DirectorySeparatorChar;

        string first = Protocol.HashGameRoot(root);
        string second = Protocol.HashGameRoot(variant);

        Assert.Equal(first, second);
        Assert.Matches("^[0-9a-f]{64}$", first);
    }

    [Fact]
    public void HashGameRoot_DiffersForDifferentInstallations()
    {
        string basePath = Path.Combine(Path.GetTempPath(), "LoopstructorHashTest");

        string first = Protocol.HashGameRoot(Path.Combine(basePath, "GameA"));
        string second = Protocol.HashGameRoot(Path.Combine(basePath, "GameB"));

        Assert.NotEqual(first, second);
    }

    [Fact]
    public void GetTicketPath_IsStableAndOwnedByApplicationTicketDirectory()
    {
        string gameRoot = Path.Combine(Path.GetTempPath(), "LoopstructorTicketTest", "Game");
        string expectedDirectory = Path.GetFullPath(Path.Combine(Protocol.DataRoot, "tickets"));
        string expectedPrefix = Protocol.HashGameRoot(gameRoot).Substring(0, 16);

        string first = Protocol.GetTicketPath(gameRoot);
        string second = Protocol.GetTicketPath(gameRoot + Path.DirectorySeparatorChar);

        Assert.Equal(first, second);
        Assert.Equal(expectedDirectory, Path.GetFullPath(Path.GetDirectoryName(first)!));
        Assert.Equal("launch-" + expectedPrefix + ".json", Path.GetFileName(first));
    }
}
