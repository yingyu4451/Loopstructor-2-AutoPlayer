using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace Loopstructor.AutoPlayer.Core;

public enum MergeAutomationPhase
{
    OpenPanel,
    QuerySelectionState,
    InspectSelectionState,
    QueryFetterOptions,
    InspectFetterOptions,
    Completed
}

public enum MergeAutomationCompletionKind
{
    None,
    SafeEmptyPanel,
    RecoveryRequired
}

/// <summary>
/// Caller-owned state for one ordinary-player merge pass. Apply <see cref="MergeAutomationDecision.NextState"/>
/// only after the returned action succeeds (or immediately for wait/complete decisions).
/// </summary>
public sealed class MergeAutomationState
{
    public MergeAutomationState(
        MergeAutomationPhase phase = MergeAutomationPhase.OpenPanel,
        IReadOnlyList<int>? candidateVehicleIndexes = null,
        string? observationFingerprint = null,
        IReadOnlyList<int>? candidateVehicleInstanceIds = null,
        IReadOnlyList<int>? candidateItemInstanceIds = null,
        int panelInstanceId = 0,
        string? rosterFingerprint = null,
        string? materialVehicleType = null,
        string? resultVehicleType = null)
    {
        Phase = phase;
        CandidateVehicleIndexes = candidateVehicleIndexes?.ToArray() ?? Array.Empty<int>();
        ObservationFingerprint = observationFingerprint ?? string.Empty;
        CandidateVehicleInstanceIds = candidateVehicleInstanceIds?.ToArray() ?? Array.Empty<int>();
        CandidateItemInstanceIds = candidateItemInstanceIds?.ToArray() ?? Array.Empty<int>();
        PanelInstanceId = panelInstanceId;
        RosterFingerprint = rosterFingerprint ?? string.Empty;
        MaterialVehicleType = materialVehicleType ?? string.Empty;
        ResultVehicleType = resultVehicleType ?? string.Empty;
    }

    public static MergeAutomationState Initial { get; } = new();

    public MergeAutomationPhase Phase { get; }
    public IReadOnlyList<int> CandidateVehicleIndexes { get; }
    public string ObservationFingerprint { get; }
    public IReadOnlyList<int> CandidateVehicleInstanceIds { get; }
    public IReadOnlyList<int> CandidateItemInstanceIds { get; }
    public int PanelInstanceId { get; }
    public string RosterFingerprint { get; }
    public string MaterialVehicleType { get; }
    public string ResultVehicleType { get; }
}

public sealed class MergeAutomationDecision
{
    internal MergeAutomationDecision(
        AutomationAction? action,
        MergeAutomationState nextState,
        string detail,
        MergeAutomationCompletionKind completionKind = MergeAutomationCompletionKind.None)
    {
        Action = action;
        NextState = nextState;
        Detail = detail;
        CompletionKind = completionKind;
    }

    public AutomationAction? Action { get; }
    public MergeAutomationState NextState { get; }
    public bool IsComplete => NextState.Phase == MergeAutomationPhase.Completed;
    public string Detail { get; }
    public MergeAutomationCompletionKind CompletionKind { get; }
}

/// <summary>
/// Plans merge-panel actions exclusively from the queryMergeState contract. It never derives or guesses a
/// vehicle index: selectMergeVehicle arguments always come from legalMergeGroups.candidateVehicleIndexes.
/// </summary>
public sealed class MergeAutomationPlanner
{
    private const string MergeOptionType = "RebuildUI_Option_Merge";

    public bool HasPotentialMergeCandidate(JObject? vehicleResult)
    {
        if (State(vehicleResult)["vehicles"] is not JArray vehicleTokens)
        {
            return false;
        }

        return vehicleTokens.OfType<JObject>()
            .Where(vehicle => vehicle["isVirtual"]?.Value<bool>() != true)
            .Where(vehicle => vehicle["isFixedHead"]?.Value<bool>() != true)
            .Select(vehicle => new
            {
                Type = (vehicle["vehicleType"]?.Value<string>() ?? vehicle["type"]?.Value<string>() ?? string.Empty)
                    .Trim()
                    .ToUpperInvariant(),
                Level = ReadInt(vehicle["level"], 0)
            })
            .Where(vehicle => vehicle.Type.Length > 0 && vehicle.Level is > 0 and < 3)
            .GroupBy(vehicle => new { vehicle.Type, vehicle.Level })
            .Any(group => group.Count() >= (group.Key.Level == 1 ? 2 : 3));
    }

    public MergeAutomationDecision Decide(
        JObject? queryResult,
        MergeAutomationState? currentState,
        IReadOnlyList<string>? currentPrimaryFetters = null)
    {
        MergeAutomationState current = currentState ?? MergeAutomationState.Initial;
        return current.Phase switch
        {
            MergeAutomationPhase.OpenPanel => Execute(
                "openMergePanel",
                null,
                new MergeAutomationState(MergeAutomationPhase.QuerySelectionState),
                "打开游戏原生合成面板。"),
            MergeAutomationPhase.QuerySelectionState => Execute(
                "queryMergeState",
                null,
                Transition(current, MergeAutomationPhase.InspectSelectionState),
                "查询合成面板，等待玩家规则给出合法素材组。"),
            MergeAutomationPhase.InspectSelectionState => InspectSelection(
                State(queryResult),
                current,
                currentPrimaryFetters),
            MergeAutomationPhase.QueryFetterOptions => Execute(
                "queryMergeState",
                null,
                Transition(current, MergeAutomationPhase.InspectFetterOptions),
                "查询合成候选附魔，等待候选动画和对象列表稳定。"),
            MergeAutomationPhase.InspectFetterOptions => InspectFetterOptions(
                State(queryResult),
                current,
                currentPrimaryFetters),
            MergeAutomationPhase.Completed => Complete(current, "本次自动合成规划已经完成。"),
            _ => Complete(
                new MergeAutomationState(MergeAutomationPhase.Completed),
                "遇到未知合成阶段，已停止且未修改游戏状态。")
        };
    }

    private static MergeAutomationDecision InspectSelection(
        JObject state,
        MergeAutomationState current,
        IReadOnlyList<string>? currentPrimaryFetters)
    {
        if (state.Count == 0)
        {
            return WaitForSelection(current, "合成查询尚未返回 state，保持等待且不选择车辆。");
        }

        if (state["mergeOpen"]?.Value<bool>() != true)
        {
            return Wait(
                new MergeAutomationState(MergeAutomationPhase.OpenPanel),
                "合成面板尚未打开或已经关闭，等待后重新打开。");
        }

        JArray? optionTokens = state["mergeOptions"] as JArray;
        if (optionTokens != null && optionTokens.Count > 0)
        {
            return ObserveFetterOptions(state, current, currentPrimaryFetters);
        }

        if (IsReadyForSubmit(state))
        {
            if (!TryReadConsistentSelection(state, out int requiredCount, out List<int> selectedIndexes)
                || requiredCount <= 0)
            {
                return WaitForSelection(current, "运行时宣称可提交，但已选车辆快照不一致，拒绝提交并重新查询。");
            }

            IReadOnlyList<int> plannedIndexes = current.CandidateVehicleIndexes;
            if (plannedIndexes.Count == 0)
            {
                List<MergeGroup> groups = ReadValidGroups(state, out _);
                MergeGroup? matchingGroup = groups.FirstOrDefault(group =>
                    SameIndexSet(group.CandidateVehicleIndexes, selectedIndexes));
                if (matchingGroup == null)
                {
                    return WaitForSelection(current, "已选车辆无法对应 legalMergeGroups，拒绝提交。");
                }

                plannedIndexes = matchingGroup.CandidateVehicleIndexes;
            }

            if (plannedIndexes.Count != requiredCount || !SameIndexSet(plannedIndexes, selectedIndexes))
            {
                return WaitForSelection(current, "已选车辆与规划的 candidateVehicleIndexes 不一致，拒绝提交。");
            }

            return Execute(
                "submitMergeSelection",
                null,
                new MergeAutomationState(
                    MergeAutomationPhase.QueryFetterOptions,
                    plannedIndexes,
                    candidateVehicleInstanceIds: current.CandidateVehicleInstanceIds,
                    candidateItemInstanceIds: current.CandidateItemInstanceIds,
                    panelInstanceId: current.PanelInstanceId,
                    rosterFingerprint: current.RosterFingerprint,
                    materialVehicleType: current.MaterialVehicleType,
                    resultVehicleType: current.ResultVehicleType),
                "玩家规则确认素材数量和公式均合法，提交当前车辆选择。");
        }

        if (current.CandidateVehicleIndexes.Count > 0)
        {
            return ContinueVehicleSelection(state, current);
        }

        if (state["legalMergeGroups"] is not JArray groupTokens)
        {
            return WaitForSelection(current, "查询结果缺少 legalMergeGroups，无法安全构造素材索引。");
        }

        int panelInstanceId = ReadInt(state["panelInstanceId"], 0);
        string rosterFingerprint = state["rosterFingerprint"]?.Value<string>()?.Trim() ?? string.Empty;
        if (panelInstanceId == 0 || rosterFingerprint.Length == 0)
        {
            return WaitForSelection(current, "查询结果缺少面板或车辆名单身份，拒绝仅凭索引选择战车。");
        }

        string fingerprint = BuildSelectionFingerprint(panelInstanceId, rosterFingerprint, groupTokens);
        if (!string.Equals(current.ObservationFingerprint, fingerprint, StringComparison.Ordinal))
        {
            return Wait(
                new MergeAutomationState(
                    MergeAutomationPhase.QuerySelectionState,
                    observationFingerprint: fingerprint,
                    panelInstanceId: panelInstanceId,
                    rosterFingerprint: rosterFingerprint),
                "合法合成组刚出现或发生变化；再查询一次确认候选稳定。");
        }

        List<MergeGroup> validGroups = ReadValidGroups(state, out bool hadInvalidGroups);
        if (validGroups.Count == 0)
        {
            string detail = hadInvalidGroups
                ? "legalMergeGroups 连续稳定但全部无效，已停止且未伪造车辆索引。"
                : "没有合法合成组，本次自动合成无需执行。";
            return Complete(
                new MergeAutomationState(MergeAutomationPhase.Completed),
                detail,
                !hadInvalidGroups && groupTokens.Count == 0
                    ? MergeAutomationCompletionKind.SafeEmptyPanel
                    : MergeAutomationCompletionKind.RecoveryRequired);
        }

        if (!TryReadConsistentSelection(state, out _, out List<int> selectedBeforePlanning))
        {
            return WaitForSelection(current, "车辆选择计数与 mergeVehicles 不一致，等待面板稳定。");
        }

        MergeGroup? group = validGroups
            .Where(candidate => IsSubset(selectedBeforePlanning, candidate.CandidateVehicleIndexes))
            .OrderBy(candidate => candidate.ResultVehicleType, StringComparer.Ordinal)
            .ThenBy(candidate => candidate.MaterialVehicleType, StringComparer.Ordinal)
            .ThenBy(candidate => candidate.IndexKey, StringComparer.Ordinal)
            .FirstOrDefault();
        if (group == null)
        {
            return Complete(
                new MergeAutomationState(MergeAutomationPhase.Completed),
                "面板已有选择不属于任何 legalMergeGroups，已停止且未继续写入。");
        }

        MergeAutomationState planned = new(
            MergeAutomationPhase.InspectSelectionState,
            group.CandidateVehicleIndexes,
            candidateVehicleInstanceIds: group.CandidateVehicleInstanceIds,
            candidateItemInstanceIds: group.CandidateItemInstanceIds,
            panelInstanceId: panelInstanceId,
            rosterFingerprint: rosterFingerprint,
            materialVehicleType: group.MaterialVehicleType,
            resultVehicleType: group.ResultVehicleType);
        return ContinueVehicleSelection(state, planned);
    }

    private static MergeAutomationDecision ContinueVehicleSelection(
        JObject state,
        MergeAutomationState current)
    {
        List<MergeGroup> validGroups = ReadValidGroups(state, out _);
        int panelInstanceId = ReadInt(state["panelInstanceId"], 0);
        string rosterFingerprint = state["rosterFingerprint"]?.Value<string>()?.Trim() ?? string.Empty;
        MergeGroup? currentGroup = validGroups.FirstOrDefault(group =>
            SameIndexSequence(group.CandidateVehicleIndexes, current.CandidateVehicleIndexes)
            && SameIndexSequence(group.CandidateItemInstanceIds, current.CandidateItemInstanceIds)
            && SameIndexSequence(group.CandidateVehicleInstanceIds, current.CandidateVehicleInstanceIds)
            && string.Equals(group.MaterialVehicleType, current.MaterialVehicleType, StringComparison.Ordinal)
            && string.Equals(group.ResultVehicleType, current.ResultVehicleType, StringComparison.Ordinal));
        if (currentGroup == null)
        {
            return WaitForSelection(
                current,
                "原 legalMergeGroups 候选已变化，拒绝使用可能过期的车辆索引。");
        }

        if (panelInstanceId == 0
            || panelInstanceId != current.PanelInstanceId
            || rosterFingerprint.Length == 0
            || !string.Equals(rosterFingerprint, current.RosterFingerprint, StringComparison.Ordinal))
        {
            return WaitForSelection(current, "合成面板或车辆名单身份已经变化，拒绝使用过期计划。");
        }

        if (!TryReadConsistentSelection(state, out int requiredCount, out List<int> selectedIndexes))
        {
            return WaitForSelection(current, "车辆选择计数尚未稳定，暂不选择下一辆车。");
        }

        if (selectedIndexes.Count > 0
            && requiredCount > 0
            && requiredCount != current.CandidateVehicleIndexes.Count)
        {
            return WaitForSelection(current, "运行时所需素材数量与 legalMergeGroups 不一致，拒绝继续选择。");
        }

        if (!IsSubset(selectedIndexes, current.CandidateVehicleIndexes))
        {
            return Complete(
                new MergeAutomationState(MergeAutomationPhase.Completed),
                "面板含有规划组以外的已选车辆，已停止且未继续写入。");
        }

        int? nextIndex = current.CandidateVehicleIndexes
            .Where(index => !selectedIndexes.Contains(index))
            .Cast<int?>()
            .FirstOrDefault();
        if (!nextIndex.HasValue)
        {
            return WaitForSelection(current, "素材已全部选中，但玩家规则尚未允许提交，等待规则状态更新。");
        }


        int identityOffset = IndexOfValue(current.CandidateVehicleIndexes, nextIndex.Value);
        if (identityOffset < 0
            || identityOffset >= current.CandidateItemInstanceIds.Count
            || identityOffset >= current.CandidateVehicleInstanceIds.Count)
        {
            return WaitForSelection(current, "下一辆素材战车缺少稳定条目身份，拒绝执行选择。");
        }

        return Execute(
            "selectMergeVehicle",
            JObject.FromObject(new
            {
                index = nextIndex.Value,
                panelInstanceId = current.PanelInstanceId,
                rosterFingerprint = current.RosterFingerprint,
                itemInstanceId = current.CandidateItemInstanceIds[identityOffset],
                vehicleInstanceId = current.CandidateVehicleInstanceIds[identityOffset],
                materialVehicleType = current.MaterialVehicleType,
                resultVehicleType = current.ResultVehicleType,
                requiredVehicleCount = current.CandidateVehicleIndexes.Count,
                candidateVehicleIndexes = current.CandidateVehicleIndexes,
                candidateItemInstanceIds = current.CandidateItemInstanceIds,
                candidateVehicleInstanceIds = current.CandidateVehicleInstanceIds
            }),
            Transition(current, MergeAutomationPhase.InspectSelectionState, observationFingerprint: string.Empty),
            $"选择 legalMergeGroups.candidateVehicleIndexes 中的车辆项 {nextIndex.Value}。");
    }

    private static MergeAutomationDecision InspectFetterOptions(
        JObject state,
        MergeAutomationState current,
        IReadOnlyList<string>? currentPrimaryFetters)
    {
        if (state.Count == 0 || state["mergeOpen"]?.Value<bool>() != true)
        {
            return WaitForOptions(current, "合成候选面板状态尚未可见，继续等待而不点击。");
        }

        return ObserveFetterOptions(state, current, currentPrimaryFetters);
    }

    private static MergeAutomationDecision ObserveFetterOptions(
        JObject state,
        MergeAutomationState current,
        IReadOnlyList<string>? currentPrimaryFetters)
    {
        if (state["mergeOptions"] is not JArray optionTokens || optionTokens.Count == 0)
        {
            return WaitForOptions(current, "合成附魔候选仍为空，等待候选动画完成。");
        }

        if (!TryValidateMergePlayerRule(state, current, out string ruleBlocker))
        {
            return WaitForOptions(
                Transition(current, current.Phase, observationFingerprint: string.Empty),
                "合成附魔候选已经出现，但玩家合成规则尚未稳定：" + ruleBlocker);
        }

        JArray observation = new()
        {
            new JObject
            {
                ["options"] = optionTokens.DeepClone(),
                ["requiredVehicleCount"] = state["requiredVehicleCount"]?.DeepClone(),
                ["selectedVehicleCount"] = state["selectedVehicleCount"]?.DeepClone(),
                ["canSubmitByPlayerRules"] = state["canSubmitByPlayerRules"]?.DeepClone(),
                ["mergeSubmitRule"] = state["mergeSubmitRule"]?.DeepClone(),
                ["blockers"] = state["blockers"]?.DeepClone()
            }
        };
        string fingerprint = BuildTokenFingerprint(observation);
        if (!string.Equals(current.ObservationFingerprint, fingerprint, StringComparison.Ordinal))
        {
            return Wait(
                Transition(current, MergeAutomationPhase.QueryFetterOptions, fingerprint),
                "合成附魔候选刚出现或发生变化；再查询一次确认候选稳定。");
        }

        List<MergeOption> validOptions = ReadValidOptions(optionTokens);
        if (validOptions.Count == 0)
        {
            return Complete(
                new MergeAutomationState(MergeAutomationPhase.Completed),
                "合成附魔候选连续稳定但全部 disabled/invalid，已停止且未点击无效索引。");
        }

        MergeOption selected = SelectOption(validOptions, currentPrimaryFetters);
        return Execute(
            "chooseMergeFetter",
            JObject.FromObject(new { index = selected.Index }),
            new MergeAutomationState(MergeAutomationPhase.Completed),
            $"选择稳定的合成附魔候选 {selected.Fetter}（索引 {selected.Index}）。");
    }

    private static bool TryValidateMergePlayerRule(
        JObject state,
        MergeAutomationState current,
        out string blocker)
    {
        blocker = string.Empty;
        int requiredCount = ReadInt(state["requiredVehicleCount"], -1);
        int selectedCount = ReadInt(state["selectedVehicleCount"], -1);
        int mergeSelectedCount = ReadInt(state["mergeSelectedCount"], -1);
        if (requiredCount <= 0 || selectedCount != requiredCount || mergeSelectedCount != requiredCount)
        {
            blocker = "素材选择计数与玩家规则不一致。";
            return false;
        }

        if (state["canSubmitByPlayerRules"]?.Value<bool>() != true ||
            state["mergeReadyForSubmit"]?.Value<bool>() != true ||
            state["blockers"] is not JArray blockers || blockers.Count != 0)
        {
            blocker = "玩家规则尚未允许提交或仍有阻塞项。";
            return false;
        }

        if (state["mergeSubmitRule"] is not JObject rule ||
            ReadInt(rule["requiredVehicleCount"], -1) != requiredCount ||
            ReadInt(rule["selectedVehicleCount"], -1) != requiredCount ||
            rule["canSubmitByPlayerRules"]?.Value<bool>() != true ||
            rule["formulaMatched"]?.Value<bool>() != true ||
            rule["blockers"] is not JArray ruleBlockers || ruleBlockers.Count != 0 ||
            !TryReadNonNegativeUniqueIndexes(rule["materialIndexes"], out int[] materialIndexes) ||
            materialIndexes.Length != requiredCount ||
            rule["selectedVehicles"] is not JArray selectedVehicles ||
            !TryReadVehicleInstanceIds(selectedVehicles, requiredCount, out int[] selectedInstanceIds))
        {
            blocker = "合成公式、素材身份或规则快照不一致。";
            return false;
        }

        if (current.CandidateVehicleInstanceIds.Count != requiredCount ||
            !SameIndexSet(current.CandidateVehicleInstanceIds, selectedInstanceIds))
        {
            blocker = "素材对象身份已经偏离本次规划的合法组合。";
            return false;
        }

        return true;
    }

    private static bool TryReadVehicleInstanceIds(
        JArray vehicles,
        int requiredCount,
        out int[] instanceIds)
    {
        instanceIds = Array.Empty<int>();
        if (requiredCount <= 0 || vehicles.Count != requiredCount)
        {
            return false;
        }

        List<int> result = new();
        HashSet<int> unique = new();
        foreach (JObject vehicle in vehicles.OfType<JObject>())
        {
            int instanceId = ReadInt(vehicle["instanceId"], 0);
            if (instanceId == 0 || !unique.Add(instanceId))
            {
                return false;
            }

            result.Add(instanceId);
        }

        if (result.Count != requiredCount)
        {
            return false;
        }

        instanceIds = result.ToArray();
        return true;
    }

    private static MergeOption SelectOption(
        IReadOnlyList<MergeOption> options,
        IReadOnlyList<string>? currentPrimaryFetters)
    {
        if (currentPrimaryFetters != null)
        {
            for (int preferenceIndex = 0; preferenceIndex < currentPrimaryFetters.Count; preferenceIndex++)
            {
                string preference = NormalizeFetter(currentPrimaryFetters[preferenceIndex]);
                if (preference.Length == 0)
                {
                    continue;
                }

                MergeOption? matching = options
                    .Where(option => string.Equals(
                        NormalizeFetter(option.Fetter),
                        preference,
                        StringComparison.OrdinalIgnoreCase))
                    .OrderBy(option => option.Index)
                    .FirstOrDefault();
                if (matching != null)
                {
                    return matching;
                }
            }
        }

        return options.OrderBy(option => option.Index).First();
    }

    private static List<MergeGroup> ReadValidGroups(JObject state, out bool hadInvalidGroups)
    {
        hadInvalidGroups = false;
        if (state["legalMergeGroups"] is not JArray groups)
        {
            return new List<MergeGroup>();
        }

        HashSet<int>? availableIndexes = ReadAvailableVehicleIndexes(state);
        List<MergeGroup> result = new();
        foreach (JToken token in groups)
        {
            if (token is not JObject group
                || !TryReadNonNegativeUniqueIndexes(group["candidateVehicleIndexes"], out int[] indexes))
            {
                hadInvalidGroups = true;
                continue;
            }

            int requiredCount = ReadInt(group["requiredVehicleCount"], -1);
            int availableCount = ReadInt(group["availableCount"], -1);
            int candidateCount = ReadInt(group["selectedVehicleCount"], -1);
            string materialType = group["materialVehicleType"]?.Value<string>()?.Trim() ?? string.Empty;
            string resultType = group["resultVehicleType"]?.Value<string>()?.Trim() ?? string.Empty;
            bool valid = group["canSubmit"]?.Value<bool>() == true
                         && requiredCount > 0
                         && indexes.Length == requiredCount
                         && candidateCount == requiredCount
                         && availableCount >= requiredCount
                         && materialType.Length > 0
                          && resultType.Length > 0
                          && availableIndexes != null
                          && indexes.All(availableIndexes.Contains);
            int[] itemInstanceIds = Array.Empty<int>();
            int[] vehicleInstanceIds = Array.Empty<int>();
            if (valid && !TryReadCandidateIdentities(
                    state,
                    indexes,
                    out itemInstanceIds,
                    out vehicleInstanceIds))
            {
                valid = false;
            }

            if (valid
                && group["candidateItemInstanceIds"] != null
                && (!TryReadNonZeroUniqueIndexes(group["candidateItemInstanceIds"], out int[] declaredItemIds)
                    || !SameIndexSequence(declaredItemIds, itemInstanceIds)))
            {
                valid = false;
            }

            if (valid
                && group["candidateVehicleInstanceIds"] != null
                && (!TryReadNonZeroUniqueIndexes(group["candidateVehicleInstanceIds"], out int[] declaredVehicleIds)
                    || !SameIndexSequence(declaredVehicleIds, vehicleInstanceIds)))
            {
                valid = false;
            }

            if (!valid)
            {
                hadInvalidGroups = true;
                continue;
            }

            result.Add(new MergeGroup(
                materialType,
                resultType,
                indexes,
                itemInstanceIds,
                vehicleInstanceIds));
        }

        return result;
    }

    private static HashSet<int>? ReadAvailableVehicleIndexes(JObject state)
    {
        if (state["mergeVehicles"] is not JArray vehicles)
        {
            return null;
        }

        HashSet<int> indexes = new();
        foreach (JObject vehicleItem in vehicles.OfType<JObject>())
        {
            int index = ReadInt(vehicleItem["index"], -1);
            if (index < 0 || vehicleItem["vehicle"] == null || vehicleItem["vehicle"]?.Type == JTokenType.Null)
            {
                continue;
            }

            if (!indexes.Add(index))
            {
                return null;
            }
        }

        return indexes;
    }

    private static bool TryReadCandidateIdentities(
        JObject state,
        IReadOnlyList<int> candidateIndexes,
        out int[] itemInstanceIds,
        out int[] vehicleInstanceIds)
    {
        itemInstanceIds = Array.Empty<int>();
        vehicleInstanceIds = Array.Empty<int>();
        if (candidateIndexes.Count == 0 || state["mergeVehicles"] is not JArray vehicles)
        {
            return false;
        }

        Dictionary<int, (int ItemInstanceId, int VehicleInstanceId)> instancesByIndex = new();
        foreach (JObject item in vehicles.OfType<JObject>())
        {
            int index = ReadInt(item["index"], -1);
            int itemInstanceId = ReadInt(item["instanceId"], 0);
            int vehicleInstanceId = ReadInt(item.SelectToken("vehicle.instanceId"), 0);
            if (index < 0 || itemInstanceId == 0 || vehicleInstanceId == 0)
            {
                continue;
            }

            if (instancesByIndex.ContainsKey(index))
            {
                return false;
            }

            instancesByIndex.Add(index, (itemInstanceId, vehicleInstanceId));
        }

        List<int> resolvedItems = new();
        List<int> resolvedVehicles = new();
        HashSet<int> uniqueItems = new();
        HashSet<int> uniqueVehicles = new();
        foreach (int index in candidateIndexes)
        {
            if (!instancesByIndex.TryGetValue(index, out var identity)
                || !uniqueItems.Add(identity.ItemInstanceId)
                || !uniqueVehicles.Add(identity.VehicleInstanceId))
            {
                return false;
            }

            resolvedItems.Add(identity.ItemInstanceId);
            resolvedVehicles.Add(identity.VehicleInstanceId);
        }

        itemInstanceIds = resolvedItems.ToArray();
        vehicleInstanceIds = resolvedVehicles.ToArray();
        return true;
    }

    private static bool TryReadConsistentSelection(
        JObject state,
        out int requiredCount,
        out List<int> selectedIndexes)
    {
        requiredCount = ReadInt(state["requiredVehicleCount"], 0);
        selectedIndexes = new List<int>();
        if (state["mergeVehicles"] is not JArray vehicles)
        {
            return false;
        }

        HashSet<int> allIndexes = new();
        foreach (JObject vehicleItem in vehicles.OfType<JObject>())
        {
            int index = ReadInt(vehicleItem["index"], -1);
            if (index < 0 || !allIndexes.Add(index))
            {
                return false;
            }

            if (vehicleItem["selected"]?.Value<bool>() == true)
            {
                selectedIndexes.Add(index);
            }
        }

        int mergeSelectedCount = ReadInt(state["mergeSelectedCount"], -1);
        int selectedVehicleCount = ReadInt(state["selectedVehicleCount"], -1);
        return mergeSelectedCount == selectedIndexes.Count
               && selectedVehicleCount == selectedIndexes.Count;
    }

    private static bool IsReadyForSubmit(JObject state)
    {
        bool topLevelReady = state["canSubmitByPlayerRules"]?.Value<bool>() == true
                             && state["mergeReadyForSubmit"]?.Value<bool>() == true;
        bool nestedReady = state.SelectToken("mergeSubmitRule.canSubmitByPlayerRules")?.Value<bool>() == true;
        bool formulaMatched = state.SelectToken("mergeSubmitRule.formulaMatched")?.Value<bool>() == true;
        return topLevelReady && nestedReady && formulaMatched;
    }

    private static List<MergeOption> ReadValidOptions(JArray options)
    {
        Dictionary<int, int> indexCounts = options
            .OfType<JObject>()
            .Select(option => ReadInt(option["index"], -1))
            .Where(index => index >= 0)
            .GroupBy(index => index)
            .ToDictionary(group => group.Key, group => group.Count());
        List<MergeOption> result = new();
        foreach (JToken token in options)
        {
            if (token is not JObject option)
            {
                continue;
            }

            int index = ReadInt(option["index"], -1);
            int instanceId = ReadInt(option["instanceId"], 0);
            string type = option["type"]?.Value<string>()?.Trim() ?? string.Empty;
            string path = option["path"]?.Value<string>()?.Trim() ?? string.Empty;
            string fetter = option["fetter"]?.Value<string>()?.Trim() ?? string.Empty;
            bool valid = index >= 0
                         && instanceId != 0
                         && string.Equals(type, MergeOptionType, StringComparison.Ordinal)
                         && path.Length > 0
                         && NormalizeFetter(fetter).Length > 0
                         && !string.Equals(NormalizeFetter(fetter), "None", StringComparison.OrdinalIgnoreCase)
                         && !IsExplicitlyDisabled(option)
                         && indexCounts.TryGetValue(index, out int indexCount)
                         && indexCount == 1;
            if (valid)
            {
                result.Add(new MergeOption(index, fetter));
            }
        }

        return result;
    }

    private static bool IsExplicitlyDisabled(JObject option)
    {
        if (option["disabled"]?.Value<bool>() == true)
        {
            return true;
        }

        string[] positiveFlags = { "active", "enabled", "interactable", "selectable", "valid", "buttonActive" };
        return positiveFlags.Any(flag => option[flag]?.Type == JTokenType.Boolean && option[flag]?.Value<bool>() == false);
    }

    private static bool TryReadNonNegativeUniqueIndexes(JToken? token, out int[] indexes)
    {
        indexes = Array.Empty<int>();
        if (token is not JArray array || array.Count == 0)
        {
            return false;
        }

        List<int> values = new();
        HashSet<int> unique = new();
        foreach (JToken item in array)
        {
            if (item.Type != JTokenType.Integer)
            {
                return false;
            }

            int value = item.Value<int>();
            if (value < 0 || !unique.Add(value))
            {
                return false;
            }

            values.Add(value);
        }

        indexes = values.ToArray();
        return true;
    }

    private static bool TryReadNonZeroUniqueIndexes(JToken? token, out int[] indexes)
    {
        indexes = Array.Empty<int>();
        if (token is not JArray array || array.Count == 0)
        {
            return false;
        }

        List<int> values = new();
        HashSet<int> unique = new();
        foreach (JToken item in array)
        {
            if (item.Type != JTokenType.Integer)
            {
                return false;
            }

            int value = item.Value<int>();
            if (value == 0 || !unique.Add(value))
            {
                return false;
            }

            values.Add(value);
        }

        indexes = values.ToArray();
        return true;
    }

    private static string BuildTokenFingerprint(JArray items) =>
        items.Count.ToString(CultureInfo.InvariantCulture) + ":\n" + string.Join(
            "\n",
            items.Select(item => item.ToString(Formatting.None)).OrderBy(value => value, StringComparer.Ordinal));

    private static string BuildSelectionFingerprint(
        int panelInstanceId,
        string rosterFingerprint,
        JArray groups) =>
        panelInstanceId.ToString(CultureInfo.InvariantCulture) + ":" + rosterFingerprint + ":" +
        BuildTokenFingerprint(groups);

    private static string NormalizeFetter(string? value)
    {
        string normalized = value?.Trim() ?? string.Empty;
        int separator = normalized.IndexOf(" - ", StringComparison.Ordinal);
        return separator >= 0 ? normalized.Substring(0, separator).Trim() : normalized;
    }

    private static bool SameIndexSet(IReadOnlyList<int> left, IReadOnlyCollection<int> right) =>
        left.Count == right.Count && left.All(right.Contains);

    private static bool SameIndexSequence(IReadOnlyList<int> left, IReadOnlyList<int> right)
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

    private static int IndexOfValue(IReadOnlyList<int> values, int expected)
    {
        for (int index = 0; index < values.Count; index++)
        {
            if (values[index] == expected)
            {
                return index;
            }
        }

        return -1;
    }

    private static bool IsSubset(IReadOnlyCollection<int> subset, IReadOnlyCollection<int> superset) =>
        subset.All(superset.Contains);

    private static int ReadInt(JToken? token, int fallback)
    {
        if (token == null)
        {
            return fallback;
        }

        if (token.Type == JTokenType.Integer)
        {
            return token.Value<int>();
        }

        return int.TryParse(token.Value<string>(), NumberStyles.Integer, CultureInfo.InvariantCulture, out int value)
            ? value
            : fallback;
    }

    private static JObject State(JObject? result)
    {
        if (result == null)
        {
            return new JObject();
        }

        return result.SelectToken("data.state") as JObject
               ?? result["state"] as JObject
               ?? result;
    }

    private static MergeAutomationDecision WaitForSelection(MergeAutomationState current, string reason) => Wait(
        Transition(current, MergeAutomationPhase.QuerySelectionState),
        reason);

    private static MergeAutomationDecision WaitForOptions(MergeAutomationState current, string reason) => Wait(
        Transition(current, MergeAutomationPhase.QueryFetterOptions),
        reason);

    private static MergeAutomationState Transition(
        MergeAutomationState current,
        MergeAutomationPhase phase,
        string? observationFingerprint = null) => new(
        phase,
        current.CandidateVehicleIndexes,
        observationFingerprint ?? current.ObservationFingerprint,
        current.CandidateVehicleInstanceIds,
        current.CandidateItemInstanceIds,
        current.PanelInstanceId,
        current.RosterFingerprint,
        current.MaterialVehicleType,
        current.ResultVehicleType);

    private static MergeAutomationDecision Wait(MergeAutomationState nextState, string reason) => new(
        AutomationAction.Wait(AutomationStage.PreparingDefense, reason),
        nextState,
        reason);

    private static MergeAutomationDecision Execute(
        string command,
        JObject? arguments,
        MergeAutomationState nextState,
        string reason) => new(
        new AutomationAction(command, arguments, AutomationStage.PreparingDefense, reason),
        nextState,
        reason);

    private static MergeAutomationDecision Complete(
        MergeAutomationState nextState,
        string detail,
        MergeAutomationCompletionKind completionKind = MergeAutomationCompletionKind.RecoveryRequired) =>
        new(null, nextState, detail, completionKind);

    private sealed class MergeGroup
    {
        public MergeGroup(
            string materialVehicleType,
            string resultVehicleType,
            int[] candidateVehicleIndexes,
            int[] candidateItemInstanceIds,
            int[] candidateVehicleInstanceIds)
        {
            MaterialVehicleType = materialVehicleType;
            ResultVehicleType = resultVehicleType;
            CandidateVehicleIndexes = candidateVehicleIndexes;
            CandidateItemInstanceIds = candidateItemInstanceIds;
            CandidateVehicleInstanceIds = candidateVehicleInstanceIds;
            IndexKey = string.Join(":", candidateVehicleIndexes.Select(index => index.ToString(CultureInfo.InvariantCulture)));
        }

        public string MaterialVehicleType { get; }
        public string ResultVehicleType { get; }
        public IReadOnlyList<int> CandidateVehicleIndexes { get; }
        public IReadOnlyList<int> CandidateItemInstanceIds { get; }
        public IReadOnlyList<int> CandidateVehicleInstanceIds { get; }
        public string IndexKey { get; }
    }

    private sealed class MergeOption
    {
        public MergeOption(int index, string fetter)
        {
            Index = index;
            Fetter = fetter;
        }

        public int Index { get; }
        public string Fetter { get; }
    }
}
