using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using Newtonsoft.Json.Linq;
using UnityEngine;

namespace Loopstructor.AutoPlayer.Plugin;

/// <summary>
/// Reads and operates the registered reward UI without whole-scene scans or full MCP snapshots.
/// </summary>
internal static class RewardUiRuntimeFallback
{
    private const string RewardPanelTypeName = "MetroTD.RewardSystem.RewardUIPanel";
    private const string RewardItemTypeName = "MetroTD.RewardSystem.GeneralRewardItemUI";
    private const string RewardMutexTypeName = "MetroTD.RewardSystem.GeneralRewardItemMutexController";
    private const string RewardSpawnerTypeName = "MetroTD.RewardSystem.RewardObjectSpawner";
    private const string RewardObjectTypeName = "RewardObjectBase";
    private const string RewardTypeName = "MetroTD.RewardSystem.Reward";
    private const string RazorRewardTypeName = "MetroTD.RewardSystem.RazorReward";
    private const string PotionRewardTypeName = "MetroTD.RewardSystem.PotionReward";
    private const string SuperModuleRewardTypeName = "MetroTD.RewardSystem.SuperModuleReward";
    private const string MapConfigControllerTypeName = "MetroTD.RoomSystem.LoopstructorMapCfgController";
    private const string ResultSource = "pluginReflection:RewardUI:light";

    private static ReflectionContract? _contract;

    internal static bool TryQueryState(out JObject result)
    {
        result = null!;
        if (!TryGetContract(out ReflectionContract contract))
        {
            return false;
        }

        try
        {
            RewardSnapshot snapshot = BuildSnapshot(contract);
            result = Success("\u5df2\u8bfb\u53d6\u8f7b\u91cf\u5956\u52b1\u754c\u9762\u72b6\u6001\u3002", snapshot.State);
            return true;
        }
        catch (Exception exception)
        {
            result = Error(
                "\u8bfb\u53d6\u5956\u52b1\u754c\u9762\u5931\u8d25\uff1a" + Unwrap(exception).Message,
                new JObject { ["source"] = ResultSource });
            return true;
        }
    }

    internal static bool TryChooseOption(JObject? arguments, out JObject result)
    {
        result = null!;
        if (!TryGetContract(out ReflectionContract contract))
        {
            return false;
        }

        RewardSnapshot before;
        try
        {
            before = BuildSnapshot(contract);
        }
        catch (Exception exception)
        {
            result = Error(
                "\u9009\u62e9\u5956\u52b1\u524d\u65e0\u6cd5\u8bfb\u53d6\u754c\u9762\u72b6\u6001\uff1a" + Unwrap(exception).Message,
                new JObject { ["source"] = ResultSource });
            return true;
        }

        if (!TryReadSelection(arguments, out string phaseToken, out int itemInstanceId, out int index))
        {
            result = SelectionError(
                "\u5956\u52b1\u9009\u62e9\u53c2\u6570\u4e0d\u5b8c\u6574\uff1b\u5fc5\u987b\u540c\u65f6\u63d0\u4f9b phaseToken\u3001itemInstanceId \u548c index\u3002",
                before.State);
            return true;
        }

        if (!before.PanelOpen)
        {
            result = SelectionError("\u5956\u52b1\u9762\u677f\u5df2\u7ecf\u5173\u95ed\u3002", before.State);
            return true;
        }

        if (!string.Equals(before.PhaseToken, phaseToken, StringComparison.Ordinal))
        {
            result = SelectionError("\u5956\u52b1\u9636\u6bb5\u5df2\u53d8\u5316\uff0c\u62d2\u7edd\u4f7f\u7528\u8fc7\u671f\u9009\u9879\u3002", before.State);
            return true;
        }

        RewardOptionSnapshot? target = before.Options.FirstOrDefault(option =>
            option.Index == index && option.InstanceId == itemInstanceId);
        if (target == null)
        {
            result = SelectionError("\u5956\u52b1\u9009\u9879\u5b9e\u4f8b\u4e0e\u7d22\u5f15\u4e0d\u5339\u914d\u3002", before.State);
            return true;
        }

        if (!before.MutexAvailable)
        {
            result = SelectionError("\u65e0\u6cd5\u9a8c\u8bc1\u5956\u52b1\u4e92\u65a5\u9501\uff0c\u4e3a\u907f\u514d\u91cd\u590d\u9886\u53d6\uff0c\u672c\u6b21\u672a\u6267\u884c\u70b9\u51fb\u3002", before.State);
            return true;
        }

        if (before.Busy)
        {
            result = SelectionError("\u5956\u52b1\u4e92\u65a5\u9501\u5fd9\uff0c\u672c\u6b21\u672a\u6267\u884c\u70b9\u51fb\u3002", before.State);
            return true;
        }

        if (before.Refresh || before.Finished)
        {
            result = SelectionError("\u5956\u52b1\u961f\u5217\u6b63\u5728\u5207\u6362\uff0c\u672c\u6b21\u672a\u6267\u884c\u70b9\u51fb\u3002", before.State);
            return true;
        }

        if (!target.ButtonActive)
        {
            result = SelectionError("\u6307\u5b9a\u5956\u52b1\u9009\u9879\u5f53\u524d\u4e0d\u53ef\u70b9\u51fb\u3002", before.State);
            return true;
        }

        bool invocationStarted = false;
        try
        {
            invocationStarted = true;
            contract.ClickEvent.Invoke(target.Item, null);

            RewardSnapshot after = BuildSnapshot(contract);
            after.State["invocationStarted"] = true;
            after.State["selectedPhaseToken"] = phaseToken;
            after.State["selectedItemInstanceId"] = itemInstanceId;
            after.State["selectedIndex"] = index;
            result = Success("\u5df2\u53d1\u9001\u4e00\u6b21\u5956\u52b1\u9009\u62e9\uff0c\u6b63\u5728\u7b49\u5f85\u754c\u9762\u6536\u655b\u3002", after.State);
            return true;
        }
        catch (Exception exception)
        {
            JObject failureState = before.State;
            failureState["source"] = ResultSource;
            if (invocationStarted)
            {
                failureState["outcomeUnknown"] = true;
                failureState["needsReconciliation"] = true;
                failureState["uncertaintyOrigin"] = "rewardClickException";
                failureState["invocationStarted"] = true;
            }

            result = Error(
                "\u5956\u52b1\u9009\u62e9\u8c03\u7528\u5931\u8d25\uff1a" + Unwrap(exception).Message,
                failureState);
            return true;
        }
    }

    internal static bool TryCollectRewardObject(JObject? arguments, out JObject result)
    {
        result = null!;
        if (!TryGetContract(out ReflectionContract contract))
        {
            return false;
        }

        if (!TryReadRewardObjectIdentity(arguments, out int instanceId))
        {
            result = Error(
                "\u6536\u53d6\u5956\u52b1\u7269\u53c2\u6570\u65e0\u6548\uff1b\u5fc5\u987b\u63d0\u4f9b\u975e\u96f6 instanceId\u3002",
                new JObject
                {
                    ["source"] = ResultSource,
                    ["targetInstanceId"] = instanceId,
                    ["invocationStarted"] = false
                });
            return true;
        }

        RewardObjectCollectionSnapshot before;
        try
        {
            before = BuildRewardObjectCollectionSnapshot(contract, instanceId);
        }
        catch (Exception exception)
        {
            result = Error(
                "\u6536\u53d6\u5956\u52b1\u7269\u524d\u65e0\u6cd5\u8bfb\u53d6\u5df2\u6ce8\u518c\u5bf9\u8c61\uff1a" + Unwrap(exception).Message,
                new JObject
                {
                    ["source"] = ResultSource,
                    ["targetInstanceId"] = instanceId,
                    ["invocationStarted"] = false
                });
            return true;
        }

        if (!before.SpawnerAvailable)
        {
            result = Error("\u5956\u52b1\u7269\u751f\u6210\u5668\u5f53\u524d\u4e0d\u53ef\u7528\uff0c\u672c\u6b21\u672a\u6267\u884c\u70b9\u51fb\u3002", before.State);
            return true;
        }

        if (before.MatchCount == 0 || before.Target == null)
        {
            result = Error("\u672a\u5728\u5df2\u6ce8\u518c\u7684\u6d3b\u52a8\u5956\u52b1\u7269\u4e2d\u627e\u5230\u6307\u5b9a instanceId\uff0c\u672c\u6b21\u672a\u6267\u884c\u70b9\u51fb\u3002", before.State);
            return true;
        }

        if (before.MatchCount != 1)
        {
            result = Error("\u6307\u5b9a instanceId \u5339\u914d\u5230\u591a\u4e2a\u5df2\u6ce8\u518c\u5956\u52b1\u7269\uff0c\u4e3a\u907f\u514d\u8bef\u6536\u53d6\uff0c\u672c\u6b21\u672a\u6267\u884c\u70b9\u51fb\u3002", before.State);
            return true;
        }

        if (contract.RewardObjectButton == null ||
            contract.RewardButtonPointEnter == null ||
            contract.RewardButtonLeftPointDown == null ||
            contract.RewardButtonLeftPointUp == null)
        {
            result = Error("\u65e0\u6cd5\u89e3\u6790\u5956\u52b1\u7269 Btn \u73a9\u5bb6\u70b9\u51fb\u94fe\uff0c\u672c\u6b21\u672a\u6267\u884c\u6536\u53d6\u3002", before.State);
            return true;
        }

        object? button;
        try
        {
            button = contract.RewardObjectButton.GetValue(before.Target, null);
        }
        catch (Exception exception)
        {
            result = Error(
                "\u8bfb\u53d6\u5956\u52b1\u7269 Btn \u5931\u8d25\uff0c\u672c\u6b21\u672a\u6267\u884c\u6536\u53d6\uff1a" + Unwrap(exception).Message,
                before.State);
            return true;
        }

        if (button == null || button is UnityEngine.Object unityButton && unityButton == null)
        {
            result = Error("\u6307\u5b9a\u5956\u52b1\u7269\u6ca1\u6709\u53ef\u7528 Btn\uff0c\u672c\u6b21\u672a\u6267\u884c\u6536\u53d6\u3002", before.State);
            return true;
        }

        bool invocationStarted = false;
        try
        {
            invocationStarted = true;
            contract.RewardButtonPointEnter.Invoke(button, null);
            contract.RewardButtonLeftPointDown.Invoke(button, null);
            contract.RewardButtonLeftPointUp.Invoke(button, null);

            result = Success(
                "\u5df2\u53d1\u9001\u5956\u52b1\u7269\u73a9\u5bb6\u70b9\u51fb\u94fe\uff0c\u6b63\u5728\u7b49\u5f85\u6536\u53d6\u7ed3\u7b97\u3002",
                new JObject
                {
                    ["source"] = ResultSource,
                    ["invocationStarted"] = true,
                    ["targetInstanceId"] = instanceId,
                    ["pending"] = true,
                    ["needsPolling"] = true
                });
            return true;
        }
        catch (Exception exception)
        {
            JObject failureState = before.State;
            failureState["source"] = ResultSource;
            failureState["targetInstanceId"] = instanceId;
            failureState["outcomeUnknown"] = true;
            failureState["needsReconciliation"] = true;
            failureState["uncertaintyOrigin"] = "rewardObjectClickException";
            failureState["invocationStarted"] = invocationStarted;
            failureState["pending"] = true;
            failureState["needsPolling"] = true;

            result = Error(
                "\u5956\u52b1\u7269\u70b9\u51fb\u94fe\u8c03\u7528\u5931\u8d25\uff0c\u7ed3\u679c\u9700\u8981\u91cd\u65b0\u5bf9\u8d26\uff1a" + Unwrap(exception).Message,
                failureState);
            return true;
        }
    }

    private static RewardObjectCollectionSnapshot BuildRewardObjectCollectionSnapshot(
        ReflectionContract contract,
        int instanceId)
    {
        JObject state = new()
        {
            ["source"] = ResultSource,
            ["targetInstanceId"] = instanceId,
            ["invocationStarted"] = false,
            ["spawnerAvailable"] = false,
            ["activeRewardObjectCount"] = 0,
            ["matchingRewardObjectCount"] = 0
        };

        if (!TryGetRegisteredObject(contract.SpawnerInstance, out object? spawner) ||
            spawner == null ||
            contract.SpawnerRewardObjects.GetValue(spawner) is not IEnumerable rewardObjects)
        {
            return new RewardObjectCollectionSnapshot(state, false, 0, null);
        }

        int activeCount = 0;
        int matchCount = 0;
        Component? target = null;
        foreach (object? candidate in rewardObjects)
        {
            if (candidate is not GameObject gameObject || gameObject == null ||
                !gameObject.scene.IsValid() || !gameObject.activeInHierarchy)
            {
                continue;
            }

            Component? rewardObject = gameObject.GetComponent(contract.RewardObjectType);
            if (rewardObject == null)
            {
                continue;
            }

            activeCount++;
            if (rewardObject.GetInstanceID() != instanceId)
            {
                continue;
            }

            matchCount++;
            target = rewardObject;
        }

        state["spawnerAvailable"] = true;
        state["activeRewardObjectCount"] = activeCount;
        state["matchingRewardObjectCount"] = matchCount;
        return new RewardObjectCollectionSnapshot(state, true, matchCount, target);
    }

    private static RewardSnapshot BuildSnapshot(ReflectionContract contract)
    {
        Component? panel = TryGetRegisteredComponent(contract.PanelInstance);
        bool panelOpen = IsOpenPanel(contract, panel);
        bool panelActive = panelOpen && contract.PanelIsActive.GetValue(panel, null) is bool active && active;
        bool refresh = panel != null && contract.PanelRefresh.GetValue(panel) is bool refreshing && refreshing;
        bool finished = panel != null && contract.PanelFinished.GetValue(panel) is bool queueFinished && queueFinished;
        object? currentQueueItem = panel == null ? null : contract.PanelCurrentQueueItem.GetValue(panel);
        int currentQueueIdentity = currentQueueItem == null ? 0 : RuntimeHelpers.GetHashCode(currentQueueItem);
        string currentQueueItemType = currentQueueItem == null
            ? string.Empty
            : contract.QueueItemType.GetValue(currentQueueItem)?.ToString() ?? string.Empty;
        int queueCount = panel != null && contract.PanelQueue.GetValue(panel) is ICollection queue
            ? queue.Count
            : 0;

        bool mutexAvailable = TryGetRegisteredObject(contract.MutexInstance, out object? mutex);
        bool busy = mutexAvailable && mutex != null &&
                    contract.MutexBusy.GetValue(mutex, null) is bool mutexBusy && mutexBusy;

        bool mapRewardVehicleGetFetter = panelOpen && ReadMapRewardVehicleGetFetter(contract);
        List<RewardOptionSnapshot> options = panelOpen && panel != null
            ? GetRewardOptions(contract, panel, mapRewardVehicleGetFetter)
            : new List<RewardOptionSnapshot>();
        string phaseToken = panelOpen && panel != null
            ? BuildPhaseToken(panel.GetInstanceID(), currentQueueIdentity, currentQueueItemType, options)
            : string.Empty;

        JArray optionStates = new(options.Select(option => option.State));
        JArray rewardObjects = BuildRewardObjects(
            contract,
            out int pooledRewardObjectCount,
            out bool spawnerAvailable);
        bool optionsPending = panelOpen && (options.Count == 0 || !mutexAvailable);
        bool rewardObjectsPending = !panelOpen && !spawnerAvailable;
        JObject state = new()
        {
            ["panelOpen"] = panelOpen,
            ["panelActive"] = panelActive,
            ["panelAvailable"] = panel != null,
            ["panelInstanceId"] = panel == null ? 0 : panel.GetInstanceID(),
            ["phaseToken"] = phaseToken,
            ["queueCount"] = queueCount,
            ["currentQueueIdentity"] = currentQueueIdentity,
            ["currentQueueItemType"] = currentQueueItemType,
            ["refresh"] = refresh,
            ["finished"] = finished,
            ["busy"] = busy,
            ["mutexAvailable"] = mutexAvailable,
            ["spawnerAvailable"] = spawnerAvailable,
            ["pending"] = optionsPending || rewardObjectsPending,
            ["needsPolling"] = optionsPending || rewardObjectsPending,
            ["activeRewardObjectCount"] = rewardObjects.Count,
            ["pooledRewardObjectCount"] = pooledRewardObjectCount,
            ["rewardObjects"] = rewardObjects,
            ["options"] = optionStates,
            ["source"] = ResultSource
        };

        return new RewardSnapshot(
            state,
            panelOpen,
            phaseToken,
            refresh,
            finished,
            mutexAvailable,
            busy,
            options);
    }

    private static List<RewardOptionSnapshot> GetRewardOptions(
        ReflectionContract contract,
        Component panel,
        bool mapRewardVehicleGetFetter)
    {
        List<RewardOptionSnapshot> options = new();
        if (contract.PanelRewardContent.GetValue(panel) is not Transform content)
        {
            return options;
        }

        for (int childIndex = 0; childIndex < content.childCount; childIndex++)
        {
            Transform child = content.GetChild(childIndex);
            if (child == null || !child.gameObject.activeInHierarchy)
            {
                continue;
            }

            Component? item = child.GetComponent(contract.RewardItemType);
            if (item == null || !item.gameObject.activeInHierarchy)
            {
                continue;
            }

            options.Add(BuildRewardOption(contract, item, options.Count, mapRewardVehicleGetFetter));
        }

        return options;
    }

    private static RewardOptionSnapshot BuildRewardOption(
        ReflectionContract contract,
        Component item,
        int index,
        bool mapRewardVehicleGetFetter)
    {
        object? reward = contract.ItemReward.GetValue(item);
        int money = contract.ItemMoney.GetValue(item) is int amount ? amount : 0;
        object? button = contract.ItemButton.GetValue(item);
        bool buttonActive = button != null &&
                            contract.ButtonActive.GetValue(button, null) is bool active && active;
        bool isVehicle = reward != null && contract.RazorRewardType.IsInstanceOfType(reward);
        bool isDisposable = reward != null && contract.PotionRewardType.IsInstanceOfType(reward);
        bool isSuperModule = reward != null && contract.SuperModuleRewardType.IsInstanceOfType(reward);
        string rewardKind = isVehicle
            ? "vehicle"
            : isDisposable
                ? "disposable"
                : isSuperModule
                    ? "superModule"
                    : money > 0
                        ? "money"
                        : "unknown";

        object? fetter = isVehicle ? contract.RazorInitFetter.GetValue(reward) : null;
        bool carryInitFetter = isVehicle && contract.RazorCarryInitFetter.GetValue(reward) is bool carry && carry;
        mapRewardVehicleGetFetter = isVehicle && mapRewardVehicleGetFetter;
        JObject? fetterState = BuildFetterState(contract, fetter);
        bool willCarryInitFetter = carryInitFetter && mapRewardVehicleGetFetter &&
                                   fetterState?["isActual"]?.Value<bool>() == true;
        JArray effectiveFetters = new();
        if (willCarryInitFetter && fetterState != null)
        {
            effectiveFetters.Add(fetterState.DeepClone());
        }

        JObject state = new()
        {
            ["index"] = index,
            ["instanceId"] = item.GetInstanceID(),
            ["type"] = item.GetType().Name,
            ["buttonActive"] = buttonActive,
            ["rewardKind"] = rewardKind,
            ["rewardType"] = NullableString(reward?.GetType().Name),
            ["rewardName"] = NullableString(reward is UnityEngine.Object rewardObject ? rewardObject.name : null),
            ["rewardRare"] = NullableString(reward == null ? null : contract.RewardRare.GetValue(reward)?.ToString()),
            ["vehicleType"] = NullableString(isVehicle ? contract.RazorVehicleType.GetValue(reward)?.ToString() : null),
            ["initFetter"] = NullableString(fetterState?["fetterEnum"]?.Value<string>()),
            ["templateInitFetter"] = (JToken?)fetterState ?? JValue.CreateNull(),
            ["carryInitFetter"] = isVehicle ? carryInitFetter : JValue.CreateNull(),
            ["mapRewardVehicleGetFetter"] = isVehicle ? mapRewardVehicleGetFetter : JValue.CreateNull(),
            ["willCarryInitFetter"] = isVehicle ? willCarryInitFetter : JValue.CreateNull(),
            ["effectiveInitFetter"] = NullableString(willCarryInitFetter ? fetterState?["fetterEnum"]?.Value<string>() : null),
            ["effectiveFetters"] = effectiveFetters,
            ["disposableEnum"] = NullableString(isDisposable ? contract.PotionDisposable.GetValue(reward)?.ToString() : null),
            ["assignDisposableEnum"] = NullableString(isDisposable ? contract.PotionAssignedDisposable.GetValue(reward)?.ToString() : null),
            ["superModuleEnum"] = NullableString(isSuperModule ? contract.SuperModule.GetValue(reward)?.ToString() : null),
            ["allowRepeatedAcquire"] = isSuperModule && contract.SuperModuleAllowRepeated.GetValue(reward) is bool repeated && repeated,
            ["money"] = money,
            ["source"] = ResultSource
        };

        return new RewardOptionSnapshot(item, index, item.GetInstanceID(), buttonActive, state);
    }

    private static JObject? BuildFetterState(ReflectionContract contract, object? fetter)
    {
        if (fetter == null)
        {
            return null;
        }

        string fetterEnum = contract.FetterEnum.GetValue(fetter)?.ToString() ?? string.Empty;
        int level = contract.FetterLevel.GetValue(fetter) is int readLevel ? readLevel : 0;
        int count = contract.FetterCount.GetValue(fetter) is int readCount ? readCount : 0;
        return new JObject
        {
            ["fetterEnum"] = fetterEnum,
            ["level"] = level,
            ["count"] = count,
            ["isActual"] = count > 0 && !string.Equals(fetterEnum, "None", StringComparison.OrdinalIgnoreCase)
        };
    }

    private static JArray BuildRewardObjects(
        ReflectionContract contract,
        out int pooledRewardObjectCount,
        out bool spawnerAvailable)
    {
        JArray activeObjects = new();
        int observedObjects = 0;
        if (!TryGetRegisteredObject(contract.SpawnerInstance, out object? spawner) ||
            spawner == null ||
            contract.SpawnerRewardObjects.GetValue(spawner) is not IEnumerable rewardObjects)
        {
            pooledRewardObjectCount = 0;
            spawnerAvailable = false;
            return activeObjects;
        }

        spawnerAvailable = true;

        foreach (object? candidate in rewardObjects)
        {
            if (candidate is not GameObject gameObject || gameObject == null)
            {
                continue;
            }

            observedObjects++;
            Component? rewardObject = gameObject.GetComponent(contract.RewardObjectType);
            if (rewardObject == null || !gameObject.scene.IsValid() || !gameObject.activeInHierarchy)
            {
                continue;
            }

            bool appearanceReady = IsAppearanceReady(gameObject, out string appearanceState, out float normalizedTime);

            activeObjects.Add(new JObject
            {
                ["index"] = activeObjects.Count,
                ["instanceId"] = rewardObject.GetInstanceID(),
                ["name"] = rewardObject.name,
                ["type"] = rewardObject.GetType().Name,
                ["active"] = true,
                ["appearanceReady"] = appearanceReady,
                ["appearanceState"] = appearanceState,
                ["appearanceNormalizedTime"] = normalizedTime,
                ["source"] = ResultSource
            });
        }

        pooledRewardObjectCount = Math.Max(0, observedObjects - activeObjects.Count);
        return activeObjects;
    }

    private static bool IsAppearanceReady(
        GameObject gameObject,
        out string state,
        out float normalizedTime)
    {
        state = "noAnimator";
        normalizedTime = 1f;
        Animator? animator = gameObject.GetComponent<Animator>() ??
                             gameObject.GetComponentInChildren<Animator>(true);
        if (animator == null || !animator.isActiveAndEnabled || animator.layerCount == 0)
        {
            return true;
        }

        if (animator.IsInTransition(0))
        {
            state = "transition";
            normalizedTime = 0f;
            return false;
        }

        AnimatorStateInfo current = animator.GetCurrentAnimatorStateInfo(0);
        normalizedTime = current.normalizedTime;
        if (current.IsName("fall"))
        {
            state = "fall";
            return normalizedTime >= 1f;
        }

        if (current.IsName("open"))
        {
            state = "open";
            return normalizedTime >= 1f;
        }

        if (current.IsName("idle") || current.IsName("Idle"))
        {
            state = "idle";
            return true;
        }

        state = current.loop ? "loop" : current.length <= 0f ? "empty" : "nonLoop";
        return current.loop || current.length <= 0f || normalizedTime >= 1f;
    }

    private static bool ReadMapRewardVehicleGetFetter(ReflectionContract contract)
    {
        try
        {
            if (!TryGetRegisteredObject(contract.MapConfigInstance, out object? controller) || controller == null)
            {
                return false;
            }

            object? config = contract.MapConfigCurrent.GetValue(controller, null);
            return config != null && contract.MapRewardVehicleGetFetter.GetValue(config) is bool enabled && enabled;
        }
        catch
        {
            return false;
        }
    }

    private static string BuildPhaseToken(
        int panelInstanceId,
        int currentQueueIdentity,
        string currentQueueItemType,
        IEnumerable<RewardOptionSnapshot> options) => string.Join(
        ":",
        panelInstanceId,
        currentQueueIdentity,
        currentQueueItemType,
        string.Join(",", options.Select(option => option.InstanceId)));

    private static bool IsOpenPanel(ReflectionContract contract, Component? panel)
    {
        if (panel == null || panel.gameObject == null || !panel.gameObject.scene.IsValid() ||
            !panel.gameObject.activeInHierarchy)
        {
            return false;
        }

        return contract.PanelIsOpen.Invoke(panel, null) is bool open && open;
    }

    private static Component? TryGetRegisteredComponent(PropertyInfo instance)
    {
        try
        {
            return instance.GetValue(null, null) as Component;
        }
        catch
        {
            return null;
        }
    }

    private static bool TryGetRegisteredObject(PropertyInfo instance, out object? value)
    {
        try
        {
            value = instance.GetValue(null, null);
            return value != null;
        }
        catch
        {
            value = null;
            return false;
        }
    }

    private static bool TryGetRegisteredObject(FieldInfo instance, out object? value)
    {
        try
        {
            value = instance.GetValue(null);
            return value != null;
        }
        catch
        {
            value = null;
            return false;
        }
    }

    private static bool TryReadSelection(
        JObject? arguments,
        out string phaseToken,
        out int itemInstanceId,
        out int index)
    {
        phaseToken = arguments?["phaseToken"]?.Type == JTokenType.String
            ? arguments["phaseToken"]!.Value<string>() ?? string.Empty
            : string.Empty;
        itemInstanceId = arguments?["itemInstanceId"]?.Type == JTokenType.Integer
            ? arguments["itemInstanceId"]!.Value<int>()
            : 0;
        index = arguments?["index"]?.Type == JTokenType.Integer
            ? arguments["index"]!.Value<int>()
            : -1;
        return !string.IsNullOrWhiteSpace(phaseToken) && itemInstanceId != 0 && index >= 0;
    }

    private static bool TryReadRewardObjectIdentity(JObject? arguments, out int instanceId)
    {
        instanceId = 0;
        JToken? identity = arguments?["instanceId"];
        if (identity?.Type != JTokenType.Integer)
        {
            return false;
        }

        try
        {
            instanceId = identity.Value<int>();
            return instanceId != 0;
        }
        catch
        {
            instanceId = 0;
            return false;
        }
    }

    private static bool TryGetContract(out ReflectionContract contract)
    {
        if (_contract != null)
        {
            contract = _contract;
            return true;
        }

        Type? panelType = FindType(RewardPanelTypeName);
        Type? itemType = FindType(RewardItemTypeName);
        Type? mutexType = FindType(RewardMutexTypeName);
        Type? spawnerType = FindType(RewardSpawnerTypeName);
        Type? rewardObjectType = FindType(RewardObjectTypeName);
        Type? rewardType = FindType(RewardTypeName);
        Type? razorRewardType = FindType(RazorRewardTypeName);
        Type? potionRewardType = FindType(PotionRewardTypeName);
        Type? superModuleRewardType = FindType(SuperModuleRewardTypeName);
        Type? mapConfigControllerType = FindType(MapConfigControllerTypeName);

        PropertyInfo? panelInstance = FindStaticProperty(panelType, "Instance");
        MethodInfo? panelIsOpen = FindInstanceMethod(panelType, "IsOpen");
        PropertyInfo? panelIsActive = FindInstanceProperty(panelType, "IsActive");
        FieldInfo? panelRewardContent = FindInstanceField(panelType, "m_rewardContent");
        FieldInfo? panelQueue = FindInstanceField(panelType, "m_currentRewardQueneItems");
        FieldInfo? panelCurrentQueueItem = FindInstanceField(panelType, "m_currentQueueItem");
        FieldInfo? panelRefresh = FindInstanceField(panelType, "m_refresh");
        FieldInfo? panelFinished = FindInstanceField(panelType, "m_currentQueueItemFinished");
        FieldInfo? queueItemType = FindInstanceField(panelCurrentQueueItem?.FieldType, "itemType");

        FieldInfo? itemReward = FindInstanceField(itemType, "m_reward");
        FieldInfo? itemMoney = FindInstanceField(itemType, "m_money");
        FieldInfo? itemButton = FindInstanceField(itemType, "m_btn");
        PropertyInfo? buttonActive = FindInstanceProperty(itemButton?.FieldType, "BtnActive");
        MethodInfo? clickEvent = FindInstanceMethod(itemType, "ClickEvent");

        FieldInfo? mutexInstance = FindStaticField(mutexType, "m_instance");
        PropertyInfo? mutexBusy = FindInstanceProperty(mutexType, "isInUsing");
        PropertyInfo? spawnerInstance = FindStaticProperty(spawnerType, "Instance");
        FieldInfo? spawnerRewardObjects = FindInstanceField(spawnerType, "m_rewardObjects");
        PropertyInfo? rewardObjectButton = FindInstanceProperty(rewardObjectType, "Btn");
        Type? rewardButtonType = rewardObjectButton?.PropertyType;
        MethodInfo? rewardButtonPointEnter = FindInstanceMethod(rewardButtonType, "PointEnter");
        MethodInfo? rewardButtonLeftPointDown = FindInstanceMethod(rewardButtonType, "LeftPointDown");
        MethodInfo? rewardButtonLeftPointUp = FindInstanceMethod(rewardButtonType, "LeftPointUp");

        FieldInfo? rewardRare = FindInstanceField(rewardType, "rewardRare");
        FieldInfo? razorVehicleType = FindInstanceField(razorRewardType, "vehicleType");
        FieldInfo? razorInitFetter = FindInstanceField(razorRewardType, "initFetterModuleData");
        FieldInfo? razorCarryInitFetter = FindInstanceField(razorRewardType, "carryInitFetter");
        FieldInfo? fetterEnum = FindInstanceField(razorInitFetter?.FieldType, "fetterEnum");
        FieldInfo? fetterLevel = FindInstanceField(razorInitFetter?.FieldType, "level");
        FieldInfo? fetterCount = FindInstanceField(razorInitFetter?.FieldType, "count");
        FieldInfo? potionDisposable = FindInstanceField(potionRewardType, "disposableEnum");
        FieldInfo? potionAssignedDisposable = FindInstanceField(potionRewardType, "assignDisposableEnum");
        FieldInfo? superModule = FindInstanceField(superModuleRewardType, "superModuleEnum");
        FieldInfo? superModuleAllowRepeated = FindInstanceField(superModuleRewardType, "allowRepeatedAcquire");

        PropertyInfo? mapConfigInstance = FindStaticProperty(mapConfigControllerType, "Instance");
        PropertyInfo? mapConfigCurrent = FindInstanceProperty(mapConfigControllerType, "CurrentCfg");
        FieldInfo? mapRewardVehicleGetFetter = FindInstanceField(mapConfigCurrent?.PropertyType, "rewardVehicleGetFetter");

        if (panelType == null || itemType == null || mutexType == null || spawnerType == null ||
            rewardObjectType == null || rewardType == null || razorRewardType == null ||
            potionRewardType == null || superModuleRewardType == null ||
            panelInstance == null || panelIsOpen == null || panelIsActive == null ||
            panelRewardContent == null || panelQueue == null || panelCurrentQueueItem == null ||
            panelRefresh == null || panelFinished == null || queueItemType == null ||
            itemReward == null || itemMoney == null || itemButton == null || buttonActive == null ||
            clickEvent == null || mutexInstance == null || mutexBusy == null ||
            spawnerInstance == null || spawnerRewardObjects == null || rewardRare == null ||
            razorVehicleType == null || razorInitFetter == null || razorCarryInitFetter == null ||
            fetterEnum == null || fetterLevel == null || fetterCount == null ||
            potionDisposable == null || potionAssignedDisposable == null || superModule == null ||
            superModuleAllowRepeated == null || mapConfigInstance == null || mapConfigCurrent == null ||
            mapRewardVehicleGetFetter == null)
        {
            contract = null!;
            return false;
        }

        _contract = new ReflectionContract(
            panelInstance, panelIsOpen, panelIsActive, panelRewardContent, panelQueue,
            panelCurrentQueueItem, panelRefresh, panelFinished, queueItemType,
            itemType, itemReward, itemMoney, itemButton, buttonActive, clickEvent,
            mutexInstance, mutexBusy, spawnerInstance, spawnerRewardObjects, rewardObjectType,
            rewardObjectButton, rewardButtonPointEnter, rewardButtonLeftPointDown, rewardButtonLeftPointUp,
            rewardRare, razorRewardType, razorVehicleType, razorInitFetter, razorCarryInitFetter,
            fetterEnum, fetterLevel, fetterCount, potionRewardType, potionDisposable,
            potionAssignedDisposable, superModuleRewardType, superModule, superModuleAllowRepeated,
            mapConfigInstance, mapConfigCurrent, mapRewardVehicleGetFetter);
        contract = _contract;
        return true;
    }

    private static Type? FindType(string fullName)
    {
        foreach (Assembly assembly in AppDomain.CurrentDomain.GetAssemblies())
        {
            Type? type = assembly.GetType(fullName, false);
            if (type != null)
            {
                return type;
            }
        }

        return null;
    }

    private static PropertyInfo? FindStaticProperty(Type? type, string name)
    {
        const BindingFlags Flags = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static | BindingFlags.FlattenHierarchy;
        for (Type? current = type; current != null; current = current.BaseType)
        {
            PropertyInfo? property = current.GetProperty(name, Flags);
            if (property != null)
            {
                return property;
            }
        }

        return null;
    }

    private static FieldInfo? FindStaticField(Type? type, string name)
    {
        const BindingFlags Flags = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static;
        for (Type? current = type; current != null; current = current.BaseType)
        {
            FieldInfo? field = current.GetField(name, Flags);
            if (field != null)
            {
                return field;
            }
        }

        return null;
    }

    private static PropertyInfo? FindInstanceProperty(Type? type, string name)
    {
        const BindingFlags Flags = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance;
        for (Type? current = type; current != null; current = current.BaseType)
        {
            PropertyInfo? property = current.GetProperty(name, Flags);
            if (property != null)
            {
                return property;
            }
        }

        return null;
    }

    private static FieldInfo? FindInstanceField(Type? type, string name)
    {
        const BindingFlags Flags = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance;
        for (Type? current = type; current != null; current = current.BaseType)
        {
            FieldInfo? field = current.GetField(name, Flags);
            if (field != null)
            {
                return field;
            }
        }

        return null;
    }

    private static MethodInfo? FindInstanceMethod(Type? type, string name)
    {
        const BindingFlags Flags = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance;
        for (Type? current = type; current != null; current = current.BaseType)
        {
            MethodInfo? method = current.GetMethod(name, Flags, null, Type.EmptyTypes, null);
            if (method != null)
            {
                return method;
            }
        }

        return null;
    }

    private static JToken NullableString(string? value) => value == null
        ? JValue.CreateNull()
        : new JValue(value);

    private static JObject Success(string message, JObject state) => new()
    {
        ["success"] = true,
        ["message"] = message,
        ["suggestion"] = JValue.CreateNull(),
        ["data"] = new JObject { ["state"] = state }
    };

    private static JObject SelectionError(string message, JObject state) => Error(message, state);

    private static JObject Error(string message, JObject state) => new()
    {
        ["success"] = false,
        ["message"] = message,
        ["suggestion"] = "\u8bf7\u91cd\u65b0\u67e5\u8be2\u5f53\u524d\u5956\u52b1\u72b6\u6001\uff0c\u4e0d\u8981\u91cd\u653e\u65e7\u9009\u9879\u3002",
        ["data"] = new JObject { ["state"] = state }
    };

    private static Exception Unwrap(Exception exception) =>
        exception is TargetInvocationException target && target.InnerException != null
            ? target.InnerException
            : exception;

    private sealed class RewardSnapshot
    {
        public RewardSnapshot(
            JObject state,
            bool panelOpen,
            string phaseToken,
            bool refresh,
            bool finished,
            bool mutexAvailable,
            bool busy,
            List<RewardOptionSnapshot> options)
        {
            State = state;
            PanelOpen = panelOpen;
            PhaseToken = phaseToken;
            Refresh = refresh;
            Finished = finished;
            MutexAvailable = mutexAvailable;
            Busy = busy;
            Options = options;
        }

        public JObject State { get; }
        public bool PanelOpen { get; }
        public string PhaseToken { get; }
        public bool Refresh { get; }
        public bool Finished { get; }
        public bool MutexAvailable { get; }
        public bool Busy { get; }
        public List<RewardOptionSnapshot> Options { get; }
    }

    private sealed class RewardOptionSnapshot
    {
        public RewardOptionSnapshot(Component item, int index, int instanceId, bool buttonActive, JObject state)
        {
            Item = item;
            Index = index;
            InstanceId = instanceId;
            ButtonActive = buttonActive;
            State = state;
        }

        public Component Item { get; }
        public int Index { get; }
        public int InstanceId { get; }
        public bool ButtonActive { get; }
        public JObject State { get; }
    }

    private sealed class RewardObjectCollectionSnapshot
    {
        public RewardObjectCollectionSnapshot(
            JObject state,
            bool spawnerAvailable,
            int matchCount,
            Component? target)
        {
            State = state;
            SpawnerAvailable = spawnerAvailable;
            MatchCount = matchCount;
            Target = target;
        }

        public JObject State { get; }
        public bool SpawnerAvailable { get; }
        public int MatchCount { get; }
        public Component? Target { get; }
    }

    private sealed class ReflectionContract
    {
        public ReflectionContract(
            PropertyInfo panelInstance,
            MethodInfo panelIsOpen,
            PropertyInfo panelIsActive,
            FieldInfo panelRewardContent,
            FieldInfo panelQueue,
            FieldInfo panelCurrentQueueItem,
            FieldInfo panelRefresh,
            FieldInfo panelFinished,
            FieldInfo queueItemType,
            Type rewardItemType,
            FieldInfo itemReward,
            FieldInfo itemMoney,
            FieldInfo itemButton,
            PropertyInfo buttonActive,
            MethodInfo clickEvent,
            FieldInfo mutexInstance,
            PropertyInfo mutexBusy,
            PropertyInfo spawnerInstance,
            FieldInfo spawnerRewardObjects,
            Type rewardObjectType,
            PropertyInfo? rewardObjectButton,
            MethodInfo? rewardButtonPointEnter,
            MethodInfo? rewardButtonLeftPointDown,
            MethodInfo? rewardButtonLeftPointUp,
            FieldInfo rewardRare,
            Type razorRewardType,
            FieldInfo razorVehicleType,
            FieldInfo razorInitFetter,
            FieldInfo razorCarryInitFetter,
            FieldInfo fetterEnum,
            FieldInfo fetterLevel,
            FieldInfo fetterCount,
            Type potionRewardType,
            FieldInfo potionDisposable,
            FieldInfo potionAssignedDisposable,
            Type superModuleRewardType,
            FieldInfo superModule,
            FieldInfo superModuleAllowRepeated,
            PropertyInfo mapConfigInstance,
            PropertyInfo mapConfigCurrent,
            FieldInfo mapRewardVehicleGetFetter)
        {
            PanelInstance = panelInstance;
            PanelIsOpen = panelIsOpen;
            PanelIsActive = panelIsActive;
            PanelRewardContent = panelRewardContent;
            PanelQueue = panelQueue;
            PanelCurrentQueueItem = panelCurrentQueueItem;
            PanelRefresh = panelRefresh;
            PanelFinished = panelFinished;
            QueueItemType = queueItemType;
            RewardItemType = rewardItemType;
            ItemReward = itemReward;
            ItemMoney = itemMoney;
            ItemButton = itemButton;
            ButtonActive = buttonActive;
            ClickEvent = clickEvent;
            MutexInstance = mutexInstance;
            MutexBusy = mutexBusy;
            SpawnerInstance = spawnerInstance;
            SpawnerRewardObjects = spawnerRewardObjects;
            RewardObjectType = rewardObjectType;
            RewardObjectButton = rewardObjectButton;
            RewardButtonPointEnter = rewardButtonPointEnter;
            RewardButtonLeftPointDown = rewardButtonLeftPointDown;
            RewardButtonLeftPointUp = rewardButtonLeftPointUp;
            RewardRare = rewardRare;
            RazorRewardType = razorRewardType;
            RazorVehicleType = razorVehicleType;
            RazorInitFetter = razorInitFetter;
            RazorCarryInitFetter = razorCarryInitFetter;
            FetterEnum = fetterEnum;
            FetterLevel = fetterLevel;
            FetterCount = fetterCount;
            PotionRewardType = potionRewardType;
            PotionDisposable = potionDisposable;
            PotionAssignedDisposable = potionAssignedDisposable;
            SuperModuleRewardType = superModuleRewardType;
            SuperModule = superModule;
            SuperModuleAllowRepeated = superModuleAllowRepeated;
            MapConfigInstance = mapConfigInstance;
            MapConfigCurrent = mapConfigCurrent;
            MapRewardVehicleGetFetter = mapRewardVehicleGetFetter;
        }

        public PropertyInfo PanelInstance { get; }
        public MethodInfo PanelIsOpen { get; }
        public PropertyInfo PanelIsActive { get; }
        public FieldInfo PanelRewardContent { get; }
        public FieldInfo PanelQueue { get; }
        public FieldInfo PanelCurrentQueueItem { get; }
        public FieldInfo PanelRefresh { get; }
        public FieldInfo PanelFinished { get; }
        public FieldInfo QueueItemType { get; }
        public Type RewardItemType { get; }
        public FieldInfo ItemReward { get; }
        public FieldInfo ItemMoney { get; }
        public FieldInfo ItemButton { get; }
        public PropertyInfo ButtonActive { get; }
        public MethodInfo ClickEvent { get; }
        public FieldInfo MutexInstance { get; }
        public PropertyInfo MutexBusy { get; }
        public PropertyInfo SpawnerInstance { get; }
        public FieldInfo SpawnerRewardObjects { get; }
        public Type RewardObjectType { get; }
        public PropertyInfo? RewardObjectButton { get; }
        public MethodInfo? RewardButtonPointEnter { get; }
        public MethodInfo? RewardButtonLeftPointDown { get; }
        public MethodInfo? RewardButtonLeftPointUp { get; }
        public FieldInfo RewardRare { get; }
        public Type RazorRewardType { get; }
        public FieldInfo RazorVehicleType { get; }
        public FieldInfo RazorInitFetter { get; }
        public FieldInfo RazorCarryInitFetter { get; }
        public FieldInfo FetterEnum { get; }
        public FieldInfo FetterLevel { get; }
        public FieldInfo FetterCount { get; }
        public Type PotionRewardType { get; }
        public FieldInfo PotionDisposable { get; }
        public FieldInfo PotionAssignedDisposable { get; }
        public Type SuperModuleRewardType { get; }
        public FieldInfo SuperModule { get; }
        public FieldInfo SuperModuleAllowRepeated { get; }
        public PropertyInfo MapConfigInstance { get; }
        public PropertyInfo MapConfigCurrent { get; }
        public FieldInfo MapRewardVehicleGetFetter { get; }
    }
}
