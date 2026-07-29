using System.Diagnostics;
using Loopstructor.AutoPlayer.Manager.UI;

namespace Loopstructor.AutoPlayer.Tests;

public sealed class GameProcessValidationTests
{
    [Fact]
    public void ValidateGameProcess_AcceptsCurrentProcessAtExactPath()
    {
        using Process process = Process.GetCurrentProcess();
        string executable = process.MainModule!.FileName;

        bool valid = MainForm.ValidateGameProcess(process.Id, executable, out string error);

        Assert.True(valid, error);
        Assert.Empty(error);
    }

    [Fact]
    public void ValidateGameProcess_RejectsMissingOrWrongProcessIdentity()
    {
        Assert.False(MainForm.ValidateGameProcess(0, "missing.exe", out string missingError));
        Assert.Contains("PID", missingError, StringComparison.Ordinal);

        using Process process = Process.GetCurrentProcess();
        string wrongExecutable = Path.Combine(Path.GetTempPath(), "different-game.exe");
        Assert.False(MainForm.ValidateGameProcess(process.Id, wrongExecutable, out string wrongPathError));
        Assert.Contains("不属于", wrongPathError, StringComparison.Ordinal);
    }
}
