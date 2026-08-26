using Loopstructor.AutoPlayer.Manager.Services;

namespace Loopstructor.AutoPlayer.Tests;

public sealed class ManagerSingleInstanceTests
{
    [Fact]
    public void SecondInstance_NotifiesExistingPrimaryWithoutTakingOwnership()
    {
        using ManagerSingleInstance primary = ManagerSingleInstance.Create();
        Assert.True(primary.IsPrimary);
        using ManualResetEventSlim activated = new(false);
        primary.StartListening(activated.Set);

        using ManagerSingleInstance secondary = ManagerSingleInstance.Create();
        Assert.False(secondary.IsPrimary);
        Assert.True(secondary.NotifyPrimary());
        Assert.True(activated.Wait(TimeSpan.FromSeconds(3)));
    }
}
