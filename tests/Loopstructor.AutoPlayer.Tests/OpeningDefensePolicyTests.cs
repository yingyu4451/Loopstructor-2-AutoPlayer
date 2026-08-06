using Loopstructor.AutoPlayer.Core;
using Newtonsoft.Json.Linq;

namespace Loopstructor.AutoPlayer.Tests;

public sealed class OpeningDefensePolicyTests
{
    [Fact]
    public void SelectableNode_IsChosenBeforeOpeningDefense()
    {
        JObject affordances = JObject.FromObject(new
        {
            success = true,
            data = new
            {
                state = new
                {
                    gameOver = false,
                    wave = new { isInWaving = false },
                    blockers = Array.Empty<object>(),
                    map = new
                    {
                        mapOpen = true,
                        canStartWave = false,
                        canSelectNextNode = true,
                        selectableNodes = new[]
                        {
                            new
                            {
                                readyIndex = 2,
                                canPlayerSelect = true,
                                rewardEnum = "vehicle",
                                isBoss = false,
                                needFight = true
                            }
                        }
                    }
                }
            }
        });

        AutomationAction action = new DecisionEngine().DecideInGame(affordances, null, null);

        Assert.Equal("selectMapNode", action.Command);
        Assert.NotEqual("prepareDefaultDefense", action.Command);
        Assert.False(OpeningDefensePolicy.ShouldPrepare(
            inWave: false,
            blocked: false,
            defensePrepared: false,
            pendingSublevel: false,
            mapOpen: true,
            canStartWave: false));
    }

    [Fact]
    public void CommittedNode_PreparesDefenseBeforeStartWaveDecision()
    {
        Assert.True(OpeningDefensePolicy.ShouldPrepare(
            inWave: false,
            blocked: false,
            defensePrepared: false,
            pendingSublevel: false,
            mapOpen: false,
            canStartWave: true));
    }

    [Theory]
    [InlineData(true, false, false, false, false, true)]
    [InlineData(false, true, false, false, false, true)]
    [InlineData(false, false, true, false, false, true)]
    [InlineData(false, false, false, true, false, true)]
    [InlineData(false, false, false, false, true, true)]
    [InlineData(false, false, false, false, false, false)]
    public void UnsafeOrUncommittedState_DoesNotPrepareDefense(
        bool inWave,
        bool blocked,
        bool defensePrepared,
        bool pendingSublevel,
        bool mapOpen,
        bool canStartWave)
    {
        Assert.False(OpeningDefensePolicy.ShouldPrepare(
            inWave,
            blocked,
            defensePrepared,
            pendingSublevel,
            mapOpen,
            canStartWave));
    }
}
