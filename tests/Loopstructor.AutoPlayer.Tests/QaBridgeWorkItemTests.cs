using Loopstructor.QA.EditorBridge;
using Newtonsoft.Json.Linq;

namespace Loopstructor.AutoPlayer.Tests;

public sealed class QaBridgeWorkItemTests
{
    [Fact]
    public void Wait_WhenPendingWorkTimesOut_PreventsLateExecution()
    {
        bool called = false;
        QaBridgeWorkItem item = new(() =>
        {
            called = true;
            return new JObject();
        });

        bool completed = item.Wait(1);
        item.Execute();

        Assert.False(completed);
        Assert.False(called);
    }

    [Fact]
    public void Execute_CompletesTheWaitingRequest()
    {
        JObject expected = new() { ["success"] = true };
        QaBridgeWorkItem item = new(() => expected);

        item.Execute();

        Assert.True(item.Wait(100));
        Assert.Same(expected, item.Result);
        Assert.Null(item.Error);
    }
}
