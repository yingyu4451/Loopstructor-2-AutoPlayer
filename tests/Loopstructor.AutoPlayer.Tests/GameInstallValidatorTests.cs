using Loopstructor.AutoPlayer.Manager.Models;
using Loopstructor.AutoPlayer.Manager.Services;

namespace Loopstructor.AutoPlayer.Tests;

public sealed class GameInstallValidatorTests
{
    [Fact]
    public async Task ValidateAsync_NonAsciiGamePathFailsBeforeFileSystemInspection()
    {
        string gameRoot = Path.GetFullPath(@"C:\游戏测试\Skyspine");
        GameInstallValidator validator = new();

        GameInstallValidation result = await validator.ValidateAsync(gameRoot);

        Assert.False(result.IsValid);
        string error = Assert.Single(result.Errors);
        Assert.Contains("非 ASCII", error, StringComparison.Ordinal);
        Assert.Contains("游戏本体本身不受此限制", error, StringComparison.Ordinal);
        Assert.Contains("仅含 ASCII 字符的路径", error, StringComparison.Ordinal);
        Assert.Contains("可包含英文字母、数字和空格", error, StringComparison.Ordinal);
        Assert.Contains(gameRoot, error, StringComparison.Ordinal);
        Assert.DoesNotContain("不存在", error, StringComparison.Ordinal);
    }
}
