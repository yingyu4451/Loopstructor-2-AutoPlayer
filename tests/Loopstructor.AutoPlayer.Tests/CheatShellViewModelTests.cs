using Loopstructor.AutoPlayer.Core;
using Loopstructor.AutoPlayer.Manager.UI;

namespace Loopstructor.AutoPlayer.Tests;

public sealed class CheatShellViewModelTests
{
    [Fact]
    public void RunState_UsesSafetyPriorityAndExposesBindablePresentation()
    {
        var viewModel = new CheatShellViewModel();
        var status = new AutoPlayerStatus
        {
            CheatAvailable = true,
            CheatModeEnabled = true,
            CheatUsed = true,
            RunState = AutoPlayerRunState.Running
        };

        viewModel.UpdateRunState(false, true, true, false, true, status, null);
        Assert.Equal("自动游玩中 · 仅可查看", viewModel.RunStateLabel);
        Assert.Contains("自动游玩期间", viewModel.RunStateDetail, StringComparison.Ordinal);

        viewModel.UpdateRunState(true, true, true, false, true, status, null);
        Assert.Equal("上次操作结果待确认", viewModel.RunStateLabel);
        Assert.Contains("避免重复修改", viewModel.RunStateDetail, StringComparison.Ordinal);
    }
}
