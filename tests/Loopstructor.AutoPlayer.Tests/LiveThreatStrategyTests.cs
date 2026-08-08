using Loopstructor.AutoPlayer.Core;
using Newtonsoft.Json.Linq;

namespace Loopstructor.AutoPlayer.Tests;

public sealed class LiveThreatStrategyTests
{
    [Fact]
    public void ReliableAccounting_WithNoFutureEnemies_UsesLivePositionInsteadOfStaticNest()
    {
        JObject threats = ThreatResult(
            liveThreats: new[] { LiveThreat(11, 1, 20, 0) },
            nests: new[] { Nest(0, -20, 0, level: 10, amount: 100) },
            remaining: 1,
            accounted: 1,
            estimatedFuture: 0);

        AutomationAction action = new BattleDecisionEngine().DecideTrainMovement(
            threats,
            TwoDirectionRailResult(),
            SingleTrainResult());

        Assert.Equal("moveTrainToLine", action.Command);
        Assert.Equal(801, action.Arguments["lineInstanceId"]?.Value<int>());
        Assert.True(action.Arguments["forward"]?.Value<bool>());
    }

    [Fact]
    public void ReliableAccounting_DistributesFutureMassToNestDirection()
    {
        JObject threats = ThreatResult(
            liveThreats: new[] { LiveThreat(12, 1, -20, 0) },
            nests: new[] { Nest(0, 20, 0, level: 4, amount: 80) },
            remaining: 11,
            accounted: 1,
            estimatedFuture: 10);

        AutomationAction action = new BattleDecisionEngine().DecideTrainMovement(
            threats,
            TwoDirectionRailResult(),
            SingleTrainResult());

        Assert.Equal("moveTrainToLine", action.Command);
        Assert.Equal(801, action.Arguments["lineInstanceId"]?.Value<int>());
    }

    [Fact]
    public void LegacyThreatPayload_WithoutLiveFields_PreservesNestOnlyBehavior()
    {
        JObject threats = Result(new
        {
            mainBase = new { world = new { x = 100, y = -50, z = 0 } },
            nests = new[] { Nest(0, 20, 0, level: 3, amount: 4) }
        });

        AutomationAction action = new BattleDecisionEngine().DecideTrainMovement(
            threats,
            TwoDirectionRailResult(),
            SingleTrainResult());

        Assert.Equal("moveTrainToLine", action.Command);
        Assert.Equal(801, action.Arguments["lineInstanceId"]?.Value<int>());
    }

    [Fact]
    public void ReliableAccounting_DeduplicatesLiveRuntimeIdentity()
    {
        JObject threats = ThreatResult(
            liveThreats: new[]
            {
                LiveThreat(14, 2, 20, 0),
                LiveThreat(14, 2, -20, 0)
            },
            nests: Array.Empty<object>(),
            remaining: 1,
            accounted: 1,
            estimatedFuture: 0);

        AutomationAction action = new BattleDecisionEngine().DecideTrainMovement(
            threats,
            TwoDirectionRailResult(),
            SingleTrainResult());

        Assert.Equal("moveTrainToLine", action.Command);
        Assert.Equal(801, action.Arguments["lineInstanceId"]?.Value<int>());
    }

    [Fact]
    public void UnreliableBossWave_CloseBossCannotBeDilutedByDistantCrowdAndNestPrior()
    {
        object[] liveThreats = new[]
            {
                LiveThreat(100, 1, 2, 0, isBoss: true)
            }
            .Concat(Enumerable.Range(0, 80)
                .Select(index => LiveThreat(200 + index, 1, -20, 0)))
            .ToArray();
        JObject threats = ThreatResult(
            liveThreats,
            new[] { Nest(0, -20, 0, level: 1, amount: 250) },
            remaining: int.MaxValue,
            accounted: liveThreats.Length,
            estimatedFuture: -1,
            remainingReliable: false);

        AutomationAction action = new BattleDecisionEngine().DecideTrainMovement(
            threats,
            TwoDirectionRailResult(leftX: -3, rightX: 3),
            SingleTrainResult());

        Assert.Equal("moveTrainToLine", action.Command);
        Assert.Equal(801, action.Arguments["lineInstanceId"]?.Value<int>());
        Assert.True(action.Arguments["forward"]?.Value<bool>());
    }

    [Fact]
    public void UrgencyReserve_DistantMassStillReceivesSecondTrainAfterCloseThreatIsCovered()
    {
        object[] liveThreats = new[]
            {
                LiveThreat(100, 1, 2, 0, isBoss: true)
            }
            .Concat(Enumerable.Range(0, 80)
                .Select(index => LiveThreat(200 + index, 1, -20, 0)))
            .ToArray();
        JObject threats = ThreatResult(
            liveThreats,
            new[] { Nest(0, -20, 0, level: 1, amount: 250) },
            remaining: int.MaxValue,
            accounted: liveThreats.Length,
            estimatedFuture: -1,
            remainingReliable: false);
        JObject rails = Result(new
        {
            rails = new[]
            {
                Rail(1, Line(900, 3, hasDriver: true)),
                Rail(2, Line(910, 4, hasDriver: true), Line(911, -3, hasDriver: false))
            }
        });
        JObject trains = Result(new
        {
            trains = new[]
            {
                Train(0, 1, "Line-900", realVehicleCount: 4),
                Train(1, 2, "Line-910", realVehicleCount: 4)
            }
        });

        AutomationAction action = new BattleDecisionEngine().DecideTrainMovement(threats, rails, trains);

        Assert.Equal("moveTrainToLine", action.Command);
        Assert.Equal(1, action.Arguments["trainIndex"]?.Value<int>());
        Assert.Equal(911, action.Arguments["lineInstanceId"]?.Value<int>());
        Assert.True(action.Arguments["forward"]?.Value<bool>());
    }

    [Fact]
    public void LiveThreatAtMainBase_IsRetainedAsMaximumUrgency()
    {
        JObject threats = ThreatResult(
            liveThreats: new[] { LiveThreat(500, 1, 0, 0) },
            nests: Array.Empty<object>(),
            remaining: 1,
            accounted: 1,
            estimatedFuture: 0);

        AutomationAction action = new BattleDecisionEngine().DecideTrainMovement(
            threats,
            TwoDirectionRailResult(leftX: -9, rightX: 2),
            SingleTrainResult());

        Assert.Equal("moveTrainToLine", action.Command);
        Assert.Equal(801, action.Arguments["lineInstanceId"]?.Value<int>());
    }

    private static JObject ThreatResult(
        object[] liveThreats,
        object[] nests,
        int remaining,
        int accounted,
        int estimatedFuture,
        bool remainingReliable = true) => Result(new
    {
        mainBase = new { world = new { x = 100, y = -50, z = 0 } },
        liveThreatsAvailable = true,
        liveThreatCount = liveThreats.Length,
        accountedLiveCount = accounted,
        liveThreats,
        enemyAccounting = new
        {
            globalRemaining = remaining,
            remainingReliable,
            estimatedFutureCount = remainingReliable ? estimatedFuture : (int?)null,
            consistent = remainingReliable ? remaining >= accounted : (bool?)null
        },
        nests
    });

    private static object LiveThreat(
        int handleId,
        int lifetime,
        double x,
        double y,
        bool isBoss = false) => new
    {
        instanceId = handleId + 1000,
        runtimeHandleId = handleId,
        lifetimeVersion = lifetime,
        isBoss,
        aiRunning = true,
        countsTowardWave = true,
        health = 100,
        healthMax = 100,
        world = new { x = 100 + x, y = -50 + y, z = 0 },
        relativeToMainBase = new { vector = new { x, y, z = 0 } }
    };

    private static object Nest(int index, double x, double y, int level, int amount) => new
    {
        index,
        active = true,
        world = new { x = 100 + x, y = -50 + y, z = 0 },
        relativeToMainBase = new { vector = new { x, y, z = 0 } },
        spawn = new { level, amount }
    };

    private static JObject TwoDirectionRailResult(double leftX = -9, double rightX = 9) => Result(new
    {
        rails = new[]
        {
            Rail(
                1,
                Line(800, leftX, hasDriver: true),
                Line(801, rightX, hasDriver: false))
        }
    });

    private static object Rail(int id, params object[] lines) => new
    {
        id,
        railInternalId = id,
        isLegalPlayerLoop = true,
        isLoop = true,
        isOnField = true,
        driverCount = 1,
        driverMaxCount = 4,
        isDriverReachToMax = false,
        lines
    };

    private static object Line(int instanceId, double x, bool hasDriver) => new
    {
        instanceId,
        lineInstanceId = instanceId,
        name = $"Line-{instanceId}",
        from = new { x, y = -1 },
        to = new { x, y = 1 },
        hasDriver,
        driverCount = hasDriver ? 1 : 0
    };

    private static JObject SingleTrainResult() => Result(new
    {
        trains = new[]
        {
            Train(0, 1, "Line-800", realVehicleCount: 4)
        }
    });

    private static object Train(int index, int railId, string line, int realVehicleCount) => new
    {
        index,
        railId,
        line,
        realVehicleCount,
        forward = true
    };

    private static JObject Result(object state) => JObject.FromObject(new
    {
        success = true,
        data = new { state }
    });
}
