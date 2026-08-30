using Loopstructor.AutoPlayer.Core;
using Newtonsoft.Json.Linq;

namespace Loopstructor.AutoPlayer.Tests;

public sealed class IndependentVehicleGameplayTests
{
    [Fact]
    public void Deployment_UsesDynamicFreeCapacityAndStableVehicleAndEnergyPointIdentities()
    {
        JObject state = IndependentState(
            RailCapacity(70, 7, 700, capacity: 3, running: new[] { 10 }, waiting: new[] { 20 }),
            Vehicle(10, 7, running: true),
            Vehicle(20, 7, queued: true),
            Vehicle(30, 0, inBag: true, basePower: 25),
            Vehicle(40, 0, inBag: true, basePower: 10));

        JObject rail = Assert.IsType<JObject>(Assert.IsType<JArray>(state["rails"])[0]);
        Assert.Equal(2, rail.Value<int>("occupiedCount"));
        Assert.Equal(1, rail.Value<int>("freeCapacity"));
        Assert.Equal(new[] { 20 }, rail["waitingVehicleIds"]!.Values<int>());

        AutomationAction action = Assert.IsType<AutomationAction>(
            new BattleDecisionEngine().DecideIndependentVehicleDeployment(state));

        Assert.Equal("deployVehicleToEnergyPoint", action.Command);
        Assert.Equal(30, action.Arguments.Value<int>("vehicleInstanceId"));
        Assert.Equal(700, action.Arguments.Value<int>("energyPointInstanceId"));
        Assert.Equal(70, action.Arguments.Value<int>("railInstanceId"));
    }

    [Fact]
    public void Expansion_IsRequestedOnlyWhenEveryLegalRailIsFullAndBagStillHasVehicle()
    {
        BattleDecisionEngine engine = new();
        JObject full = IndependentState(
            RailCapacity(70, 7, 700, 2, new[] { 10 }, new[] { 20 }),
            RailCapacity(80, 8, 800, 1, new[] { 30 }, Array.Empty<int>()),
            Vehicle(40, 0, inBag: true));

        Assert.Null(engine.DecideIndependentVehicleDeployment(full));
        Assert.True(engine.NeedsIndependentDefenseExpansion(full));

        JObject upgradedCapacity = (JObject)full.DeepClone();
        JObject rail = (JObject)upgradedCapacity.SelectToken("rails[1]")!;
        rail["capacity"] = 2;
        rail["freeCapacity"] = 1;
        Assert.False(engine.NeedsIndependentDefenseExpansion(upgradedCapacity));
        Assert.NotNull(engine.DecideIndependentVehicleDeployment(upgradedCapacity));
    }

    [Fact]
    public void NewLoopExpansion_RequiresAFullDirectionalLoopAroundTheBase()
    {
        BattleDecisionEngine engine = new();
        JObject full = IndependentState(
            RailCapacity(70, 7, 700, 1, new[] { 10 }, Array.Empty<int>()),
            Vehicle(20, 0, inBag: true));
        JObject oneSided = new()
        {
            ["catapults"] = new JArray(
                ExpansionPoint(101, isAttribute: true, -4, 0),
                ExpansionPoint(102, isAttribute: false, -3, 2),
                ExpansionPoint(103, isAttribute: false, -3, -2))
        };

        Assert.Null(engine.DecideDefenseExpansion(full, oneSided));
        Assert.Equal("FreePoint", engine.RequiredExpansionDisposable(oneSided));

        JObject enclosing = new()
        {
            ["catapults"] = new JArray(
                ExpansionPoint(201, isAttribute: true, 0, 4),
                ExpansionPoint(202, isAttribute: false, 4, -3),
                ExpansionPoint(203, isAttribute: false, -4, -3))
        };
        AutomationAction action = Assert.IsType<AutomationAction>(
            engine.DecideDefenseExpansion(full, enclosing));
        Assert.Equal("drawRailPath", action.Command);
        Assert.Equal(3, Assert.IsType<JArray>(action.Arguments["linePointInstanceIds"]).Count);
    }

    [Fact]
    public void RailExpansion_AggregatesEachVehiclesOwnSpeedAndBaseOutput()
    {
        JObject rails = new()
        {
            ["rails"] = new JArray(new JObject
            {
                ["instanceId"] = 70,
                ["railInternalId"] = 7,
                ["isLegalPlayerLoop"] = true,
                ["isLoop"] = true,
                ["isOnField"] = true,
                ["loopCycleSecondsKnown"] = true,
                ["loopCycleSeconds"] = 10d,
                ["railLength"] = 20d,
                ["stationCount"] = 4,
                ["orderedStations"] = new JArray(
                    GridPoint(0, 0),
                    GridPoint(5, 0),
                    GridPoint(5, 5),
                    GridPoint(0, 5)),
                ["lines"] = new JArray(new JObject
                {
                    ["lineInstanceId"] = 900,
                    ["from"] = new JObject { ["x"] = 0, ["y"] = 0 },
                    ["to"] = new JObject { ["x"] = 5, ["y"] = 0 }
                })
            })
        };
        JObject catapults = new()
        {
            ["catapults"] = new JArray(new JObject
            {
                ["active"] = true,
                ["canUseForNewRail"] = true,
                ["canPickLine"] = true,
                ["railMembershipCount"] = 0,
                ["linePointInstanceId"] = 901,
                ["catapultInstanceId"] = 902,
                ["grid"] = new JObject { ["x"] = 3, ["y"] = 4 }
            })
        };
        JObject vehicles = new()
        {
            ["vehicles"] = new JArray(
                new JObject
                {
                    ["instanceId"] = 10, ["railId"] = 7, ["running"] = true,
                    ["baseCombatPower"] = 10d, ["configuredSpeed"] = 2d
                },
                new JObject
                {
                    ["instanceId"] = 20, ["railId"] = 7, ["queued"] = true,
                    ["baseCombatPower"] = 5d, ["configuredSpeed"] = 1d
                })
        };

        RailInsertionCandidate candidate = Assert.Single(
            new RailExpansionPlanner().BuildCandidates(rails, catapults, vehicles));

        Assert.Equal(5d, candidate.VehicleThroughputScore, 6);
        Assert.Null(candidate.PreviewArguments["vehicleInstanceId"]);
        Assert.Equal(
            new[]
            {
                new AutoPlayerGrid(0, 0),
                new AutoPlayerGrid(3, 4),
                new AutoPlayerGrid(5, 0),
                new AutoPlayerGrid(5, 5),
                new AutoPlayerGrid(0, 5)
            },
            candidate.PredictedStationGrids);

        JObject preview = JObject.FromObject(new
        {
            success = true,
            data = new
            {
                state = new
                {
                    wouldBeLegal = true,
                    sideEffectCheckPassed = true,
                    statePolluted = false,
                    requiresSpeedSource = true,
                    affectedRailId = 7,
                    currentLoopCycleSeconds = (double?)null,
                    predictedLoopCycleSeconds = (double?)null,
                    predictedRailLength = (double?)null,
                    beforeRailCount = 1,
                    afterRailCount = 1
                }
            }
        });

        Assert.True(new RailExpansionPlanner().TryScorePreview(candidate, preview, out RailInsertionPreviewScore score));
        Assert.Equal(candidate.VehicleThroughputScore, score.BaselineEffectiveAttackRate, 6);
        Assert.Equal(candidate.PredictedVehicleThroughputScore, score.PredictedEffectiveAttackRate, 6);
    }

    [Fact]
    public void RequiredMapStationInsertion_DoesNotDeadlockOnCoverageRegression()
    {
        RailInsertionPreviewScore legalRegression = new()
        {
            Candidate = new RailInsertionCandidate
            {
                RailInstanceId = 1,
                LineInstanceId = 2,
                StationLinePointInstanceId = 3
            },
            PredictedEffectiveAttackRate = 12d,
            PredictedTriggerRate = 1.5d,
            PredictedRailLength = 8d,
            BaselineLayout = new RailLayoutScore
            {
                IsValid = true,
                IsSimpleCycle = true,
                EncirclesBase = true,
                CoveredQuadrants = 4,
                MaxAngularGapDegrees = 80d
            },
            PredictedLayout = new RailLayoutScore
            {
                IsValid = false,
                IsSimpleCycle = false,
                EncirclesBase = true,
                CoveredQuadrants = 4,
                MaxAngularGapDegrees = 95d
            }
        };

        Assert.Same(
            legalRegression,
            new RailExpansionPlanner().SelectBestRequiredTopology(new[] { legalRegression }));
    }

    [Fact]
    public void InvalidIncrementalLoop_IsReorderedWithAllCompatibleUnassignedStationsBeforeRedraw()
    {
        JObject railState = new()
        {
            ["rails"] = new JArray(new JObject
            {
                ["instanceId"] = 70,
                ["railInternalId"] = 7,
                ["isLegalPlayerLoop"] = true,
                ["isLoop"] = true,
                ["loopCycleSeconds"] = JValue.CreateNull(),
                ["orderedStations"] = new JArray(
                    RepairStation(702, 72, false, 0, -4),
                    RepairStation(701, 71, false, 4, 0),
                    RepairStation(700, 70, true, 0, 4),
                    RepairStation(703, 73, false, -4, 0))
            })
        };
        JObject independent = IndependentState(
            RailCapacity(70, 7, 700, 2, new[] { 10 }, Array.Empty<int>()),
            Vehicle(10, 7, running: true));
        JObject catapults = new()
        {
            ["catapults"] = new JArray(ExpansionPoint(704, false, -3, 3))
        };
        RailRebuildTransactionPlanner planner = new();

        RailRebuildSnapshot repair = Assert.IsType<RailRebuildSnapshot>(
            planner.BuildFullTopologyRepair(railState, independent, catapults));

        Assert.Equal(700, repair.OrderedLinePointInstanceIds[0]);
        Assert.Equal(5, repair.OrderedLinePointInstanceIds.Count);
        Assert.Contains(704, repair.OrderedLinePointInstanceIds);
        Assert.Equal(repair.OrderedLinePointInstanceIds, repair.OriginalOrderedLinePointInstanceIds);
        Assert.True(planner.IsLegalPreview(
            JObject.FromObject(new
            {
                wouldBeLegal = true,
                sideEffectCheckPassed = true,
                statePolluted = false,
                predictedLoopCycleSeconds = (double?)null
            }),
            repair,
            out double unavailableCycle));
        Assert.Equal(0d, unavailableCycle);
    }

    [Fact]
    public void NewLoopCommonPlacement_FirstSpreadsThenCompletesAnEnclosingTriangle()
    {
        JObject attributeOnly = new()
        {
            ["catapults"] = new JArray(ExpansionPoint(1, true, 0, 4))
        };
        AutoPlayerGrid[] firstCandidates =
        {
            new(0, 2),
            new(4, -2)
        };
        AutoPlayerGrid first = DefenseStationGridRanker.RankPlacement(
            "RuntimeSpecialCommonPoint",
            firstCandidates,
            attributeOnly,
            placementIsAttribute: false).First();
        Assert.Equal(new AutoPlayerGrid(4, -2), first);

        JObject oneCommon = new()
        {
            ["catapults"] = new JArray(
                ExpansionPoint(1, true, 0, 4),
                ExpansionPoint(2, false, 4, -2))
        };
        AutoPlayerGrid second = DefenseStationGridRanker.RankPlacement(
            "RuntimeSpecialCommonPoint",
            new[] { new AutoPlayerGrid(3, -1), new AutoPlayerGrid(-4, -2) },
            oneCommon,
            placementIsAttribute: false).First();
        Assert.Equal(new AutoPlayerGrid(-4, -2), second);
    }

    [Fact]
    public void NewLoopAttributePlacement_ReservesOneLiveSpacingBandBeforeFirstWrite()
    {
        JObject emptyField = new()
        {
            ["catapults"] = new JArray()
        };
        AutoPlayerGrid[] candidates =
        {
            new(-2, -2),
            new(5, 0),
            new(0, 5),
            new(9, 0)
        };

        AutoPlayerGrid selected = DefenseStationGridRanker.RankPlacement(
            "FreePoint_Attribute",
            candidates,
            emptyField,
            new StationSpacingRules(2.1d, 2.1d),
            placementIsAttribute: true).First();

        Assert.NotEqual(new AutoPlayerGrid(-2, -2), selected);
        double radius = Math.Sqrt((double)selected.X * selected.X + (double)selected.Y * selected.Y);
        Assert.InRange(radius, 4.99d, 5.01d);
    }

    [Fact]
    public void NewLoopCommonPlacement_RejectsRemotePointThatWouldBreakFinalRadiusBand()
    {
        JObject attributeOnly = new()
        {
            ["catapults"] = new JArray(ExpansionPoint(1, true, -2, -2))
        };

        IReadOnlyList<AutoPlayerGrid> ranked = DefenseStationGridRanker.RankPlacement(
            "FreePoint",
            new[] { new AutoPlayerGrid(15, -4), new AutoPlayerGrid(2, 3) },
            attributeOnly,
            new StationSpacingRules(2.1d, 2.1d),
            placementIsAttribute: false);

        Assert.Equal(new AutoPlayerGrid(2, 3), Assert.Single(ranked));
    }

    [Fact]
    public void NewLoopCommonPlacement_MatchesOpeningRadiusBeforeExactAngularTieBreak()
    {
        JObject attributeOnly = new()
        {
            ["catapults"] = new JArray(ExpansionPoint(1, true, 0, 5))
        };

        AutoPlayerGrid selected = DefenseStationGridRanker.RankPlacement(
            "FreePoint",
            new[] { new AutoPlayerGrid(9, -5), new AutoPlayerGrid(4, -3) },
            attributeOnly,
            new StationSpacingRules(2.1d, 2.1d),
            placementIsAttribute: false).First();

        Assert.Equal(new AutoPlayerGrid(4, -3), selected);
    }

    [Fact]
    public void Rebuild_PreservesFifoAndAcceptsCapacityShrinkReturnToBag()
    {
        RailRebuildTransactionPlanner planner = new();
        JObject railState = RebuildRailState();
        JObject before = IndependentState(
            RailCapacity(70, 7, 700, 3, new[] { 10 }, new[] { 20, 30 }),
            Vehicle(10, 7, running: true, gameVehicleId: 110),
            Vehicle(20, 7, queued: true, gameVehicleId: 120),
            Vehicle(30, 7, queued: true, gameVehicleId: 130));
        RailRebuildSnapshot snapshot = Assert.IsType<RailRebuildSnapshot>(planner.Capture(railState, 70, before));

        Assert.Equal(new[] { 20, 30 }, snapshot.WaitingVehicleInstanceIds);
        JObject afterShrink = IndependentState(
            RailCapacity(70, 7, 700, 2, new[] { 10 }, new[] { 20 }),
            Vehicle(10, 7, running: true),
            Vehicle(20, 7, queued: true),
            Vehicle(30, 0, inBag: true));

        RailRebuildVerification verification = planner.VerifyRestored(railState, snapshot, afterShrink);
        Assert.True(verification.Verified);
        Assert.True(verification.VehiclesRestored);

        JObject reordered = IndependentState(
            RailCapacity(70, 7, 700, 3, new[] { 10 }, new[] { 30, 20 }),
            Vehicle(10, 7, running: true),
            Vehicle(20, 7, queued: true),
            Vehicle(30, 7, queued: true));
        RailRebuildVerification invalid = planner.VerifyRestored(railState, snapshot, reordered);
        Assert.False(invalid.Verified);
        Assert.Contains("FIFO", invalid.Detail);
    }

    [Fact]
    public void Rebuild_MatchesStablePointIdsWhenDisconnectChangesUnityInstances()
    {
        RailRebuildTransactionPlanner planner = new();
        JObject before = RebuildRailState();
        JObject independent = IndependentState(
            RailCapacity(70, 7, 700, 1, new[] { 10 }, Array.Empty<int>()),
            Vehicle(10, 7, running: true));
        RailRebuildSnapshot snapshot = Assert.IsType<RailRebuildSnapshot>(
            planner.Capture(before, 70, independent));
        JObject after = RebuildRailState();
        JObject[] stations = Assert.IsType<JArray>(after.SelectToken("rails[0].orderedStations"))
            .OfType<JObject>().ToArray();
        foreach (JObject station in stations)
        {
            station["linePointInstanceId"] = station.Value<int>("linePointInstanceId") + 1000;
        }

        Assert.True(planner.VerifyRestored(after, snapshot).Verified);
    }

    private static JObject IndependentState(params JObject[] items)
    {
        JArray rails = new(items.Where(item => item["energyPointInstanceId"] != null));
        JArray vehicles = new(items.Where(item => item["energyPointInstanceId"] == null));
        return new JObject { ["rails"] = rails, ["vehicles"] = vehicles };
    }

    private static JObject GridPoint(int x, int y) => new()
    {
        ["grid"] = new JObject { ["x"] = x, ["y"] = y }
    };

    private static JObject ExpansionPoint(int id, bool isAttribute, int x, int y) => new()
    {
        ["active"] = true,
        ["canUseForNewRail"] = true,
        ["canPickLine"] = true,
        ["railMembershipCount"] = 0,
        ["linePointInstanceId"] = id,
        ["isAttribute"] = isAttribute,
        ["grid"] = new JObject { ["x"] = x, ["y"] = y }
    };

    private static JObject RailCapacity(
        int instanceId,
        int railId,
        int energyPointId,
        int capacity,
        IReadOnlyCollection<int> running,
        IReadOnlyCollection<int> waiting) => new()
    {
        ["instanceId"] = instanceId,
        ["railInstanceId"] = instanceId,
        ["railId"] = railId,
        ["isLegalPlayerLoop"] = true,
        ["isLoop"] = true,
        ["isOnField"] = true,
        ["energyPointCount"] = 1,
        ["energyPointInstanceId"] = energyPointId,
        ["capacity"] = capacity,
        ["runningVehicleIds"] = new JArray(running),
        ["waitingVehicleIds"] = new JArray(waiting),
        ["runningCount"] = running.Count,
        ["waitingCount"] = waiting.Count,
        ["occupiedCount"] = running.Count + waiting.Count,
        ["freeCapacity"] = Math.Max(0, capacity - running.Count - waiting.Count)
    };

    private static JObject Vehicle(
        int instanceId,
        int railId,
        bool running = false,
        bool queued = false,
        bool inBag = false,
        double basePower = 1,
        int gameVehicleId = 0) => new()
    {
        ["instanceId"] = instanceId,
        ["gameVehicleId"] = gameVehicleId,
        ["railId"] = railId,
        ["running"] = running,
        ["queued"] = queued,
        ["inBag"] = inBag,
        ["baseCombatPower"] = basePower
    };

    private static JObject RebuildRailState() => new()
    {
        ["rails"] = new JArray(new JObject
        {
            ["instanceId"] = 70,
            ["railInternalId"] = 7,
            ["isLegalPlayerLoop"] = true,
            ["isLoop"] = true,
            ["loopCycleSeconds"] = 8d,
            ["orderedStations"] = new JArray(
                Station(700, 70, true),
                Station(701, 71, false),
                Station(702, 72, false))
        })
    };

    private static JObject Station(int instanceId, int pointId, bool attribute) => new()
    {
        ["linePointInstanceId"] = instanceId,
        ["pointId"] = pointId,
        ["isAttribute"] = attribute,
        ["grid"] = new JObject { ["x"] = instanceId - 700, ["y"] = pointId - 70 }
    };

    private static JObject RepairStation(
        int instanceId,
        int pointId,
        bool attribute,
        int x,
        int y) => new()
    {
        ["linePointInstanceId"] = instanceId,
        ["pointId"] = pointId,
        ["isAttribute"] = attribute,
        ["grid"] = new JObject { ["x"] = x, ["y"] = y }
    };
}
