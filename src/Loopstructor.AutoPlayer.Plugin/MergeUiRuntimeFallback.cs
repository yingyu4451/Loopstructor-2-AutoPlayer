using System.Collections;
using System.Globalization;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using Newtonsoft.Json.Linq;
using UnityEngine;

namespace Loopstructor.AutoPlayer.Plugin;

/// <summary>
/// Completes the two merge-panel button actions missing from the packaged MCP contract.
/// </summary>
internal static class MergeUiRuntimeFallback
{
    private const string MergePanelTypeName = "MetroTD.UISystem.RebuildUI_MergeRebuildPanel";
    private const string MergeVehicleItemTypeName = "MetroTD.UISystem.RebuildUI_MergeRebuildPanel_VehicleItem";
    private const string MergeOptionTypeName = "MetroTD.UISystem.RebuildUI_Option_Merge";
    private const string FormulaManagerTypeName = "MetroTD.VehicleSystem.CarriageSyntheticFormulaManager";
    private const string ResultSource = "pluginReflection:MergeUI";
    private const string AutomationResultSource = "pluginReflection:MergeUI:light";
    private static ReflectionContract? _contract;
    private static AutomationReflectionContract? _automationContract;
    private static long _observationSequence;

    internal static bool TryQueryState(out JObject result)
    {
        result = null!;
        if (!TryGetContract(out ReflectionContract contract))
        {
            return false;
        }

        try
        {
            result = Success(
                "已读取游戏原生合成面板阶段。",
                TryFindOpenPanel(contract, out Component panel)
                    ? BuildState(contract, panel)
                    : BuildState(contract, null));
            return true;
        }
        catch (Exception exception)
        {
            result = Error(
                "读取合成面板阶段失败：" + Unwrap(exception).Message,
                "保留当前面板并暂停自动合成。",
                new JObject { ["source"] = ResultSource });
            return true;
        }
    }

    /// <summary>
    /// Reads only the live merge panel instance and its owned children. This path intentionally avoids
    /// global Unity object scans and the game's full MCP panel snapshot.
    /// </summary>
    internal static bool TryQueryAutomationState(out JObject result)
    {
        result = null!;
        if (!TryGetAutomationContract(out AutomationReflectionContract contract))
        {
            result = AutomationContractUnavailable("无法读取轻量合成状态：当前游戏构建缺少所需的合成面板反射成员。");
            return true;
        }

        try
        {
            Component? panel = TryFindRegisteredOpenPanel(contract.Panel, out Component registered)
                ? registered
                : null;
            AutomationSnapshot snapshot = BuildAutomationSnapshot(contract, panel);
            result = Success("已读取轻量合成自动化状态。", snapshot.State);
            return true;
        }
        catch (Exception exception)
        {
            result = Error(
                "读取轻量合成自动化状态失败：" + Unwrap(exception).Message,
                "保留当前面板并等待下一次轻量查询。",
                new JObject { ["source"] = AutomationResultSource });
            return true;
        }
    }

    /// <summary>
    /// Selects exactly one merge vehicle after validating the full panel, roster, item, vehicle and group identity.
    /// </summary>
    internal static bool TrySelectMergeVehicle(JObject? arguments, out JObject result)
    {
        result = null!;
        if (!TryGetAutomationContract(out AutomationReflectionContract contract))
        {
            result = AutomationContractUnavailable(
                "无法选择合成战车：当前游戏构建缺少所需的合成面板反射成员；本次未执行点击。");
            return true;
        }

        AutomationSnapshot before;
        try
        {
            Component? panel = TryFindRegisteredOpenPanel(contract.Panel, out Component registered)
                ? registered
                : null;
            before = BuildAutomationSnapshot(contract, panel);
        }
        catch (Exception exception)
        {
            result = Error(
                "选择战车前无法读取轻量合成状态：" + Unwrap(exception).Message,
                "未执行点击；请重新查询合成面板。",
                new JObject { ["source"] = AutomationResultSource });
            return true;
        }

        if (!SelectionRequest.TryCreate(arguments, out SelectionRequest request, out string requestError))
        {
            result = SelectionError("合成战车选择参数无效：" + requestError, before.State);
            return true;
        }

        if (!TryValidateSelectionRequest(before, request, out MergeVehicleSnapshot target, out string blocker))
        {
            result = SelectionError("拒绝选择合成战车：" + blocker, before.State);
            return true;
        }

        bool invocationStarted = false;
        try
        {
            invocationStarted = true;
            contract.VehicleClick.Invoke(target.Item, new object[] { true });

            Component? panel = TryFindRegisteredOpenPanel(contract.Panel, out Component registered)
                ? registered
                : null;
            AutomationSnapshot after = BuildAutomationSnapshot(contract, panel);
            if (!IsExpectedSelectionResult(before, after, request, out string postconditionError))
            {
                after.State["invocationStarted"] = true;
                after.State["selectionWriteVerified"] = false;
                after.State["outcomeUnknown"] = true;
                after.State["needsReconciliation"] = true;
                result = SelectionError(
                    "合成战车点击已返回，但可读状态未满足单次选择后置条件，最终结果未知：" + postconditionError,
                    after.State);
                return true;
            }

            after.State["selectionWriteVerified"] = true;
            result = Success("已选择并验证指定合成战车。", after.State);
            return true;
        }
        catch (Exception exception)
        {
            JObject failureState = (JObject)before.State.DeepClone();
            if (invocationStarted)
            {
                failureState["statePolluted"] = true;
                failureState["needsReset"] = true;
                failureState["invocationStarted"] = true;
            }

            result = Error(
                "选择合成战车失败：" + Unwrap(exception).Message,
                invocationStarted
                    ? "原生点击已经开始但无法确认结果；请停止本轮并重置游戏状态。"
                    : "未执行点击；请重新查询合成面板。",
                failureState);
            return true;
        }
    }

    private static bool TryValidateSelectionRequest(
        AutomationSnapshot snapshot,
        SelectionRequest request,
        out MergeVehicleSnapshot target,
        out string blocker)
    {
        target = null!;
        blocker = string.Empty;
        if (!snapshot.IsOpen || snapshot.Panel == null || !snapshot.IsSelecting)
        {
            blocker = "合成面板不在选车阶段。";
            return false;
        }

        if (snapshot.SettlementVisible || snapshot.OptionCount > 0)
        {
            blocker = "合成面板已经进入候选或结算阶段。";
            return false;
        }

        if (!snapshot.SelectionConsistent)
        {
            blocker = "面板已选条目无法与当前车辆名单一一对应。";
            return false;
        }

        if (request.PanelInstanceId != snapshot.PanelInstanceId
            || !string.Equals(request.RosterFingerprint, snapshot.RosterFingerprint, StringComparison.Ordinal))
        {
            blocker = "面板或车辆名单身份已变化。";
            return false;
        }

        if (request.RequiredVehicleCount <= 0
            || request.CandidateVehicleIndexes.Count != request.RequiredVehicleCount
            || request.CandidateItemInstanceIds.Count != request.RequiredVehicleCount
            || request.CandidateVehicleInstanceIds.Count != request.RequiredVehicleCount)
        {
            blocker = "完整合成组的数量或身份序列不一致。";
            return false;
        }

        MergeGroupSnapshot? group = snapshot.Groups.FirstOrDefault(candidate =>
            string.Equals(candidate.MaterialVehicleType, request.MaterialVehicleType, StringComparison.Ordinal)
            && string.Equals(candidate.ResultVehicleType, request.ResultVehicleType, StringComparison.Ordinal)
            && candidate.RequiredVehicleCount == request.RequiredVehicleCount
            && SameSequence(candidate.VehicleIndexes, request.CandidateVehicleIndexes)
            && SameSequence(candidate.ItemInstanceIds, request.CandidateItemInstanceIds)
            && SameSequence(candidate.VehicleInstanceIds, request.CandidateVehicleInstanceIds));
        if (group == null)
        {
            blocker = "请求中的完整合成组已不属于当前玩家规则。";
            return false;
        }

        int targetOffset = request.CandidateVehicleIndexes.IndexOf(request.Index);
        if (targetOffset < 0
            || request.CandidateItemInstanceIds[targetOffset] != request.ItemInstanceId
            || request.CandidateVehicleInstanceIds[targetOffset] != request.VehicleInstanceId)
        {
            blocker = "目标索引、条目身份和战车身份不属于同一计划位置。";
            return false;
        }

        target = snapshot.Vehicles.FirstOrDefault(vehicle => vehicle.Index == request.Index)!;
        if (target == null
            || target.ItemInstanceId != request.ItemInstanceId
            || target.VehicleInstanceId != request.VehicleInstanceId)
        {
            blocker = "目标条目或战车对象已经变化。";
            return false;
        }

        HashSet<int> plannedItems = new(request.CandidateItemInstanceIds);
        if (snapshot.SelectedItemInstanceIds.Any(selected => !plannedItems.Contains(selected)))
        {
            blocker = "当前选择包含计划合成组以外的条目。";
            return false;
        }

        if (snapshot.SelectedItemInstanceIds.Contains(target.ItemInstanceId))
        {
            blocker = "目标战车已经处于选中状态。";
            return false;
        }

        if (!target.CanSelect)
        {
            blocker = "目标战车当前不允许作为合成素材。";
            return false;
        }

        return true;
    }

    private static bool IsExpectedSelectionResult(
        AutomationSnapshot before,
        AutomationSnapshot after,
        SelectionRequest request,
        out string blocker)
    {
        blocker = string.Empty;
        if (!after.IsOpen
            || after.PanelInstanceId != request.PanelInstanceId
            || !string.Equals(after.RosterFingerprint, request.RosterFingerprint, StringComparison.Ordinal))
        {
            blocker = "点击后面板或车辆名单身份发生变化。";
            return false;
        }

        if (!after.SelectionConsistent)
        {
            blocker = "点击后的已选条目无法与车辆名单对应。";
            return false;
        }

        HashSet<int> expected = new(before.SelectedItemInstanceIds) { request.ItemInstanceId };
        if (after.SelectedItemInstanceIds.Count != expected.Count
            || !expected.SetEquals(after.SelectedItemInstanceIds))
        {
            blocker = "点击未产生且仅产生目标条目这一项选择变化。";
            return false;
        }

        HashSet<int> plannedItems = new(request.CandidateItemInstanceIds);
        if (after.SelectedItemInstanceIds.Any(selected => !plannedItems.Contains(selected)))
        {
            blocker = "点击后的选择超出计划合成组。";
            return false;
        }

        return true;
    }

    private static JObject SelectionError(string message, JObject state) => Error(
        message,
        "未执行额外点击；请使用最新轻量查询重新规划合成选择。",
        state);

    internal static bool TryClosePanel(out JObject result)
    {
        result = null!;
        bool closeStarted = false;
        if (!TryGetContract(out ReflectionContract contract))
        {
            return false;
        }

        try
        {
            if (!TryFindOpenPanel(contract, out Component panel))
            {
                result = Success("合成面板已经关闭。", BuildState(contract, null));
                return true;
            }

            if (IsSettlementVisible(contract, panel))
            {
                result = Error(
                    "合成结算仍在显示，拒绝直接关闭面板。",
                    "请先确认合成结算，再关闭面板。",
                    BuildState(contract, panel));
                return true;
            }

            if (!IsInSelection(contract, panel) || ReadSelectedVehicleCount(contract, panel) != 0)
            {
                result = Error(
                    "合成面板不在空白选车阶段，拒绝自动关闭。",
                    "保留当前玩家选择或候选界面，等待人工确认。",
                    BuildState(contract, panel));
                return true;
            }

            closeStarted = true;
            contract.CloseSelf.Invoke(panel, null);
            JObject closedState = BuildState(contract, panel);
            if (closedState["mergeOpen"]?.Value<bool>() == true)
            {
                result = Error(
                    "已调用合成面板原生关闭动作，但面板仍保持打开。",
                    "保留当前面板并暂停自动合成。",
                    closedState);
                return true;
            }

            result = Success("已按合成面板关闭按钮的原生行为关闭面板。", closedState);
            return true;
        }
        catch (Exception exception)
        {
            JObject failureState = new() { ["source"] = ResultSource };
            if (closeStarted)
            {
                failureState["statePolluted"] = true;
                failureState["needsReset"] = true;
                failureState["invocationStarted"] = true;
            }
            result = Error(
                "关闭合成面板失败：" + Unwrap(exception).Message,
                "停止本轮防线维护，并重新查询合成面板状态。",
                failureState);
            return true;
        }
    }

    internal static bool TryConfirmSettlement(out JObject result)
    {
        result = null!;
        bool confirmationStarted = false;
        if (!TryGetContract(out ReflectionContract contract))
        {
            return false;
        }

        try
        {
            if (!TryFindOpenPanel(contract, out Component panel))
            {
                result = Error(
                    "找不到正在显示的合成结算面板。",
                    "重新查询战车状态，确认合成是否已经完成。",
                    BuildState(contract, null));
                return true;
            }

            if (!IsSettlementVisible(contract, panel))
            {
                result = Error(
                    "合成面板尚未进入结算阶段，拒绝提前确认。",
                    "等待合成动画和结算界面出现后再确认。",
                    BuildState(contract, panel));
                return true;
            }

            confirmationStarted = true;
            contract.FinishCurrent.Invoke(panel, null);
            JObject completedState = BuildState(contract, panel);
            if (completedState["mergeOpen"]?.Value<bool>() == true)
            {
                result = Error(
                    "已调用合成结算原生确认动作，但面板仍保持打开。",
                    "保留当前面板并暂停自动合成。",
                    completedState);
                return true;
            }

            result = Success("已按合成结算确认按钮的原生行为完成结算。", completedState);
            return true;
        }
        catch (Exception exception)
        {
            JObject failureState = new() { ["source"] = ResultSource };
            if (confirmationStarted)
            {
                failureState["statePolluted"] = true;
                failureState["needsReset"] = true;
                failureState["invocationStarted"] = true;
            }
            result = Error(
                "确认合成结算失败：" + Unwrap(exception).Message,
                "停止本轮防线维护，并保留当前合成结算界面。",
                failureState);
            return true;
        }
    }

    private static AutomationSnapshot BuildAutomationSnapshot(
        AutomationReflectionContract contract,
        Component? panel)
    {
        long observationSequence = Interlocked.Increment(ref _observationSequence);
        if (panel == null || !IsOpenPanel(contract.Panel, panel))
        {
            JObject closedState = new()
            {
                ["mergeOpen"] = false,
                ["isInSelect"] = false,
                ["panelInstanceId"] = 0,
                ["rosterFingerprint"] = string.Empty,
                ["observationSequence"] = observationSequence,
                ["mergeSelectedCount"] = 0,
                ["mergeVehicles"] = new JArray(),
                ["requiredVehicleCount"] = 0,
                ["selectedVehicleCount"] = 0,
                ["canSubmitByPlayerRules"] = false,
                ["blockers"] = new JArray("合成面板未打开。"),
                ["legalMergeGroups"] = new JArray(),
                ["mergeSubmitRule"] = BuildEmptyRule("合成面板未打开。"),
                ["mergeReadyForSubmit"] = false,
                ["mergeOptions"] = new JArray(),
                ["settlementVisible"] = false,
                ["rosterReady"] = false,
                ["mergeGroupsReady"] = false,
                ["selectionConsistent"] = true,
                ["phase"] = "closed",
                ["source"] = AutomationResultSource
            };
            return new AutomationSnapshot(closedState);
        }

        int panelInstanceId = panel.GetInstanceID();
        HashSet<int> selectedItemIds = ReadSelectedItemInstanceIds(
            contract.Panel,
            panel,
            out int rawSelectedCount,
            out bool selectedListReadable);
        List<MergeVehicleSnapshot> vehicles = ReadMergeVehicles(
            contract,
            panel,
            selectedItemIds,
            out int rosterEntryCount,
            out bool rosterReadable);
        bool rosterComplete = rosterReadable && vehicles.Count == rosterEntryCount;
        bool selectionConsistent = rosterComplete
                                   && selectedListReadable
                                   && rawSelectedCount == selectedItemIds.Count
                                   && selectedItemIds.All(id => vehicles.Any(vehicle => vehicle.ItemInstanceId == id));
        string rosterFingerprint = rosterComplete
            ? BuildRosterFingerprint(panelInstanceId, rosterEntryCount, vehicles)
            : string.Empty;
        List<MergeGroupSnapshot> groups = BuildLegalMergeGroups(contract, vehicles, out bool groupsReadable);
        RuleSnapshot rule = BuildRuleSnapshot(contract, panel, vehicles);
        JArray options = BuildMergeOptions(contract, panel);
        bool selecting = IsInSelection(contract.Panel, panel);
        bool settlementVisible = IsSettlementVisible(contract.Panel, panel);

        JArray vehicleStates = new(vehicles.Select(vehicle => vehicle.State));
        JArray groupStates = new(groups.Select(group => group.State));
        JObject state = new()
        {
            ["mergeOpen"] = true,
            ["isInSelect"] = selecting,
            ["panelInstanceId"] = panelInstanceId,
            ["rosterFingerprint"] = rosterFingerprint,
            ["observationSequence"] = observationSequence,
            ["mergeSelectedCount"] = selectedItemIds.Count,
            ["mergeVehicles"] = vehicleStates,
            ["requiredVehicleCount"] = rule.RequiredVehicleCount,
            ["selectedVehicleCount"] = rule.SelectedVehicleCount,
            ["canSubmitByPlayerRules"] = rule.CanSubmit,
            ["blockers"] = rule.Blockers.DeepClone(),
            ["legalMergeGroups"] = rosterComplete && groupsReadable ? groupStates : JValue.CreateNull(),
            ["mergeSubmitRule"] = rule.State,
            ["mergeReadyForSubmit"] = rule.CanSubmit,
            ["mergeOptions"] = options,
            ["settlementVisible"] = settlementVisible,
            ["rosterReady"] = rosterComplete,
            ["mergeGroupsReady"] = groupsReadable,
            ["selectionConsistent"] = selectionConsistent,
            ["phase"] = settlementVisible
                ? "settlement"
                : options.Count > 0
                    ? "candidate"
                    : selecting
                        ? "selection"
                        : "transition",
            ["source"] = AutomationResultSource
        };

        return new AutomationSnapshot(
            state,
            panel,
            panelInstanceId,
            rosterFingerprint,
            selecting,
            settlementVisible,
            options.Count,
            selectionConsistent,
            vehicles,
            groups,
            selectedItemIds);
    }

    private static List<MergeVehicleSnapshot> ReadMergeVehicles(
        AutomationReflectionContract contract,
        Component panel,
        ISet<int> selectedItemIds,
        out int rosterEntryCount,
        out bool rosterReadable)
    {
        List<MergeVehicleSnapshot> vehicles = new();
        rosterEntryCount = 0;
        rosterReadable = false;
        if (contract.CurrentItemList.GetValue(panel, null) is not IEnumerable currentItems)
        {
            return vehicles;
        }

        rosterReadable = true;

        foreach (object? rawItem in currentItems)
        {
            int index = rosterEntryCount++;
            GameObject? itemObject = rawItem as GameObject;
            Component? item = itemObject?.GetComponent(contract.VehicleItemType);
            if (item == null)
            {
                continue;
            }

            object? vehicle = contract.VehicleController.GetValue(item, null);
            if (vehicle is UnityEngine.Object destroyedVehicle && destroyedVehicle == null)
            {
                vehicle = null;
            }

            int itemInstanceId = item.GetInstanceID();
            int vehicleInstanceId = vehicle is UnityEngine.Object unityVehicle ? unityVehicle.GetInstanceID() : 0;
            object? vehicleType = vehicle == null ? null : contract.VehicleType.GetValue(vehicle, null);
            string vehicleTypeText = vehicleType?.ToString() ?? string.Empty;
            int level = vehicle == null ? 0 : ConvertToInt(contract.VehicleLevel.GetValue(vehicle, null));
            bool active = item.gameObject != null
                          && item.gameObject.scene.IsValid()
                          && item.gameObject.activeInHierarchy;
            bool canSelect = active
                             && contract.CanMergeSelect.GetValue(item, null) is bool allowed
                             && allowed;
            string vehicleName = vehicle is UnityEngine.Object namedVehicle ? namedVehicle.name : string.Empty;
            JObject state = new()
            {
                ["index"] = index,
                ["instanceId"] = itemInstanceId,
                ["path"] = BuildHierarchyPath(item.transform),
                ["active"] = active,
                ["selected"] = selectedItemIds.Contains(itemInstanceId),
                ["canSelect"] = canSelect,
                ["vehicle"] = vehicle == null
                    ? JValue.CreateNull()
                    : new JObject
                    {
                        ["instanceId"] = vehicleInstanceId,
                        ["name"] = vehicleName,
                        ["vehicleType"] = vehicleTypeText,
                        ["type"] = vehicleTypeText,
                        ["level"] = level
                    }
            };
            vehicles.Add(new MergeVehicleSnapshot(
                index,
                item,
                itemInstanceId,
                vehicle,
                vehicleInstanceId,
                vehicleType,
                vehicleTypeText,
                level,
                canSelect,
                selectedItemIds.Contains(itemInstanceId),
                state));
        }

        return vehicles;
    }

    private static HashSet<int> ReadSelectedItemInstanceIds(
        ReflectionContract contract,
        Component panel,
        out int rawCount,
        out bool readable)
    {
        HashSet<int> selected = new();
        rawCount = 0;
        readable = contract.CurrentChooseList.GetValue(panel, null) is IEnumerable;
        if (contract.CurrentChooseList.GetValue(panel, null) is not IEnumerable currentChooseList)
        {
            return selected;
        }

        foreach (object? item in currentChooseList)
        {
            rawCount++;
            if (item is UnityEngine.Object unityItem && unityItem != null)
            {
                selected.Add(unityItem.GetInstanceID());
            }
        }

        return selected;
    }

    private static List<MergeGroupSnapshot> BuildLegalMergeGroups(
        AutomationReflectionContract contract,
        IReadOnlyList<MergeVehicleSnapshot> vehicles,
        out bool readable)
    {
        List<MergeGroupSnapshot> groups = new();
        readable = true;
        object? manager;
        try
        {
            manager = contract.FormulaManagerInstance.GetValue(null, null);
        }
        catch
        {
            readable = false;
            return groups;
        }

        if (manager == null)
        {
            readable = false;
            return groups;
        }

        IEnumerable<IGrouping<string, MergeVehicleSnapshot>> vehiclesByType = vehicles
            .Where(vehicle => vehicle.Vehicle != null
                              && vehicle.VehicleType != null
                              && vehicle.VehicleInstanceId != 0
                              && vehicle.VehicleTypeText.Length > 0)
            .GroupBy(vehicle => vehicle.VehicleTypeText, StringComparer.Ordinal)
            .OrderBy(group => group.Key, StringComparer.Ordinal);
        foreach (IGrouping<string, MergeVehicleSnapshot> typeGroup in vehiclesByType)
        {
            List<MergeVehicleSnapshot> available = typeGroup.OrderBy(vehicle => vehicle.Index).ToList();
            object vehicleType = available[0].VehicleType!;
            int requiredCount;
            try
            {
                requiredCount = ConvertToInt(contract.GetNeedMergeCount.Invoke(manager, new[] { vehicleType }));
            }
            catch
            {
                readable = false;
                continue;
            }

            if (requiredCount <= 0 || available.Count < requiredCount)
            {
                continue;
            }

            List<MergeVehicleSnapshot> candidates = available.Take(requiredCount).ToList();
            if (candidates.Any(candidate => !candidate.CanSelect))
            {
                continue;
            }

            IList formulaVehicles = (IList)Activator.CreateInstance(contract.FormulaVehicleListType)!;
            foreach (MergeVehicleSnapshot candidate in candidates)
            {
                formulaVehicles.Add(candidate.Vehicle);
            }

            object?[] formulaArguments =
            {
                formulaVehicles,
                null,
                Activator.CreateInstance(contract.FormulaResultType)
            };
            bool formulaMatched;
            try
            {
                formulaMatched = contract.CheckFormula.Invoke(manager, formulaArguments) is bool matched && matched;
            }
            catch
            {
                readable = false;
                continue;
            }

            string resultVehicleType = formulaMatched
                ? contract.FormulaResultVehicleType.GetValue(formulaArguments[2])?.ToString() ?? string.Empty
                : string.Empty;
            if (!formulaMatched || resultVehicleType.Length == 0)
            {
                continue;
            }

            int[] indexes = candidates.Select(candidate => candidate.Index).ToArray();
            int[] itemIds = candidates.Select(candidate => candidate.ItemInstanceId).ToArray();
            int[] vehicleIds = candidates.Select(candidate => candidate.VehicleInstanceId).ToArray();
            JObject state = new()
            {
                ["materialVehicleType"] = typeGroup.Key,
                ["resultVehicleType"] = resultVehicleType,
                ["requiredVehicleCount"] = requiredCount,
                ["availableCount"] = available.Count,
                ["selectedVehicleCount"] = candidates.Count,
                ["canSubmit"] = candidates.Count == requiredCount,
                ["candidateVehicleIndexes"] = new JArray(indexes),
                ["candidateItemInstanceIds"] = new JArray(itemIds),
                ["candidateVehicleInstanceIds"] = new JArray(vehicleIds)
            };
            groups.Add(new MergeGroupSnapshot(
                typeGroup.Key,
                resultVehicleType,
                requiredCount,
                indexes,
                itemIds,
                vehicleIds,
                state));
        }

        return groups;
    }

    private static RuleSnapshot BuildRuleSnapshot(
        AutomationReflectionContract contract,
        Component panel,
        IReadOnlyList<MergeVehicleSnapshot> vehicles)
    {
        ParameterInfo[] parameters = contract.TryGetPlayerRule.GetParameters();
        object?[] arguments = new object?[parameters.Length];
        for (int index = 0; index < parameters.Length; index++)
        {
            Type valueType = parameters[index].ParameterType.GetElementType() ?? parameters[index].ParameterType;
            arguments[index] = valueType.IsValueType ? Activator.CreateInstance(valueType) : null;
        }

        bool canSubmit;
        try
        {
            canSubmit = contract.TryGetPlayerRule.Invoke(panel, arguments) is bool allowed && allowed;
        }
        catch (Exception exception)
        {
            string blocker = "读取玩家合成规则失败：" + Unwrap(exception).Message;
            return new RuleSnapshot(0, 0, false, new JArray(blocker), BuildEmptyRule(blocker));
        }

        int requiredCount = ConvertToInt(arguments[0]);
        int selectedCount = ConvertToInt(arguments[1]);
        bool formulaMatched = arguments[2] is bool matched && matched;
        JArray materialIndexes = ToIntegerArray(arguments[3] as IEnumerable);
        string resultVehicleType = formulaMatched
            ? contract.FormulaResultVehicleType.GetValue(arguments[4])?.ToString() ?? string.Empty
            : string.Empty;
        JArray selectedVehicles = new();
        if (arguments[5] is IEnumerable selected)
        {
            foreach (object? vehicle in selected)
            {
                int instanceId = vehicle is UnityEngine.Object unityVehicle && unityVehicle != null
                    ? unityVehicle.GetInstanceID()
                    : 0;
                MergeVehicleSnapshot? item = vehicles.FirstOrDefault(candidate =>
                    candidate.VehicleInstanceId == instanceId && instanceId != 0);
                selectedVehicles.Add(new JObject
                {
                    ["index"] = item?.Index ?? -1,
                    ["instanceId"] = instanceId,
                    ["vehicleType"] = item?.VehicleTypeText ?? string.Empty,
                    ["level"] = item?.Level ?? 0
                });
            }
        }

        string blockerText = arguments[6]?.ToString()?.Trim() ?? string.Empty;
        JArray blockers = blockerText.Length == 0 ? new JArray() : new JArray(blockerText);
        JObject state = new()
        {
            ["requiredVehicleCount"] = requiredCount,
            ["selectedVehicleCount"] = selectedCount,
            ["canSubmitByPlayerRules"] = canSubmit,
            ["blockers"] = blockers.DeepClone(),
            ["formulaMatched"] = formulaMatched,
            ["resultVehicleType"] = resultVehicleType.Length == 0 ? JValue.CreateNull() : resultVehicleType,
            ["materialIndexes"] = materialIndexes,
            ["selectedVehicles"] = selectedVehicles
        };
        return new RuleSnapshot(requiredCount, selectedCount, canSubmit, blockers, state);
    }

    private static JObject BuildEmptyRule(string blocker) => new()
    {
        ["requiredVehicleCount"] = 0,
        ["selectedVehicleCount"] = 0,
        ["canSubmitByPlayerRules"] = false,
        ["blockers"] = new JArray(blocker),
        ["formulaMatched"] = false,
        ["resultVehicleType"] = JValue.CreateNull(),
        ["materialIndexes"] = new JArray(),
        ["selectedVehicles"] = new JArray()
    };

    private static JArray BuildMergeOptions(AutomationReflectionContract contract, Component panel)
    {
        JArray options = new();
        if (contract.SelectedFetterContent.GetValue(panel) is not Transform content)
        {
            return options;
        }

        for (int index = 0; index < content.childCount; index++)
        {
            Transform child = content.GetChild(index);
            Component? option = child.GetComponent(contract.MergeOptionType);
            if (option == null
                || option.gameObject == null
                || !option.gameObject.scene.IsValid()
                || !option.gameObject.activeInHierarchy)
            {
                continue;
            }

            object? module = contract.FetterModuleData.GetValue(option, null);
            options.Add(new JObject
            {
                ["index"] = options.Count,
                ["instanceId"] = option.GetInstanceID(),
                ["type"] = option.GetType().Name,
                ["path"] = BuildHierarchyPath(option.transform),
                ["fetter"] = module?.ToString() ?? string.Empty
            });
        }

        return options;
    }

    private static string BuildRosterFingerprint(
        int panelInstanceId,
        int rosterEntryCount,
        IEnumerable<MergeVehicleSnapshot> vehicles)
    {
        StringBuilder source = new();
        source.Append(panelInstanceId.ToString(CultureInfo.InvariantCulture));
        source.Append('|').Append(rosterEntryCount.ToString(CultureInfo.InvariantCulture));
        foreach (MergeVehicleSnapshot vehicle in vehicles.OrderBy(vehicle => vehicle.Index))
        {
            source.Append('|').Append(vehicle.Index.ToString(CultureInfo.InvariantCulture));
            source.Append(':').Append(vehicle.ItemInstanceId.ToString(CultureInfo.InvariantCulture));
            source.Append(':').Append(vehicle.VehicleInstanceId.ToString(CultureInfo.InvariantCulture));
            source.Append(':').Append(vehicle.VehicleTypeText);
            source.Append(':').Append(vehicle.Level.ToString(CultureInfo.InvariantCulture));
        }

        using SHA256 sha256 = SHA256.Create();
        byte[] hash = sha256.ComputeHash(Encoding.UTF8.GetBytes(source.ToString()));
        return BitConverter.ToString(hash).Replace("-", string.Empty).ToLowerInvariant();
    }

    private static string BuildHierarchyPath(Transform? transform)
    {
        Stack<string> segments = new();
        for (Transform? current = transform; current != null; current = current.parent)
        {
            segments.Push(current.name ?? string.Empty);
        }

        return string.Join("/", segments);
    }

    private static JArray ToIntegerArray(IEnumerable? values)
    {
        JArray result = new();
        if (values == null)
        {
            return result;
        }

        foreach (object? value in values)
        {
            result.Add(ConvertToInt(value));
        }

        return result;
    }

    private static int ConvertToInt(object? value)
    {
        try
        {
            return value == null ? 0 : Convert.ToInt32(value, CultureInfo.InvariantCulture);
        }
        catch
        {
            return 0;
        }
    }

    private static bool SameSequence(IReadOnlyList<int> left, IReadOnlyList<int> right)
    {
        if (left.Count != right.Count)
        {
            return false;
        }

        for (int index = 0; index < left.Count; index++)
        {
            if (left[index] != right[index])
            {
                return false;
            }
        }

        return true;
    }

    private static JObject BuildState(ReflectionContract contract, Component? panel)
    {
        bool open = panel != null && IsOpenPanel(contract, panel);
        bool selecting = open && IsInSelection(contract, panel!);
        bool settlementVisible = open && IsSettlementVisible(contract, panel!);
        return new JObject
        {
            ["mergeOpen"] = open,
            ["isInSelect"] = selecting,
            ["selectedVehicleCount"] = open ? ReadSelectedVehicleCount(contract, panel!) : 0,
            ["settlementVisible"] = settlementVisible,
            ["phase"] = !open
                ? "closed"
                : settlementVisible
                    ? "settlement"
                    : selecting
                        ? "selection"
                        : "candidate",
            ["source"] = ResultSource
        };
    }

    private static bool IsInSelection(ReflectionContract contract, Component panel) =>
        contract.IsInSelect.GetValue(panel, null) is bool selecting && selecting;

    private static int ReadSelectedVehicleCount(ReflectionContract contract, Component panel) =>
        contract.CurrentChooseList.GetValue(panel, null) is ICollection selected ? selected.Count : -1;

    private static bool IsSettlementVisible(ReflectionContract contract, Component panel) =>
        contract.SettlementPanel.GetValue(panel) is GameObject settlement &&
        settlement != null &&
        settlement.activeSelf;

    private static bool TryFindOpenPanel(ReflectionContract contract, out Component panel)
    {
        panel = null!;
        try
        {
            if (contract.Instance.GetValue(null, null) is Component registered &&
                IsOpenPanel(contract, registered))
            {
                panel = registered;
                return true;
            }

            foreach (Component candidate in Resources.FindObjectsOfTypeAll(contract.PanelType).OfType<Component>())
            {
                if (IsOpenPanel(contract, candidate))
                {
                    panel = candidate;
                    return true;
                }
            }
        }
        catch
        {
            // The native MCP remains available when this fallback cannot inspect the panel.
        }

        return false;
    }

    private static bool TryFindRegisteredOpenPanel(ReflectionContract contract, out Component panel)
    {
        panel = null!;
        try
        {
            if (contract.Instance.GetValue(null, null) is Component registered
                && IsOpenPanel(contract, registered))
            {
                panel = registered;
                return true;
            }
        }
        catch
        {
            // A missing registered instance is a closed/temporarily unavailable lightweight snapshot.
        }

        return false;
    }

    private static bool IsOpenPanel(ReflectionContract contract, Component panel) =>
        panel != null &&
        panel.gameObject != null &&
        panel.gameObject.scene.IsValid() &&
        panel.gameObject.activeInHierarchy &&
        contract.IsOpen.Invoke(panel, null) is bool open &&
        open;

    private static bool TryGetContract(out ReflectionContract contract)
    {
        if (_contract != null)
        {
            contract = _contract;
            return true;
        }

        Type? panelType = FindType(MergePanelTypeName);
        const BindingFlags PublicInstance = BindingFlags.Public | BindingFlags.Instance;
        PropertyInfo? instance = panelType?.GetProperty(
            "Instance",
            BindingFlags.Public | BindingFlags.Static | BindingFlags.FlattenHierarchy);
        MethodInfo? isOpen = panelType?.GetMethod("IsOpen", PublicInstance);
        PropertyInfo? isInSelect = FindInstanceProperty(panelType, "IsInSelect");
        PropertyInfo? currentChooseList = FindInstanceProperty(panelType, "CurrentChooseList");
        FieldInfo? settlementPanel = FindInstanceField(panelType, "m_settlementPanel");
        MethodInfo? closeSelf = panelType?.GetMethod("CloseSelf", PublicInstance);
        MethodInfo? finishCurrent = panelType?.GetMethod("FinishCurrent", PublicInstance);
        if (panelType == null || instance == null || isOpen == null || isInSelect == null ||
            currentChooseList == null || settlementPanel == null || closeSelf == null || finishCurrent == null)
        {
            contract = null!;
            return false;
        }

        _contract = new ReflectionContract(
            panelType,
            instance,
            isOpen,
            isInSelect,
            currentChooseList,
            settlementPanel,
            closeSelf,
            finishCurrent);
        contract = _contract;
        return true;
    }

    private static bool TryGetAutomationContract(out AutomationReflectionContract contract)
    {
        if (_automationContract != null)
        {
            contract = _automationContract;
            return true;
        }

        if (!TryGetContract(out ReflectionContract panelContract))
        {
            contract = null!;
            return false;
        }

        Type? vehicleItemType = FindType(MergeVehicleItemTypeName);
        Type? mergeOptionType = FindType(MergeOptionTypeName);
        Type? formulaManagerType = FindType(FormulaManagerTypeName);
        const BindingFlags PublicInstance = BindingFlags.Public | BindingFlags.Instance;
        PropertyInfo? currentItemList = FindInstanceProperty(panelContract.PanelType, "CurrentItemList");
        FieldInfo? selectedFetterContent = FindInstanceField(panelContract.PanelType, "m_selectedFetterContent");
        MethodInfo? tryGetPlayerRule = panelContract.PanelType.GetMethods(PublicInstance)
            .FirstOrDefault(method => method.Name == "TryGetCurrentMergePlayerRule"
                                      && method.GetParameters().Length == 7);
        PropertyInfo? vehicleController = FindInstanceProperty(vehicleItemType, "VehicleController");
        PropertyInfo? canMergeSelect = FindInstanceProperty(vehicleItemType, "CanMergeSelect");
        MethodInfo? vehicleClick = vehicleItemType?.GetMethod(
            "Click",
            PublicInstance,
            null,
            new[] { typeof(bool) },
            null);
        PropertyInfo? vehicleType = FindInstanceProperty(vehicleController?.PropertyType, "vehicleType");
        PropertyInfo? vehicleLevel = FindInstanceProperty(vehicleController?.PropertyType, "level");
        PropertyInfo? fetterModuleData = FindInstanceProperty(mergeOptionType, "FetterModuleData");
        PropertyInfo? formulaManagerInstance = FindStaticProperty(formulaManagerType, "Instance");
        MethodInfo? getNeedMergeCount = formulaManagerType?.GetMethods(PublicInstance)
            .FirstOrDefault(method => method.Name == "GetNeedMergeCount"
                                      && method.GetParameters().Length == 1);
        MethodInfo? checkFormula = formulaManagerType?.GetMethods(PublicInstance)
            .FirstOrDefault(method => method.Name == "CheckSingleCarriageSyntheticFormula"
                                      && method.GetParameters().Length == 3);
        ParameterInfo[] formulaParameters = checkFormula?.GetParameters() ?? Array.Empty<ParameterInfo>();
        Type? formulaResultType = formulaParameters.Length == 3
            ? formulaParameters[2].ParameterType.GetElementType()
            : null;
        FieldInfo? formulaResultVehicleType = formulaResultType?.GetField(
            "vehicleType",
            BindingFlags.Public | BindingFlags.Instance);

        if (vehicleItemType == null
            || mergeOptionType == null
            || formulaManagerType == null
            || currentItemList == null
            || selectedFetterContent == null
            || tryGetPlayerRule == null
            || vehicleController == null
            || canMergeSelect == null
            || vehicleClick == null
            || vehicleType == null
            || vehicleLevel == null
            || fetterModuleData == null
            || formulaManagerInstance == null
            || getNeedMergeCount == null
            || checkFormula == null
            || formulaParameters.Length != 3
            || formulaResultType == null
            || formulaResultVehicleType == null)
        {
            contract = null!;
            return false;
        }

        _automationContract = new AutomationReflectionContract(
            panelContract,
            vehicleItemType,
            mergeOptionType,
            currentItemList,
            selectedFetterContent,
            tryGetPlayerRule,
            vehicleController,
            canMergeSelect,
            vehicleClick,
            vehicleType,
            vehicleLevel,
            fetterModuleData,
            formulaManagerInstance,
            getNeedMergeCount,
            checkFormula,
            formulaParameters[0].ParameterType,
            formulaResultType,
            formulaResultVehicleType);
        contract = _automationContract;
        return true;
    }

    private static PropertyInfo? FindInstanceProperty(Type? type, string name)
    {
        const BindingFlags Flags = BindingFlags.Public | BindingFlags.NonPublic |
                                   BindingFlags.Instance | BindingFlags.DeclaredOnly;
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
        const BindingFlags Flags = BindingFlags.Public | BindingFlags.NonPublic |
                                   BindingFlags.Instance | BindingFlags.DeclaredOnly;
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

    private static PropertyInfo? FindStaticProperty(Type? type, string name)
    {
        const BindingFlags Flags = BindingFlags.Public | BindingFlags.NonPublic |
                                   BindingFlags.Static | BindingFlags.FlattenHierarchy;
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

    private static JObject Success(string message, JObject state) => new()
    {
        ["success"] = true,
        ["message"] = message,
        ["suggestion"] = JValue.CreateNull(),
        ["data"] = new JObject { ["state"] = state }
    };

    private static JObject AutomationContractUnavailable(string message) => Error(
        message,
        "已阻止回落到原生全场景扫描实现；请保留当前界面并停止本轮自动合成。",
        new JObject
        {
            ["source"] = AutomationResultSource,
            ["contractAvailable"] = false,
            ["nativeFallbackBlocked"] = true,
            ["invocationStarted"] = false
        });

    private static JObject Error(string message, string suggestion, JObject state) => new()
    {
        ["success"] = false,
        ["message"] = message,
        ["suggestion"] = suggestion,
        ["data"] = new JObject { ["state"] = state }
    };

    private static Exception Unwrap(Exception exception) =>
        exception is TargetInvocationException target && target.InnerException != null
            ? target.InnerException
            : exception;

    private sealed class SelectionRequest
    {
        private SelectionRequest(
            int index,
            int panelInstanceId,
            string rosterFingerprint,
            int itemInstanceId,
            int vehicleInstanceId,
            string materialVehicleType,
            string resultVehicleType,
            int requiredVehicleCount,
            List<int> candidateVehicleIndexes,
            List<int> candidateItemInstanceIds,
            List<int> candidateVehicleInstanceIds)
        {
            Index = index;
            PanelInstanceId = panelInstanceId;
            RosterFingerprint = rosterFingerprint;
            ItemInstanceId = itemInstanceId;
            VehicleInstanceId = vehicleInstanceId;
            MaterialVehicleType = materialVehicleType;
            ResultVehicleType = resultVehicleType;
            RequiredVehicleCount = requiredVehicleCount;
            CandidateVehicleIndexes = candidateVehicleIndexes;
            CandidateItemInstanceIds = candidateItemInstanceIds;
            CandidateVehicleInstanceIds = candidateVehicleInstanceIds;
        }

        public int Index { get; }
        public int PanelInstanceId { get; }
        public string RosterFingerprint { get; }
        public int ItemInstanceId { get; }
        public int VehicleInstanceId { get; }
        public string MaterialVehicleType { get; }
        public string ResultVehicleType { get; }
        public int RequiredVehicleCount { get; }
        public List<int> CandidateVehicleIndexes { get; }
        public List<int> CandidateItemInstanceIds { get; }
        public List<int> CandidateVehicleInstanceIds { get; }

        public static bool TryCreate(
            JObject? arguments,
            out SelectionRequest request,
            out string blocker)
        {
            request = null!;
            blocker = string.Empty;
            if (arguments == null
                || !TryReadInteger(arguments["index"], allowZero: true, out int index)
                || index < 0
                || !TryReadInteger(arguments["panelInstanceId"], allowZero: false, out int panelInstanceId)
                || !TryReadInteger(arguments["itemInstanceId"], allowZero: false, out int itemInstanceId)
                || !TryReadInteger(arguments["vehicleInstanceId"], allowZero: false, out int vehicleInstanceId)
                || !TryReadInteger(arguments["requiredVehicleCount"], allowZero: false, out int requiredVehicleCount))
            {
                blocker = "缺少有效的索引或对象实例 ID。";
                return false;
            }

            string rosterFingerprint = arguments["rosterFingerprint"]?.Value<string>()?.Trim() ?? string.Empty;
            string materialVehicleType = arguments["materialVehicleType"]?.Value<string>()?.Trim() ?? string.Empty;
            string resultVehicleType = arguments["resultVehicleType"]?.Value<string>()?.Trim() ?? string.Empty;
            if (rosterFingerprint.Length == 0
                || materialVehicleType.Length == 0
                || resultVehicleType.Length == 0)
            {
                blocker = "缺少车辆名单或合成组类型身份。";
                return false;
            }

            if (!TryReadIntegerList(
                    arguments["candidateVehicleIndexes"],
                    allowZero: true,
                    rejectNegative: true,
                    out List<int> candidateIndexes)
                || !TryReadIntegerList(
                    arguments["candidateItemInstanceIds"],
                    allowZero: false,
                    rejectNegative: false,
                    out List<int> candidateItemIds)
                || !TryReadIntegerList(
                    arguments["candidateVehicleInstanceIds"],
                    allowZero: false,
                    rejectNegative: false,
                    out List<int> candidateVehicleIds))
            {
                blocker = "合成组身份数组缺失、重复或包含无效值。";
                return false;
            }

            request = new SelectionRequest(
                index,
                panelInstanceId,
                rosterFingerprint,
                itemInstanceId,
                vehicleInstanceId,
                materialVehicleType,
                resultVehicleType,
                requiredVehicleCount,
                candidateIndexes,
                candidateItemIds,
                candidateVehicleIds);
            return true;
        }

        private static bool TryReadInteger(JToken? token, bool allowZero, out int value)
        {
            value = 0;
            if (token?.Type != JTokenType.Integer)
            {
                return false;
            }

            value = token.Value<int>();
            return allowZero || value != 0;
        }

        private static bool TryReadIntegerList(
            JToken? token,
            bool allowZero,
            bool rejectNegative,
            out List<int> values)
        {
            values = new List<int>();
            if (token is not JArray array || array.Count == 0)
            {
                return false;
            }

            HashSet<int> unique = new();
            foreach (JToken item in array)
            {
                if (!TryReadInteger(item, allowZero, out int value)
                    || rejectNegative && value < 0
                    || !unique.Add(value))
                {
                    return false;
                }

                values.Add(value);
            }

            return true;
        }
    }

    private sealed class AutomationSnapshot
    {
        public AutomationSnapshot(JObject state)
            : this(
                state,
                null,
                0,
                string.Empty,
                false,
                false,
                0,
                true,
                new List<MergeVehicleSnapshot>(),
                new List<MergeGroupSnapshot>(),
                new HashSet<int>())
        {
        }

        public AutomationSnapshot(
            JObject state,
            Component? panel,
            int panelInstanceId,
            string rosterFingerprint,
            bool isSelecting,
            bool settlementVisible,
            int optionCount,
            bool selectionConsistent,
            List<MergeVehicleSnapshot> vehicles,
            List<MergeGroupSnapshot> groups,
            HashSet<int> selectedItemInstanceIds)
        {
            State = state;
            Panel = panel;
            PanelInstanceId = panelInstanceId;
            RosterFingerprint = rosterFingerprint;
            IsSelecting = isSelecting;
            SettlementVisible = settlementVisible;
            OptionCount = optionCount;
            SelectionConsistent = selectionConsistent;
            Vehicles = vehicles;
            Groups = groups;
            SelectedItemInstanceIds = selectedItemInstanceIds;
        }

        public JObject State { get; }
        public Component? Panel { get; }
        public bool IsOpen => Panel != null;
        public int PanelInstanceId { get; }
        public string RosterFingerprint { get; }
        public bool IsSelecting { get; }
        public bool SettlementVisible { get; }
        public int OptionCount { get; }
        public bool SelectionConsistent { get; }
        public List<MergeVehicleSnapshot> Vehicles { get; }
        public List<MergeGroupSnapshot> Groups { get; }
        public HashSet<int> SelectedItemInstanceIds { get; }
    }

    private sealed class MergeVehicleSnapshot
    {
        public MergeVehicleSnapshot(
            int index,
            Component item,
            int itemInstanceId,
            object? vehicle,
            int vehicleInstanceId,
            object? vehicleType,
            string vehicleTypeText,
            int level,
            bool canSelect,
            bool selected,
            JObject state)
        {
            Index = index;
            Item = item;
            ItemInstanceId = itemInstanceId;
            Vehicle = vehicle;
            VehicleInstanceId = vehicleInstanceId;
            VehicleType = vehicleType;
            VehicleTypeText = vehicleTypeText;
            Level = level;
            CanSelect = canSelect;
            Selected = selected;
            State = state;
        }

        public int Index { get; }
        public Component Item { get; }
        public int ItemInstanceId { get; }
        public object? Vehicle { get; }
        public int VehicleInstanceId { get; }
        public object? VehicleType { get; }
        public string VehicleTypeText { get; }
        public int Level { get; }
        public bool CanSelect { get; }
        public bool Selected { get; }
        public JObject State { get; }
    }

    private sealed class MergeGroupSnapshot
    {
        public MergeGroupSnapshot(
            string materialVehicleType,
            string resultVehicleType,
            int requiredVehicleCount,
            int[] vehicleIndexes,
            int[] itemInstanceIds,
            int[] vehicleInstanceIds,
            JObject state)
        {
            MaterialVehicleType = materialVehicleType;
            ResultVehicleType = resultVehicleType;
            RequiredVehicleCount = requiredVehicleCount;
            VehicleIndexes = vehicleIndexes;
            ItemInstanceIds = itemInstanceIds;
            VehicleInstanceIds = vehicleInstanceIds;
            State = state;
        }

        public string MaterialVehicleType { get; }
        public string ResultVehicleType { get; }
        public int RequiredVehicleCount { get; }
        public IReadOnlyList<int> VehicleIndexes { get; }
        public IReadOnlyList<int> ItemInstanceIds { get; }
        public IReadOnlyList<int> VehicleInstanceIds { get; }
        public JObject State { get; }
    }

    private sealed class RuleSnapshot
    {
        public RuleSnapshot(
            int requiredVehicleCount,
            int selectedVehicleCount,
            bool canSubmit,
            JArray blockers,
            JObject state)
        {
            RequiredVehicleCount = requiredVehicleCount;
            SelectedVehicleCount = selectedVehicleCount;
            CanSubmit = canSubmit;
            Blockers = blockers;
            State = state;
        }

        public int RequiredVehicleCount { get; }
        public int SelectedVehicleCount { get; }
        public bool CanSubmit { get; }
        public JArray Blockers { get; }
        public JObject State { get; }
    }

    private sealed class AutomationReflectionContract
    {
        public AutomationReflectionContract(
            ReflectionContract panel,
            Type vehicleItemType,
            Type mergeOptionType,
            PropertyInfo currentItemList,
            FieldInfo selectedFetterContent,
            MethodInfo tryGetPlayerRule,
            PropertyInfo vehicleController,
            PropertyInfo canMergeSelect,
            MethodInfo vehicleClick,
            PropertyInfo vehicleType,
            PropertyInfo vehicleLevel,
            PropertyInfo fetterModuleData,
            PropertyInfo formulaManagerInstance,
            MethodInfo getNeedMergeCount,
            MethodInfo checkFormula,
            Type formulaVehicleListType,
            Type formulaResultType,
            FieldInfo formulaResultVehicleType)
        {
            Panel = panel;
            VehicleItemType = vehicleItemType;
            MergeOptionType = mergeOptionType;
            CurrentItemList = currentItemList;
            SelectedFetterContent = selectedFetterContent;
            TryGetPlayerRule = tryGetPlayerRule;
            VehicleController = vehicleController;
            CanMergeSelect = canMergeSelect;
            VehicleClick = vehicleClick;
            VehicleType = vehicleType;
            VehicleLevel = vehicleLevel;
            FetterModuleData = fetterModuleData;
            FormulaManagerInstance = formulaManagerInstance;
            GetNeedMergeCount = getNeedMergeCount;
            CheckFormula = checkFormula;
            FormulaVehicleListType = formulaVehicleListType;
            FormulaResultType = formulaResultType;
            FormulaResultVehicleType = formulaResultVehicleType;
        }

        public ReflectionContract Panel { get; }
        public Type VehicleItemType { get; }
        public Type MergeOptionType { get; }
        public PropertyInfo CurrentItemList { get; }
        public FieldInfo SelectedFetterContent { get; }
        public MethodInfo TryGetPlayerRule { get; }
        public PropertyInfo VehicleController { get; }
        public PropertyInfo CanMergeSelect { get; }
        public MethodInfo VehicleClick { get; }
        public PropertyInfo VehicleType { get; }
        public PropertyInfo VehicleLevel { get; }
        public PropertyInfo FetterModuleData { get; }
        public PropertyInfo FormulaManagerInstance { get; }
        public MethodInfo GetNeedMergeCount { get; }
        public MethodInfo CheckFormula { get; }
        public Type FormulaVehicleListType { get; }
        public Type FormulaResultType { get; }
        public FieldInfo FormulaResultVehicleType { get; }
    }

    private sealed class ReflectionContract
    {
        public ReflectionContract(
            Type panelType,
            PropertyInfo instance,
            MethodInfo isOpen,
            PropertyInfo isInSelect,
            PropertyInfo currentChooseList,
            FieldInfo settlementPanel,
            MethodInfo closeSelf,
            MethodInfo finishCurrent)
        {
            PanelType = panelType;
            Instance = instance;
            IsOpen = isOpen;
            IsInSelect = isInSelect;
            CurrentChooseList = currentChooseList;
            SettlementPanel = settlementPanel;
            CloseSelf = closeSelf;
            FinishCurrent = finishCurrent;
        }

        public Type PanelType { get; }
        public PropertyInfo Instance { get; }
        public MethodInfo IsOpen { get; }
        public PropertyInfo IsInSelect { get; }
        public PropertyInfo CurrentChooseList { get; }
        public FieldInfo SettlementPanel { get; }
        public MethodInfo CloseSelf { get; }
        public MethodInfo FinishCurrent { get; }
    }
}
