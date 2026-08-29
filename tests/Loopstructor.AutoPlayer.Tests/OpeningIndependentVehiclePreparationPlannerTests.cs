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

        OpeningDefensePreparationDecision specials = planner.Decide();
        Assert.Equal(OpeningDefensePreparationPhase.QuerySpecialStationDisposable, specials.Phase);
        planner.Observe(specials.Action!, DisposableInventory(), accepted: true);

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

    [Fact]
    public void EmptyField_PlacesAttributeThenContinuesWithBackpackCommonPoints()
    {
        RecordingGridProbe probe = new();
        OpeningDefensePreparationPlanner planner = new(probe);

        OpeningDefensePreparationDecision catapults = planner.Decide();
        planner.Observe(catapults.Action!, new JObject
        {
            ["catapults"] = new JArray()
        }, accepted: true);

        OpeningDefensePreparationDecision inventory = planner.Decide();
        Assert.Equal(OpeningDefensePreparationPhase.QueryPlacementDisposable, inventory.Phase);
        Assert.Equal("queryDisposable", inventory.Action?.Command);
        planner.Observe(
            inventory.Action!,
            DisposableInventory("FreePoint_Attribute", count: 1, instanceId: 701),
            accepted: true);

        OpeningDefensePreparationDecision probeDecision = planner.Decide();
        Assert.Equal(OpeningDefensePreparationPhase.ProbeStationGrid, probeDecision.Phase);
        Assert.Equal("FreePoint_Attribute", probe.LastDisposableEnum);
        Assert.Equal("wait", probeDecision.Action?.Command);
        Assert.Contains("下一帧继续", probeDecision.Detail);

        OpeningDefensePreparationDecision selected = planner.Decide();
        Assert.Equal("queryOpeningDefenseInteractionGuard", selected.Action?.Command);
        planner.Observe(selected.Action!, IdleInteractionGuard(), accepted: true);

        OpeningDefensePreparationDecision confirm = planner.Decide();
        Assert.Equal("confirmDisposableGrid", confirm.Action?.Command);
        Assert.Equal("FreePoint_Attribute", confirm.Action?.Arguments["disposableEnum"]?.Value<string>());
        Assert.Equal(701, confirm.Action?.Arguments["itemInstanceId"]?.Value<int>());
        Assert.Equal(4, confirm.Action?.Arguments.SelectToken("grid.x")?.Value<int>());
        Assert.Equal(-2, confirm.Action?.Arguments.SelectToken("grid.y")?.Value<int>());

        planner.Observe(confirm.Action!, new JObject
        {
            ["success"] = true,
            ["data"] = new JObject
            {
                ["state"] = new JObject { ["isInPreview"] = false }
            }
        }, accepted: true);
        planner.MarkPlacementPreviewReleased();

        OpeningDefensePreparationDecision verify = planner.Decide();
        Assert.Equal(OpeningDefensePreparationPhase.VerifyStationPlacement, verify.Phase);
        planner.Observe(verify.Action!, new JObject
        {
            ["catapults"] = new JArray(Point(101, true, 4, -2, "FreePoint_Attribute"))
        }, accepted: true);

        Assert.Equal(OpeningDefensePreparationPhase.QueryCatapults, planner.Phase);
        OpeningDefensePreparationDecision refreshCatapults = planner.Decide();
        planner.Observe(refreshCatapults.Action!, new JObject
        {
            ["catapults"] = new JArray(Point(101, true, 4, -2, "FreePoint_Attribute"))
        }, accepted: true);

        Assert.Equal("FreePoint", planner.PlacementDisposableEnum);
        Assert.Equal(1, probe.InitializationCount);
        Assert.Equal(OpeningDefensePreparationPhase.QueryPlacementDisposable, planner.Phase);
        Assert.NotEqual(OpeningDefensePreparationPhase.PlacementVerificationFailed, planner.Phase);

        OpeningDefensePreparationDecision refreshedInventory = planner.Decide();
        Assert.Equal("queryDisposable", refreshedInventory.Action?.Command);
        planner.Observe(
            refreshedInventory.Action!,
            DisposableInventory("FreePoint", count: 2, instanceId: 702),
            accepted: true);

        Assert.Equal("FreePoint", probe.LastDisposableEnum);
        Assert.Equal(2, probe.InitializationCount);
        Assert.Equal(OpeningDefensePreparationPhase.ProbeStationGrid, planner.Phase);
    }

    [Fact]
    public void ExistingCommonPoints_RefreshesAttributeInventoryBeforeProbingAttributePlacement()
    {
        RecordingGridProbe probe = new();
        OpeningDefensePreparationPlanner planner = new(probe);

        OpeningDefensePreparationDecision catapults = planner.Decide();
        planner.Observe(catapults.Action!, new JObject
        {
            ["catapults"] = new JArray(
                Point(101, false, -4, -2),
                Point(102, false, 4, -2))
        }, accepted: true);

        OpeningDefensePreparationDecision inventory = planner.Decide();
        Assert.Equal(OpeningDefensePreparationPhase.QueryPlacementDisposable, inventory.Phase);
        Assert.Equal("queryDisposable", inventory.Action?.Command);
        planner.Observe(
            inventory.Action!,
            DisposableInventory("FreePoint_Attribute", count: 1, instanceId: 801),
            accepted: true);

        Assert.Equal("FreePoint_Attribute", planner.PlacementDisposableEnum);
        Assert.Equal("FreePoint_Attribute", probe.LastDisposableEnum);
        Assert.Equal(1, probe.InitializationCount);
        Assert.Equal(OpeningDefensePreparationPhase.ProbeStationGrid, planner.Phase);
    }

    [Fact]
    public void ReadyOpeningDefense_QueriesAndPlacesRuntimeMovableSpecialStation()
    {
        RecordingGridProbe probe = new();
        OpeningDefensePreparationPlanner planner = new(probe);
        OpeningDefensePreparationDecision catapults = planner.Decide();
        planner.Observe(catapults.Action!, new JObject
        {
            ["catapults"] = new JArray(
                Point(101, true, 0, 4, "FreePoint_Attribute"),
                Point(102, false, 4, -2, "FreePoint"),
                Point(103, false, -4, -2, "FreePoint"))
        }, accepted: true);

        OpeningDefensePreparationDecision inventory = planner.Decide();
        Assert.Equal(OpeningDefensePreparationPhase.QuerySpecialStationDisposable, inventory.Phase);
        planner.Observe(inventory.Action!, SpecialInventory("闪电路径弹射点", 901), accepted: true);

        Assert.Equal("闪电路径弹射点", planner.PlacementDisposableEnum);
        Assert.Equal("闪电路径弹射点", probe.LastDisposableEnum);
        Assert.Equal(OpeningDefensePreparationPhase.ProbeStationGrid, planner.Phase);
    }

    private static JObject DisposableInventory() => new()
    {
        ["state"] = new JObject
        {
            ["isInPreview"] = false,
            ["items"] = new JArray()
        }
    };

    private static JObject DisposableInventory(string disposableEnum, int count, int instanceId) => new()
    {
        ["state"] = new JObject
        {
            ["isInPreview"] = false,
            ["items"] = new JArray(new JObject
            {
                ["index"] = 0,
                ["instanceId"] = instanceId,
                ["itemInstanceId"] = instanceId,
                ["active"] = true,
                ["buttonActive"] = true,
                ["disposableEnum"] = disposableEnum,
                ["count"] = count,
                ["interactionType"] = "GridChooseInteraction"
            })
        }
    };

    private static JObject SpecialInventory(string disposableEnum, int instanceId) => new()
    {
        ["state"] = new JObject
        {
            ["items"] = new JArray(new JObject
            {
                ["index"] = 2,
                ["instanceId"] = instanceId,
                ["itemInstanceId"] = instanceId,
                ["active"] = true,
                ["buttonActive"] = true,
                ["disposableEnum"] = disposableEnum,
                ["count"] = 1,
                ["interactionType"] = "GridChooseInteraction",
                ["effectFacts"] = new JObject
                {
                    ["stationKind"] = "CommonCatapult",
                    ["canAlwaysMove"] = true,
                    ["buffIdentity"] = "LightningPath"
                }
            })
        }
    };

    private static JObject IdleInteractionGuard() => new()
    {
        ["state"] = new JObject
        {
            ["noActiveInteraction"] = true,
            ["observationConsistent"] = true,
            ["isInPreview"] = false,
            ["hasLastInteraction"] = false
        }
    };

    private static JObject Point(
        int instanceId,
        bool isAttribute,
        int x,
        int y,
        string? recycleDisposableEnum = null) => new()
    {
        ["linePointInstanceId"] = instanceId,
        ["isAttribute"] = isAttribute,
        ["recycleDisposableEnum"] = recycleDisposableEnum,
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
        public bool TryInitialize(
            string disposableEnum,
            JObject? catapultResult,
            bool placementIsAttribute,
            out string error)
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

    private sealed class RecordingGridProbe : IOpeningDefenseGridProbe
    {
        private int _probeCount;

        public string LastDisposableEnum { get; private set; } = string.Empty;
        public int InitializationCount { get; private set; }

        public bool TryInitialize(
            string disposableEnum,
            JObject? catapultResult,
            bool placementIsAttribute,
            out string error)
        {
            LastDisposableEnum = disposableEnum;
            InitializationCount++;
            _probeCount = 0;
            error = string.Empty;
            return true;
        }

        public OpeningDefenseGridProbeResult ProbeNext()
        {
            _probeCount++;
            return _probeCount == 1
                ? new OpeningDefenseGridProbeResult(OpeningDefenseGridProbeStatus.Probing, totalProbed: 1, detail: "继续")
                : new OpeningDefenseGridProbeResult(OpeningDefenseGridProbeStatus.Found, new OpeningDefenseGrid(4, -2), 2);
        }

        public void Reset()
        {
            _probeCount = 0;
        }
    }
}
