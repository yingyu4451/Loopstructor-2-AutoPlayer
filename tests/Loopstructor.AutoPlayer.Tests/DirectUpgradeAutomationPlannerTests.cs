using Loopstructor.AutoPlayer.Core;
using Newtonsoft.Json.Linq;

namespace Loopstructor.AutoPlayer.Tests;

public sealed class DirectUpgradeAutomationPlannerTests
{
    [Fact]
    public void Selecting_PicksHighestBaseOutputThenStableInstanceIdentity()
    {
        JObject state = State("Selecting",
            Vehicle(300, 30, 20d, eligible: true),
            Vehicle(200, 20, 20d, eligible: true),
            Vehicle(100, 10, 99d, eligible: false));

        AutomationAction action = new DirectUpgradeAutomationPlanner().Decide(state);

        Assert.Equal("selectDirectUpgradeVehicle", action.Command);
        Assert.Equal(200, action.Arguments.Value<int>("vehicleInstanceId"));
        Assert.Equal(20, action.Arguments.Value<int>("itemInstanceId"));
        Assert.DoesNotContain("L1", action.Reason, StringComparison.Ordinal);
        Assert.DoesNotContain("L3", action.Reason, StringComparison.Ordinal);
    }

    [Fact]
    public void SelectedVehicle_IsConfirmedWithAllPersonalEnchantmentsInFingerprint()
    {
        JObject vehicle = Vehicle(200, 20, 20d, eligible: true);
        vehicle["personalEnchantments"] = new JArray(
            Enumerable.Range(0, 12).Select(index => Enchantment("Personal" + index, index + 1)));
        JObject state = State("Selecting", vehicle);
        state["selectedVehicleInstanceId"] = 200;

        AutomationAction action = new DirectUpgradeAutomationPlanner().Decide(state);

        Assert.Equal("confirmDirectUpgradeVehicle", action.Command);
        string fingerprint = action.Arguments.Value<string>("enchantmentFingerprint")!;
        Assert.Contains("Personal0", fingerprint);
        Assert.Contains("Personal11", fingerprint);
    }

    [Fact]
    public void RewardSelection_PrefersExistingSameNamePersonalEnchantment()
    {
        JObject vehicle = Vehicle(200, 20, 20d, eligible: false);
        vehicle["personalEnchantments"] = new JArray(
            Enchantment("Burn", 2),
            Enchantment("Poison_Train", 9));
        JObject state = State("RewardSelecting", vehicle);
        state["selectedVehicleInstanceId"] = 200;
        state["rewards"] = new JArray(
            Reward(500, 0, "Freeze"),
            Reward(501, 1, "Poison_Train"),
            Reward(502, 2, "Burn"));

        AutomationAction action = new DirectUpgradeAutomationPlanner().Decide(state);

        Assert.Equal("chooseDirectUpgradeEnchantment", action.Command);
        Assert.Equal(502, action.Arguments.Value<int>("rewardInstanceId"));
        Assert.Equal("Burn", action.Arguments.Value<string>("fetterEnum"));
        Assert.Contains("同名", action.Reason);
    }

    [Fact]
    public void RewardSelection_RequiresExactlyThreeStableCandidatesAndOtherwiseUsesLowestIndex()
    {
        JObject vehicle = Vehicle(200, 20, 20d, eligible: false);
        JObject unstable = State("RewardSelecting", vehicle);
        unstable["selectedVehicleInstanceId"] = 200;
        unstable["rewards"] = new JArray(Reward(500, 0, "Freeze"), Reward(501, 1, "Burn"));
        Assert.Equal("wait", new DirectUpgradeAutomationPlanner().Decide(unstable).Command);

        JObject stable = (JObject)unstable.DeepClone();
        stable["rewards"] = new JArray(
            Reward(502, 2, "Burn"),
            Reward(500, 0, "Freeze"),
            Reward(501, 1, "Shock"));
        AutomationAction action = new DirectUpgradeAutomationPlanner().Decide(stable);
        Assert.Equal(500, action.Arguments.Value<int>("rewardInstanceId"));
    }

    [Fact]
    public void Settlement_ConfirmsOnlyAfterUpgradeAndEnchantmentIntegrityAreVerified()
    {
        JObject vehicle = Vehicle(200, 20, 20d, eligible: false);
        vehicle["upgraded"] = true;
        JObject state = State("Settlement", vehicle);
        state["selectedVehicleInstanceId"] = 200;
        state["originalEnchantmentsPreserved"] = true;
        state["rewardApplied"] = false;
        Assert.Equal("wait", new DirectUpgradeAutomationPlanner().Decide(state).Command);

        state["rewardApplied"] = true;
        AutomationAction action = new DirectUpgradeAutomationPlanner().Decide(state);
        Assert.Equal("confirmDirectUpgradeSettlement", action.Command);
        Assert.Equal(200, action.Arguments.Value<int>("vehicleInstanceId"));
    }

    private static JObject State(string phase, params JObject[] vehicles) => new()
    {
        ["panelOpen"] = true,
        ["panelInstanceId"] = 900,
        ["phase"] = phase,
        ["vehicles"] = new JArray(vehicles)
    };

    private static JObject Vehicle(
        int vehicleInstanceId,
        int itemInstanceId,
        double power,
        bool eligible) => new()
    {
        ["vehicleInstanceId"] = vehicleInstanceId,
        ["itemInstanceId"] = itemInstanceId,
        ["realVehicle"] = true,
        ["eligible"] = eligible,
        ["upgraded"] = false,
        ["baseCombatPower"] = power,
        ["personalEnchantments"] = new JArray()
    };

    private static JObject Enchantment(string name, int level) => new()
    {
        ["fetterEnum"] = name,
        ["level"] = level,
        ["count"] = 1
    };

    private static JObject Reward(int instanceId, int index, string name) => new()
    {
        ["instanceId"] = instanceId,
        ["index"] = index,
        ["fetterEnum"] = name
    };
}
