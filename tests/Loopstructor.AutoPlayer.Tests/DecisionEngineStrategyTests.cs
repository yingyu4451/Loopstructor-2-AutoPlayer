using Loopstructor.AutoPlayer.Core;
using Newtonsoft.Json.Linq;

namespace Loopstructor.AutoPlayer.Tests;

public sealed class DecisionEngineStrategyTests
{
    [Theory]
    [InlineData("nonne", "normal")]
    [InlineData("normal", "rare")]
    [InlineData("rare", "epic")]
    [InlineData("epic", "boss1")]
    [InlineData("boss1", "boss2")]
    [InlineData("boss2", "boss3")]
    [InlineData("boss3", "boss4")]
    public void RewardSelection_OrdersTheRealRewardRareValues(
        string lowerRarity,
        string higherRarity)
    {
        JObject reward = RewardOptions(
            new
            {
                index = 0,
                buttonActive = true,
                rewardKind = "superModule",
                rewardRare = lowerRarity,
                superModuleEnum = "LowerRarityModule"
            },
            new
            {
                index = 7,
                buttonActive = true,
                rewardKind = "superModule",
                rewardRare = higherRarity,
                superModuleEnum = "HigherRarityModule"
            });

        AutomationAction action = new DecisionEngine().DecideReward(reward);

        Assert.Equal("chooseRewardOption", action.Command);
        Assert.Equal(7, action.Arguments.Value<int>("index"));
    }

    [Fact]
    public void RewardSelection_ImmediateMergeBeatsAnEpicSuperModule()
    {
        JObject reward = RewardOptions(
            new
            {
                index = 6,
                buttonActive = true,
                rewardKind = "vehicle",
                rewardRare = "normal",
                vehicleType = "Link_ElectricGun_L1",
                effectiveFetters = Array.Empty<object>()
            },
            new
            {
                index = 1,
                buttonActive = true,
                rewardKind = "superModule",
                rewardRare = "epic",
                superModuleEnum = "ImmediateDamageModule"
            });
        JObject vehicles = VehicleState(
            Vehicle("Link_ElectricGun_L1", inBag: false),
            Vehicle("Link_ElectricGun_L1", inBag: true));

        AutomationAction action = new DecisionEngine().DecideReward(reward, vehicles);

        Assert.Equal("chooseRewardOption", action.Command);
        Assert.Equal(6, action.Arguments.Value<int>("index"));
    }

    [Fact]
    public void RewardSelection_UnmatchedLevelOneVehicleYieldsToAnEpicSuperModule()
    {
        JObject reward = RewardOptions(
            new
            {
                index = 0,
                buttonActive = true,
                rewardKind = "vehicle",
                rewardRare = "normal",
                vehicleType = "Link_ElectricGun_L1",
                effectiveFetters = Array.Empty<object>()
            },
            new
            {
                index = 5,
                buttonActive = true,
                rewardKind = "superModule",
                rewardRare = "epic",
                superModuleEnum = "ImmediateDamageModule"
            });

        AutomationAction action = new DecisionEngine().DecideReward(reward, VehicleState());

        Assert.Equal("chooseRewardOption", action.Command);
        Assert.Equal(5, action.Arguments.Value<int>("index"));
    }

    [Theory]
    [InlineData("FreePoint_Attribute")]
    [InlineData("AddNewPoint_Attribute")]
    public void RewardSelection_PrioritizesRailExpansionDisposableEnums(string disposableEnum)
    {
        JObject reward = RewardOptions(
            new
            {
                index = 8,
                buttonActive = true,
                rewardKind = "vehicle",
                rewardRare = "boss4",
                vehicleType = "Link_ElectricGun_L3",
                effectiveFetters = Array.Empty<object>()
            },
            new
            {
                index = 4,
                buttonActive = true,
                rewardKind = "disposable",
                rewardRare = "normal",
                disposableEnum,
                assignDisposableEnum = "None"
            });
        JObject vehicles = VehicleState(
            Vehicle("Link_ElectricGun_L3", inBag: false),
            Vehicle("Link_ElectricGun_L3", inBag: true));

        AutomationAction action = new DecisionEngine().DecideReward(reward, vehicles);

        Assert.Equal("chooseRewardOption", action.Command);
        Assert.Equal(4, action.Arguments.Value<int>("index"));
    }

    [Fact]
    public void RewardSelection_RecognizesAssignedFreePointAttributeFromAutoUseReward()
    {
        JObject reward = RewardOptions(
            new
            {
                index = 0,
                buttonActive = true,
                rewardKind = "superModule",
                rewardRare = "boss4",
                superModuleEnum = "ImmediateDamageModule"
            },
            new
            {
                index = 6,
                buttonActive = true,
                rewardKind = "disposable",
                rewardRare = "normal",
                disposableEnum = "EnergyExpansion",
                assignDisposableEnum = "FreePoint_Attribute"
            });

        AutomationAction action = new DecisionEngine().DecideReward(reward);

        Assert.Equal("chooseRewardOption", action.Command);
        Assert.Equal(6, action.Arguments.Value<int>("index"));
    }

    [Fact]
    public void RewardSelection_EqualRailExpansionRewardsRemainDeterministicByIndex()
    {
        JObject reward = RewardOptions(
            new
            {
                index = 7,
                buttonActive = true,
                rewardKind = "disposable",
                rewardRare = "normal",
                disposableEnum = "FreePoint_Attribute"
            },
            new
            {
                index = 2,
                buttonActive = true,
                rewardKind = "disposable",
                rewardRare = "normal",
                disposableEnum = "AddNewPoint_Attribute"
            });

        AutomationAction first = new DecisionEngine().DecideReward(reward);
        AutomationAction second = new DecisionEngine().DecideReward((JObject)reward.DeepClone());

        Assert.Equal(2, first.Arguments.Value<int>("index"));
        Assert.Equal(first.Arguments.Value<int>("index"), second.Arguments.Value<int>("index"));
    }

    [Fact]
    public void RouteSelection_DoesNotTreatCurrentItemTagsAsGuaranteedRailExpansion()
    {
        static object Route(int readyIndex, string[] disposableTags) => new
        {
            readyIndex,
            canPlayerSelect = true,
            rewardEnum = "Common",
            needFight = true,
            isBoss = false,
            totalEnemyAmount = 20,
            dropCounts = new
            {
                vehicle = 0,
                catapult = 0,
                money = 0,
                disposable = 1,
                superModule = 0
            },
            disposableTags
        };

        JObject affordances = Affordances(
            Route(7, new[] { "Fetter_Energy", "Special_Point" }),
            Route(1, Array.Empty<string>()));

        AutomationAction action = new DecisionEngine().DecideInGame(affordances, null, null);

        Assert.Equal("selectMapNode", action.Command);
        Assert.Equal(1, action.Arguments.Value<int>("readyIndex"));
    }

    [Fact]
    public void RouteSelection_ThirtyEnemyVehicleRewardBeatsSafeEvent()
    {
        JObject affordances = Affordances(
            VehicleRoute(5, enemyCount: 30),
            SafeEventRoute(0));

        AutomationAction action = new DecisionEngine().DecideInGame(affordances, null, null);

        Assert.Equal("selectMapNode", action.Command);
        Assert.Equal(5, action.Arguments.Value<int>("readyIndex"));
    }

    [Fact]
    public void RouteSelection_SeventyEnemyVehicleRewardYieldsToSafeEvent()
    {
        JObject affordances = Affordances(
            VehicleRoute(0, enemyCount: 70),
            SafeEventRoute(4));

        AutomationAction action = new DecisionEngine().DecideInGame(affordances, null, null);

        Assert.Equal("selectMapNode", action.Command);
        Assert.Equal(4, action.Arguments.Value<int>("readyIndex"));
    }

    [Fact]
    public void RouteSelection_SeventyEnemyCommonLootYieldsToSafeBuildNode()
    {
        JObject affordances = Affordances(
            new
            {
                readyIndex = 0,
                canPlayerSelect = true,
                rewardEnum = "Common",
                needFight = true,
                isBoss = false,
                totalEnemyAmount = 70,
                dropCounts = new
                {
                    vehicle = 1,
                    catapult = 0,
                    money = 1,
                    disposable = 2,
                    superModule = 0
                }
            },
            new
            {
                readyIndex = 3,
                canPlayerSelect = true,
                rewardEnum = "Build",
                needFight = false,
                isBoss = false,
                totalEnemyAmount = 0,
                dropCounts = new
                {
                    vehicle = 0,
                    catapult = 0,
                    money = 0,
                    disposable = 0,
                    superModule = 0
                }
            });

        AutomationAction action = new DecisionEngine().DecideInGame(affordances, null, null);

        Assert.Equal("selectMapNode", action.Command);
        Assert.Equal(3, action.Arguments.Value<int>("readyIndex"));
    }

    [Fact]
    public void RouteSelection_EquivalentRewardsPreferFewerEnemiesBeforeReadyIndex()
    {
        JObject affordances = Affordances(
            VehicleRoute(0, enemyCount: 55),
            VehicleRoute(8, enemyCount: 20));

        AutomationAction action = new DecisionEngine().DecideInGame(affordances, null, null);

        Assert.Equal("selectMapNode", action.Command);
        Assert.Equal(8, action.Arguments.Value<int>("readyIndex"));
    }

    [Fact]
    public void RewardSelection_RespectsConfiguredVehicleOrCatapultPriority()
    {
        JObject reward = RewardOptions(
            new
            {
                index = 3,
                buttonActive = true,
                rewardKind = "vehicle",
                rewardRare = "boss4",
                vehicleType = "Link_ElectricGun_L3",
                effectiveFetters = Array.Empty<object>()
            },
            new
            {
                index = 7,
                buttonActive = true,
                rewardKind = "disposable",
                rewardRare = "normal",
                disposableEnum = "FreePoint_Attribute"
            });
        JObject vehicles = VehicleState(
            Vehicle("Link_ElectricGun_L3", inBag: false),
            Vehicle("Link_ElectricGun_L3", inBag: true));
        DecisionEngine engine = new();

        AutomationAction vehicleFirst = engine.DecideReward(
            reward,
            vehicles,
            AutomationDecisionPriority.ThreeStarVehicles);
        AutomationAction pointFirst = engine.DecideReward(
            reward,
            vehicles,
            AutomationDecisionPriority.CatapultPoints);

        Assert.Equal(3, vehicleFirst.Arguments.Value<int>("index"));
        Assert.Equal(7, pointFirst.Arguments.Value<int>("index"));
    }

    [Theory]
    [InlineData("FreePoint")]
    [InlineData("AddNewPoint")]
    [InlineData("FreePoint_Attribute")]
    [InlineData("AddNewPoint_Attribute")]
    [InlineData("EnergyPoint")]
    [InlineData("CreateFreeEnergyExpansion")]
    public void RewardSelection_CatapultPriorityRecognizesEveryPointAcquisitionPath(string disposableEnum)
    {
        JObject reward = RewardOptions(
            new
            {
                index = 1,
                buttonActive = true,
                rewardKind = "vehicle",
                rewardRare = "boss4",
                vehicleType = "Link_ElectricGun_L3",
                effectiveFetters = Array.Empty<object>()
            },
            new
            {
                index = 9,
                buttonActive = true,
                rewardKind = "disposable",
                rewardRare = "normal",
                disposableEnum
            });

        AutomationAction action = new DecisionEngine().DecideReward(
            reward,
            VehicleState(),
            AutomationDecisionPriority.CatapultPoints);

        Assert.Equal(9, action.Arguments.Value<int>("index"));
    }

    [Fact]
    public void RouteSelection_RespectsConfiguredVehicleOrCatapultPriority()
    {
        JObject affordances = Affordances(
            VehicleRoute(2, enemyCount: 20),
            CatapultRoute(6, enemyCount: 20));
        DecisionEngine engine = new();

        AutomationAction vehicleFirst = engine.DecideInGame(
            affordances,
            null,
            null,
            AutomationDecisionPriority.ThreeStarVehicles);
        AutomationAction pointFirst = engine.DecideInGame(
            affordances,
            null,
            null,
            AutomationDecisionPriority.CatapultPoints);

        Assert.Equal(2, vehicleFirst.Arguments.Value<int>("readyIndex"));
        Assert.Equal(6, pointFirst.Arguments.Value<int>("readyIndex"));
    }

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

    private static JObject VehicleState(params object[] vehicles) => JObject.FromObject(new
    {
        success = true,
        data = new
        {
            state = new
            {
                vehicles
            }
        }
    });

    private static object Vehicle(string vehicleType, bool inBag) => new
    {
        vehicleType,
        type = vehicleType,
        level = 1,
        active = true,
        inBag,
        isVirtual = false,
        isFixedHead = false,
        fetters = Array.Empty<object>()
    };

    private static JObject Affordances(params object[] nodes) => JObject.FromObject(new
    {
        data = new
        {
            state = new
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
            }
        }
    });

    private static object VehicleRoute(int readyIndex, int enemyCount) => new
    {
        readyIndex,
        canPlayerSelect = true,
        rewardEnum = "Common",
        needFight = true,
        isBoss = false,
        totalEnemyAmount = enemyCount,
        dropCounts = new
        {
            vehicle = 1,
            catapult = 0,
            money = 0,
            disposable = 0,
            superModule = 0
        },
        vehicleTags = new[] { "Fetter_Link" }
    };

    private static object CatapultRoute(int readyIndex, int enemyCount) => new
    {
        readyIndex,
        canPlayerSelect = true,
        rewardEnum = "CommonCatapult",
        needFight = true,
        isBoss = false,
        totalEnemyAmount = enemyCount,
        dropCounts = new
        {
            vehicle = 0,
            catapult = 1,
            money = 0,
            disposable = 0,
            superModule = 0
        },
        disposableTags = new[] { "Special_Point" }
    };

    private static object SafeEventRoute(int readyIndex) => new
    {
        readyIndex,
        canPlayerSelect = true,
        rewardEnum = "RandomEvent",
        needFight = false,
        isBoss = false,
        totalEnemyAmount = 0,
        dropCounts = new
        {
            vehicle = 0,
            catapult = 0,
            money = 0,
            disposable = 0,
            superModule = 0
        },
        vehicleTags = Array.Empty<string>()
    };
}
