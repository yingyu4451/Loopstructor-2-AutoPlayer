using Loopstructor.AutoPlayer.Core;
using Newtonsoft.Json.Linq;

namespace Loopstructor.AutoPlayer.Tests;

public sealed class BattleDecisionEngineTests
{
    [Fact]
    public void Decide_DoesNotTouchPreviewThatWasNotStartedByAutoPlay()
    {
        JObject disposable = Result(new
        {
            isInPreview = true,
            interactionType = "GridChooseInteraction",
            items = new[] { Disposable(3, "Bomb", 1, "grid") }
        });

        AutomationAction? action = Decide(
            new BattleDecisionContext(),
            ActiveWave(),
            disposable,
            TrainResult(2, 1, 101),
            VehicleResult(Vehicle(201, 5, true)));

        Assert.Null(action);
    }

    [Fact]
    public void Decide_ConfirmsGridPreviewAtFirstValidatedGrid()
    {
        BattleDecisionContext context = new()
        {
            DisposablePhase = BattleDisposablePhase.AwaitingPreview,
            DisposableGridOptionsResult = Result(new
            {
                validGrids = new[]
                {
                    new { grid = new { x = 7, y = -2 } },
                    new { grid = new { x = 8, y = -2 } }
                }
            })
        };
        JObject disposable = Result(new
        {
            isInPreview = true,
            disposableEnum = "FreePoint_Attribute",
            confirmContract = new { confirmKind = "grid" }
        });

        AutomationAction? action = Decide(context, ActiveWave(), disposable);

        Assert.NotNull(action);
        Assert.Equal("confirmDisposableGrid", action.Command);
        Assert.Equal(7, action.Arguments.SelectToken("grid.x")?.Value<int>());
        Assert.Equal(-2, action.Arguments.SelectToken("grid.y")?.Value<int>());
    }

    [Fact]
    public void Decide_UsesCallerWorldWithoutMutatingIt()
    {
        JObject supplied = JObject.FromObject(new { world = new { x = 3.5, y = 2, z = 0 } });
        BattleDecisionContext context = new()
        {
            DisposablePhase = BattleDisposablePhase.Confirming,
            DisposableConfirmationArguments = supplied
        };
        JObject disposable = Result(new
        {
            isInPreview = true,
            disposableEnum = "WorldBomb",
            confirmContract = new { confirmKind = "world" }
        });

        AutomationAction? action = Decide(context, ActiveWave(), disposable);

        Assert.NotNull(action);
        Assert.Equal("confirmDisposableWorld", action.Command);
        Assert.NotSame(supplied, action.Arguments);
        action.Arguments["extra"] = true;
        Assert.Null(supplied["extra"]);
    }

    [Fact]
    public void Decide_ConfirmsTargetPreviewAtFirstPassingCandidate()
    {
        JObject disposable = Result(new
        {
            isInPreview = true,
            disposableEnum = "TargetBuff",
            confirmContract = new { confirmKind = "targetRaycast" },
            targetCandidates = new object[]
            {
                new { instanceId = 51, conditionPass = false },
                new { instanceId = 52, conditionPass = true },
                new { instanceId = 53, conditionPass = true }
            }
        });

        AutomationAction? action = Decide(
            new BattleDecisionContext { DisposablePhase = BattleDisposablePhase.Confirming },
            ActiveWave(),
            disposable);

        Assert.NotNull(action);
        Assert.Equal("confirmDisposableTarget", action.Command);
        Assert.Equal(52, action.Arguments["targetInstanceId"]?.Value<int>());
    }

    [Fact]
    public void Decide_WaitsForConfirmationDataInsteadOfDiscardingOwnedPreview()
    {
        JObject disposable = Result(new
        {
            isInPreview = true,
            disposableEnum = "WorldBomb",
            confirmContract = new { confirmKind = "world" }
        });

        AutomationAction? action = Decide(
            new BattleDecisionContext { DisposablePhase = BattleDisposablePhase.AwaitingPreview },
            ActiveWave(),
            disposable,
            TrainResult(3, 1, 101),
            VehicleResult(Vehicle(201, 5, true)));

        Assert.Null(action);
    }

    [Fact]
    public void Decide_UsesHighestPriorityAvailableDisposableDuringBattle()
    {
        JObject disposable = Result(new
        {
            isInPreview = false,
            items = new object[]
            {
                Disposable(10, "GridStation", 5, "grid", "createStationWithBuiltInBuff"),
                Disposable(11, "DisabledBuff", 2, "grid", "vehicleBuff", buttonActive: false),
                Disposable(12, "TeamBuff", 1, "world", "vehicleBuff"),
                Disposable(13, "EmptyBuff", 0, "targetRaycast", "targetBuff")
            }
        });

        AutomationAction? action = Decide(
            new BattleDecisionContext(),
            ActiveWave(),
            disposable,
            TrainResult(4, 1, 101),
            VehicleResult(Vehicle(201, 5, true)));

        Assert.NotNull(action);
        Assert.Equal("useDisposable", action.Command);
        Assert.Equal(12, action.Arguments["itemInstanceId"]?.Value<int>());
        Assert.DoesNotContain("cheat", action.Command, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Decide_DoesNotSpendDisposableOutsideAnActiveWave()
    {
        JObject disposable = Result(new
        {
            isInPreview = false,
            items = new[] { Disposable(12, "TeamBuff", 1, "world", "vehicleBuff") }
        });

        AutomationAction? action = Decide(new BattleDecisionContext(), Result(new
        {
            isInWaving = false,
            enemy = new { remaining = 8 }
        }), disposable);

        Assert.Null(action);
    }

    [Fact]
    public void Decide_NeverUsesDestructiveDisposableTools()
    {
        JObject disposable = Result(new
        {
            isInPreview = false,
            items = new object[]
            {
                Disposable(21, "RemoveUtil", 3, "grid", "RemoveUtilBehaviour"),
                Disposable(22, "SellVehicle", 2, "targetRaycast", "SellVehicleBehaviour")
            }
        });

        AutomationAction? action = Decide(new BattleDecisionContext(), ActiveWave(), disposable);

        Assert.Null(action);
    }

    [Fact]
    public void Decide_SkipsTargetRaycastDisposableThatRequiresSceneWideCandidateScan()
    {
        JObject disposable = Result(new
        {
            isInPreview = false,
            items = new[]
            {
                Disposable(23, "TargetBuff", 2, "targetRaycast", "targetBuff")
            }
        });

        AutomationAction? action = Decide(new BattleDecisionContext(), ActiveWave(), disposable);

        Assert.Null(action);
    }

    [Fact]
    public void Decide_MovesHighestLevelBagVehicleIntoTrainWithMostFreeCapacity()
    {
        JObject trains = Result(new
        {
            trains = new object[]
            {
                new
                {
                    index = 0,
                    realVehicleCount = 2,
                    capacity = 3,
                    vehicles = new[] { Vehicle(101, 0, false), Vehicle(102, 0, false) }
                },
                new
                {
                    index = 1,
                    realVehicleCount = 1,
                    capacity = 4,
                    vehicles = new[] { Vehicle(111, 0, false) }
                },
                new
                {
                    index = 2,
                    realVehicleCount = 4,
                    capacity = 4,
                    vehicles = new[] { Vehicle(121, 0, false) }
                }
            }
        });
        JObject vehicles = Result(new
        {
            vehicles = new object[]
            {
                Vehicle(201, 2, true, "Low"),
                Vehicle(202, 5, true, "High"),
                Vehicle(203, 9, false, "AlreadyPlaced"),
                new { index = 3, instanceId = 204, level = 20, inBag = true, isFixedHead = true }
            }
        });

        AutomationAction? action = Decide(new BattleDecisionContext(), Result(new { isInWaving = false }), null, trains, vehicles);

        Assert.NotNull(action);
        Assert.Equal("moveVehicleInTrain", action.Command);
        Assert.Equal(202, action.Arguments["instanceId"]?.Value<int>());
        Assert.Equal(111, action.Arguments.SelectToken("relative.instanceId")?.Value<int>());
        Assert.Equal(AutomationStage.PreparingDefense, action.Stage);
    }

    [Fact]
    public void Decide_DoesNotInterleaveDefenseWhileWaitingForDisposablePreview()
    {
        AutomationAction? action = Decide(
            new BattleDecisionContext { DisposablePhase = BattleDisposablePhase.AwaitingPreview },
            ActiveWave(),
            Result(new { isInPreview = false }),
            TrainResult(3, 1, 101),
            VehicleResult(Vehicle(201, 5, true)));

        Assert.Null(action);
    }

    [Fact]
    public void Decide_ReturnsNoActionWhenTrainHasNoCapacity()
    {
        AutomationAction? action = Decide(
            new BattleDecisionContext { AllowDisposableUse = false },
            ActiveWave(),
            Result(new { isInPreview = false }),
            TrainResult(2, 2, 101),
            VehicleResult(Vehicle(201, 5, true)));

        Assert.Null(action);
    }

    [Fact]
    public void Decide_ReinsertsVirtualizedVehicleThatIsBackInTheBag()
    {
        JObject vehicles = VehicleResult(Vehicle(205, 6, true, "Returned", isVirtual: true));

        AutomationAction? action = Decide(
            new BattleDecisionContext { AllowDisposableUse = false },
            Result(new { isInWaving = false }),
            null,
            TrainResult(3, 1, 101),
            vehicles);

        Assert.NotNull(action);
        Assert.Equal("moveVehicleInTrain", action.Command);
        Assert.Equal(205, action.Arguments["instanceId"]?.Value<int>());
    }

    [Fact]
    public void DecideTrainMovement_MovesStrongestTrainTowardPrimaryThreatWithCorrectDirection()
    {
        JObject threats = Result(new
        {
            mainBase = new { world = new { x = 0, y = 0, z = 0 } },
            nests = new object[]
            {
                new { index = 0, active = true, world = new { x = -12, y = 0, z = 0 }, spawn = new { level = 1, amount = 2 } },
                new { index = 1, active = true, world = new { x = 12, y = 1, z = 0 }, spawn = new { level = 4, amount = 3 } }
            }
        });
        JObject rails = Result(new
        {
            rails = new object[]
            {
                Rail(1, true,
                    Line(701, -10, -1, -10, 1, hasDriver: true),
                    Line(702, 9, -1, 9, 1)),
                Rail(2, true, Line(703, 6, 5, 8, 5))
            }
        });
        JObject trains = Result(new
        {
            trains = new object[]
            {
                new { index = 0, railId = 2, line = "Line-703", realVehicleCount = 1, forward = true },
                new { index = 4, railId = 1, line = "Line-701", realVehicleCount = 3, forward = false }
            }
        });

        AutomationAction action = new BattleDecisionEngine().DecideTrainMovement(threats, rails, trains);

        Assert.Equal("moveTrainToLine", action.Command);
        Assert.Equal(4, action.Arguments["trainIndex"]?.Value<int>());
        Assert.Equal(702, action.Arguments["lineInstanceId"]?.Value<int>());
        Assert.True(action.Arguments["forward"]?.Value<bool>());
        Assert.DoesNotContain("cheat", action.Command, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void DecideTrainMovement_FiltersIllegalOccupiedAndFullCrossRailTargets()
    {
        JObject threats = ThreatResult(10, 0);
        JObject rails = Result(new
        {
            rails = new object[]
            {
                Rail(1, false, Line(801, 9, -1, 9, 1)),
                Rail(2, true, Line(802, 8, -1, 8, 1, hasDriver: true)),
                new
                {
                    id = 3,
                    railInternalId = 3,
                    isLegalPlayerLoop = true,
                    isLoop = true,
                    isOnField = true,
                    driverCount = 1,
                    driverMaxCount = 1,
                    isDriverReachToMax = true,
                    lines = new[] { Line(803, 7, -1, 7, 1) }
                },
                Rail(4, true,
                    Line(804, -6, -1, -6, 1, hasDriver: true),
                    Line(805, 6, -1, 6, 1))
            }
        });
        JObject trains = Result(new
        {
            trains = new[] { new { index = 2, railId = 4, line = "Line-804", realVehicleCount = 2, forward = true } }
        });

        AutomationAction action = new BattleDecisionEngine().DecideTrainMovement(threats, rails, trains);

        Assert.Equal("moveTrainToLine", action.Command);
        Assert.Equal(805, action.Arguments["lineInstanceId"]?.Value<int>());
    }

    [Theory]
    [InlineData("nests")]
    [InlineData("rails")]
    [InlineData("trains")]
    public void DecideTrainMovement_ReturnsWaitWhenRequiredStateIsMissing(string missing)
    {
        JObject threats = missing == "nests"
            ? Result(new { mainBase = new { world = new { x = 0, y = 0 } }, nests = Array.Empty<object>() })
            : ThreatResult(10, 0);
        JObject rails = missing == "rails"
            ? Result(new { rails = Array.Empty<object>() })
            : Result(new { rails = new[] { Rail(1, true, Line(901, 8, -1, 8, 1)) } });
        JObject trains = missing == "trains"
            ? Result(new { trains = Array.Empty<object>() })
            : Result(new { trains = new[] { new { index = 0, railId = 1, realVehicleCount = 2, forward = true } } });

        AutomationAction action = new BattleDecisionEngine().DecideTrainMovement(threats, rails, trains);

        Assert.Equal("wait", action.Command);
        Assert.Equal(AutomationStage.Battle, action.Stage);
    }

    [Fact]
    public void DecideTrainMovement_DoesNotChooseLineBehindTheBase()
    {
        JObject rails = Result(new
        {
            rails = new[] { Rail(1, true, Line(910, -2, -1, -2, 1, hasDriver: true)) }
        });
        JObject trains = Result(new
        {
            trains = new[] { new { index = 0, railId = 1, line = "Line-910", realVehicleCount = 2, forward = true } }
        });

        AutomationAction action = new BattleDecisionEngine().DecideTrainMovement(ThreatResult(10, 0), rails, trains);

        Assert.Equal("wait", action.Command);
    }

    [Fact]
    public void DecideTrainMovement_UsesRelativeThreatDirectionWhenWorldOriginIsNotGridOrigin()
    {
        JObject threats = Result(new
        {
            mainBase = new { world = new { x = 900, y = -400, z = 0 } },
            nests = new[]
            {
                new
                {
                    index = 0,
                    active = true,
                    world = new { x = 930, y = -400, z = 0 },
                    relativeToMainBase = new { vector = new { x = 30, y = 0, z = 0 } },
                    spawn = new { level = 1, amount = 1 }
                }
            }
        });
        JObject rails = Result(new
        {
            rails = new[]
            {
                Rail(1, true, Line(920, 8, -1, 8, 1), Line(921, -8, -1, -8, 1, hasDriver: true))
            }
        });
        JObject trains = Result(new
        {
            trains = new[] { new { index = 0, railId = 1, line = "Line-921", realVehicleCount = 2, forward = true } }
        });

        AutomationAction action = new BattleDecisionEngine().DecideTrainMovement(threats, rails, trains);

        Assert.Equal("moveTrainToLine", action.Command);
        Assert.Equal(920, action.Arguments["lineInstanceId"]?.Value<int>());
    }

    [Fact]
    public void DecideTrainMovement_DoesNotEnterRailThatAlreadyContainsAnotherTrain()
    {
        JObject rails = Result(new
        {
            rails = new object[]
            {
                Rail(1, true, Line(930, -8, -1, -8, 1, hasDriver: true)),
                Rail(2, true, Line(931, 8, -1, 8, 1))
            }
        });
        JObject trains = Result(new
        {
            trains = new[] { new { index = 0, railId = 1, line = "Line-930", realVehicleCount = 2, forward = true } }
        });

        AutomationAction action = new BattleDecisionEngine().DecideTrainMovement(ThreatResult(10, 0), rails, trains);

        Assert.Equal("wait", action.Command);
    }

    [Fact]
    public void DecideTrainMovement_DoesNotMoveWhenCurrentLineIsAlreadyClosestToThreat()
    {
        JObject rails = Result(new
        {
            rails = new[]
            {
                Rail(1, true,
                    Line(940, 4, -1, 4, 1, hasDriver: true),
                    Line(941, 8, -1, 8, 1))
            }
        });
        JObject trains = Result(new
        {
            trains = new[] { new { index = 0, railId = 1, line = "Line-940", realVehicleCount = 2, forward = false } }
        });

        AutomationAction action = new BattleDecisionEngine().DecideTrainMovement(ThreatResult(10, 0), rails, trains);

        Assert.Equal("wait", action.Command);
        Assert.Contains("无需重复调度", action.Reason, StringComparison.Ordinal);
    }

    private static AutomationAction? Decide(
        BattleDecisionContext context,
        JObject wave,
        JObject? disposable = null,
        JObject? trains = null,
        JObject? vehicles = null) =>
        new BattleDecisionEngine().Decide(context, wave, disposable, trains, vehicles);

    private static JObject ActiveWave() => Result(new
    {
        isInWaving = true,
        enemy = new { remaining = 10 }
    });

    private static JObject Result(object state) => JObject.FromObject(new
    {
        success = true,
        data = new { state }
    });

    private static object Disposable(
        int itemInstanceId,
        string disposableEnum,
        int count,
        string confirmKind,
        string effectKind = "other",
        bool buttonActive = true) => new
    {
        index = itemInstanceId,
        itemInstanceId,
        disposableEnum,
        count,
        active = true,
        buttonActive,
        confirmContract = new { confirmKind },
        effectFacts = new { effectKind }
    };

    private static object Vehicle(
        int instanceId,
        int level,
        bool inBag,
        string? name = null,
        bool isVirtual = false) => new
    {
        index = instanceId,
        instanceId,
        name = name ?? $"Vehicle-{instanceId}",
        level,
        inBag,
        isVirtual,
        isFixedHead = false
    };

    private static JObject VehicleResult(params object[] vehicles) => Result(new { vehicles });

    private static JObject TrainResult(int capacity, int count, int relativeInstanceId) => Result(new
    {
        trains = new[]
        {
            new
            {
                index = 0,
                realVehicleCount = count,
                capacity,
                vehicles = new[] { Vehicle(relativeInstanceId, 0, false) }
            }
        }
    });

    private static JObject ThreatResult(double x, double y) => Result(new
    {
        mainBase = new { world = new { x = 100, y = -50, z = 0 } },
        nests = new[]
        {
            new
            {
                index = 0,
                active = true,
                world = new { x = 100 + x, y = -50 + y, z = 0 },
                relativeToMainBase = new { vector = new { x, y, z = 0 } },
                spawn = new { level = 1, amount = 1 }
            }
        }
    });

    private static object Rail(int railId, bool legal, params object[] lines) => new
    {
        id = railId,
        railInternalId = railId,
        isLegalPlayerLoop = legal,
        isLoop = true,
        isOnField = true,
        driverCount = 1,
        driverMaxCount = 4,
        isDriverReachToMax = false,
        lines
    };

    private static object Line(
        int instanceId,
        double fromX,
        double fromY,
        double toX,
        double toY,
        bool hasDriver = false) => new
    {
        instanceId,
        lineInstanceId = instanceId,
        name = $"Line-{instanceId}",
        from = new { x = fromX, y = fromY },
        to = new { x = toX, y = toY },
        hasDriver,
        driverCount = hasDriver ? 1 : 0
    };
}
