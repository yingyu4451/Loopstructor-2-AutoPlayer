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
        Assert.Equal("战斗中：首领节点，剩余 12 个敌人。", action.Reason);
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

    [Theory]
    [InlineData("EventUI", "事件界面")]
    [InlineData("RepairUI", "修理界面")]
    public void EventAction_LocalizesDisplayTextWithoutChangingPanelProtocol(
        string panel,
        string expectedDisplayName)
    {
        AutomationAction action = new DecisionEngine().DecideEvent(EnabledEvent(), panel);

        Assert.Equal("chooseWaveFunctionOption", action.Command);
        Assert.Equal(panel, action.Arguments.Value<string>("panel"));
        Assert.NotEqual(0, action.Arguments.Value<int>("panelInstanceId"));
        Assert.NotEqual(0, action.Arguments.Value<int>("instanceId"));
        Assert.False(string.IsNullOrWhiteSpace(action.Arguments.Value<string>("optionIdentity")));
        Assert.Contains(expectedDisplayName, action.Reason, StringComparison.Ordinal);
        Assert.DoesNotContain(panel, action.Reason, StringComparison.Ordinal);
    }

    [Fact]
    public void RewardSelection_IsDeterministicByStrategicValueThenIndex()
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
                        new { index = 5, rewardKind = "vehicle", rewardRare = "epic" },
                        new { index = 2, rewardKind = "vehicle", rewardRare = "epic" },
                        new { index = 0, rewardKind = "superModule", rewardRare = "epic" }
                    }
                }
            }
        });

        AutomationAction first = new DecisionEngine().DecideReward(reward);
        AutomationAction second = new DecisionEngine().DecideReward((JObject)reward.DeepClone());

        Assert.Equal("chooseRewardOption", first.Command);
        Assert.Equal(0, first.Arguments.Value<int>("index"));
        Assert.Equal(first.Arguments.Value<int>("index"), second.Arguments.Value<int>("index"));
    }

    [Fact]
    public void RewardSelection_NeverChoosesInactiveOption()
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
                        new { index = 0, buttonActive = false, rewardKind = "vehicle", rewardRare = "legend" },
                        new { index = 4, buttonActive = true, rewardKind = "money", rewardRare = "normal" }
                    }
                }
            }
        });

        AutomationAction action = new DecisionEngine().DecideReward(reward);

        Assert.Equal("chooseRewardOption", action.Command);
        Assert.Equal(4, action.Arguments.Value<int>("index"));
    }

    [Fact]
    public void RewardSelection_WithoutUsableIndexedOption_WaitsSafely()
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
                        new { index = 0, buttonActive = false, rewardKind = "vehicle" },
                        new { buttonActive = true, rewardKind = "vehicle" }
                    }
                }
            }
        });

        AutomationAction action = new DecisionEngine().DecideReward(reward);

        Assert.Equal("wait", action.Command);
        Assert.Equal(AutomationStage.ManagingRewards, action.Stage);
        Assert.Empty(action.Arguments);
    }

    [Fact]
    public void RewardSelection_SkipsBlockedOptionAndChoosesAvailableAlternative()
    {
        JObject reward = RewardOptions(
            new { index = 0, buttonActive = true, canAcquire = false, rewardKind = "vehicle", rewardRare = "epic" },
            new { index = 1, buttonActive = true, canAcquire = true, rewardKind = "money", rewardRare = "normal" });

        AutomationAction action = new DecisionEngine().DecideReward(reward);

        Assert.Equal("chooseRewardOption", action.Command);
        Assert.Equal(1, action.Arguments.Value<int>("index"));
    }

    [Fact]
    public void RewardSelection_WhenAllOptionsBlocked_SkipsOptionalOpportunity()
    {
        JObject reward = RewardOptionsWithState(
            "phase-17",
            canSkip: true,
            currentQueueMandatory: false,
            new { index = 0, buttonActive = true, canAcquire = false, rewardKind = "disposable" },
            new { index = 1, buttonActive = true, canAcquire = false, rewardKind = "superModule" });

        AutomationAction action = new DecisionEngine().DecideReward(reward);

        Assert.Equal("skipReward", action.Command);
        Assert.Equal("phase-17", action.Arguments.Value<string>("phaseToken"));
    }

    [Fact]
    public void RewardSelection_WhenAllOptionsBlockedAndMandatory_WaitsSafely()
    {
        JObject reward = RewardOptionsWithState(
            "phase-18",
            canSkip: false,
            currentQueueMandatory: true,
            new { index = 0, buttonActive = true, canAcquire = false, rewardKind = "disposable" });

        AutomationAction action = new DecisionEngine().DecideReward(reward);

        Assert.Equal("wait", action.Command);
        Assert.Contains("强制选择", action.Reason);
    }

    [Fact]
    public void RewardSelection_WithVehicleContext_PrefersImmediateSameTypeMerge()
    {
        JObject reward = RewardOptions(
            new
            {
                index = 8,
                buttonActive = true,
                rewardKind = "vehicle",
                rewardRare = "legend",
                vehicleType = "Link_IceRipple_L2",
                effectiveFetters = Array.Empty<object>()
            },
            new
            {
                index = 3,
                buttonActive = true,
                rewardKind = "vehicle",
                rewardRare = "normal",
                vehicleType = "Link_ElectricGun_L1",
                effectiveFetters = Array.Empty<object>()
            });
        JObject vehicles = VehicleState(
            Vehicle("Link_ElectricGun_L1", 1, true),
            Vehicle("Link_ElectricGun_L1", 1, false));

        AutomationAction action = new DecisionEngine().DecideReward(reward, vehicles);

        Assert.Equal("chooseRewardOption", action.Command);
        Assert.Equal(3, action.Arguments.Value<int>("index"));
    }

    [Fact]
    public void RewardSelection_WithVehicleContext_PrefersHigherVehicleLevel()
    {
        JObject reward = RewardOptions(
            new
            {
                index = 0,
                buttonActive = true,
                rewardKind = "vehicle",
                rewardRare = "legend",
                vehicleType = "Link_ElectricGun_L1",
                effectiveFetters = Array.Empty<object>()
            },
            new
            {
                index = 5,
                buttonActive = true,
                rewardKind = "vehicle",
                rewardRare = "normal",
                vehicleType = "Link_IceRipple_L2",
                effectiveFetters = Array.Empty<object>()
            });

        AutomationAction action = new DecisionEngine().DecideReward(reward, VehicleState());

        Assert.Equal("chooseRewardOption", action.Command);
        Assert.Equal(5, action.Arguments.Value<int>("index"));
    }

    [Fact]
    public void RewardSelection_WithInvalidVehicleContext_UsesContextFreeStrategicScore()
    {
        JObject reward = RewardOptions(
            new
            {
                index = 0,
                buttonActive = true,
                rewardKind = "vehicle",
                rewardRare = "legend",
                vehicleType = "Link_ElectricGun_L1"
            },
            new
            {
                index = 5,
                buttonActive = true,
                rewardKind = "vehicle",
                rewardRare = "normal",
                vehicleType = "Link_IceRipple_L2"
            });
        JObject invalidVehicleResult = JObject.FromObject(new { success = true, data = new { state = new { } } });

        AutomationAction action = new DecisionEngine().DecideReward(reward, invalidVehicleResult);

        Assert.Equal("chooseRewardOption", action.Command);
        Assert.Equal(5, action.Arguments.Value<int>("index"));
    }

    [Fact]
    public void RewardSelection_WithVehicleContext_PrefersCurrentMainFetter()
    {
        JObject reward = RewardOptions(
            new
            {
                index = 2,
                buttonActive = true,
                rewardKind = "vehicle",
                rewardRare = "normal",
                vehicleType = "Shell_SoulChaser_L1",
                effectiveFetters = new[] { new { fetterEnum = "Energy", level = 1, count = 1, isActual = true } }
            },
            new
            {
                index = 7,
                buttonActive = true,
                rewardKind = "vehicle",
                rewardRare = "normal",
                vehicleType = "Shell_Projectile_L1",
                effectiveFetters = new[] { new { fetterEnum = "Poison", level = 1, count = 1, isActual = true } }
            });
        JObject vehicles = VehicleState(new
        {
            vehicleType = "Link_ElectricGun_L1",
            type = "Link_ElectricGun_L1",
            level = 1,
            active = true,
            inBag = false,
            isFixedHead = false,
            fetters = new object[]
            {
                new { fetterEnum = "Poison", level = 1, count = 3 },
                new { fetterEnum = "Energy", level = 1, count = 1 }
            }
        });

        AutomationAction first = new DecisionEngine().DecideReward(reward, vehicles);
        AutomationAction second = new DecisionEngine().DecideReward((JObject)reward.DeepClone(), (JObject)vehicles.DeepClone());

        Assert.Equal("chooseRewardOption", first.Command);
        Assert.Equal(7, first.Arguments.Value<int>("index"));
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
    public void RewardObject_WithoutStableTarget_WaitsInsteadOfInventingIndex()
    {
        JObject reward = JObject.FromObject(new
        {
            data = new
            {
                state = new
                {
                    activeRewardObjectCount = 1,
                    rewardObjects = Array.Empty<object>(),
                    options = Array.Empty<object>()
                }
            }
        });

        AutomationAction action = new DecisionEngine().DecideReward(reward);

        Assert.Equal("wait", action.Command);
        Assert.Empty(action.Arguments);
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
        Assert.Equal(2, first.Arguments.Value<int>("readyIndex"));
        Assert.Equal(first.Arguments.Value<int>("readyIndex"), second.Arguments.Value<int>("readyIndex"));
    }

    [Fact]
    public void RouteSelection_UsesCurrentMapDropCountsInsteadOfReadyIndex()
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
                    new
                    {
                        readyIndex = 0,
                        canPlayerSelect = true,
                        rewardEnum = "Common",
                        needFight = true,
                        totalEnemyAmount = 12,
                        dropCounts = new { vehicle = 0, catapult = 0, money = 3, disposable = 0, superModule = 0 },
                        vehicleTags = Array.Empty<string>()
                    },
                    new
                    {
                        readyIndex = 3,
                        canPlayerSelect = true,
                        rewardEnum = "Common",
                        needFight = true,
                        totalEnemyAmount = 30,
                        dropCounts = new { vehicle = 1, catapult = 0, money = 0, disposable = 0, superModule = 0 },
                        vehicleTags = new[] { "Fetter_Link" }
                    }
                }
            }
        });

        AutomationAction action = new DecisionEngine().DecideInGame(affordances, null, null);

        Assert.Equal("selectMapNode", action.Command);
        Assert.Equal(3, action.Arguments.Value<int>("readyIndex"));
    }

    [Fact]
    public void RouteSelection_PrefersBuildOverLowValueCombatRewards()
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
                    new
                    {
                        readyIndex = 0,
                        canPlayerSelect = true,
                        rewardEnum = "Common",
                        needFight = true,
                        totalEnemyAmount = 20,
                        dropCounts = new { vehicle = 0, catapult = 0, money = 2, disposable = 0, superModule = 0 }
                    },
                    new
                    {
                        readyIndex = 2,
                        canPlayerSelect = true,
                        rewardEnum = "Build",
                        needFight = false,
                        totalEnemyAmount = 0,
                        dropCounts = new { vehicle = 0, catapult = 0, money = 0, disposable = 0, superModule = 0 }
                    }
                }
            }
        });

        AutomationAction action = new DecisionEngine().DecideInGame(affordances, null, null);

        Assert.Equal(2, action.Arguments.Value<int>("readyIndex"));
    }

    [Fact]
    public void RouteSelection_AvoidsShopUntilPurchasingIsImplemented()
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
                    new
                    {
                        readyIndex = 0,
                        canPlayerSelect = true,
                        rewardEnum = "Shop",
                        needFight = false,
                        totalEnemyAmount = 0,
                        dropCounts = new { vehicle = 0, catapult = 0, money = 0, disposable = 0, superModule = 0 }
                    },
                    new
                    {
                        readyIndex = 4,
                        canPlayerSelect = true,
                        rewardEnum = "Common",
                        needFight = true,
                        totalEnemyAmount = 20,
                        dropCounts = new { vehicle = 0, catapult = 0, money = 1, disposable = 0, superModule = 0 }
                    }
                }
            }
        });

        AutomationAction action = new DecisionEngine().DecideInGame(affordances, null, null);

        Assert.Equal("selectMapNode", action.Command);
        Assert.Equal(4, action.Arguments.Value<int>("readyIndex"));
    }

    [Fact]
    public void RouteSelection_UsesEnemyBurdenForEquivalentPowerRewards()
    {
        static object Node(int readyIndex, string rewardEnum, int enemyCount) => new
        {
            readyIndex,
            canPlayerSelect = true,
            rewardEnum,
            needFight = true,
            totalEnemyAmount = enemyCount,
            dropCounts = new { vehicle = 1, catapult = 0, money = 0, disposable = 0, superModule = 0 },
            vehicleTags = new[] { "Fetter_Link" }
        };

        JObject affordances = Affordances(new
        {
            gameOver = false,
            wave = new { isInWaving = false },
            blockers = Array.Empty<object>(),
            map = new
            {
                canSelectNextNode = true,
                selectableNodes = new[]
                {
                    Node(1, "Elite", 100),
                    Node(4, "Common", 20)
                }
            }
        });

        AutomationAction action = new DecisionEngine().DecideInGame(affordances, null, null);

        Assert.Equal(4, action.Arguments.Value<int>("readyIndex"));
    }

    [Fact]
    public void RouteSelection_AvoidsSingleVehicleRewardBehindTwoHundredFiftyEnemies()
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
                    new
                    {
                        readyIndex = 0,
                        canPlayerSelect = true,
                        rewardEnum = "Common",
                        needFight = true,
                        isBoss = false,
                        totalEnemyAmount = 250,
                        dropCounts = new { vehicle = 1, catapult = 0, money = 0, disposable = 0, superModule = 0 },
                        vehicleTags = new[] { "Fetter_Link" }
                    },
                    new
                    {
                        readyIndex = 4,
                        canPlayerSelect = true,
                        rewardEnum = "RandomEvent",
                        needFight = false,
                        isBoss = false,
                        totalEnemyAmount = 0,
                        dropCounts = new { vehicle = 0, catapult = 0, money = 0, disposable = 0, superModule = 0 }
                    }
                }
            }
        });

        AutomationAction action = new DecisionEngine().DecideInGame(affordances, null, null);

        Assert.Equal("selectMapNode", action.Command);
        Assert.Equal(4, action.Arguments.Value<int>("readyIndex"));
    }

    [Fact]
    public void RouteSelection_LowRiskVehicleRewardStillBeatsSafeEvent()
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
                    new
                    {
                        readyIndex = 3,
                        canPlayerSelect = true,
                        rewardEnum = "Common",
                        needFight = true,
                        isBoss = false,
                        totalEnemyAmount = 30,
                        dropCounts = new { vehicle = 1, catapult = 0, money = 0, disposable = 0, superModule = 0 },
                        vehicleTags = new[] { "Fetter_Link" }
                    },
                    new
                    {
                        readyIndex = 1,
                        canPlayerSelect = true,
                        rewardEnum = "RandomEvent",
                        needFight = false,
                        isBoss = false,
                        totalEnemyAmount = 0,
                        dropCounts = new { vehicle = 0, catapult = 0, money = 0, disposable = 0, superModule = 0 }
                    }
                }
            }
        });

        AutomationAction action = new DecisionEngine().DecideInGame(affordances, null, null);

        Assert.Equal("selectMapNode", action.Command);
        Assert.Equal(3, action.Arguments.Value<int>("readyIndex"));
    }

    [Fact]
    public void RouteSelection_AvoidsSingleVehicleBossWhenSafeEventExists()
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
                    new
                    {
                        readyIndex = 2,
                        canPlayerSelect = true,
                        rewardEnum = "Boss",
                        needFight = true,
                        isBoss = true,
                        totalEnemyAmount = 20,
                        dropCounts = new { vehicle = 1, catapult = 0, money = 0, disposable = 0, superModule = 0 },
                        vehicleTags = new[] { "Fetter_Link" }
                    },
                    new
                    {
                        readyIndex = 6,
                        canPlayerSelect = true,
                        rewardEnum = "RandomEvent",
                        needFight = false,
                        isBoss = false,
                        totalEnemyAmount = 0,
                        dropCounts = new { vehicle = 0, catapult = 0, money = 0, disposable = 0, superModule = 0 }
                    }
                }
            }
        });

        AutomationAction action = new DecisionEngine().DecideInGame(affordances, null, null);

        Assert.Equal("selectMapNode", action.Command);
        Assert.Equal(6, action.Arguments.Value<int>("readyIndex"));
    }

    [Fact]
    public void RouteSelection_UsesRewardQuantityBeforeReadyIndex()
    {
        static object Node(int readyIndex, int vehicleCount) => new
        {
            readyIndex,
            canPlayerSelect = true,
            rewardEnum = "Common",
            needFight = true,
            totalEnemyAmount = 20,
            dropCounts = new { vehicle = vehicleCount, catapult = 0, money = 0, disposable = 0, superModule = 0 },
            vehicleTags = new[] { "Fetter_Link" }
        };

        JObject affordances = Affordances(new
        {
            gameOver = false,
            wave = new { isInWaving = false },
            blockers = Array.Empty<object>(),
            map = new
            {
                canSelectNextNode = true,
                selectableNodes = new[]
                {
                    Node(0, 1),
                    Node(5, 2)
                }
            }
        });

        AutomationAction action = new DecisionEngine().DecideInGame(affordances, null, null);

        Assert.Equal(5, action.Arguments.Value<int>("readyIndex"));
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

    [Fact]
    public void RepairSelection_PrefersSelfContainedFortRepairOverSecondaryInteractions()
    {
        JObject eventResult = JObject.FromObject(new
        {
            data = new
            {
                state = new
                {
                    repairPanel = new
                    {
                        options = new object[]
                        {
                            new
                            {
                                index = 0,
                                conditionPass = true,
                                buttonActive = true,
                                behaviourTypeIds = new[]
                                {
                                    "MetroTD.UISystem.OpenUIPanelBehaviour",
                                    "MetroTD.UISystem.WaveFunctionBehaviour"
                                }
                            },
                            new
                            {
                                index = 1,
                                conditionPass = true,
                                buttonActive = true,
                                behaviourTypes = new[]
                                {
                                    "DisposableInvokeBehaviour",
                                    "WaveFunctionBehaviour"
                                }
                            },
                            new
                            {
                                index = 3,
                                conditionPass = true,
                                buttonActive = true,
                                behaviourTypeIds = new[]
                                {
                                    "MetroTD.RoomSystem.ResourcesControl.ResourcesControl_MainHp",
                                    "MetroTD.UISystem.WaveFunctionBehaviour",
                                    "MetroTD.RoomSystem.OverWaveBehaviour"
                                }
                            }
                        }
                    }
                }
            }
        });

        AutomationAction action = new DecisionEngine().DecideEvent(eventResult, "RepairUI");

        Assert.Equal("chooseWaveFunctionOption", action.Command);
        Assert.Equal(3, action.Arguments.Value<int>("index"));
    }

    [Fact]
    public void EventSelection_PrefersSelfContainedEndingOptionOverSecondaryFlows()
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
                            new
                            {
                                index = 0,
                                conditionPass = true,
                                buttonActive = true,
                                behaviourTypeIds = new[]
                                {
                                    "MetroTD.UISystem.WaveFunctionOptionFlowBehaviour",
                                    "MetroTD.UISystem.WaveFunctionBehaviour",
                                    "MetroTD.RoomSystem.OverWaveBehaviour"
                                }
                            },
                            new
                            {
                                index = 1,
                                conditionPass = true,
                                buttonActive = true,
                                behaviourTypes = new[]
                                {
                                    "OpenUIPanelBehaviour",
                                    "WaveFunctionBehaviour"
                                }
                            },
                            new
                            {
                                index = 2,
                                conditionPass = true,
                                buttonActive = true,
                                behaviourTypes = new[]
                                {
                                    "DisposableInvokeBehaviour",
                                    "OverWaveBehaviour"
                                }
                            },
                            new
                            {
                                index = 3,
                                conditionPass = true,
                                buttonActive = true,
                                behaviourTypeIds = new[]
                                {
                                    "MetroTD.UISystem.WaveFunctionBehaviour",
                                    "MetroTD.RoomSystem.OverWaveBehaviour"
                                }
                            }
                        }
                    }
                }
            }
        });

        AutomationAction action = new DecisionEngine().DecideEvent(eventResult, "EventUI");

        Assert.Equal("chooseWaveFunctionOption", action.Command);
        Assert.Equal(3, action.Arguments.Value<int>("index"));
    }

    private static JObject Affordances(object state) => JObject.FromObject(new
    {
        success = true,
        data = new { state }
    });

    private static object[] Blockers(params string[] keys) =>
        keys.Select(key => (object)new { key }).ToArray();

    private static JObject RewardOptions(params object[] options) => JObject.FromObject(new
    {
        data = new
        {
            state = new
            {
                activeRewardObjectCount = 0,
                options
            }
        }
    });

    private static JObject RewardOptionsWithState(
        string phaseToken,
        bool canSkip,
        bool currentQueueMandatory,
        params object[] options) => JObject.FromObject(new
    {
        data = new
        {
            state = new
            {
                activeRewardObjectCount = 0,
                phaseToken,
                canSkip,
                currentQueueMandatory,
                options
            }
        }
    });

    private static JObject VehicleState(params object[] vehicles) => JObject.FromObject(new
    {
        data = new { state = new { vehicles } }
    });

    private static object Vehicle(string vehicleType, int level, bool inBag) => new
    {
        vehicleType,
        type = vehicleType,
        level,
        active = !inBag,
        inBag,
        isFixedHead = false,
        fetters = Array.Empty<object>()
    };

    private static JObject RewardWithObject() => JObject.FromObject(new
    {
        data = new
        {
            state = new
            {
                activeRewardObjectCount = 1,
                rewardObjects = new[] { new { index = 0, instanceId = -101, active = true } },
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
                eventPanel = new
                {
                    panelInstanceId = 71,
                    options = new[]
                    {
                        new
                        {
                            index = 3,
                            instanceId = 301,
                            conditionPass = true,
                            buttonActive = true,
                            displayText = "继续事件"
                        }
                    }
                },
                repairPanel = new
                {
                    panelInstanceId = 72,
                    options = new[]
                    {
                        new
                        {
                            index = 4,
                            instanceId = 401,
                            conditionPass = true,
                            buttonActive = true,
                            displayText = "完成修整"
                        }
                    }
                }
            }
        }
    });
}
