using System;
using System.Collections.Generic;
using System.Linq;
using Newtonsoft.Json.Linq;

namespace Loopstructor.AutoPlayer.Core;

/// <summary>
/// Plans the decoration-factory single-vehicle upgrade flow exclusively from stable panel,
/// vehicle and reward identities. Internal level numbers are treated as runtime compatibility
/// facts and never appear in user-facing reasons.
/// </summary>
public sealed class DirectUpgradeAutomationPlanner
{
    public AutomationAction Decide(JObject? result)
    {
        JObject state = State(result);
        if (state["panelOpen"]?.Value<bool>() != true)
        {
            return AutomationAction.Wait(AutomationStage.ManagingEvent, "等待装修厂升级面板打开。");
        }

        string phase = state["phase"]?.Value<string>() ?? string.Empty;
        int panelInstanceId = ReadInt(state["panelInstanceId"]);
        if (panelInstanceId == 0)
        {
            return AutomationAction.Wait(AutomationStage.ManagingEvent, "装修厂升级面板缺少稳定身份。");
        }

        return phase switch
        {
            "Selecting" => DecideSelecting(state, panelInstanceId),
            "RewardSelecting" => DecideReward(state, panelInstanceId),
            "Settlement" => DecideSettlement(state, panelInstanceId),
            "Resolved" => AutomationAction.Wait(AutomationStage.ManagingEvent, "装修厂升级流程已经结束。"),
            _ => AutomationAction.Wait(AutomationStage.ManagingEvent, "等待装修厂升级面板进入稳定阶段。")
        };
    }

    private static AutomationAction DecideSelecting(JObject state, int panelInstanceId)
    {
        int selectedVehicleInstanceId = ReadInt(state["selectedVehicleInstanceId"]);
        JObject? selected = Vehicles(state)
            .SingleOrDefault(vehicle => ReadInt(vehicle["vehicleInstanceId"]) == selectedVehicleInstanceId);
        if (selected != null)
        {
            return new AutomationAction(
                "confirmDirectUpgradeVehicle",
                JObject.FromObject(new
                {
                    panelInstanceId,
                    vehicleInstanceId = selectedVehicleInstanceId,
                    enchantmentFingerprint = EnchantmentFingerprint(selected)
                }),
                AutomationStage.ManagingEvent,
                "确认已锁定的未升级战车，并读取三个附魔候选。");
        }

        JObject? target = Vehicles(state)
            .Where(IsEligible)
            .OrderByDescending(vehicle => ReadDouble(vehicle["baseCombatPower"]))
            .ThenBy(vehicle => ReadInt(vehicle["vehicleInstanceId"], int.MaxValue))
            .FirstOrDefault();
        if (target == null)
        {
            return AutomationAction.Wait(AutomationStage.ManagingEvent, "装修厂当前没有真实、未升级且可升级的战车。");
        }

        return new AutomationAction(
            "selectDirectUpgradeVehicle",
            JObject.FromObject(new
            {
                panelInstanceId,
                vehicleInstanceId = ReadInt(target["vehicleInstanceId"]),
                itemInstanceId = ReadInt(target["itemInstanceId"]),
                enchantmentFingerprint = EnchantmentFingerprint(target)
            }),
            AutomationStage.ManagingEvent,
            "按基础输出选择未升级战车；平局使用稳定实例身份。");
    }

    private static AutomationAction DecideReward(JObject state, int panelInstanceId)
    {
        int vehicleInstanceId = ReadInt(state["selectedVehicleInstanceId"]);
        JObject? vehicle = Vehicles(state)
            .SingleOrDefault(item => ReadInt(item["vehicleInstanceId"]) == vehicleInstanceId);
        JObject[] rewards = (state["rewards"] as JArray)?.OfType<JObject>()
            .Where(item => ReadInt(item["instanceId"]) != 0)
            .OrderBy(item => ReadInt(item["index"], int.MaxValue))
            .ThenBy(item => ReadInt(item["instanceId"], int.MaxValue))
            .ToArray() ?? Array.Empty<JObject>();
        if (vehicle == null || rewards.Length != 3)
        {
            return AutomationAction.Wait(AutomationStage.ManagingEvent, "等待三个稳定附魔候选和目标战车身份。");
        }

        HashSet<string> existing = new(PersonalEnchantments(vehicle), StringComparer.Ordinal);
        JObject selected = rewards
            .OrderByDescending(item => existing.Contains(item["fetterEnum"]?.Value<string>() ?? string.Empty))
            .ThenBy(item => ReadInt(item["index"], int.MaxValue))
            .ThenBy(item => ReadInt(item["instanceId"], int.MaxValue))
            .First();
        return new AutomationAction(
            "chooseDirectUpgradeEnchantment",
            JObject.FromObject(new
            {
                panelInstanceId,
                vehicleInstanceId,
                rewardInstanceId = ReadInt(selected["instanceId"]),
                rewardIndex = ReadInt(selected["index"]),
                fetterEnum = selected["fetterEnum"]?.Value<string>() ?? string.Empty,
                enchantmentFingerprint = EnchantmentFingerprint(vehicle)
            }),
            AutomationStage.ManagingEvent,
            existing.Contains(selected["fetterEnum"]?.Value<string>() ?? string.Empty)
                ? "优先选择与该战车已有个人附魔同名的候选，以触发同名升级。"
                : "没有同名个人附魔候选；选择稳定索引最小的附魔。");
    }

    private static AutomationAction DecideSettlement(JObject state, int panelInstanceId)
    {
        int vehicleInstanceId = ReadInt(state["selectedVehicleInstanceId"]);
        JObject? vehicle = Vehicles(state)
            .SingleOrDefault(item => ReadInt(item["vehicleInstanceId"]) == vehicleInstanceId);
        if (vehicle == null || vehicle["upgraded"]?.Value<bool>() != true ||
            state["originalEnchantmentsPreserved"]?.Value<bool>() != true ||
            state["rewardApplied"]?.Value<bool>() != true)
        {
            return AutomationAction.Wait(AutomationStage.ManagingEvent, "正在验证升级形态与个人附魔完整性。");
        }

        return new AutomationAction(
            "confirmDirectUpgradeSettlement",
            JObject.FromObject(new { panelInstanceId, vehicleInstanceId }),
            AutomationStage.ManagingEvent,
            "升级形态和附魔变化已验证，确认装修厂结算。");
    }

    private static IEnumerable<JObject> Vehicles(JObject state) =>
        (state["vehicles"] as JArray)?.OfType<JObject>() ?? Enumerable.Empty<JObject>();

    private static bool IsEligible(JObject vehicle) =>
        vehicle["realVehicle"]?.Value<bool>() == true &&
        vehicle["eligible"]?.Value<bool>() == true &&
        vehicle["upgraded"]?.Value<bool>() != true &&
        ReadInt(vehicle["vehicleInstanceId"]) != 0 &&
        ReadInt(vehicle["itemInstanceId"]) != 0;

    private static IEnumerable<string> PersonalEnchantments(JObject vehicle) =>
        (vehicle["personalEnchantments"] as JArray)?.OfType<JObject>()
            .Select(item => item["fetterEnum"]?.Value<string>() ?? string.Empty)
            .Where(name => !string.IsNullOrWhiteSpace(name) && !name.EndsWith("_Train", StringComparison.Ordinal))
        ?? Enumerable.Empty<string>();

    private static string EnchantmentFingerprint(JObject vehicle) => string.Join(
        "|",
        (vehicle["personalEnchantments"] as JArray)?.OfType<JObject>()
            .Select(item => string.Join(
                ":",
                item["fetterEnum"]?.Value<string>() ?? string.Empty,
                ReadInt(item["level"]),
                ReadInt(item["count"])))
            .OrderBy(value => value, StringComparer.Ordinal)
        ?? Enumerable.Empty<string>());

    private static JObject State(JObject? result) =>
        result?.SelectToken("data.state") as JObject ?? result?["state"] as JObject ?? result ?? new JObject();

    private static int ReadInt(JToken? token, int fallback = 0) =>
        token?.Type == JTokenType.Integer ? token.Value<int>() : fallback;

    private static double ReadDouble(JToken? token) =>
        token?.Type is JTokenType.Integer or JTokenType.Float ? token.Value<double>() : 0d;
}
