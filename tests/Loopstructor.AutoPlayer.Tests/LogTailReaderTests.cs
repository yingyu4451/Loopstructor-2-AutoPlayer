using Loopstructor.AutoPlayer.Manager.Services;

namespace Loopstructor.AutoPlayer.Tests;

public sealed class LogTailReaderTests
{
    [Fact]
    public void Reset_StartAtEnd_SkipsPreviousProcessHistory()
    {
        using TemporaryDirectory temporary = new();
        string path = Path.Combine(temporary.Root, "Player.log");
        File.WriteAllLines(path, new[] { "old warning", "old error" });
        LogTailReader reader = new();

        reader.Reset(path, startAtEnd: true);
        File.AppendAllLines(path, new[] { "current info" });

        Assert.Equal(new[] { "current info" }, reader.ReadAvailable());
    }

    [Fact]
    public void Reset_FromBeginning_ReadsNewManagerLaunchLog()
    {
        using TemporaryDirectory temporary = new();
        string path = Path.Combine(temporary.Root, "Player.log");
        File.WriteAllLines(path, new[] { "current startup", "current ready" });
        LogTailReader reader = new();

        reader.Reset(path);

        Assert.Equal(new[] { "current startup", "current ready" }, reader.ReadAvailable());
    }

    [Fact]
    public void ReadAvailable_RewindsWhenCurrentProcessTruncatesLog()
    {
        using TemporaryDirectory temporary = new();
        string path = Path.Combine(temporary.Root, "Player.log");
        File.WriteAllText(path, new string('x', 128) + Environment.NewLine);
        LogTailReader reader = new();
        reader.Reset(path);
        Assert.Single(reader.ReadAvailable());

        File.WriteAllText(path, "new process" + Environment.NewLine);

        Assert.Equal(new[] { "new process" }, reader.ReadAvailable());
    }

    private sealed class TemporaryDirectory : IDisposable
    {
        public TemporaryDirectory()
        {
            Root = Path.Combine(Path.GetTempPath(), "Loopstructor.AutoPlayer.LogTests", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Root);
        }

        public string Root { get; }

        public void Dispose()
        {
            if (Directory.Exists(Root)) Directory.Delete(Root, recursive: true);
        }
    }
}
