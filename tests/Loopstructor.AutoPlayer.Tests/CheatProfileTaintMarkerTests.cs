using Loopstructor.AutoPlayer.Core;
using Newtonsoft.Json.Linq;

namespace Loopstructor.AutoPlayer.Tests;

public sealed class CheatProfileTaintMarkerTests
{
    [Fact]
    public void TryMark_CreatesPersistentMarkerWithoutOverwritingFirstWrite()
    {
        string profileRoot = NewTemporaryPath();
        try
        {
            Assert.False(CheatProfileTaintMarker.IsTainted(profileRoot));
            Assert.True(CheatProfileTaintMarker.TryMark(
                profileRoot,
                "request-first",
                CheatCommands.GrantVehicle,
                out string firstError), firstError);
            Assert.True(CheatProfileTaintMarker.IsTainted(profileRoot));

            string markerPath = CheatProfileTaintMarker.GetPath(profileRoot);
            string firstPayload = File.ReadAllText(markerPath);
            JObject marker = JObject.Parse(firstPayload);
            Assert.Equal(1, marker.Value<int>("schemaVersion"));
            Assert.Equal("request-first", marker.Value<string>("requestId"));
            Assert.Equal(CheatCommands.GrantVehicle, marker.Value<string>("command"));

            Assert.True(CheatProfileTaintMarker.TryMark(
                profileRoot,
                "request-second",
                CheatCommands.SpawnEnemy,
                out string secondError), secondError);
            Assert.Equal(firstPayload, File.ReadAllText(markerPath));
        }
        finally
        {
            DeleteTemporaryPath(profileRoot);
        }
    }

    [Fact]
    public void IsTainted_TreatsMalformedOrDirectoryMarkerAsTainted()
    {
        string malformedRoot = NewTemporaryPath();
        string directoryRoot = NewTemporaryPath();
        try
        {
            Directory.CreateDirectory(malformedRoot);
            File.WriteAllText(CheatProfileTaintMarker.GetPath(malformedRoot), "incomplete");
            Assert.True(CheatProfileTaintMarker.IsTainted(malformedRoot));

            Directory.CreateDirectory(CheatProfileTaintMarker.GetPath(directoryRoot));
            Assert.True(CheatProfileTaintMarker.IsTainted(directoryRoot));
            Assert.True(CheatProfileTaintMarker.TryMark(
                directoryRoot,
                "request",
                CheatCommands.ClearEnemies,
                out string error), error);
        }
        finally
        {
            DeleteTemporaryPath(malformedRoot);
            DeleteTemporaryPath(directoryRoot);
        }
    }

    [Fact]
    public void TryMark_WhenProfileRootCannotBeCreated_FailsClosedWithoutMarker()
    {
        string profileRoot = NewTemporaryPath();
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(profileRoot)!);
            File.WriteAllText(profileRoot, "this path is a file");

            Assert.False(CheatProfileTaintMarker.TryMark(
                profileRoot,
                "request",
                CheatCommands.ModifyEnemy,
                out string error));
            Assert.NotEmpty(error);
            Assert.True(CheatProfileTaintMarker.IsTainted(profileRoot));
        }
        finally
        {
            DeleteTemporaryPath(profileRoot);
        }
    }

    [Fact]
    public void InvalidProfilePath_IsTaintedForReadsButCannotAuthorizeWrite()
    {
        string invalidProfileRoot = "invalid" + '\0' + "profile";

        Assert.True(CheatProfileTaintMarker.IsTainted(invalidProfileRoot));
        Assert.False(CheatProfileTaintMarker.TryMark(
            invalidProfileRoot,
            "request",
            CheatCommands.EndWave,
            out string error));
        Assert.NotEmpty(error);
    }

    private static string NewTemporaryPath() => Path.Combine(
        Path.GetTempPath(),
        "Loopstructor.AutoPlayer.Tests",
        Guid.NewGuid().ToString("N"));

    private static void DeleteTemporaryPath(string path)
    {
        if (Directory.Exists(path)) Directory.Delete(path, true);
        else if (File.Exists(path)) File.Delete(path);
    }
}
