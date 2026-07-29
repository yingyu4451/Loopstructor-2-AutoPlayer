using Loopstructor.AutoPlayer.Core;
using Newtonsoft.Json.Linq;

namespace Loopstructor.AutoPlayer.Tests;

public sealed class DecisionEngineInGameTests
{
    [Fact]
    public void GameOver_HasPriorityOverBattleAndAllBlockers()
    {
        JObject affordances = Affordances(new
        {
            gameOver = true,
            wave = new { isInWaving = true },
            blockers = Blockers("reward", "EventUI", "shop")
        });

        AutomationAction action = new DecisionEngine().DecideInGame(affordances, RewardWithObject(), EnabledEvent());

        Assert.Equal("wait", action.Command);
        Assert.Equal(AutomationStage.Completed, action.Stage);
    }

    [Fact]
    public void Battle_HasPriorityOverUiBlockers()
    {
        JObject affordances = Affordances(new
        {
            gameOver = false,
            wave = new { isInWaving = true, nodeType = "boss", enemy = new { remaining = 12 } },
            blockers = Blockers("reward", "EventUI", "shop")
        });

        AutomationAction action = new DecisionEngine().DecideInGame(affordances, RewardWithObject(), EnabledEvent());

        Assert.Equal("wait", action.Command);
        Assert.Equal(AutomationStage.Battle, action.Stage);
    }

    [Fact]
    public void Reward_HasPriorityOverOtherUiBlockers()
    {
        JObject affordances = Affordances(new
        {
            gameOver = false,
            wave = new { isInWaving = false },
            blockers = Blockers("disposablePreview", "UI_PopPanel_Option", "shop", "RepairUI", "EventUI", "reward")
        });

        AutomationAction action = new DecisionEngine().DecideInGame(affordances, RewardWithObject(), EnabledEvent());

        Assert.Equal("collectRewardObject", action.Command);
        Assert.Equal(AutomationStage.ManagingRewards, action.Stage);
    }

    [Theory]
    [InlineData("EventUI", "chooseWaveFunctionOption", AutomationStage.ManagingEvent)]
    [InlineData("RepairUI", "chooseWaveFunctionOption", AutomationStage.ManagingEvent)]
    [InlineData("shop", "closeShop", AutomationStage.ManagingShop)]
    [InlineData("UI_PopPanel_Option", "submitPopOption", AutomationStage.Recovery)]
    [InlineData("disposablePreview", "cancelDisposable", AutomationStage.Recovery)]
    public void EachUiBlocker_MapsToItsRecoveryAction(
        string blocker,
        string expectedCommand,
        AutomationStage expectedStage)
    {
        JObject affordances = Affordances(new
        {
            gameOver = false,
            wave = new { isInWaving = false },
            blockers = Blockers(blocker)
        });

        AutomationAction action = new DecisionEngine().DecideInGame(affordances, new JObject(), EnabledEvent());

        Assert.Equal(expectedCommand, action.Command);
        Assert.Equal(expectedStage, action.Stage);
    }

    [Fact]
    public void RewardSelection_IsDeterministicByKindThenRarityThenIndex()
    {
        JObject reward = JObject.FromObject(new
        {
            data = new
            {
                state = new
                {
                    activeRewardObjectCount = 0,
                    options = new object[]
                    {
                        new { index = 8, rewardKind = "vehicle", rewardRare = "normal" },
                        new { index = 5, rewardKind = "vehicle", rewardRare = "legend" },
                        new { index = 2, rewardKind = "vehicle", rewardRare = "legend" },
                        new { index = 0, rewardKind = "superModule", rewardRare = "legend" }
                    }
                }
            }
        });

        AutomationAction first = new DecisionEngine().DecideReward(reward);
        AutomationAction second = new DecisionEngine().DecideReward((JObject)reward.DeepClone());

        Assert.Equal("chooseRewardOption", first.Command);
        Assert.Equal(2, first.Arguments.Value<int>("index"));
        Assert.Equal(first.Arguments.Value<int>("index"), second.Arguments.Value<int>("index"));
    }

    [Fact]
    public void RewardObject_UsesStableInstanceIdInsteadOfReusableIndex()
    {
        JObject reward = JObject.FromObject(new
        {
            data = new
            {
                state = new
                {
                    activeRewardObjectCount = 1,
                    rewardObjects = new[] { new { index = 0, instanceId = -731, active = true } },
                    options = Array.Empty<object>()
                }
            }
        });

        AutomationAction action = new DecisionEngine().DecideReward(reward);

        Assert.Equal("collectRewardObject", action.Command);
        Assert.Equal(-731, action.Arguments.Value<int>("instanceId"));
        Assert.Null(action.Arguments["index"]);
    }

    [Fact]
    public void RouteSelection_IsDeterministicAndExcludesUnavailableNodes()
    {
        JObject affordances = Affordances(new
        {
            gameOver = false,
            wave = new { isInWaving = false },
            blockers = Array.Empty<object>(),
            map = new
            {
                canSelectNextNode = true,
                selectableNodes = new object[]
                {
                    new { readyIndex = 1, canPlayerSelect = true, rewardEnum = "vehicle", isBoss = false, needFight = true },
                    new { readyIndex = 7, canPlayerSelect = false, rewardEnum = "vehicle", isBoss = false, needFight = false },
                    new { readyIndex = 0, canPlayerSelect = true, rewardEnum = "vehicle", isBoss = false, needFight = true },
                    new { readyIndex = 2, canPlayerSelect = true, rewardEnum = "superModule", isBoss = false, needFight = false }
                }
            }
        });

        AutomationAction first = new DecisionEngine().DecideInGame(affordances, null, null);
        AutomationAction second = new DecisionEngine().DecideInGame((JObject)affordances.DeepClone(), null, null);

        Assert.Equal("selectMapNode", first.Command);
        Assert.Equal(0, first.Arguments.Value<int>("readyIndex"));
        Assert.Equal(first.Arguments.Value<int>("readyIndex"), second.Arguments.Value<int>("readyIndex"));
    }

    [Fact]
    public void CommittedRoute_StartsWaveEvenWhenReadyNodesRemainVisible()
    {
        JObject affordances = Affordances(new
        {
            gameOver = false,
            wave = new { isInWaving = false },
            blockers = Array.Empty<object>(),
            map = new
            {
                canStartWave = true,
                canSelectNextNode = true,
                selectableNodes = new[]
                {
                    new { readyIndex = 0, canPlayerSelect = true, rewardEnum = "vehicle" }
                }
            }
        });

        AutomationAction action = new DecisionEngine().DecideInGame(affordances, null, null);

        Assert.Equal("startWave", action.Command);
        Assert.Equal(AutomationStage.StartingWave, action.Stage);
    }

    [Theory]
    [MemberData(nameof(InvalidRouteCandidates))]
    public void RouteSelection_WithoutAnExplicitSelectableReadyIndex_Waits(object[] nodes)
    {
        JObject affordances = Affordances(new
        {
            gameOver = false,
            wave = new { isInWaving = false },
            blockers = Array.Empty<object>(),
            map = new
            {
                canStartWave = false,
                canSelectNextNode = true,
                selectableNodes = nodes
            }
        });

        AutomationAction action = new DecisionEngine().DecideInGame(affordances, null, null);

        Assert.Equal("wait", action.Command);
        Assert.Equal(AutomationStage.SelectingRoute, action.Stage);
    }

    public static TheoryData<object[]> InvalidRouteCandidates => new()
    {
        Array.Empty<object>(),
        new object[] { new { readyIndex = 0, canPlayerSelect = false } },
        new object[] { new { canPlayerSelect = true } },
        new object[] { new { readyIndex = -1, canPlayerSelect = true } },
        new object[] { new { readyIndex = 0 } }
    };

    [Fact]
    public void EventWithoutEnabledOptions_WaitsInsteadOfClicking()
    {
        JObject eventResult = JObject.FromObject(new
        {
            data = new
            {
                state = new
                {
                    eventPanel = new
                    {
                        options = new object[]
                        {
                            new { index = 0, conditionPass = false, buttonActive = true },
                            new { index = 1, conditionPass = true, buttonActive = false }
                        }
                    }
                }
            }
        });

        AutomationAction action = new DecisionEngine().DecideEvent(eventResult, "EventUI");

        Assert.Equal("wait", action.Command);
        Assert.Equal(AutomationStage.ManagingEvent, action.Stage);
    }

    private static JObject Affordances(object state) => JObject.FromObject(new
    {
        success = true,
        data = new { state }
    });

    private static object[] Blockers(params string[] keys) =>
        keys.Select(key => (object)new { key }).ToArray();

    private static JObject RewardWithObject() => JObject.FromObject(new
    {
        data = new
        {
            state = new
            {
                activeRewardObjectCount = 1,
                options = Array.Empty<object>()
            }
        }
    });

    private static JObject EnabledEvent() => JObject.FromObject(new
    {
        data = new
        {
            state = new
            {
                eventPanel = new { options = new[] { new { index = 3, conditionPass = true, buttonActive = true } } },
                repairPanel = new { options = new[] { new { index = 4, conditionPass = true, buttonActive = true } } }
            }
        }
    });
}
