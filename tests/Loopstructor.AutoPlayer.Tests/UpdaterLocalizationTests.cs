namespace Loopstructor.AutoPlayer.Tests;

public sealed class UpdaterLocalizationTests
{
    [Fact]
    public void SystemException_WithChinesePath_DoesNotExposeEnglishMessage()
    {
        IOException exception = new(@"Access denied: D:\游戏\release");

        string message = Loopstructor.AutoPlayer.Updater.Program.GetUserFacingFailureMessage(exception);

        Assert.Equal("更新文件读写失败，请关闭正在占用文件的程序后重试。", message);
        Assert.DoesNotContain("Access denied", message, StringComparison.OrdinalIgnoreCase);
    }
}
