using Loopstructor.AutoPlayer.Core;
using Newtonsoft.Json.Linq;

namespace Loopstructor.AutoPlayer.Tests;

public sealed class OpeningIndependentVehiclePreparationPlannerTests
{
    [Fact]
    public void ExistingAttributePoint_SelectsHighestBaseOutputVehicleAndPreviewsIdentityOnlyRail()
    {
        OpeningDefensePreparationPlanner planner = new(new UnusedGridProbe());

        OpeningDefensePreparationDecision catapultDecision = planner.Decide();
        Assert.Equal("queryCatapults", catapultDecision.Action?.Command);
        planner.Observe(catapultDecision.Action!, new JObject
        {
            ["catapults"] = new JArray(
                Point(101, true, 0, 4),
                Point(102, false, -4, -2),
                Point(103, false, 4, -2))
        }, accepted: true);

        OpeningDefensePreparationDecision vehicleDecision = planner.Decide();
        Assert.Equal("queryIndependentVehicleState", vehicleDecision.Action?.Command);
        planner.Observe(vehicleDecision.Action!, new JObject
        {
            ["vehicles"] = new JArray(
                new JObject
                {
                    ["instanceId"] = 9001,
                    ["inBag"] = true,
                    ["baseCombatPower"] = 15d
                },
                new JObject
                {
                    ["instanceId"] = 9002,
                    ["inBag"] = true,
                    ["baseCombatPower"] = 38d
                })
        }, accepted: true);

        Assert.Equal(9002, planner.SelectedVehicleInstanceId);
        OpeningDefensePreparationDecision preview = planner.Decide();
        Assert.Equal("previewRailPath", preview.Action?.Command);
        Assert.Null(preview.Action?.Arguments["vehicleInstanceId"]);
        Assert.Equal(3, (preview.Action?.Arguments["linePointInstanceIds"] as JArray)?.Count);

        planner.Observe(preview.Action!, PreviewWithoutVehicleSpeed(), accepted: true);

        OpeningDefensePreparationDecision baseline = planner.Decide();
        Assert.Equal(OpeningDefensePreparationPhase.QueryRailBaseline, baseline.Phase);
        Assert.Equal("queryRail", baseline.Action?.Command);
    }

    [Fact]
    public void PreviewWithoutVehicleSpeed_IsAcceptedWhenLegalityAndNoSideEffectsAreProven()
    {
        BattleDecisionEngine engine = new();

        Assert.True(engine.IsLegalDefenseExpansionPreview(PreviewWithoutVehicleSpeed()));
    }

    private static JObject Point(int instanceId, bool isAttribute, int x, int y) => new()
    {
        ["linePointInstanceId"] = instanceId,
        ["isAttribute"] = isAttribute,
        ["active"] = true,
        ["canUseForNewRail"] = true,
        ["canPickLine"] = true,
        ["railMembershipCount"] = 0,
        ["grid"] = new JObject
        {
            ["x"] = x,
            ["y"] = y
        }
    };

    private static JObject PreviewWithoutVehicleSpeed() => new()
    {
        ["success"] = true,
        ["data"] = new JObject
        {
            ["state"] = new JObject
            {
                ["wouldBeLegal"] = true,
                ["illegalReasons"] = new JArray(),
                ["predictedLoopCycleSeconds"] = JValue.CreateNull(),
                ["requiresSpeedSource"] = true,
                ["predictionMissingReason"] = "driverSpeedUnavailable",
                ["statePolluted"] = false,
                ["sideEffectCheckPassed"] = true,
                ["beforeRailCount"] = 1,
                ["afterRailCount"] = 1
            }
        }
    };

    private sealed class UnusedGridProbe : IOpeningDefenseGridProbe
    {
        public bool TryInitialize(IReadOnlyList<OpeningDefenseGrid> commonPointAnchors, out string error)
        {
            error = string.Empty;
            throw new InvalidOperationException("已有属性点时不应启动网格探测。");
        }

        public OpeningDefenseGridProbeResult ProbeNext() =>
            throw new InvalidOperationException("已有属性点时不应继续网格探测。");

        public void Reset()
        {
        }
    }
}
