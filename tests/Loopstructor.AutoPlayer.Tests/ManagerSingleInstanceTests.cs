using Loopstructor.AutoPlayer.Manager.Services;

namespace Loopstructor.AutoPlayer.Tests;

public sealed class ManagerSingleInstanceTests
{
    [Fact]
    public void SecondInstance_NotifiesExistingPrimaryWithoutTakingOwnership()
    {
        string isolationKey = "test-" + Guid.NewGuid().ToString("N");
        using ManagerSingleInstance primary = ManagerSingleInstance.Create(isolationKey);
        Assert.True(primary.IsPrimary);
        using ManualResetEventSlim activated = new(false);
        primary.StartListening(activated.Set);

        using ManagerSingleInstance secondary = ManagerSingleInstance.Create(isolationKey);
        Assert.False(secondary.IsPrimary);
        Assert.True(secondary.NotifyPrimary());
        Assert.True(activated.Wait(TimeSpan.FromSeconds(3)));
    }
}
