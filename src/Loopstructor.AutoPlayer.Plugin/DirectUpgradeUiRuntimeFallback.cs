using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Newtonsoft.Json.Linq;
using UnityEngine;

namespace Loopstructor.AutoPlayer.Plugin;

/// <summary>
/// Identity-safe adapter for the decoration-factory direct-upgrade UI. Every mutation validates
/// panel phase and object identities first, then returns a fresh state used for read-only
/// reconciliation.
/// </summary>
internal static class DirectUpgradeUiRuntimeFallback
{
    private const string PanelTypeName = "MetroTD.UISystem.RebuildUI_DirectUpgradePanel";
    private const string VehicleItemTypeName = "MetroTD.UISystem.RebuildUI_DirectUpgradeVehicleItem";
    private const string RewardItemTypeName = "MetroTD.UISystem.RebuildUI_DirectUpgradeRewardItem";
    private const string Source = "pluginReflection:DirectUpgrade";
    private static readonly Dictionary<int, UpgradeContext> Contexts = new();
    private static ReflectionContract? _contract;

    internal static bool IsAvailable => TryGetContract(out _);

    internal static bool TryQuery(out JObject result)
    {
        result = null!;
        if (!TryGetContract(out ReflectionContract contract)) return false;
        try
        {
            Component? panel = FindPanel(contract);
            result = panel == null
                ? Success("装修厂升级面板未打开。", new JObject
                {
                    ["panelOpen"] = false,
                    ["phase"] = "Resolved",
                    ["source"] = Source
                })
                : Success("已读取装修厂升级面板。", BuildState(contract, panel));
            return true;
        }
        catch (Exception exception)
        {
            result = Error("读取装修厂升级面板失败：" + Unwrap(exception).Message, false);
            return true;
        }
    }

    internal static bool TrySelectVehicle(JObject? arguments, out JObject result) =>
        TryMutate(arguments, "Selecting", (contract, panel, state) =>
        {
            int itemInstanceId = ReadInt(arguments?["itemInstanceId"]);
            int vehicleInstanceId = ReadInt(arguments?["vehicleInstanceId"]);
            Component? item = FindVehicleItems(contract, panel).SingleOrDefault(candidate =>
                candidate.GetInstanceID() == itemInstanceId &&
                VehicleInstanceId(contract, candidate) == vehicleInstanceId);
            JObject? vehicle = Vehicles(state).SingleOrDefault(candidate =>
                ReadInt(candidate["vehicleInstanceId"]) == vehicleInstanceId &&
                ReadInt(candidate["itemInstanceId"]) == itemInstanceId);
            if (item == null || vehicle == null ||
                !string.Equals(
                    arguments?["enchantmentFingerprint"]?.Value<string>() ?? string.Empty,
                    vehicle["enchantmentFingerprint"]?.Value<string>() ?? string.Empty,
                    StringComparison.Ordinal))
                return MutationFailure("目标战车或个人附魔身份已变化，未选择。", state);

            int panelId = panel.GetInstanceID();
            Contexts[panelId] = new UpgradeContext(
                vehicleInstanceId,
                ReadEnchantments(vehicle),
                string.Empty);
            contract.HandleSelectionChanged.Invoke(panel, new object[] { item, true });
            JObject after = BuildState(contract, panel);
            return ReadInt(after["selectedVehicleInstanceId"]) == vehicleInstanceId
                ? Success("已选择装修厂升级目标。", after)
                : MutationFailure("选择动作未能绑定目标战车。", after, true);
        }, out result);

    internal static bool TryConfirmVehicle(JObject? arguments, out JObject result) =>
        TryMutate(arguments, "Selecting", (contract, panel, state) =>
        {
            int vehicleInstanceId = ReadInt(arguments?["vehicleInstanceId"]);
            JObject? vehicle = Vehicles(state).SingleOrDefault(candidate =>
                ReadInt(candidate["vehicleInstanceId"]) == vehicleInstanceId);
            if (ReadInt(state["selectedVehicleInstanceId"]) != vehicleInstanceId || vehicle == null ||
                !string.Equals(
                    arguments?["enchantmentFingerprint"]?.Value<string>() ?? string.Empty,
                    vehicle["enchantmentFingerprint"]?.Value<string>() ?? string.Empty,
                    StringComparison.Ordinal))
                return MutationFailure("确认前目标战车或附魔快照已变化，未确认。", state);

            int panelId = panel.GetInstanceID();
            if (!Contexts.TryGetValue(panelId, out UpgradeContext? stored) ||
                stored.VehicleInstanceId != vehicleInstanceId)
            {
                Contexts[panelId] = new UpgradeContext(
                    vehicleInstanceId,
                    ReadEnchantments(vehicle),
                    string.Empty);
            }

            contract.ConfirmDirectUpgrade.Invoke(panel, null);
            JObject after = BuildState(contract, panel);
            return string.Equals(after["phase"]?.Value<string>(), "RewardSelecting", StringComparison.Ordinal) &&
                   (after["rewards"] as JArray)?.Count == 3
                ? Success("已确认升级目标并读取三个附魔候选。", after)
                : MutationFailure("确认后未进入稳定的三选一附魔阶段。", after, true);
        }, out result);

    internal static bool TryChooseEnchantment(JObject? arguments, out JObject result) =>
        TryMutate(arguments, "RewardSelecting", (contract, panel, state) =>
        {
            int vehicleInstanceId = ReadInt(arguments?["vehicleInstanceId"]);
            int rewardInstanceId = ReadInt(arguments?["rewardInstanceId"]);
            int rewardIndex = ReadInt(arguments?["rewardIndex"], -1);
            string fetterEnum = arguments?["fetterEnum"]?.Value<string>() ?? string.Empty;
            JObject? vehicle = Vehicles(state).SingleOrDefault(candidate =>
                ReadInt(candidate["vehicleInstanceId"]) == vehicleInstanceId);
            JObject? reward = (state["rewards"] as JArray)?.OfType<JObject>().SingleOrDefault(candidate =>
                ReadInt(candidate["instanceId"]) == rewardInstanceId &&
                ReadInt(candidate["index"], -1) == rewardIndex &&
                string.Equals(candidate["fetterEnum"]?.Value<string>(), fetterEnum, StringComparison.Ordinal));
            Component? rewardItem = FindRewardItems(contract, panel).SingleOrDefault(candidate =>
                candidate.GetInstanceID() == rewardInstanceId);
            object? module = rewardItem == null ? null : contract.RewardModule.GetValue(rewardItem, null);
            if (vehicle == null || reward == null || module == null ||
                ReadInt(state["selectedVehicleInstanceId"]) != vehicleInstanceId ||
                !string.Equals(
                    arguments?["enchantmentFingerprint"]?.Value<string>() ?? string.Empty,
                    vehicle["enchantmentFingerprint"]?.Value<string>() ?? string.Empty,
                    StringComparison.Ordinal))
                return MutationFailure("附魔提交前目标、候选或个人附魔快照已变化，未提交。", state);

            int panelId = panel.GetInstanceID();
            UpgradeContext baseline = Contexts.TryGetValue(panelId, out UpgradeContext? stored) &&
                                      stored.VehicleInstanceId == vehicleInstanceId
                ? stored
                : new UpgradeContext(vehicleInstanceId, ReadEnchantments(vehicle), string.Empty);
            Contexts[panelId] = new UpgradeContext(
                baseline.VehicleInstanceId,
                baseline.OriginalEnchantments,
                fetterEnum);
            contract.SubmitReward.Invoke(panel, new[] { module });
            JObject after = BuildState(contract, panel);
            return string.Equals(after["phase"]?.Value<string>(), "Settlement", StringComparison.Ordinal) &&
                   after["originalEnchantmentsPreserved"]?.Value<bool>() == true &&
                   after["rewardApplied"]?.Value<bool>() == true
                ? Success("装修厂升级与附魔选择已完成。", after)
                : MutationFailure("升级后未能验证原个人附魔完整保留及新附魔生效。", after, true);
        }, out result);

    internal static bool TryConfirmSettlement(JObject? arguments, out JObject result) =>
        TryMutate(arguments, "Settlement", (contract, panel, state) =>
        {
            int vehicleInstanceId = ReadInt(arguments?["vehicleInstanceId"]);
            if (ReadInt(state["selectedVehicleInstanceId"]) != vehicleInstanceId ||
                state["originalEnchantmentsPreserved"]?.Value<bool>() != true ||
                state["rewardApplied"]?.Value<bool>() != true)
                return MutationFailure("结算前升级或附魔完整性验证失败，未确认。", state);

            contract.ConfirmSettlement.Invoke(panel, null);
            Contexts.Remove(panel.GetInstanceID());
            return Success("已确认装修厂升级结算。", new JObject
            {
                ["panelOpen"] = false,
                ["phase"] = "Resolved",
                ["vehicleInstanceId"] = vehicleInstanceId,
                ["invocationStarted"] = true,
                ["source"] = Source
            });
        }, out result);

    private static bool TryMutate(
        JObject? arguments,
        string expectedPhase,
        Func<ReflectionContract, Component, JObject, JObject> mutation,
        out JObject result)
    {
        result = null!;
        if (!TryGetContract(out ReflectionContract contract)) return false;
        Component? panel = FindPanel(contract);
        if (panel == null)
        {
            result = Error("装修厂升级面板身份已失效，未执行写入。", false);
            return true;
        }
        JObject state = BuildState(contract, panel);
        if (ReadInt(arguments?["panelInstanceId"]) != panel.GetInstanceID() ||
            !string.Equals(state["phase"]?.Value<string>(), expectedPhase, StringComparison.Ordinal))
        {
            result = MutationFailure("装修厂面板身份或阶段已变化，未执行写入。", state);
            return true;
        }
        try
        {
            result = mutation(contract, panel, state);
            return true;
        }
        catch (Exception exception)
        {
            JObject uncertain = (JObject)state.DeepClone();
            uncertain["outcomeUnknown"] = true;
            uncertain["needsReconciliation"] = true;
            uncertain["invocationStarted"] = true;
            result = Error("装修厂升级写入异常：" + Unwrap(exception).Message, true, uncertain);
            return true;
        }
    }

    private static JObject BuildState(ReflectionContract contract, Component panel)
    {
        string phase = contract.PanelState.GetValue(panel)?.ToString() ?? string.Empty;
        object? selectedVehicle = contract.CurChooseVehicle.GetValue(panel, null);
        int selectedVehicleId = selectedVehicle is UnityEngine.Object selectedObject
            ? selectedObject.GetInstanceID()
            : 0;
        JArray vehicles = new(FindVehicleItems(contract, panel)
            .Select(item => BuildVehicle(contract, item))
            .Where(item => item != null)
            .Cast<JObject>()
            .GroupBy(item => ReadInt(item["vehicleInstanceId"]))
            .Select(group => group.First())
            .OrderBy(item => ReadInt(item["vehicleInstanceId"])));
        if (selectedVehicle != null &&
            vehicles.OfType<JObject>().All(item => ReadInt(item["vehicleInstanceId"]) != selectedVehicleId))
        {
            JObject selected = BuildVehicleFromController(selectedVehicle, 0);
            vehicles.Add(selected);
        }

        JArray rewards = new(FindRewardItems(contract, panel)
            .Select((item, index) => BuildReward(contract, item, index))
            .Where(item => item != null)
            .Cast<JObject>()
            .OrderBy(item => ReadInt(item["index"])));
        JObject state = new()
        {
            ["panelOpen"] = panel.gameObject.activeInHierarchy,
            ["panelInstanceId"] = panel.GetInstanceID(),
            ["phase"] = phase,
            ["isSubmitting"] = contract.IsSubmitting.GetValue(panel) is bool submitting && submitting,
            ["selectedVehicleInstanceId"] = selectedVehicleId,
            ["vehicles"] = vehicles,
            ["rewards"] = rewards,
            ["rewardCount"] = rewards.Count,
            ["source"] = Source
        };
        if (Contexts.TryGetValue(panel.GetInstanceID(), out UpgradeContext? context) &&
            context.VehicleInstanceId == selectedVehicleId)
        {
            JObject? selected = vehicles.OfType<JObject>().SingleOrDefault(item =>
                ReadInt(item["vehicleInstanceId"]) == selectedVehicleId);
            IReadOnlyDictionary<string, int> current = selected == null
                ? new Dictionary<string, int>()
                : ReadEnchantments(selected);
            state["originalEnchantmentsPreserved"] = context.OriginalEnchantments.All(pair =>
                current.TryGetValue(pair.Key, out int value) && value >= pair.Value);
            state["rewardApplied"] = !string.IsNullOrWhiteSpace(context.RewardFetter) &&
                                     current.TryGetValue(context.RewardFetter, out int rewardValue) &&
                                     rewardValue > context.OriginalEnchantments.GetValueOrDefault(context.RewardFetter);
        }
        else
        {
            state["originalEnchantmentsPreserved"] = false;
            state["rewardApplied"] = false;
        }
        return state;
    }

    private static JObject? BuildVehicle(ReflectionContract contract, Component item)
    {
        object? vehicle = contract.ItemVehicle.GetValue(item, null);
        return vehicle == null ? null : BuildVehicleFromController(vehicle, item.GetInstanceID());
    }

    private static JObject BuildVehicleFromController(object vehicle, int itemInstanceId)
    {
        Component? component = vehicle as Component;
        JArray enchantments = new(ReadPersonalEnchantments(vehicle));
        int level = ReadMemberInt(vehicle, "level");
        return new JObject
        {
            ["itemInstanceId"] = itemInstanceId,
            ["vehicleInstanceId"] = component?.GetInstanceID() ?? 0,
            ["vehicleType"] = ReadMember(vehicle, "vehicleType")?.ToString() ?? string.Empty,
            ["internalLevel"] = level,
            ["realVehicle"] = ReadMemberBool(vehicle, "IsFixedHead") != true &&
                              ReadMemberBool(vehicle, "IsRuntimeProjection") != true,
            ["eligible"] = level == 1,
            ["upgraded"] = level == 3,
            ["baseCombatPower"] = component == null ? 0d : IndependentVehicleRuntimeFallback.ReadBaseCombatPower(component),
            ["personalEnchantments"] = enchantments,
            ["enchantmentFingerprint"] = Fingerprint(enchantments)
        };
    }

    private static JObject? BuildReward(ReflectionContract contract, Component item, int index)
    {
        object? module = contract.RewardModule.GetValue(item, null);
        if (module == null) return null;
        string fetter = ReadMember(module, "fetterEnum")?.ToString() ?? string.Empty;
        if (string.IsNullOrWhiteSpace(fetter) || fetter.EndsWith("_Train", StringComparison.Ordinal)) return null;
        return new JObject
        {
            ["index"] = index,
            ["instanceId"] = item.GetInstanceID(),
            ["fetterEnum"] = fetter,
            ["level"] = ReadMemberInt(module, "level"),
            ["count"] = ReadMemberInt(module, "count")
        };
    }

    private static IEnumerable<JObject> ReadPersonalEnchantments(object vehicle)
    {
        if (ReadMember(vehicle, "CurrentFetterModuleDatas") is not IEnumerable modules) yield break;
        foreach (object? module in modules)
        {
            if (module == null) continue;
            string name = ReadMember(module, "fetterEnum")?.ToString() ?? string.Empty;
            if (string.IsNullOrWhiteSpace(name) || name == "None" || name.EndsWith("_Train", StringComparison.Ordinal))
                continue;
            yield return new JObject
            {
                ["fetterEnum"] = name,
                ["level"] = Math.Max(1, ReadMemberInt(module, "level")),
                ["count"] = Math.Max(1, ReadMemberInt(module, "count"))
            };
        }
    }

    private static IReadOnlyDictionary<string, int> ReadEnchantments(JObject vehicle) =>
        (vehicle["personalEnchantments"] as JArray)?.OfType<JObject>()
            .GroupBy(item => item["fetterEnum"]?.Value<string>() ?? string.Empty, StringComparer.Ordinal)
            .Where(group => !string.IsNullOrWhiteSpace(group.Key))
            .ToDictionary(
                group => group.Key,
                group => group.Sum(item => Math.Max(1, ReadInt(item["level"])) * Math.Max(1, ReadInt(item["count"]))),
                StringComparer.Ordinal)
        ?? new Dictionary<string, int>(StringComparer.Ordinal);

    private static string Fingerprint(JArray enchantments) => string.Join(
        "|",
        enchantments.OfType<JObject>()
            .Select(item => string.Join(":", item["fetterEnum"], ReadInt(item["level"]), ReadInt(item["count"])))
            .OrderBy(value => value, StringComparer.Ordinal));

    private static IEnumerable<Component> FindVehicleItems(ReflectionContract contract, Component panel) =>
        Resources.FindObjectsOfTypeAll(contract.VehicleItemType).OfType<Component>()
            .Where(item => item != null && item.transform.IsChildOf(panel.transform));

    private static IEnumerable<Component> FindRewardItems(ReflectionContract contract, Component panel) =>
        Resources.FindObjectsOfTypeAll(contract.RewardItemType).OfType<Component>()
            .Where(item => item != null && item.transform.IsChildOf(panel.transform) && item.gameObject.activeInHierarchy)
            .OrderBy(item => item.transform.GetSiblingIndex())
            .ThenBy(item => item.GetInstanceID());

    private static int VehicleInstanceId(ReflectionContract contract, Component item) =>
        contract.ItemVehicle.GetValue(item, null) is UnityEngine.Object vehicle ? vehicle.GetInstanceID() : 0;

    private static Component? FindPanel(ReflectionContract contract)
    {
        object? instance = contract.Instance.GetValue(null, null);
        return instance as Component is { } panel && panel.gameObject.activeInHierarchy ? panel : null;
    }

    private static bool TryGetContract(out ReflectionContract contract)
    {
        if (_contract != null) { contract = _contract; return true; }
        Type? panelType = FindType(PanelTypeName);
        Type? vehicleItemType = FindType(VehicleItemTypeName);
        Type? rewardItemType = FindType(RewardItemTypeName);
        const BindingFlags All = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static;
        PropertyInfo? instance = panelType?.GetProperty("Instance", All);
        FieldInfo? panelState = panelType?.GetField("m_panelState", All);
        FieldInfo? isSubmitting = panelType?.GetField("m_isSubmitting", All);
        MethodInfo? handleSelectionChanged = panelType?.GetMethod("HandleSelectionChanged", All);
        MethodInfo? confirmDirectUpgrade = panelType?.GetMethod("ConfirmDirectUpgrade", All);
        MethodInfo? submitReward = panelType?.GetMethod("SubmitReward", All);
        MethodInfo? confirmSettlement = panelType?.GetMethod("ConfirmSettlement", All);
        PropertyInfo? curChooseVehicle = panelType?.GetProperty("CurChooseVehicle", All);
        PropertyInfo? itemVehicle = vehicleItemType?.GetProperty("VehicleController", All);
        PropertyInfo? rewardModule = rewardItemType?.GetProperty("FetterModuleData", All);
        if (panelType == null || vehicleItemType == null || rewardItemType == null || instance == null ||
            panelState == null || isSubmitting == null || handleSelectionChanged == null ||
            confirmDirectUpgrade == null || submitReward == null || confirmSettlement == null ||
            curChooseVehicle == null || itemVehicle == null || rewardModule == null)
        {
            contract = null!;
            return false;
        }
        _contract = contract = new ReflectionContract(
            instance, panelState, isSubmitting, handleSelectionChanged, confirmDirectUpgrade,
            submitReward, confirmSettlement, curChooseVehicle, vehicleItemType, itemVehicle,
            rewardItemType, rewardModule);
        return true;
    }

    private static Type? FindType(string fullName) => AppDomain.CurrentDomain.GetAssemblies()
        .Select(assembly => assembly.GetType(fullName, false)).FirstOrDefault(type => type != null);

    private static object? ReadMember(object? target, string name)
    {
        if (target == null) return null;
        const BindingFlags All = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance;
        Type type = target.GetType();
        return type.GetProperty(name, All)?.GetValue(target, null) ?? type.GetField(name, All)?.GetValue(target);
    }
    private static int ReadMemberInt(object target, string name) => ConvertToInt(ReadMember(target, name));
    private static bool? ReadMemberBool(object target, string name) => ReadMember(target, name) as bool?;
    private static int ConvertToInt(object? value) { try { return value == null ? 0 : Convert.ToInt32(value); } catch { return 0; } }
    private static int ReadInt(JToken? token, int fallback = 0) => token?.Type == JTokenType.Integer ? token.Value<int>() : fallback;
    private static Exception Unwrap(Exception exception) => exception is TargetInvocationException target && target.InnerException != null ? target.InnerException : exception;
    private static IEnumerable<JObject> Vehicles(JObject state) => (state["vehicles"] as JArray)?.OfType<JObject>() ?? Enumerable.Empty<JObject>();

    private static JObject Success(string message, JObject state) => new()
    {
        ["success"] = true, ["message"] = message, ["suggestion"] = string.Empty,
        ["data"] = new JObject { ["state"] = state }
    };
    private static JObject MutationFailure(string message, JObject state, bool started = false) => Error(message, started, state);
    private static JObject Error(string message, bool started, JObject? state = null)
    {
        JObject value = state == null ? new JObject() : (JObject)state.DeepClone();
        value["invocationStarted"] = started;
        return new JObject
        {
            ["success"] = false, ["message"] = message,
            ["suggestion"] = started ? "写入可能已开始；只读对账且不要重放。" : "重新读取面板后再规划。",
            ["data"] = new JObject { ["state"] = value }
        };
    }

    private sealed class ReflectionContract
    {
        public ReflectionContract(PropertyInfo instance, FieldInfo panelState, FieldInfo isSubmitting,
            MethodInfo handleSelectionChanged, MethodInfo confirmDirectUpgrade, MethodInfo submitReward,
            MethodInfo confirmSettlement, PropertyInfo curChooseVehicle, Type vehicleItemType,
            PropertyInfo itemVehicle, Type rewardItemType, PropertyInfo rewardModule)
        {
            Instance = instance; PanelState = panelState; IsSubmitting = isSubmitting;
            HandleSelectionChanged = handleSelectionChanged; ConfirmDirectUpgrade = confirmDirectUpgrade;
            SubmitReward = submitReward; ConfirmSettlement = confirmSettlement; CurChooseVehicle = curChooseVehicle;
            VehicleItemType = vehicleItemType; ItemVehicle = itemVehicle; RewardItemType = rewardItemType;
            RewardModule = rewardModule;
        }
        public PropertyInfo Instance { get; }
        public FieldInfo PanelState { get; }
        public FieldInfo IsSubmitting { get; }
        public MethodInfo HandleSelectionChanged { get; }
        public MethodInfo ConfirmDirectUpgrade { get; }
        public MethodInfo SubmitReward { get; }
        public MethodInfo ConfirmSettlement { get; }
        public PropertyInfo CurChooseVehicle { get; }
        public Type VehicleItemType { get; }
        public PropertyInfo ItemVehicle { get; }
        public Type RewardItemType { get; }
        public PropertyInfo RewardModule { get; }
    }

    private sealed class UpgradeContext
    {
        public UpgradeContext(
            int vehicleInstanceId,
            IReadOnlyDictionary<string, int> originalEnchantments,
            string rewardFetter)
        {
            VehicleInstanceId = vehicleInstanceId;
            OriginalEnchantments = originalEnchantments;
            RewardFetter = rewardFetter;
        }

        public int VehicleInstanceId { get; }
        public IReadOnlyDictionary<string, int> OriginalEnchantments { get; }
        public string RewardFetter { get; }
    }
}
