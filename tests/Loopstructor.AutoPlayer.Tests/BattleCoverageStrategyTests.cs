using Loopstructor.AutoPlayer.Core;
using Newtonsoft.Json.Linq;

namespace Loopstructor.AutoPlayer.Tests;

public sealed class BattleCoverageStrategyTests
{
    [Fact]
    public void DecideDefenseExpansion_WhenExistingRailCoversLeft_SelectsSeparatedRightLoop()
    {
        JObject catapults = CatapultResult(
            // Existing left-side loop.
            Catapult(10, true, -12, 0, railMembershipCount: 1),
            Catapult(11, false, -13, -1, railMembershipCount: 1),
            Catapult(12, false, -13, 1, railMembershipCount: 1),
            // Equally compact candidates on both sides. Stable-id ordering favors the left loop.
            Catapult(100, true, -8, 0),
            Catapult(101, false, -9, -1),
            Catapult(102, false, -9, 1),
            Catapult(200, true, 8, 0),
            Catapult(201, false, 9, -1),
            Catapult(202, false, 9, 1));

        AutomationAction? action = new BattleDecisionEngine().DecideDefenseExpansion(
            FullTrainResult(),
            BagVehicleResult(),
            catapults);

        Assert.NotNull(action);
        Assert.Equal("drawRailPath", action.Command);
        Assert.Equal(new[] { 200, 201, 202 }, SelectedPointIds(action));
    }

    [Fact]
    public void DecideDefenseExpansion_MirroredLayout_SelectsMirroredSeparatedLoop()
    {
        JObject catapults = CatapultResult(
            // Mirror the existing coverage to the right. Stable-id ordering still favors the right candidate.
            Catapult(10, true, 12, 0, railMembershipCount: 1),
            Catapult(11, false, 13, -1, railMembershipCount: 1),
            Catapult(12, false, 13, 1, railMembershipCount: 1),
            Catapult(100, true, 8, 0),
            Catapult(101, false, 9, -1),
            Catapult(102, false, 9, 1),
            Catapult(200, true, -8, 0),
            Catapult(201, false, -9, -1),
            Catapult(202, false, -9, 1));

        AutomationAction? action = new BattleDecisionEngine().DecideDefenseExpansion(
            FullTrainResult(),
            BagVehicleResult(),
            catapults);

        Assert.NotNull(action);
        Assert.Equal("drawRailPath", action.Command);
        Assert.Equal(new[] { 200, 201, 202 }, SelectedPointIds(action));
    }

    [Fact]
    public void DecideDefenseExpansion_WithoutExistingRail_FallsBackToDeterministicShortestLoop()
    {
        object[] candidates =
        {
            Catapult(100, true, 0, 0),
            Catapult(101, false, 1, 0),
            Catapult(102, false, 0, 1),
            Catapult(200, true, 20, 0),
            Catapult(201, false, 21, 0),
            Catapult(202, false, 20, 2)
        };
        BattleDecisionEngine engine = new();

        AutomationAction? ordered = engine.DecideDefenseExpansion(
            FullTrainResult(),
            BagVehicleResult(),
            CatapultResult(candidates));
        AutomationAction? reversed = engine.DecideDefenseExpansion(
            FullTrainResult(),
            BagVehicleResult(),
            CatapultResult(candidates.Reverse().ToArray()));

        Assert.NotNull(ordered);
        Assert.NotNull(reversed);
        Assert.Equal(new[] { 100, 101, 102 }, SelectedPointIds(ordered));
        Assert.Equal(SelectedPointIds(ordered), SelectedPointIds(reversed));
    }

    [Fact]
    public void SelectExpansionAttributeGrid_PrefersCompactOppositeLoopOverDistantAngularMatch()
    {
        JObject catapults = CatapultResult(
            Catapult(10, true, -10, 0, railMembershipCount: 1),
            Catapult(11, false, -11, -1, railMembershipCount: 1),
            Catapult(12, false, -11, 1, railMembershipCount: 1),
            Catapult(100, false, 5, -1),
            Catapult(101, false, 5, 2));
        JObject options = JObject.FromObject(new
        {
            success = true,
            data = new
            {
                state = new
                {
                    disposableEnum = "FreePoint_Attribute",
                    validGrids = new object[]
                    {
                        new { grid = new { x = 100, y = -1 } },
                        new { grid = new { x = 4, y = 0 } }
                    }
                }
            }
        });

        JObject? selected = new BattleDecisionEngine().SelectExpansionAttributeGrid(options, catapults);

        Assert.NotNull(selected);
        Assert.Equal(4, selected["x"]?.Value<int>());
        Assert.Equal(0, selected["y"]?.Value<int>());
    }

    [Fact]
    public void SelectExpansionAttributeGrid_DoesNotTradeCompactLoopForDistantOppositeSide()
    {
        JObject catapults = CatapultResult(
            Catapult(10, true, -10, 0, railMembershipCount: 1),
            Catapult(11, false, -11, -1, railMembershipCount: 1),
            Catapult(12, false, -11, 1, railMembershipCount: 1),
            Catapult(100, false, -5, -1),
            Catapult(101, false, -5, 1));
        JObject options = JObject.FromObject(new
        {
            success = true,
            data = new
            {
                state = new
                {
                    disposableEnum = "FreePoint_Attribute",
                    validGrids = new object[]
                    {
                        new { grid = new { x = 100, y = 0 } },
                        new { grid = new { x = -6, y = 0 } }
                    }
                }
            }
        });

        JObject? selected = new BattleDecisionEngine().SelectExpansionAttributeGrid(options, catapults);

        Assert.NotNull(selected);
        Assert.Equal(-6, selected["x"]?.Value<int>());
        Assert.Equal(0, selected["y"]?.Value<int>());
    }

    [Fact]
    public void DecideTrainMovement_WhenLeftNestIsCoveredAndRightNestIsNot_MovesSecondTrainRight()
    {
        JObject threats = ThreatResult(
            Nest(0, -20, 0, level: 3, amount: 4),
            Nest(1, 20, 0, level: 3, amount: 4));
        JObject rails = RailResult(
            Rail(1, Line(700, -9, 0, hasDriver: true)),
            Rail(2,
                Line(710, 0, 0, hasDriver: true),
                Line(711, -9, 0),
                Line(712, 9, 0)));
        JObject trains = TrainResult(
            Train(0, railId: 1, lineInstanceId: 700, vehicleCount: 4),
            Train(1, railId: 2, lineInstanceId: 710, vehicleCount: 2));

        AutomationAction action = new BattleDecisionEngine().DecideTrainMovement(threats, rails, trains);

        Assert.Equal("moveTrainToLine", action.Command);
        Assert.Equal(1, action.Arguments["trainIndex"]?.Value<int>());
        Assert.Equal(712, action.Arguments["lineInstanceId"]?.Value<int>());
        Assert.True(action.Arguments["forward"]?.Value<bool>());
    }

    [Fact]
    public void DecideTrainMovement_WhenBestTrainIsExcluded_SelectsAnotherTrain()
    {
        JObject threats = ThreatResult(Nest(0, 20, 0, level: 4, amount: 5));
        JObject rails = RailResult(
            Rail(1,
                Line(800, -9, 0, hasDriver: true),
                Line(801, 9, 0)),
            Rail(2,
                Line(810, -8, 0, hasDriver: true),
                Line(811, 8, 0)));
        JObject trains = TrainResult(
            Train(0, railId: 1, lineInstanceId: 800, vehicleCount: 4),
            Train(1, railId: 2, lineInstanceId: 810, vehicleCount: 2));
        BattleDecisionEngine engine = new();

        AutomationAction first = engine.DecideTrainMovement(threats, rails, trains);
        AutomationAction excludingFirst = engine.DecideTrainMovement(
            threats,
            rails,
            trains,
            new HashSet<int> { 0 });

        Assert.Equal(0, first.Arguments["trainIndex"]?.Value<int>());
        Assert.Equal("moveTrainToLine", excludingFirst.Command);
        Assert.Equal(1, excludingFirst.Arguments["trainIndex"]?.Value<int>());
        Assert.Equal(811, excludingFirst.Arguments["lineInstanceId"]?.Value<int>());
    }

    [Fact]
    public void DecideTrainMovement_DoesNotTreatAnEntireLongPatrolAsInstantCoverage()
    {
        JObject threats = ThreatResult(Nest(0, 4, 0, level: 4, amount: 5));
        JObject rails = RailResult(
            Rail(1,
                LineSegment(900, -20, -1, -20, 1, hasDriver: true),
                LineSegment(901, -25, 0, 25, 0),
                LineSegment(902, 2, -1, 2, 1)));
        JObject trains = TrainResult(Train(0, railId: 1, lineInstanceId: 900, vehicleCount: 4));

        AutomationAction action = new BattleDecisionEngine().DecideTrainMovement(threats, rails, trains);

        Assert.Equal("moveTrainToLine", action.Command);
        Assert.Equal(902, action.Arguments["lineInstanceId"]?.Value<int>());
    }

    [Fact]
    public void DecideTrainMovement_LayerSixteenRegression_PrefersCompactRailEdge()
    {
        JObject threats = ThreatResult(Nest(0, 0, 2, level: 5, amount: 10));
        JObject rails = RailResult(
            Rail(11,
                LineSegment(920, 0, -4, -25, -11, hasDriver: true),
                LineSegment(921, 3, -3, 0, -4),
                LineSegment(922, -25, -11, 3, -3)));
        JObject trains = TrainResult(Train(1, railId: 11, lineInstanceId: 920, vehicleCount: 4));

        AutomationAction action = new BattleDecisionEngine().DecideTrainMovement(threats, rails, trains);

        Assert.Equal("moveTrainToLine", action.Command);
        Assert.Equal(921, action.Arguments["lineInstanceId"]?.Value<int>());
    }

    [Theory]
    [InlineData(-8, true)]
    [InlineData(8, false)]
    public void DecideTrainMovement_StartsFromEndpointNearestThreat(int threatWorldX, bool expectedForward)
    {
        double currentX = threatWorldX < 0 ? 20 : -20;
        JObject threats = ThreatResult(Nest(0, threatWorldX, 0, level: 4, amount: 5));
        JObject rails = RailResult(
            Rail(1,
                LineSegment(910, currentX, -1, currentX, 1, hasDriver: true),
                LineSegment(911, -5, 0, 5, 0)));
        JObject trains = TrainResult(Train(0, railId: 1, lineInstanceId: 910, vehicleCount: 4));

        AutomationAction action = new BattleDecisionEngine().DecideTrainMovement(threats, rails, trains);

        Assert.Equal("moveTrainToLine", action.Command);
        Assert.Equal(911, action.Arguments["lineInstanceId"]?.Value<int>());
        Assert.Equal(expectedForward, action.Arguments["forward"]?.Value<bool>());
    }

    private static int[] SelectedPointIds(AutomationAction action) =>
        action.Arguments["linePointInstanceIds"]!.Values<int>().ToArray();

    private static JObject FullTrainResult() => Result(new
    {
        trains = new[]
        {
            new
            {
                index = 0,
                railId = 1,
                realVehicleCount = 3,
                capacity = 3,
                vehicles = new[] { new { instanceId = 900, isFixedHead = true } }
            }
        }
    });

    private static JObject BagVehicleResult() => Result(new
    {
        vehicles = new[]
        {
            new { index = 0, instanceId = 901, level = 1, inBag = true, isFixedHead = false }
        }
    });

    private static JObject CatapultResult(params object[] catapults) => Result(new { catapults });

    private static object Catapult(
        int linePointInstanceId,
        bool isAttribute,
        int x,
        int y,
        int railMembershipCount = 0) => new
    {
        instanceId = linePointInstanceId + 10000,
        linePointInstanceId,
        name = $"Point-{linePointInstanceId}",
        isAttribute,
        active = true,
        canUseForNewRail = railMembershipCount == 0,
        canPickLine = true,
        frozen = false,
        railReachMax = false,
        railMembershipCount,
        grid = new { x, y }
    };

    private static JObject ThreatResult(params object[] nests) => Result(new
    {
        mainBase = new { world = new { x = 100, y = -50, z = 0 } },
        nests
    });

    private static object Nest(int index, double x, double y, int level, int amount) => new
    {
        index,
        active = true,
        world = new { x = 100 + x, y = -50 + y, z = 0 },
        relativeToMainBase = new { vector = new { x, y, z = 0 } },
        spawn = new { level, amount }
    };

    private static JObject RailResult(params object[] rails) => Result(new { rails });

    private static object Rail(int railId, params object[] lines) => new
    {
        id = railId,
        railInternalId = railId,
        isLegalPlayerLoop = true,
        isLoop = true,
        isOnField = true,
        driverCount = 1,
        driverMaxCount = 4,
        isDriverReachToMax = false,
        lines
    };

    private static object Line(int instanceId, double x, double y, bool hasDriver = false) => new
    {
        instanceId,
        lineInstanceId = instanceId,
        name = $"Line-{instanceId}",
        from = new { x, y = y - 1 },
        to = new { x, y = y + 1 },
        hasDriver,
        driverCount = hasDriver ? 1 : 0
    };

    private static object LineSegment(
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

    private static JObject TrainResult(params object[] trains) => Result(new { trains });

    private static object Train(int index, int railId, int lineInstanceId, int vehicleCount) => new
    {
        index,
        railId,
        line = $"Line-{lineInstanceId}",
        realVehicleCount = vehicleCount,
        forward = true
    };

    private static JObject Result(object state) => JObject.FromObject(new
    {
        success = true,
        data = new { state }
    });
}
