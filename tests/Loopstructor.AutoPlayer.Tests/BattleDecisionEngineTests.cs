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
    public void Decide_SkipsTargetRaycastDisposableWithoutCompleteMcpContract()
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
    public void Decide_UsesSafeTargetRaycastDisposableWithCompleteMcpContract()
    {
        JObject disposable = Result(new
        {
            isInPreview = false,
            items = new[]
            {
                CompleteTargetRaycastDisposable(23, "EnergyExpansion", 2)
            }
        });

        AutomationAction? action = Decide(new BattleDecisionContext(), ActiveWave(), disposable);

        Assert.NotNull(action);
        Assert.Equal("useDisposable", action.Command);
        Assert.Equal(23, action.Arguments["itemInstanceId"]?.Value<int>());
    }

    [Theory]
    [InlineData("EnergyPoint")]
    [InlineData("FreePoint")]
    [InlineData("FreePoint_Attribute")]
    [InlineData("AddNewPoint")]
    [InlineData("AddNewPoint_Attribute")]
    [InlineData("CreateFreeEnergyExpansion")]
    public void Decide_NeverConsumesReservedRailExpansionDisposable(string disposableEnum)
    {
        JObject disposable = Result(new
        {
            isInPreview = false,
            items = new[]
            {
                CompleteTargetRaycastDisposable(23, disposableEnum, 2)
            }
        });

        AutomationAction? action = Decide(new BattleDecisionContext(), ActiveWave(), disposable);

        Assert.Null(action);
    }

    [Fact]
    public void Decide_TargetConfirmationSkipsPassingCandidateWithoutStableIdentity()
    {
        JObject disposable = Result(new
        {
            isInPreview = true,
            disposableEnum = "EnergyExpansion",
            confirmContract = new { confirmKind = "targetRaycast" },
            targetCandidates = new object[]
            {
                new { index = 0, conditionPass = true },
                new { instanceId = 51, conditionPass = false },
                new { path = "Scene/Defense/AttributePoint", conditionPass = true }
            }
        });

        AutomationAction? action = Decide(
            new BattleDecisionContext { DisposablePhase = BattleDisposablePhase.Confirming },
            ActiveWave(),
            disposable);

        Assert.NotNull(action);
        Assert.Equal("confirmDisposableTarget", action.Command);
        Assert.Equal("Scene/Defense/AttributePoint", action.Arguments["path"]?.Value<string>());
        Assert.Null(action.Arguments["targetInstanceId"]);
    }

    [Fact]
    public void Decide_TargetConfirmationReplacesCallerPositionWithPassingStableCandidate()
    {
        JObject supplied = JObject.FromObject(new
        {
            world = new { x = 99, y = 99, z = 0 },
            trace = "keep"
        });
        JObject disposable = Result(new
        {
            isInPreview = true,
            disposableEnum = "EnergyExpansion",
            confirmContract = new { confirmKind = "targetRaycast" },
            targetCandidates = new[]
            {
                new { instanceId = 52, conditionPass = true }
            }
        });

        AutomationAction? action = Decide(
            new BattleDecisionContext
            {
                DisposablePhase = BattleDisposablePhase.Confirming,
                DisposableConfirmationArguments = supplied
            },
            ActiveWave(),
            disposable);

        Assert.NotNull(action);
        Assert.Equal(52, action.Arguments["targetInstanceId"]?.Value<int>());
        Assert.Null(action.Arguments["world"]);
        Assert.Equal("keep", action.Arguments["trace"]?.Value<string>());
        Assert.NotSame(supplied, action.Arguments);
        Assert.NotNull(supplied["world"]);
    }

    [Fact]
    public void Decide_TargetConfirmationWaitsWhenNoPassingCandidateHasStableIdentity()
    {
        JObject disposable = Result(new
        {
            isInPreview = true,
            disposableEnum = "EnergyExpansion",
            confirmContract = new { confirmKind = "targetRaycast" },
            targetCandidates = new object[]
            {
                new { index = 0, conditionPass = true },
                new { instanceId = 51, conditionPass = false }
            }
        });

        AutomationAction? action = Decide(
            new BattleDecisionContext { DisposablePhase = BattleDisposablePhase.Confirming },
            ActiveWave(),
            disposable);

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
    public void DecideDefenseExpansion_CreatesShortestLegalLoopWhenExistingTrainIsFull()
    {
        BattleDecisionEngine engine = new();
        JObject catapults = Result(new
        {
            catapults = new object[]
            {
                Catapult(100, "动力站 Z", true, 0, 0),
                Catapult(101, "动力站 A", true, 10, 10),
                Catapult(201, "近站 1", false, 11, 10),
                Catapult(202, "近站 2", false, 10, 12),
                Catapult(203, "远站", false, 0, 0)
            }
        });

        AutomationAction? action = engine.DecideDefenseExpansion(
            TrainResult(2, 2, 111),
            VehicleResult(Vehicle(2010, 8, true)),
            catapults);

        Assert.NotNull(action);
        Assert.Equal("drawRailPath", action.Command);
        Assert.Equal(
            new[] { 101, 201, 202 },
            action.Arguments["linePointInstanceIds"]!.Values<int>().ToArray());
        Assert.Equal(AutomationStage.PreparingDefense, action.Stage);
        Assert.DoesNotContain("cheat", action.Command, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void DecideDefenseExpansion_RejectsOccupiedOrIncompleteStationSets()
    {
        BattleDecisionEngine engine = new();
        JObject catapults = Result(new
        {
            catapults = new object[]
            {
                Catapult(100, "动力站", true, 0, 0),
                Catapult(201, "已占用站", false, 1, 0, railMembershipCount: 1),
                Catapult(202, "可用站", false, 0, 1)
            }
        });

        AutomationAction? action = engine.DecideDefenseExpansion(
            TrainResult(2, 2, 111),
            VehicleResult(Vehicle(2010, 8, true)),
            catapults);

        Assert.Null(action);
    }

    [Fact]
    public void DecideDefenseExpansion_DoesNotRepeatRejectedStationPath()
    {
        BattleDecisionEngine engine = new();
        JObject catapults = Result(new
        {
            catapults = new object[]
            {
                Catapult(100, "动力站", true, 0, 0),
                Catapult(201, "普通站 1", false, 1, 0),
                Catapult(202, "普通站 2", false, 0, 1)
            }
        });

        AutomationAction? action = engine.DecideDefenseExpansion(
            TrainResult(2, 2, 111),
            VehicleResult(Vehicle(2010, 8, true)),
            catapults,
            new HashSet<string>(StringComparer.Ordinal) { "100:201:202" });

        Assert.Null(action);
    }

    [Fact]
    public void DecideDefenseExpansion_PrefersNonCollinearEqualDistancePathAfterRejection()
    {
        BattleDecisionEngine engine = new();
        JObject catapults = Result(new
        {
            catapults = new object[]
            {
                Catapult(100, "动力站", true, 0, 0),
                Catapult(201, "普通站 1", false, 1, 0),
                Catapult(202, "普通站 2", false, 0, 1),
                Catapult(203, "普通站 3", false, 3, 0)
            }
        });

        AutomationAction? action = engine.DecideDefenseExpansion(
            TrainResult(2, 2, 111),
            VehicleResult(Vehicle(2010, 8, true)),
            catapults,
            new HashSet<string>(StringComparer.Ordinal) { "100:201:202" });

        Assert.NotNull(action);
        Assert.Equal(
            new[] { 100, 202, 203 },
            action.Arguments["linePointInstanceIds"]!.Values<int>().ToArray());
    }

    [Fact]
    public void DecideExpansionVehiclePlacement_PlacesHighestLevelBagVehicleOnNewEmptyRail()
    {
        BattleDecisionEngine engine = new();
        JObject drawResult = Result(new
        {
            rail = new
            {
                lines = new object[]
                {
                    new { lineInstanceId = 501, hasDriver = false, driverCount = 0 },
                    new { lineInstanceId = 502, hasDriver = true, driverCount = 1 }
                }
            }
        });

        AutomationAction? action = engine.DecideExpansionVehiclePlacement(
            VehicleResult(Vehicle(301, 2, true), Vehicle(302, 7, true)),
            drawResult);

        Assert.NotNull(action);
        Assert.Equal("placeVehicleOnLine", action.Command);
        Assert.Equal(302, action.Arguments["instanceId"]?.Value<int>());
        Assert.Equal(501, action.Arguments["lineInstanceId"]?.Value<int>());
        Assert.True(action.Arguments["forward"]?.Value<bool>());
    }

    [Fact]
    public void NeedsDefenseExpansion_RequiresBagVehicleAndAllExistingTrainsFull()
    {
        BattleDecisionEngine engine = new();

        Assert.True(engine.NeedsDefenseExpansion(
            TrainResult(2, 2, 111),
            VehicleResult(Vehicle(2010, 8, true))));
        Assert.False(engine.NeedsDefenseExpansion(
            TrainResult(3, 2, 111),
            VehicleResult(Vehicle(2010, 8, true))));
        Assert.False(engine.NeedsDefenseExpansion(
            TrainResult(2, 2, 111),
            VehicleResult(Vehicle(2010, 8, false))));
        Assert.False(engine.NeedsDefenseExpansion(
            Result(new { trains = Array.Empty<object>() }),
            VehicleResult(Vehicle(2010, 8, true))));
    }

    [Fact]
    public void IsLegalDefenseExpansionPreview_RequiresLegalSideEffectFreeUnchangedState()
    {
        BattleDecisionEngine engine = new();

        Assert.True(engine.IsLegalDefenseExpansionPreview(Result(new
        {
            wouldBeLegal = true,
            sideEffectCheckPassed = true,
            statePolluted = false,
            requiresSpeedSource = false,
            predictedLoopCycleSeconds = 8.5,
            beforeRailCount = 2,
            afterRailCount = 2
        })));
        Assert.False(engine.IsLegalDefenseExpansionPreview(Result(new
        {
            wouldBeLegal = true,
            sideEffectCheckPassed = true,
            statePolluted = false,
            requiresSpeedSource = false,
            predictedLoopCycleSeconds = 8.5,
            beforeRailCount = 2,
            afterRailCount = 3
        })));

        Assert.False(engine.IsLegalDefenseExpansionPreview(Result(new
        {
            wouldBeLegal = true,
            sideEffectCheckPassed = true,
            statePolluted = false,
            requiresSpeedSource = true,
            predictedLoopCycleSeconds = 8.5,
            beforeRailCount = 2,
            afterRailCount = 2
        })));

        Assert.False(engine.IsLegalDefenseExpansionPreview(Result(new
        {
            wouldBeLegal = true,
            sideEffectCheckPassed = true,
            statePolluted = false,
            predictedLoopCycleSeconds = 8.5,
            beforeRailCount = 2,
            afterRailCount = 2
        })));
    }

    [Fact]
    public void VerifyDefenseExpansionRail_AcceptsExactlyOneMatchingLegalRail()
    {
        BattleDecisionEngine engine = new();
        JObject baseline = RailSnapshot(ExpansionRail(100, 11, 12, 13));
        JObject current = RailSnapshot(
            ExpansionRail(100, 11, 12, 13),
            ExpansionRail(200, 21, 22, 23));
        JObject drawResult = Result(new { rail = ExpansionRail(200, 21, 22, 23) });
        AutomationAction drawAction = DrawExpansionAction(21, 22, 23);

        DefenseExpansionRailVerification verification = engine.VerifyDefenseExpansionRail(
            baseline,
            drawResult,
            current,
            drawAction,
            expectedRailInstanceId: 200);

        Assert.True(verification.Verified);
        Assert.False(verification.Pending);
        Assert.Equal(200, verification.RailInstanceId);
        Assert.Equal(200, verification.Rail?["instanceId"]?.Value<int>());
    }

    [Fact]
    public void VerifyDefenseExpansionRail_ReturnsPendingOnlyWhileSnapshotIsUnchanged()
    {
        BattleDecisionEngine engine = new();
        JObject baseline = RailSnapshot(ExpansionRail(100, 11, 12, 13));

        DefenseExpansionRailVerification verification = engine.VerifyDefenseExpansionRail(
            baseline,
            Result(new { rail = ExpansionRail(200, 21, 22, 23) }),
            RailSnapshot(ExpansionRail(100, 11, 12, 13)),
            DrawExpansionAction(21, 22, 23),
            expectedRailInstanceId: 200);

        Assert.False(verification.Verified);
        Assert.True(verification.Pending);
    }

    [Fact]
    public void VerifyDefenseExpansionRail_RejectsMultipleNewRailsWithoutPendingRetry()
    {
        BattleDecisionEngine engine = new();
        JObject baseline = RailSnapshot(ExpansionRail(100, 11, 12, 13));
        JObject current = RailSnapshot(
            ExpansionRail(100, 11, 12, 13),
            ExpansionRail(200, 21, 22, 23),
            ExpansionRail(201, 31, 32, 33));

        DefenseExpansionRailVerification verification = engine.VerifyDefenseExpansionRail(
            baseline,
            Result(new { rail = ExpansionRail(200, 21, 22, 23) }),
            current,
            DrawExpansionAction(21, 22, 23),
            expectedRailInstanceId: 200);

        Assert.False(verification.Verified);
        Assert.False(verification.Pending);
        Assert.Contains("新增身份数 2", verification.Detail, StringComparison.Ordinal);
    }

    [Fact]
    public void VerifyDefenseExpansionRail_RejectsWrongDrawIdentityOrPointSet()
    {
        BattleDecisionEngine engine = new();
        JObject baseline = RailSnapshot(ExpansionRail(100, 11, 12, 13));
        JObject current = RailSnapshot(
            ExpansionRail(100, 11, 12, 13),
            ExpansionRail(200, 21, 22, 99));

        DefenseExpansionRailVerification verification = engine.VerifyDefenseExpansionRail(
            baseline,
            Result(new { rail = ExpansionRail(201, 21, 22, 23) }),
            current,
            DrawExpansionAction(21, 22, 23),
            expectedRailInstanceId: 201);

        Assert.False(verification.Verified);
        Assert.False(verification.Pending);
        Assert.Contains("不一致", verification.Detail, StringComparison.Ordinal);
    }

    [Fact]
    public void IsUsableDefenseExpansionRailBaseline_RequiresCountAndUniqueInstanceIds()
    {
        BattleDecisionEngine engine = new();

        Assert.True(engine.IsUsableDefenseExpansionRailBaseline(
            RailSnapshot(ExpansionRail(100, 11, 12, 13))));
        Assert.False(engine.IsUsableDefenseExpansionRailBaseline(Result(new
        {
            railCount = 2,
            rails = new[] { ExpansionRail(100, 11, 12, 13) }
        })));
        Assert.False(engine.IsUsableDefenseExpansionRailBaseline(Result(new
        {
            railCount = 2,
            rails = new[]
            {
                ExpansionRail(100, 11, 12, 13),
                ExpansionRail(100, 21, 22, 23)
            }
        })));
    }

    [Fact]
    public void NeedsExpansionAttributePlacement_RequiresTwoUnusedCommonStationsAndNoAttributeStation()
    {
        BattleDecisionEngine engine = new();

        Assert.True(engine.NeedsExpansionAttributePlacement(Result(new
        {
            catapults = new object[]
            {
                Catapult(201, "普通站 1", false, 1, 0),
                Catapult(202, "普通站 2", false, 0, 1)
            }
        })));
        Assert.False(engine.NeedsExpansionAttributePlacement(Result(new
        {
            catapults = new object[]
            {
                Catapult(100, "动力站", true, 0, 0),
                Catapult(201, "普通站 1", false, 1, 0),
                Catapult(202, "普通站 2", false, 0, 1)
            }
        })));
        Assert.False(engine.NeedsExpansionAttributePlacement(Result(new
        {
            catapults = new[] { Catapult(201, "普通站 1", false, 1, 0) }
        })));
    }

    [Fact]
    public void DecideExpansionAttributeDisposableUse_SelectsAvailableGridItemByStableIdentity()
    {
        BattleDecisionEngine engine = new();
        JObject disposable = Result(new
        {
            isInPreview = false,
            items = new object[]
            {
                new
                {
                    index = 0,
                    itemInstanceId = 710,
                    disposableEnum = "Other",
                    count = 3,
                    active = true,
                    buttonActive = true,
                    interactionType = "GridChooseInteraction"
                },
                new
                {
                    index = 1,
                    itemInstanceId = 711,
                    disposableEnum = "FreePoint_Attribute",
                    count = 1,
                    active = true,
                    buttonActive = true,
                    interactionType = "GridChooseInteraction"
                }
            }
        });

        AutomationAction? action = engine.DecideExpansionAttributeDisposableUse(disposable);

        Assert.NotNull(action);
        Assert.Equal("useDisposable", action.Command);
        Assert.Equal(711, action.Arguments["itemInstanceId"]?.Value<int>());
        Assert.Equal("FreePoint_Attribute", action.Arguments["disposableEnum"]?.Value<string>());
        Assert.Equal(AutomationStage.PreparingDefense, action.Stage);
    }

    [Fact]
    public void SelectExpansionAttributeGrid_PrefersGridNearestTwoUnusedCommonStationsDeterministically()
    {
        BattleDecisionEngine engine = new();
        JObject catapults = Result(new
        {
            catapults = new object[]
            {
                Catapult(201, "普通站 1", false, 0, 0),
                Catapult(202, "普通站 2", false, 10, 0),
                Catapult(203, "已占用站", false, 5, 20, railMembershipCount: 1)
            }
        });
        JObject options = Result(new
        {
            disposableEnum = "FreePoint_Attribute",
            validGrids = new object[]
            {
                new { grid = new { x = 0, y = 10 } },
                new { grid = new { x = 5, y = 0 } },
                new { grid = new { x = 5, y = 1 } }
            }
        });

        JObject? grid = engine.SelectExpansionAttributeGrid(options, catapults);

        Assert.NotNull(grid);
        Assert.Equal(5, grid["x"]?.Value<int>());
        Assert.Equal(1, grid["y"]?.Value<int>());
    }

    [Fact]
    public void DecideExpansionAttributeConfirmation_RequiresSameEnumAndInteractionIdentity()
    {
        BattleDecisionEngine engine = new();
        AutomationAction use = new(
            "useDisposable",
            JObject.FromObject(new { itemInstanceId = 711, disposableEnum = "FreePoint_Attribute" }),
            AutomationStage.PreparingDefense,
            "test");
        JObject preview = Result(new
        {
            isInPreview = true,
            disposableEnum = "FreePoint_Attribute",
            interactionType = "GridChooseInteraction",
            interactionInstanceId = 901
        });

        AutomationAction? action = engine.DecideExpansionAttributeConfirmation(
            use,
            JObject.FromObject(new { x = 5, y = 0 }),
            preview,
            901);
        AutomationAction? wrongIdentity = engine.DecideExpansionAttributeConfirmation(
            use,
            JObject.FromObject(new { x = 5, y = 0 }),
            preview,
            902);

        Assert.NotNull(action);
        Assert.Equal("confirmDisposableGrid", action.Command);
        Assert.Equal(901, action.Arguments["interactionInstanceId"]?.Value<int>());
        Assert.Equal(5, action.Arguments.SelectToken("grid.x")?.Value<int>());
        Assert.Null(wrongIdentity);
        Assert.True(engine.IsOwnedExpansionAttributePreview(preview, 901, requireGridInteraction: true));
        Assert.False(engine.IsOwnedExpansionAttributePreview(preview, 902, requireGridInteraction: false));
    }

    [Fact]
    public void DecideExpansionAttributeDirectConfirmation_PreservesStableItemIdentityWithoutTransientPreviewId()
    {
        BattleDecisionEngine engine = new();
        AutomationAction itemIdentity = new(
            "useDisposable",
            JObject.FromObject(new
            {
                itemInstanceId = 711,
                disposableEnum = "FreePoint_Attribute",
                interactionInstanceId = 901
            }),
            AutomationStage.PreparingDefense,
            "test");

        AutomationAction? action = engine.DecideExpansionAttributeDirectConfirmation(
            itemIdentity,
            JObject.FromObject(new { x = 5, y = -2 }));

        Assert.NotNull(action);
        Assert.Equal("confirmDisposableGrid", action.Command);
        Assert.Equal(711, action.Arguments["itemInstanceId"]?.Value<int>());
        Assert.Equal("FreePoint_Attribute", action.Arguments["disposableEnum"]?.Value<string>());
        Assert.Equal(5, action.Arguments.SelectToken("grid.x")?.Value<int>());
        Assert.Equal(-2, action.Arguments.SelectToken("grid.y")?.Value<int>());
        Assert.Null(action.Arguments["interactionInstanceId"]);
    }

    [Fact]
    public void DecideExpansionAttributeDirectConfirmation_RejectsMissingItemIdentityOrInvalidGrid()
    {
        BattleDecisionEngine engine = new();
        AutomationAction missingIdentity = new(
            "useDisposable",
            JObject.FromObject(new { disposableEnum = "FreePoint_Attribute" }),
            AutomationStage.PreparingDefense,
            "test");
        AutomationAction wrongEnum = new(
            "useDisposable",
            JObject.FromObject(new { itemInstanceId = 711, disposableEnum = "Bomb" }),
            AutomationStage.PreparingDefense,
            "test");

        Assert.Null(engine.DecideExpansionAttributeDirectConfirmation(
            missingIdentity,
            JObject.FromObject(new { x = 5, y = -2 })));
        Assert.Null(engine.DecideExpansionAttributeDirectConfirmation(
            wrongEnum,
            JObject.FromObject(new { x = 5, y = -2 })));
        Assert.Null(engine.DecideExpansionAttributeDirectConfirmation(
            new AutomationAction(
                "useDisposable",
                JObject.FromObject(new { itemInstanceId = 711, disposableEnum = "FreePoint_Attribute" }),
                AutomationStage.PreparingDefense,
                "test"),
            JObject.FromObject(new { x = 5.5, y = -2 })));
    }

    [Fact]
    public void CleanDisposableInteractionIdle_RequiresCompleteFailClosedGuardProof()
    {
        BattleDecisionEngine engine = new();
        JObject cleanIdle = Result(new
        {
            contractAvailable = true,
            observationConsistent = true,
            noActiveInteraction = true,
            isInPreview = false,
            hasLastInteraction = false,
            interactionInstanceId = 0
        });
        JObject inconsistent = Result(new
        {
            contractAvailable = true,
            observationConsistent = false,
            noActiveInteraction = true,
            isInPreview = false,
            hasLastInteraction = false,
            interactionInstanceId = 0
        });
        JObject foreignPreview = Result(new
        {
            contractAvailable = true,
            observationConsistent = true,
            noActiveInteraction = false,
            isInPreview = true,
            hasLastInteraction = true,
            interactionInstanceId = 902
        });

        Assert.True(engine.IsCleanDisposableInteractionIdle(cleanIdle));
        Assert.False(engine.IsCleanDisposableInteractionIdle(inconsistent));
        Assert.False(engine.IsCleanDisposableInteractionIdle(foreignPreview));
    }

    [Fact]
    public void DecideExpansionAttributeCancellation_RequiresExactEnumAndInteractionIdentity()
    {
        BattleDecisionEngine engine = new();
        JObject ownedPreview = Result(new
        {
            isInPreview = true,
            disposableEnum = "FreePoint_Attribute",
            interactionType = "GridChooseInteraction",
            interactionInstanceId = 901
        });
        JObject differentPreview = Result(new
        {
            isInPreview = true,
            disposableEnum = "Bomb",
            interactionType = "GridChooseInteraction",
            interactionInstanceId = 901
        });

        AutomationAction? action = engine.DecideExpansionAttributeCancellation(ownedPreview, 901);

        Assert.NotNull(action);
        Assert.Equal("cancelDisposable", action.Command);
        Assert.Equal("FreePoint_Attribute", action.Arguments["disposableEnum"]?.Value<string>());
        Assert.Equal(901, action.Arguments["interactionInstanceId"]?.Value<int>());
        Assert.Null(engine.DecideExpansionAttributeCancellation(ownedPreview, 902));
        Assert.Null(engine.DecideExpansionAttributeCancellation(differentPreview, 901));
        Assert.Null(engine.DecideExpansionAttributeCancellation(Result(new
        {
            isInPreview = false,
            disposableEnum = "FreePoint_Attribute",
            interactionInstanceId = 901
        }), 901));
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
        Assert.False(action.Arguments["forward"]?.Value<bool>());
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

    [Fact]
    public void DecideTrainMovement_MovesSecondTrainWhenStrongestTrainIsAlreadyBestPositioned()
    {
        JObject rails = Result(new
        {
            rails = new object[]
            {
                Rail(1, true, Line(950, 5, -1, 5, 1, hasDriver: true)),
                Rail(2, true,
                    Line(951, -5, -1, -5, 1, hasDriver: true),
                    Line(952, 4, -1, 4, 1))
            }
        });
        JObject trains = Result(new
        {
            trains = new object[]
            {
                new { index = 0, railId = 1, line = "Line-950", realVehicleCount = 4, forward = true },
                new { index = 1, railId = 2, line = "Line-951", realVehicleCount = 2, forward = false }
            }
        });

        AutomationAction action = new BattleDecisionEngine().DecideTrainMovement(ThreatResult(10, 0), rails, trains);

        Assert.Equal("moveTrainToLine", action.Command);
        Assert.Equal(1, action.Arguments["trainIndex"]?.Value<int>());
        Assert.Equal(952, action.Arguments["lineInstanceId"]?.Value<int>());
        Assert.True(action.Arguments["forward"]?.Value<bool>());
    }

    [Fact]
    public void DecideTrainMovement_DoesNotUseFreeLineOnRailOccupiedByAnotherTrain()
    {
        JObject rails = Result(new
        {
            rails = new object[]
            {
                Rail(1, true,
                    Line(960, -5, -1, -5, 1, hasDriver: true),
                    Line(961, 2, -1, 2, 1)),
                Rail(2, true,
                    Line(962, 5, -1, 5, 1, hasDriver: true),
                    Line(963, 4, -1, 4, 1))
            }
        });
        JObject trains = Result(new
        {
            trains = new object[]
            {
                new { index = 0, railId = 1, line = "Line-960", realVehicleCount = 3, forward = true },
                new { index = 1, railId = 2, line = "Line-962", realVehicleCount = 1, forward = true }
            }
        });

        AutomationAction action = new BattleDecisionEngine().DecideTrainMovement(ThreatResult(10, 0), rails, trains);

        Assert.Equal("moveTrainToLine", action.Command);
        Assert.Equal(0, action.Arguments["trainIndex"]?.Value<int>());
        Assert.Equal(961, action.Arguments["lineInstanceId"]?.Value<int>());
        Assert.NotEqual(963, action.Arguments["lineInstanceId"]?.Value<int>());
    }

    [Fact]
    public void DecideTrainMovement_BreaksEqualImprovementByVehicleCountTrainIndexAndLineId()
    {
        JObject rails = Result(new
        {
            rails = new object[]
            {
                Rail(1, true,
                    Line(970, -5, -1, -5, 1, hasDriver: true),
                    Line(971, 3, -1, 3, 1)),
                Rail(2, true,
                    Line(980, -5, -1, -5, 1, hasDriver: true),
                    Line(982, 3, -1, 3, 1),
                    Line(981, 3, -1, 3, 1)),
                Rail(3, true,
                    Line(990, -5, -1, -5, 1, hasDriver: true),
                    Line(991, 3, -1, 3, 1))
            }
        });
        JObject trains = Result(new
        {
            trains = new object[]
            {
                new { index = 0, railId = 1, line = "Line-970", realVehicleCount = 2, forward = true },
                new { index = 2, railId = 2, line = "Line-980", realVehicleCount = 3, forward = true },
                new { index = 4, railId = 3, line = "Line-990", realVehicleCount = 3, forward = true }
            }
        });

        AutomationAction action = new BattleDecisionEngine().DecideTrainMovement(ThreatResult(10, 0), rails, trains);

        Assert.Equal("moveTrainToLine", action.Command);
        Assert.Equal(2, action.Arguments["trainIndex"]?.Value<int>());
        Assert.Equal(981, action.Arguments["lineInstanceId"]?.Value<int>());
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

    private static object CompleteTargetRaycastDisposable(
        int itemInstanceId,
        string disposableEnum,
        int count) => new
    {
        index = itemInstanceId,
        itemInstanceId,
        disposableEnum,
        count,
        active = true,
        buttonActive = true,
        confirmCommand = "guigame_confirm_disposable_target",
        confirmContract = new
        {
            confirmKind = "targetRaycast",
            allowedArgs = new[] { "targetInstanceId", "instanceId", "path", "world", "grid" },
            needsTarget = true,
            needsWorldPosition = true,
            targetCandidatesRequired = true
        },
        effectFacts = new { effectKind = "targetBuff" },
        restoreIdentityArgs = new[] { "disposableEnum", "index", "itemInstanceId", "instanceId", "path" },
        sameItemIdentityRequiredForConfirm = true
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

    private static JObject RailSnapshot(params object[] rails) => Result(new
    {
        railCount = rails.Length,
        rails
    });

    private static object ExpansionRail(int instanceId, params int[] pointInstanceIds) => new
    {
        instanceId,
        id = instanceId + 1000,
        railInternalId = instanceId + 1000,
        isLegalPlayerLoop = true,
        isLoop = true,
        isOnField = true,
        points = pointInstanceIds.Select((pointId, index) => new
        {
            index,
            instanceId = pointId
        }).ToArray(),
        lines = new[]
        {
            new { lineInstanceId = instanceId + 5000, hasDriver = false, driverCount = 0 }
        }
    };

    private static AutomationAction DrawExpansionAction(params int[] pointInstanceIds) => new(
        "drawRailPath",
        new JObject { ["linePointInstanceIds"] = new JArray(pointInstanceIds) },
        AutomationStage.PreparingDefense,
        "test");

    private static object Catapult(
        int linePointInstanceId,
        string name,
        bool isAttribute,
        int x,
        int y,
        int railMembershipCount = 0) => new
    {
        instanceId = linePointInstanceId + 10000,
        linePointInstanceId,
        name,
        isAttribute,
        active = true,
        canUseForNewRail = railMembershipCount == 0,
        canPickLine = true,
        frozen = false,
        railReachMax = false,
        railMembershipCount,
        grid = new { x, y }
    };

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
