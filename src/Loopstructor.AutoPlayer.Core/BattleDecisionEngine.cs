using System;
using System.Collections.Generic;
using System.Linq;
using Newtonsoft.Json.Linq;

namespace Loopstructor.AutoPlayer.Core;

public enum BattleDisposablePhase
{
    Ready,
    AwaitingPreview,
    Confirming
}

public sealed class BattleDecisionContext
{
    public BattleDisposablePhase DisposablePhase { get; set; } = BattleDisposablePhase.Ready;
    public bool AllowDisposableUse { get; set; } = true;
    public bool AllowVehicleReinforcement { get; set; } = true;
    public JObject? DisposableConfirmationArguments { get; set; }
    public JObject? DisposableGridOptionsResult { get; set; }
    public JObject? IndependentVehicleState { get; set; }
}

public sealed class DefenseExpansionRailVerification
{
    public bool Verified { get; set; }
    public bool Pending { get; set; }
    public string Detail { get; set; } = string.Empty;
    public int RailInstanceId { get; set; }
    public JObject? Rail { get; set; }
}

public sealed class RuntimeSpecialStationDisposable
{
    public string DisposableEnum { get; set; } = string.Empty;
    public string StationKind { get; set; } = string.Empty;
    public string EffectIdentity { get; set; } = string.Empty;
    public int Count { get; set; }
    public int Index { get; set; }
    public JObject ItemIdentity { get; set; } = new();
    public bool IsAttribute => string.Equals(StationKind, "AttributeCatapult", StringComparison.Ordinal);
}

/// <summary>
/// Chooses one ordinary player action from already queried runtime state.
/// This policy is deliberately stateless; the caller owns polling and the disposable phase transition.
/// </summary>
public sealed class BattleDecisionEngine
{
    private const string ExpansionAttributeDisposableEnum = "FreePoint_Attribute";
    private const string ExpansionCommonDisposableEnum = "FreePoint";
    private static readonly HashSet<string> ReservedRailExpansionDisposableEnums = new(StringComparer.OrdinalIgnoreCase)
    {
        "EnergyPoint",
        "FreePoint",
        "FreePoint_Attribute",
        "AddNewPoint",
        "AddNewPoint_Attribute",
        "CreateFreeEnergyExpansion"
    };

    public AutomationAction? Decide(
        BattleDecisionContext context,
        JObject? waveResult,
        JObject? disposableResult,
        JObject? independentVehicleState,
        JObject? vehicleResult)
    {
        if (context == null)
        {
            throw new ArgumentNullException(nameof(context));
        }

        JObject disposable = State(disposableResult);
        bool isInPreview = disposable["isInPreview"]?.Value<bool>() == true;
        if (isInPreview)
        {
            if (context.DisposablePhase == BattleDisposablePhase.Ready)
            {
                return null;
            }

            AutomationAction? confirmation = DecideDisposableConfirmation(context, disposable);
            if (confirmation != null)
            {
                return confirmation;
            }

            if (!IsSupportedPreview(disposable))
            {
                return new AutomationAction(
                    "cancelDisposable",
                    null,
                    AutomationStage.Battle,
                    "取消无法安全确认的道具预览，恢复游戏输入。");
            }

            return null;
        }

        if (context.DisposablePhase != BattleDisposablePhase.Ready)
        {
            return null;
        }

        JObject wave = State(waveResult);
        if (context.AllowDisposableUse && IsActiveBattle(wave))
        {
            AutomationAction? useDisposable = DecideDisposableUse(disposable);
            if (useDisposable != null)
            {
                return useDisposable;
            }
        }

        return context.AllowVehicleReinforcement
            ? DecideIndependentVehicleDeployment(
                vehicleResult ?? independentVehicleState ?? context.IndependentVehicleState)
            : null;
    }

    /// <summary>
    /// Chooses one identity-safe deployment from the independent-vehicle snapshot. Capacity is
    /// authoritative only when it includes both running and FIFO-waiting vehicles.
    /// </summary>
    public AutomationAction? DecideIndependentVehicleDeployment(JObject? stateResult)
    {
        JObject state = State(stateResult);
        JObject? vehicle = (state["vehicles"] as JArray)?.OfType<JObject>()
            .Where(IsBagVehicle)
            .Where(item => ReadInt(item["instanceId"], 0) != 0)
            .OrderByDescending(item => ReadDouble(item["baseCombatPower"], 0d))
            .ThenBy(item => ReadInt(item["instanceId"], int.MaxValue))
            .FirstOrDefault();
        JObject? rail = (state["rails"] as JArray)?.OfType<JObject>()
            .Where(IsUsablePlayerRail)
            .Where(item => ReadInt(item["energyPointCount"], 0) == 1)
            .Where(item => ReadInt(item["energyPointInstanceId"], 0) != 0)
            .Where(item => ReadInt(item["freeCapacity"], 0) > 0)
            .OrderByDescending(item => ReadInt(item["occupiedCount"], 0))
            .ThenBy(item => ReadDouble(item["loopCycleSeconds"], double.MaxValue))
            .ThenBy(item => ReadInt(item["instanceId"], int.MaxValue))
            .FirstOrDefault();
        if (vehicle == null || rail == null) return null;

        int vehicleInstanceId = ReadInt(vehicle["instanceId"], 0);
        int energyPointInstanceId = ReadInt(rail["energyPointInstanceId"], 0);
        int railInstanceId = ReadInt(rail["instanceId"], ReadInt(rail["railInstanceId"], 0));
        if (vehicleInstanceId == 0 || energyPointInstanceId == 0 || railInstanceId == 0) return null;

        string name = vehicle["name"]?.Value<string>()
                      ?? vehicle["vehicleType"]?.Value<string>()
                      ?? "未知战车";
        return new AutomationAction(
            "deployVehicleToEnergyPoint",
            JObject.FromObject(new { vehicleInstanceId, energyPointInstanceId, railInstanceId }),
            AutomationStage.PreparingDefense,
            $"轨道动态容量尚余 {ReadInt(rail["freeCapacity"], 0)}；向唯一能量点投放 {name}，占用时由游戏 FIFO 排队。");
    }

    public bool NeedsIndependentDefenseExpansion(JObject? stateResult)
    {
        JObject state = State(stateResult);
        bool hasBagVehicle = (state["vehicles"] as JArray)?.OfType<JObject>().Any(IsBagVehicle) == true;
        JObject[] rails = (state["rails"] as JArray)?.OfType<JObject>()
            .Where(IsUsablePlayerRail)
            .Where(item => ReadInt(item["energyPointCount"], 0) == 1)
            .ToArray() ?? Array.Empty<JObject>();
        return hasBagVehicle && rails.Length > 0 &&
               rails.All(item => ReadInt(item["freeCapacity"], 0) <= 0);
    }

    public AutomationAction? DecideDefenseExpansion(
        JObject? independentVehicleState,
        JObject? catapultResult,
        ISet<string>? rejectedPathKeys = null)
    {
        if (!NeedsIndependentDefenseExpansion(independentVehicleState))
        {
            return null;
        }

        JObject catapultsState = State(catapultResult);
        List<JObject> allPoints = (catapultsState["catapults"] as JArray)?
            .OfType<JObject>()
            .ToList() ?? new List<JObject>();
        List<JObject> candidates = allPoints
            .Where(IsAvailableExpansionPoint)
            .ToList();
        List<JObject> occupiedPoints = allPoints
            .Where(item => ReadInt(item["railMembershipCount"], 0) > 0)
            .Where(item => TryReadPoint(item["grid"], out _, out _))
            .ToList();
        List<JObject> attributes = candidates
            .Where(item => item["isAttribute"]?.Value<bool>() == true)
            .OrderBy(item => item["name"]?.Value<string>() ?? string.Empty, StringComparer.Ordinal)
            .ThenBy(item => ReadInt(item["linePointInstanceId"], int.MaxValue))
            .ToList();
        List<JObject> commonPoints = candidates
            .Where(item => item["isAttribute"]?.Value<bool>() != true)
            .Where(item => TryReadPoint(item["grid"], out _, out _))
            .OrderBy(item => item["name"]?.Value<string>() ?? string.Empty, StringComparer.Ordinal)
            .ThenBy(item => ReadInt(item["linePointInstanceId"], int.MaxValue))
            .ToList();
        if (attributes.Count == 0 || commonPoints.Count < 2)
        {
            return null;
        }

        List<ExpansionPathCandidate> paths = new();
        List<RailLoopPointCandidate> loopPoints = new();
        foreach (JObject point in attributes.Concat(commonPoints))
        {
            int instanceId = ReadInt(point["linePointInstanceId"], 0);
            if (instanceId == 0 || !TryReadPoint(point["grid"], out double x, out double y))
            {
                continue;
            }

            loopPoints.Add(new RailLoopPointCandidate
            {
                InstanceId = instanceId,
                IsAttribute = point["isAttribute"]?.Value<bool>() == true,
                Grid = new RailLayoutPoint(x, y)
            });
        }
        RailLoopPlan? plannedLoop = RailLayoutStrategyPlanner.PlanPlayerLoop(loopPoints);
        if (plannedLoop != null &&
            plannedLoop.OrderedPointInstanceIds.Count >= 3 &&
            IsAcceptableNewDefenseLoop(plannedLoop.Score))
        {
            JArray plannedIds = new(plannedLoop.OrderedPointInstanceIds);
            string plannedKey = BuildDefenseExpansionPathKey(plannedIds);
            if (rejectedPathKeys?.Contains(plannedKey) != true)
            {
                paths.Add(new ExpansionPathCandidate(
                    ScoreExpansionPlan(plannedLoop, occupiedPoints),
                    plannedKey,
                    plannedIds));
            }
        }

        foreach (JObject attribute in attributes)
        {
            if (!TryReadPoint(attribute["grid"], out double attributeX, out double attributeY))
            {
                continue;
            }

            int attributeId = ReadInt(attribute["linePointInstanceId"], 0);
            if (attributeId == 0)
            {
                continue;
            }

            for (int first = 0; first < commonPoints.Count - 1; first++)
            {
                int firstId = ReadInt(commonPoints[first]["linePointInstanceId"], 0);
                if (firstId == 0) continue;

                for (int second = first + 1; second < commonPoints.Count; second++)
                {
                    int secondId = ReadInt(commonPoints[second]["linePointInstanceId"], 0);
                    if (secondId == 0) continue;

                    JArray ids = new(attributeId, firstId, secondId);
                    string key = BuildDefenseExpansionPathKey(ids);
                    if (rejectedPathKeys?.Contains(key) == true) continue;

                    ExpansionLayoutScore score = ScoreExpansionLayout(
                        attributeX,
                        attributeY,
                        commonPoints[first],
                        commonPoints[second],
                        occupiedPoints);
                    if (score.Area <= 0.000001d || !IsAcceptableNewDefenseLoop(score.Layout))
                    {
                        continue;
                    }
                    paths.Add(new ExpansionPathCandidate(score, key, ids));
                }
            }
        }

        JArray? linePointInstanceIds = paths
            .OrderBy(
                path => path.Score.Layout,
                Comparer<RailLayoutScore>.Create(RailLayoutStrategyPlanner.CompareForDefense))
            .ThenBy(path => path.Score.HasCoverageContext ? path.Score.SideRank : 0)
            .ThenBy(path => path.Score.HasCoverageContext ? path.Score.DirectionCosine : 0d)
            .ThenBy(path => path.Score.Distance)
            .ThenByDescending(path => path.Score.Area)
            .ThenBy(path => path.Key, StringComparer.Ordinal)
            .Select(path => path.Ids)
            .FirstOrDefault();
        if (linePointInstanceIds == null)
        {
            return null;
        }

        JObject expansionArguments = new()
        {
            ["linePointInstanceIds"] = linePointInstanceIds
        };

        return new AutomationAction(
            "drawRailPath",
            expansionArguments,
            AutomationStage.PreparingDefense,
            occupiedPoints.Count > 0
                ? "所有合法轨道均已满载且背包仍有战车；创建一条仅含一个能量点、优先补足相反方向的额外合法闭环。"
                : "所有合法轨道均已满载且背包仍有战车；创建一条仅含一个能量点的最短额外合法闭环。");
    }

    public static string BuildDefenseExpansionPathKey(JToken? linePointInstanceIds) =>
        linePointInstanceIds is JArray ids
            ? string.Join(":", ids.Values<int>())
            : string.Empty;

    public bool IsLegalDefenseExpansionPreview(JObject? previewResult)
    {
        JObject state = State(previewResult);
        return state["wouldBeLegal"]?.Value<bool>() == true
               && state["sideEffectCheckPassed"]?.Value<bool>() == true
               && state["statePolluted"]?.Value<bool>() != true
               && ReadInt(state["beforeRailCount"], -1) == ReadInt(state["afterRailCount"], -2);
    }

    public bool IsUsableDefenseExpansionRailBaseline(JObject? railResult) =>
        TryReadRailSnapshot(railResult, out _, out _, out _);

    public int ReadDrawnRailInstanceId(JObject? drawResult) =>
        ReadInt(State(drawResult).SelectToken("rail.instanceId"), 0);

    public DefenseExpansionRailVerification VerifyDefenseExpansionRail(
        JObject? baselineResult,
        JObject? drawResult,
        JObject? currentRailResult,
        AutomationAction? drawAction,
        int expectedRailInstanceId)
    {
        if (!TryReadRailSnapshot(
                baselineResult,
                out int baselineCount,
                out Dictionary<int, JObject> baselineRails,
                out string baselineFailure))
        {
            return RailVerificationFailure("轨道基线无效：" + baselineFailure);
        }

        if (!TryReadRailSnapshot(
                currentRailResult,
                out int currentCount,
                out Dictionary<int, JObject> currentRails,
                out string currentFailure))
        {
            return RailVerificationFailure("扩建后轨道状态无效：" + currentFailure);
        }

        if (baselineRails.Keys.Any(id => !currentRails.ContainsKey(id)))
        {
            return RailVerificationFailure("扩建后既有轨道身份发生变化，无法证明本次只新增一条轨道。");
        }

        int[] addedRailIds = currentRails.Keys
            .Where(id => !baselineRails.ContainsKey(id))
            .OrderBy(id => id)
            .ToArray();
        if (addedRailIds.Length == 0 && currentCount == baselineCount)
        {
            return new DefenseExpansionRailVerification
            {
                Pending = true,
                Detail = "尚未观察到新增轨道。"
            };
        }

        if (currentCount != baselineCount + 1 || addedRailIds.Length != 1)
        {
            return RailVerificationFailure(
                $"预期轨道数增加 1 且仅有一个新增身份，实际数量 {baselineCount}->{currentCount}，新增身份数 {addedRailIds.Length}。");
        }

        int addedRailId = addedRailIds[0];
        if (expectedRailInstanceId != 0 && addedRailId != expectedRailInstanceId)
        {
            return RailVerificationFailure(
                $"新增轨道身份 {addedRailId} 与 drawResult 身份 {expectedRailInstanceId} 不一致。");
        }

        int[] selectedPointIds = (drawAction?.Arguments["linePointInstanceIds"] as JArray)?
            .Values<int>()
            .ToArray() ?? Array.Empty<int>();
        if (selectedPointIds.Length < 3 ||
            selectedPointIds.Any(id => id == 0) ||
            selectedPointIds.Distinct().Count() != selectedPointIds.Length)
        {
            return RailVerificationFailure("本次扩建缺少有效且互不重复的选定轨道点身份。");
        }

        JObject addedRail = currentRails[addedRailId];
        if (!RailMatchesSelectedPoints(addedRail, selectedPointIds))
        {
            return RailVerificationFailure("唯一新增轨道的站点身份与本次选定点集不一致。");
        }

        if (addedRail["isLegalPlayerLoop"]?.Value<bool>() != true ||
            addedRail["isLoop"]?.Value<bool>() != true ||
            addedRail["isOnField"]?.Value<bool>() == false)
        {
            return RailVerificationFailure("唯一新增轨道不是场上的合法玩家闭环。");
        }

        JObject? drawRail = State(drawResult)["rail"] as JObject;
        if (drawRail != null)
        {
            int drawRailId = ReadInt(drawRail["instanceId"], 0);
            if (drawRailId != 0 && drawRailId != addedRailId)
            {
                return RailVerificationFailure("queryRail 的新增轨道身份与 drawResult.rail 不一致。");
            }

            if (!RailMatchesSelectedPoints(drawRail, selectedPointIds))
            {
                return RailVerificationFailure("drawResult.rail 的站点身份与本次选定点集不一致。");
            }

            int drawInternalId = ReadInt(drawRail["railInternalId"], ReadInt(drawRail["id"], 0));
            int queriedInternalId = ReadInt(addedRail["railInternalId"], ReadInt(addedRail["id"], 0));
            if (drawInternalId != 0 && queriedInternalId != 0 && drawInternalId != queriedInternalId)
            {
                return RailVerificationFailure("queryRail 的新增轨道内部 ID 与 drawResult.rail 不一致。");
            }
        }

        return new DefenseExpansionRailVerification
        {
            Verified = true,
            Detail = "已验证唯一新增轨道的数量、身份、合法性和站点集合。",
            RailInstanceId = addedRailId,
            Rail = (JObject)addedRail.DeepClone()
        };
    }

    public int CountAvailableExpansionAttributes(JObject? catapultResult) =>
        AvailableExpansionPoints(catapultResult)
            .Count(item => item["isAttribute"]?.Value<bool>() == true);

    public int CountAvailableExpansionStations(JObject? catapultResult, string disposableEnum)
    {
        List<JObject> matching = AvailableExpansionPoints(catapultResult)
            .Where(item => string.Equals(
                item["recycleDisposableEnum"]?.Value<string>(),
                disposableEnum,
                StringComparison.Ordinal))
            .ToList();
        if (matching.Count > 0) return matching.Count;
        bool attribute = string.Equals(disposableEnum, ExpansionAttributeDisposableEnum, StringComparison.Ordinal);
        return AvailableExpansionPoints(catapultResult).Count(item =>
            attribute
                ? item["isAttribute"]?.Value<bool>() == true
                : false);
    }

    public string RequiredExpansionDisposable(JObject? catapultResult)
    {
        List<JObject> points = AvailableExpansionPoints(catapultResult);
        if (points.All(item => item["isAttribute"]?.Value<bool>() != true))
        {
            return ExpansionAttributeDisposableEnum;
        }

        List<JObject> attributes = points
            .Where(item => item["isAttribute"]?.Value<bool>() == true)
            .Where(item => TryReadPoint(item["grid"], out _, out _))
            .ToList();
        List<JObject> commons = points
            .Where(item => item["isAttribute"]?.Value<bool>() != true)
            .Where(item => TryReadPoint(item["grid"], out _, out _))
            .ToList();
        if (commons.Count < 2)
        {
            return ExpansionCommonDisposableEnum;
        }

        foreach (JObject attribute in attributes)
        {
            TryReadPoint(attribute["grid"], out double ax, out double ay);
            for (int first = 0; first < commons.Count - 1; first++)
            {
                TryReadPoint(commons[first]["grid"], out double bx, out double by);
                for (int second = first + 1; second < commons.Count; second++)
                {
                    TryReadPoint(commons[second]["grid"], out double cx, out double cy);
                    RailLayoutScore layout = RailLayoutStrategyPlanner.EvaluateEstimated(new[]
                    {
                        new RailLayoutPoint(ax, ay),
                        new RailLayoutPoint(bx, by),
                        new RailLayoutPoint(cx, cy)
                    });
                    if (IsAcceptableNewDefenseLoop(layout))
                    {
                        return string.Empty;
                    }
                }
            }
        }

        return ExpansionCommonDisposableEnum;
    }

    private static bool IsAcceptableNewDefenseLoop(RailLayoutScore? layout) =>
        layout?.IsValid == true &&
        layout.IsSimpleCycle &&
        layout.EncirclesBase;

    public bool NeedsExpansionAttributePlacement(JObject? catapultResult)
    {
        List<JObject> points = AvailableExpansionPoints(catapultResult);
        return points.All(item => item["isAttribute"]?.Value<bool>() != true)
               && points.Count(item => item["isAttribute"]?.Value<bool>() != true) >= 2;
    }

    public AutomationAction? DecideExpansionAttributeDisposableUse(JObject? disposableResult) =>
        DecideExpansionDisposableUse(disposableResult, ExpansionAttributeDisposableEnum);

    public AutomationAction? DecideExpansionDisposableUse(
        JObject? disposableResult,
        string disposableEnum)
    {
        JObject state = State(disposableResult);
        if (state["isInPreview"]?.Value<bool>() == true)
        {
            return null;
        }

        JObject? item = (state["items"] as JArray)?
            .OfType<JObject>()
            .Where(candidate =>
                string.Equals(
                    candidate["disposableEnum"]?.Value<string>(),
                    disposableEnum,
                    StringComparison.Ordinal))
            .Where(candidate =>
                candidate["active"]?.Value<bool>() != false
                && candidate["buttonActive"]?.Value<bool>() != false
                && ReadInt(candidate["count"], 0) > 0
                && string.Equals(
                    candidate["interactionType"]?.Value<string>(),
                    "GridChooseInteraction",
                    StringComparison.Ordinal))
            .OrderBy(candidate => ReadInt(candidate["index"], int.MaxValue))
            .FirstOrDefault();
        if (item == null)
        {
            return null;
        }

        JObject identity = BuildIdentity(item, preferItemInstanceId: true);
        if (!identity.HasValues)
        {
            return null;
        }

        identity["disposableEnum"] = disposableEnum;
        return new AutomationAction(
            "useDisposable",
            identity,
            AutomationStage.PreparingDefense,
            disposableEnum == ExpansionAttributeDisposableEnum
                ? "使用背包中的动力弹射点道具，进入玩家格子预览流程。"
                : "使用左下角背包中的普通弹射点道具，进入玩家格子预览流程。");
    }

    public JObject? SelectExpansionAttributeGrid(JObject? gridOptionsResult, JObject? catapultResult)
    {
        JObject options = State(gridOptionsResult);
        if (!string.Equals(
                options["disposableEnum"]?.Value<string>(),
                ExpansionAttributeDisposableEnum,
                StringComparison.Ordinal))
        {
            return null;
        }

        List<JObject> allPoints = ((State(catapultResult)["catapults"] as JArray)?.OfType<JObject>()
                ?? Enumerable.Empty<JObject>())
            .ToList();
        List<JObject> commonPoints = allPoints
            .Where(IsAvailableExpansionPoint)
            .Where(item => item["isAttribute"]?.Value<bool>() != true)
            .Where(item => TryReadPoint(item["grid"], out _, out _))
            .ToList();
        List<JObject> occupiedPoints = allPoints
            .Where(item => ReadInt(item["railMembershipCount"], 0) > 0)
            .Where(item => TryReadPoint(item["grid"], out _, out _))
            .ToList();
        if (commonPoints.Count < 2)
        {
            return null;
        }

        List<ExpansionGridCandidate> candidates = new();
        foreach (JObject option in (options["validGrids"] as JArray)?.OfType<JObject>()
                 ?? Enumerable.Empty<JObject>())
        {
            if (option["grid"] is not JObject grid ||
                !TryReadPoint(grid, out double x, out double y))
            {
                continue;
            }

            for (int first = 0; first < commonPoints.Count - 1; first++)
            {
                for (int second = first + 1; second < commonPoints.Count; second++)
                {
                    ExpansionLayoutScore score = ScoreExpansionLayout(
                        x,
                        y,
                        commonPoints[first],
                        commonPoints[second],
                        occupiedPoints);
                    if (score.Area <= 0.000001d)
                    {
                        continue;
                    }
                    candidates.Add(new ExpansionGridCandidate(score, (int)x, (int)y, grid));
                }
            }
        }

        double reasonableLoopLengthLimit =
            DefenseExpansionAttributeGridRanker.CalculateReasonableLoopLengthLimit(
                candidates.Select(candidate => candidate.Score.Layout));
        JObject? selected = candidates
            .Where(candidate => candidate.Score.Layout.LoopLength <= reasonableLoopLengthLimit)
            .OrderBy(
                candidate => candidate.Score.Layout,
                Comparer<RailLayoutScore>.Create(RailLayoutStrategyPlanner.CompareForDefense))
            .ThenBy(candidate => candidate.Score.HasCoverageContext ? candidate.Score.SideRank : 0)
            .ThenBy(candidate => candidate.Score.HasCoverageContext ? candidate.Score.DirectionCosine : 0d)
            .ThenBy(candidate => candidate.Score.Distance)
            .ThenByDescending(candidate => candidate.Score.Area)
            .ThenBy(candidate => candidate.X)
            .ThenBy(candidate => candidate.Y)
            .Select(candidate => candidate.Grid)
            .FirstOrDefault();
        return selected == null ? null : (JObject)selected.DeepClone();
    }

    public int ReadExpansionAttributeInteractionId(JObject? disposableResult)
        => ReadExpansionInteractionId(disposableResult, ExpansionAttributeDisposableEnum);

    public int ReadExpansionInteractionId(JObject? disposableResult, string disposableEnum)
    {
        JObject state = State(disposableResult);
        return state["isInPreview"]?.Value<bool>() == true
               && string.Equals(
                   state["disposableEnum"]?.Value<string>(),
                   disposableEnum,
                   StringComparison.Ordinal)
            ? ReadInt(state["interactionInstanceId"], 0)
            : 0;
    }

    public bool IsOwnedExpansionAttributePreview(
        JObject? disposableResult,
        int interactionInstanceId,
        bool requireGridInteraction)
        => IsOwnedExpansionPreview(
            disposableResult,
            interactionInstanceId,
            ExpansionAttributeDisposableEnum,
            requireGridInteraction);

    public bool IsOwnedExpansionPreview(
        JObject? disposableResult,
        int interactionInstanceId,
        string disposableEnum,
        bool requireGridInteraction)
    {
        JObject state = State(disposableResult);
        if (interactionInstanceId == 0 ||
            state["isInPreview"]?.Value<bool>() != true ||
            ReadInt(state["interactionInstanceId"], 0) != interactionInstanceId ||
            !string.Equals(
                state["disposableEnum"]?.Value<string>(),
                disposableEnum,
                StringComparison.Ordinal))
        {
            return false;
        }

        return !requireGridInteraction || string.Equals(
            state["interactionType"]?.Value<string>(),
            "GridChooseInteraction",
            StringComparison.Ordinal);
    }

    public bool IsCleanDisposableInteractionIdle(JObject? interactionGuardResult)
    {
        JObject state = State(interactionGuardResult);
        return state["contractAvailable"]?.Value<bool>() == true &&
               state["observationConsistent"]?.Value<bool>() == true &&
               state["noActiveInteraction"]?.Value<bool>() == true &&
               state["isInPreview"]?.Value<bool>() != true &&
               state["hasLastInteraction"]?.Value<bool>() != true &&
               ReadInt(state["interactionInstanceId"], 0) == 0;
    }

    public AutomationAction? DecideExpansionAttributeCancellation(
        JObject? disposableResult,
        int interactionInstanceId)
        => DecideExpansionCancellation(
            disposableResult,
            interactionInstanceId,
            ExpansionAttributeDisposableEnum);

    public AutomationAction? DecideExpansionCancellation(
        JObject? disposableResult,
        int interactionInstanceId,
        string disposableEnum)
    {
        if (!IsOwnedExpansionPreview(
                disposableResult,
                interactionInstanceId,
                disposableEnum,
                requireGridInteraction: false))
        {
            return null;
        }

        return new AutomationAction(
            "cancelDisposable",
            JObject.FromObject(new
            {
                disposableEnum,
                interactionInstanceId
            }),
            AutomationStage.PreparingDefense,
            "取消由自动游玩创建的弹射点预览，恢复玩家输入。");
    }

    public AutomationAction? DecideExpansionAttributeConfirmation(
        AutomationAction? useAction,
        JObject? selectedGrid,
        JObject? disposableResult,
        int interactionInstanceId)
    {
        if (useAction == null ||
            selectedGrid == null ||
            !IsOwnedExpansionAttributePreview(disposableResult, interactionInstanceId, requireGridInteraction: true))
        {
            return null;
        }

        JObject arguments = (JObject)useAction.Arguments.DeepClone();
        arguments["disposableEnum"] = ExpansionAttributeDisposableEnum;
        arguments["interactionInstanceId"] = interactionInstanceId;
        arguments["grid"] = selectedGrid.DeepClone();
        return new AutomationAction(
            "confirmDisposableGrid",
            arguments,
            AutomationStage.PreparingDefense,
            "在已验证的格子确认动力弹射点道具。");
    }

    public AutomationAction? DecideExpansionAttributeDirectConfirmation(
        AutomationAction? itemIdentityAction,
        JObject? selectedGrid)
        => DecideExpansionDirectConfirmation(
            itemIdentityAction,
            selectedGrid,
            ExpansionAttributeDisposableEnum);

    public AutomationAction? DecideExpansionDirectConfirmation(
        AutomationAction? itemIdentityAction,
        JObject? selectedGrid,
        string disposableEnum)
    {
        if (itemIdentityAction == null ||
            selectedGrid == null ||
            !string.Equals(itemIdentityAction.Command, "useDisposable", StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(
                itemIdentityAction.Arguments["disposableEnum"]?.Value<string>(),
                disposableEnum,
                StringComparison.Ordinal) ||
            !HasIdentity(itemIdentityAction.Arguments) ||
            !TryReadPoint(selectedGrid, out double gridX, out double gridY) ||
            gridX != Math.Truncate(gridX) ||
            gridY != Math.Truncate(gridY))
        {
            return null;
        }

        JObject arguments = (JObject)itemIdentityAction.Arguments.DeepClone();
        arguments.Remove("interactionInstanceId");
        // These values are AutoPlayer-only placement verification metadata. The
        // game's native confirmation command must receive only its own schema.
        arguments.Remove("stationKind");
        arguments.Remove("effectIdentity");
        arguments["disposableEnum"] = disposableEnum;
        arguments["grid"] = selectedGrid.DeepClone();
        return new AutomationAction(
            "confirmDisposableGrid",
            arguments,
            AutomationStage.PreparingDefense,
            disposableEnum == ExpansionAttributeDisposableEnum
                ? "按背包道具的稳定身份在单次玩家等价命令中打开并确认动力弹射点。"
                : "按背包道具的稳定身份在单次玩家等价命令中打开并确认普通弹射点。");
    }

    public IReadOnlyList<RuntimeSpecialStationDisposable> DiscoverMovableStationDisposables(
        JObject? disposableResult)
    {
        JObject state = State(disposableResult);
        return ((state["items"] as JArray)?.OfType<JObject>() ?? Enumerable.Empty<JObject>())
            .Where(item => item["active"]?.Value<bool>() != false &&
                           item["buttonActive"]?.Value<bool>() != false &&
                           ReadInt(item["count"], 0) > 0 &&
                           string.Equals(item["interactionType"]?.Value<string>(),
                               "GridChooseInteraction", StringComparison.Ordinal) &&
                           item.SelectToken("effectFacts.canAlwaysMove")?.Value<bool>() == true &&
                           (string.Equals(item.SelectToken("effectFacts.stationKind")?.Value<string>(),
                                "AttributeCatapult", StringComparison.Ordinal) ||
                            string.Equals(item.SelectToken("effectFacts.stationKind")?.Value<string>(),
                                "CommonCatapult", StringComparison.Ordinal)))
            .Select(item => new RuntimeSpecialStationDisposable
            {
                DisposableEnum = item["disposableEnum"]?.Value<string>() ?? string.Empty,
                StationKind = item.SelectToken("effectFacts.stationKind")?.Value<string>() ?? string.Empty,
                EffectIdentity = item.SelectToken("effectFacts.buffIdentity")?.Value<string>() ??
                                 item.SelectToken("effectFacts.buffFlag")?.Value<string>() ?? string.Empty,
                Count = ReadInt(item["count"], 0),
                Index = ReadInt(item["index"], int.MaxValue),
                ItemIdentity = BuildIdentity(item, preferItemInstanceId: true)
            })
            .Where(item => !string.IsNullOrWhiteSpace(item.DisposableEnum) && item.ItemIdentity.HasValues)
            .OrderBy(item => SpecialStationNeedRank(item.EffectIdentity))
            .ThenByDescending(item => item.IsAttribute)
            .ThenBy(item => item.Index)
            .ThenBy(item => item.DisposableEnum, StringComparer.Ordinal)
            .ToArray();
    }

    public AutomationAction? DecideMovableStationDisposableUse(
        JObject? disposableResult,
        bool requireAttribute,
        bool requireCommon = false)
    {
        IEnumerable<RuntimeSpecialStationDisposable> candidates =
            DiscoverMovableStationDisposables(disposableResult)
            .Where(item => !requireAttribute || item.IsAttribute)
            .Where(item => !requireCommon || !item.IsAttribute);
        // An existing loop normally needs a movable relay before it needs a second origin. Keep
        // the runtime effect-priority order inside the same station kind, but never hard-code an enum.
        if (!requireAttribute && !requireCommon)
        {
            candidates = candidates.OrderBy(item => item.IsAttribute ? 1 : 0);
        }
        RuntimeSpecialStationDisposable? selected = candidates.FirstOrDefault();
        if (selected == null) return null;
        JObject arguments = (JObject)selected.ItemIdentity.DeepClone();
        arguments["disposableEnum"] = selected.DisposableEnum;
        arguments["stationKind"] = selected.StationKind;
        arguments["effectIdentity"] = selected.EffectIdentity;
        return new AutomationAction(
            "useDisposable",
            arguments,
            AutomationStage.PreparingDefense,
            $"使用运行时发现的可移动特殊{(selected.IsAttribute ? "始发" : "中继")}站 " +
            selected.DisposableEnum + "。");
    }

    public static bool IsMovableStationDisposable(JObject item) =>
        string.Equals(item["interactionType"]?.Value<string>(), "GridChooseInteraction", StringComparison.Ordinal) &&
        item.SelectToken("effectFacts.canAlwaysMove")?.Value<bool>() == true &&
        (string.Equals(item.SelectToken("effectFacts.stationKind")?.Value<string>(),
             "AttributeCatapult", StringComparison.Ordinal) ||
         string.Equals(item.SelectToken("effectFacts.stationKind")?.Value<string>(),
             "CommonCatapult", StringComparison.Ordinal));

    private static int SpecialStationNeedRank(string effectIdentity)
    {
        string value = effectIdentity ?? string.Empty;
        if (ContainsAny(value, "Capacity", "Launch", "Start", "容量", "发车", "始发")) return 0;
        if (ContainsAny(value, "Speed", "Energy", "Trigger", "Cycle", "速度", "能量", "触发", "回转")) return 1;
        return 2;
    }

    private static bool ContainsAny(string value, params string[] needles) =>
        needles.Any(needle => value.IndexOf(needle, StringComparison.OrdinalIgnoreCase) >= 0);

    private static AutomationAction? DecideDisposableUse(JObject disposable)
    {
        JObject? item = (disposable["items"] as JArray)?
            .OfType<JObject>()
            .Where(IsUsableDisposable)
            .Where(item => !IsMovableStationDisposable(item))
            .OrderByDescending(DisposableScore)
            .ThenBy(item => ReadInt(item["index"], int.MaxValue))
            .FirstOrDefault();
        if (item == null)
        {
            return null;
        }

        JObject identity = BuildIdentity(item, preferItemInstanceId: true);
        if (!identity.HasValues)
        {
            return null;
        }

        string name = item["disposableEnum"]?.Value<string>() ?? "未知道具";
        return new AutomationAction(
            "useDisposable",
            identity,
            AutomationStage.Battle,
            $"使用可用消耗品 {name}，进入玩家预览流程。");
    }

    private static AutomationAction? DecideDisposableConfirmation(
        BattleDecisionContext context,
        JObject disposable)
    {
        string confirmKind = ResolveConfirmKind(disposable);
        JObject arguments = context.DisposableConfirmationArguments != null
            ? (JObject)context.DisposableConfirmationArguments.DeepClone()
            : new JObject();

        string? command = confirmKind switch
        {
            "grid" => BuildGridConfirmation(arguments, context.DisposableGridOptionsResult)
                ? "confirmDisposableGrid"
                : null,
            "world" => HasObject(arguments, "world")
                ? "confirmDisposableWorld"
                : null,
            "positionRaycast" => HasObject(arguments, "world") || HasObject(arguments, "grid")
                ? "confirmDisposableTarget"
                : null,
            "targetRaycast" => BuildTargetConfirmation(arguments, disposable)
                ? "confirmDisposableTarget"
                : null,
            _ => null
        };
        if (command == null)
        {
            return null;
        }

        string name = disposable["disposableEnum"]?.Value<string>() ?? "当前道具";
        return new AutomationAction(
            command,
            arguments,
            AutomationStage.Battle,
            $"确认消耗品 {name} 的有效目标。");
    }

    private static bool IsUsablePlayerRail(JObject rail) =>
        rail["isLegalPlayerLoop"]?.Value<bool>() == true &&
        rail["isLoop"]?.Value<bool>() == true &&
        rail["isOnField"]?.Value<bool>() != false;

    private static bool RailHasDriverCapacity(JObject rail)
    {
        int driverCount = ReadInt(rail["driverCount"], 0);
        int driverMaxCount = ReadInt(rail["driverMaxCount"], 0);
        return rail["isDriverReachToMax"]?.Value<bool>() != true &&
               driverMaxCount > 0 &&
               driverCount < driverMaxCount;
    }

    private static bool IsActiveBattle(JObject wave)
    {
        bool active = wave["isInWaving"]?.Value<bool>() == true
                      || wave.SelectToken("wave.isInWaving")?.Value<bool>() == true;
        int? remaining = wave.SelectToken("enemy.remaining")?.Value<int?>()
                         ?? wave.SelectToken("wave.enemy.remaining")?.Value<int?>();
        return active && (!remaining.HasValue || remaining.Value > 0);
    }

    private static bool IsUsableDisposable(JObject item)
    {
        if (item["active"]?.Value<bool>() == false
            || item["buttonActive"]?.Value<bool>() == false
            || ReadInt(item["count"], 0) <= 0)
        {
            return false;
        }

        return HasIdentity(item) && IsSupportedDisposable(item);
    }

    private static bool IsSupportedDisposable(JObject item)
    {
        string disposableEnum = item["disposableEnum"]?.Value<string>() ?? string.Empty;
        if (ReservedRailExpansionDisposableEnums.Contains(disposableEnum))
        {
            return false;
        }

        string confirmKind = ResolveConfirmKind(item);
        string effectKind = item.SelectToken("effectFacts.effectKind")?.Value<string>() ?? string.Empty;
        bool safeEffect = effectKind is
            "vehicleBuff" or
            "targetBuff" or
            "createStationWithBuiltInBuff" or
            "createStationWithLegacyBuff";
        bool supportedConfirmation = confirmKind is "none" or "grid" or "world" or "positionRaycast"
                                     || confirmKind == "targetRaycast" && HasCompleteTargetRaycastContract(item);
        return safeEffect && supportedConfirmation;
    }

    private static bool HasCompleteTargetRaycastContract(JObject item)
    {
        if (item["confirmContract"] is not JObject contract ||
            contract["needsTarget"]?.Value<bool>() != true ||
            contract["needsWorldPosition"]?.Value<bool>() != true ||
            contract["targetCandidatesRequired"]?.Value<bool>() != true ||
            item["sameItemIdentityRequiredForConfirm"]?.Value<bool>() != true ||
            ReadInt(item["itemInstanceId"], ReadInt(item["instanceId"], 0)) == 0)
        {
            return false;
        }

        string confirmCommand = item["confirmCommand"]?.Value<string>() ?? string.Empty;
        bool hasTargetIdentityArgument = ContainsString(contract["allowedArgs"] as JArray, "targetInstanceId")
                                         || ContainsString(contract["allowedArgs"] as JArray, "instanceId")
                                         || ContainsString(contract["allowedArgs"] as JArray, "path");
        bool hasItemRestoreIdentity = ContainsString(item["restoreIdentityArgs"] as JArray, "itemInstanceId")
                                      || ContainsString(item["restoreIdentityArgs"] as JArray, "instanceId")
                                      || ContainsString(item["restoreIdentityArgs"] as JArray, "path");
        return confirmCommand.EndsWith("confirm_disposable_target", StringComparison.OrdinalIgnoreCase)
               && hasTargetIdentityArgument
               && hasItemRestoreIdentity;
    }

    private static bool IsSupportedPreview(JObject disposable)
    {
        string confirmKind = ResolveConfirmKind(disposable);
        return confirmKind is "grid" or "world" or "positionRaycast" or "targetRaycast";
    }

    private static int DisposableScore(JObject item)
    {
        string effectKind = item.SelectToken("effectFacts.effectKind")?.Value<string>() ?? string.Empty;
        int effectScore = effectKind switch
        {
            "vehicleBuff" => 500,
            "targetBuff" => 400,
            "createStationWithBuiltInBuff" => 300,
            "createStationWithLegacyBuff" => 290,
            _ => 100
        };
        return effectScore + Math.Min(ReadInt(item["count"], 0), 20);
    }

    private static bool IsBagVehicle(JObject vehicle) =>
        vehicle["inBag"]?.Value<bool>() == true
        && vehicle["isFixedHead"]?.Value<bool>() != true
        && HasIdentity(vehicle);

    private static List<JObject> AvailableExpansionPoints(JObject? catapultResult) =>
        ((State(catapultResult)["catapults"] as JArray)?.OfType<JObject>()
            ?? Enumerable.Empty<JObject>())
        .Where(IsAvailableExpansionPoint)
        .ToList();

    private static bool TryReadRailSnapshot(
        JObject? railResult,
        out int railCount,
        out Dictionary<int, JObject> rails,
        out string failure)
    {
        JObject state = State(railResult);
        railCount = ReadInt(state["railCount"], -1);
        rails = new Dictionary<int, JObject>();
        failure = string.Empty;
        if (railCount < 0 || state["rails"] is not JArray railItems)
        {
            failure = "缺少 railCount 或 rails。";
            return false;
        }

        foreach (JObject rail in railItems.OfType<JObject>())
        {
            int instanceId = ReadInt(rail["instanceId"], 0);
            if (instanceId == 0)
            {
                failure = "轨道缺少 instanceId。";
                return false;
            }

            if (rails.ContainsKey(instanceId))
            {
                failure = "轨道 instanceId 重复。";
                return false;
            }

            rails[instanceId] = rail;
        }

        if (railItems.Count != rails.Count || railCount != rails.Count)
        {
            failure = $"railCount={railCount} 与唯一轨道状态数量 {rails.Count} 不一致。";
            return false;
        }

        return true;
    }

    private static bool RailMatchesSelectedPoints(JObject rail, IReadOnlyCollection<int> selectedPointIds)
    {
        int[] railPointIds = (rail["points"] as JArray)?
            .OfType<JObject>()
            .Select(point => ReadInt(point["instanceId"], 0))
            .ToArray() ?? Array.Empty<int>();
        return railPointIds.Length == selectedPointIds.Count
               && railPointIds.All(id => id != 0)
               && new HashSet<int>(railPointIds).SetEquals(selectedPointIds);
    }

    private static DefenseExpansionRailVerification RailVerificationFailure(string detail) => new()
    {
        Detail = detail
    };

    private static bool IsAvailableExpansionPoint(JObject point) =>
        point["active"]?.Value<bool>() != false
        && point["canUseForNewRail"]?.Value<bool>() == true
        && point["canPickLine"]?.Value<bool>() != false
        && point["frozen"]?.Value<bool>() != true
        && point["railReachMax"]?.Value<bool>() != true
        && ReadInt(point["railMembershipCount"], 0) == 0
        && ReadInt(point["linePointInstanceId"], 0) != 0;

    private static double ExpansionPointDistanceSquared(JObject point, double attributeX, double attributeY)
    {
        return TryReadPoint(point["grid"], out double x, out double y)
            ? DistanceSquared(x, y, attributeX, attributeY)
            : double.MaxValue;
    }

    private static ExpansionLayoutScore ScoreExpansionLayout(
        double attributeX,
        double attributeY,
        JObject first,
        JObject second,
        IReadOnlyCollection<JObject> occupiedPoints)
    {
        if (!TryReadPoint(first["grid"], out double firstX, out double firstY) ||
            !TryReadPoint(second["grid"], out double secondX, out double secondY))
        {
            return new ExpansionLayoutScore(false, 1, 1d, double.MaxValue, 0d, new RailLayoutScore());
        }

        double distance = DistanceSquared(firstX, firstY, attributeX, attributeY)
                          + DistanceSquared(secondX, secondY, attributeX, attributeY)
                          + DistanceSquared(firstX, firstY, secondX, secondY);
        double area = Math.Abs(
            (firstX - attributeX) * (secondY - attributeY)
            - (firstY - attributeY) * (secondX - attributeX));
        RailLayoutPoint[] layoutPoints =
        {
            new RailLayoutPoint(attributeX, attributeY),
            new RailLayoutPoint(firstX, firstY),
            new RailLayoutPoint(secondX, secondY)
        };
        return ScoreExpansionContext(
            RailLayoutStrategyPlanner.EvaluateEstimated(layoutPoints),
            layoutPoints,
            occupiedPoints,
            distance,
            area);
    }

    private static ExpansionLayoutScore ScoreExpansionPlan(
        RailLoopPlan plan,
        IReadOnlyCollection<JObject> occupiedPoints)
    {
        RailLayoutPoint[] points = plan.OrderedPoints.ToArray();
        double distance = 0d;
        double twiceArea = 0d;
        for (int index = 0; index < points.Length; index++)
        {
            RailLayoutPoint from = points[index];
            RailLayoutPoint to = points[(index + 1) % points.Length];
            double dx = from.X - to.X;
            double dy = from.Y - to.Y;
            distance += dx * dx + dy * dy;
            twiceArea += from.X * to.Y - from.Y * to.X;
        }
        return ScoreExpansionContext(
            plan.Score,
            points,
            occupiedPoints,
            distance,
            Math.Abs(twiceArea));
    }

    private static ExpansionLayoutScore ScoreExpansionContext(
        RailLayoutScore layout,
        IReadOnlyCollection<RailLayoutPoint> candidatePoints,
        IReadOnlyCollection<JObject> occupiedPoints,
        double distance,
        double area)
    {
        JObject[] locatedOccupied = occupiedPoints
            .Where(item => TryReadPoint(item["grid"], out _, out _))
            .ToArray();
        if (locatedOccupied.Length == 0 || candidatePoints.Count == 0)
        {
            return new ExpansionLayoutScore(false, 0, 0d, distance, area, layout);
        }

        double existingX = locatedOccupied.Average(item =>
        {
            TryReadPoint(item["grid"], out double x, out _);
            return x;
        });
        double existingY = locatedOccupied.Average(item =>
        {
            TryReadPoint(item["grid"], out _, out double y);
            return y;
        });
        double candidateX = candidatePoints.Average(point => point.X);
        double candidateY = candidatePoints.Average(point => point.Y);
        double existingMagnitude = Math.Sqrt(existingX * existingX + existingY * existingY);
        double candidateMagnitude = Math.Sqrt(candidateX * candidateX + candidateY * candidateY);
        if (existingMagnitude <= 0.000001d || candidateMagnitude <= 0.000001d)
        {
            return new ExpansionLayoutScore(false, 0, 0d, distance, area, layout);
        }

        double cosine = (existingX * candidateX + existingY * candidateY)
                        / (existingMagnitude * candidateMagnitude);
        return new ExpansionLayoutScore(
            true,
            cosine <= 0d ? 0 : 1,
            cosine,
            distance,
            area,
            layout);
    }

    private static double DistanceSquared(double x1, double y1, double x2, double y2)
    {
        double x = x1 - x2;
        double y = y1 - y2;
        return x * x + y * y;
    }

    private static bool BuildGridConfirmation(JObject arguments, JObject? optionsResult)
    {
        if (HasObject(arguments, "grid"))
        {
            return true;
        }

        JObject options = State(optionsResult);
        JObject? grid = options.SelectToken("validGrids[0].grid") as JObject;
        if (grid == null || grid["x"]?.Type != JTokenType.Integer || grid["y"]?.Type != JTokenType.Integer)
        {
            return false;
        }

        arguments["grid"] = grid.DeepClone();
        return true;
    }

    private static bool BuildTargetConfirmation(JObject arguments, JObject disposable)
    {
        JObject? candidate = (disposable["targetCandidates"] as JArray)?
            .OfType<JObject>()
            .FirstOrDefault(item =>
                item["conditionPass"]?.Value<bool>() == true && HasStableTargetIdentity(item));
        if (candidate == null)
        {
            return false;
        }

        arguments.Remove("target");
        arguments.Remove("targetInstanceId");
        arguments.Remove("instanceId");
        arguments.Remove("path");
        arguments.Remove("world");
        arguments.Remove("grid");

        int instanceId = ReadInt(candidate["instanceId"], 0);
        if (instanceId != 0)
        {
            arguments["targetInstanceId"] = instanceId;
            return true;
        }

        string? path = candidate["path"]?.Value<string>();
        if (string.IsNullOrWhiteSpace(path))
        {
            return false;
        }

        arguments["path"] = path;
        return true;
    }

    private static string ResolveConfirmKind(JObject state)
    {
        string? kind = state.SelectToken("confirmContract.confirmKind")?.Value<string>();
        if (!string.IsNullOrWhiteSpace(kind))
        {
            return kind!;
        }

        string interactionType = state["interactionType"]?.Value<string>() ?? string.Empty;
        if (interactionType.Equals("GridChooseInteraction", StringComparison.OrdinalIgnoreCase)) return "grid";
        if (interactionType.Equals("FreeChooseInteraction", StringComparison.OrdinalIgnoreCase)) return "world";
        if (interactionType.Equals("FreeRaycastInteraction", StringComparison.OrdinalIgnoreCase)) return "targetRaycast";

        string command = state["confirmCommand"]?.Value<string>() ?? string.Empty;
        if (command.EndsWith("confirm_disposable_grid", StringComparison.OrdinalIgnoreCase)) return "grid";
        if (command.EndsWith("confirm_disposable_world", StringComparison.OrdinalIgnoreCase)) return "world";
        if (command.EndsWith("confirm_disposable_target", StringComparison.OrdinalIgnoreCase)) return "targetRaycast";
        return "unknown";
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

    private static JObject BuildIdentity(JObject item, bool preferItemInstanceId)
    {
        int instanceId = preferItemInstanceId
            ? ReadInt(item["itemInstanceId"], ReadInt(item["instanceId"], 0))
            : ReadInt(item["instanceId"], 0);
        if (instanceId != 0)
        {
            return new JObject
            {
                [preferItemInstanceId ? "itemInstanceId" : "instanceId"] = instanceId
            };
        }

        string? path = item[preferItemInstanceId ? "itemPath" : "path"]?.Value<string>()
                       ?? item["path"]?.Value<string>();
        if (!string.IsNullOrWhiteSpace(path))
        {
            return new JObject { ["path"] = path };
        }

        int index = ReadInt(item["index"], -1);
        return index >= 0 ? new JObject { ["index"] = index } : new JObject();
    }

    private static bool HasIdentity(JObject item) =>
        ReadInt(item["itemInstanceId"], ReadInt(item["instanceId"], 0)) != 0
        || !string.IsNullOrWhiteSpace(item["itemPath"]?.Value<string>())
        || !string.IsNullOrWhiteSpace(item["path"]?.Value<string>())
        || ReadInt(item["index"], -1) >= 0;

    private static bool HasStableTargetIdentity(JObject arguments) =>
        ReadInt(arguments["targetInstanceId"], ReadInt(arguments["instanceId"], 0)) != 0
        || !string.IsNullOrWhiteSpace(arguments["path"]?.Value<string>());

    private static bool ContainsString(JArray? values, string expected) =>
        values?.Values<string>().Any(value => string.Equals(value, expected, StringComparison.OrdinalIgnoreCase)) == true;

    private static bool HasObject(JObject value, string property) =>
        value[property] is JObject;

    private static int ReadInt(JToken? token, int fallback)
    {
        if (token == null || token.Type == JTokenType.Null)
        {
            return fallback;
        }

        if (token.Type == JTokenType.Integer)
        {
            return token.Value<int>();
        }

        return int.TryParse(token.Value<string>(), out int value) ? value : fallback;
    }

    private static int? ReadNullableInt(JToken? token)
    {
        int value = ReadInt(token, int.MinValue);
        return value == int.MinValue ? null : value;
    }

    private static bool TryReadPoint(JToken? token, out double x, out double y)
    {
        x = 0d;
        y = 0d;
        if (token is not JObject point)
        {
            return false;
        }

        return TryReadDouble(point["x"], out x) && TryReadDouble(point["y"], out y);
    }

    private static bool TryReadThreatVector(JObject threats, JObject nest, out double x, out double y)
    {
        if (TryReadPoint(nest.SelectToken("relativeToMainBase.vector"), out x, out y))
        {
            return true;
        }

        if (TryReadPoint(threats.SelectToken("mainBase.world"), out double baseX, out double baseY)
            && TryReadPoint(nest["world"], out double nestX, out double nestY))
        {
            x = nestX - baseX;
            y = nestY - baseY;
            return true;
        }

        x = 0d;
        y = 0d;
        return false;
    }

    private static double ReadDouble(JToken? token, double fallback) =>
        TryReadDouble(token, out double value) ? value : fallback;

    private static bool TryReadDouble(JToken? token, out double value)
    {
        value = 0d;
        if (token == null || token.Type == JTokenType.Null)
        {
            return false;
        }

        if (token.Type is JTokenType.Integer or JTokenType.Float)
        {
            value = token.Value<double>();
            return true;
        }

        return double.TryParse(
            token.Value<string>(),
            System.Globalization.NumberStyles.Float,
            System.Globalization.CultureInfo.InvariantCulture,
            out value);
    }

    private static bool IsFinite(double value) =>
        !double.IsNaN(value) && !double.IsInfinity(value);

    private sealed class ExpansionLayoutScore
    {
        public ExpansionLayoutScore(
            bool hasCoverageContext,
            int sideRank,
            double directionCosine,
            double distance,
            double area,
            RailLayoutScore layout)
        {
            HasCoverageContext = hasCoverageContext;
            SideRank = sideRank;
            DirectionCosine = directionCosine;
            Distance = distance;
            Area = area;
            Layout = layout;
        }

        public bool HasCoverageContext { get; }
        public int SideRank { get; }
        public double DirectionCosine { get; }
        public double Distance { get; }
        public double Area { get; }
        public RailLayoutScore Layout { get; }
    }

    private sealed class ExpansionPathCandidate
    {
        public ExpansionPathCandidate(ExpansionLayoutScore score, string key, JArray ids)
        {
            Score = score;
            Key = key;
            Ids = ids;
        }

        public ExpansionLayoutScore Score { get; }
        public string Key { get; }
        public JArray Ids { get; }
    }

    private sealed class ExpansionGridCandidate
    {
        public ExpansionGridCandidate(ExpansionLayoutScore score, int x, int y, JObject grid)
        {
            Score = score;
            X = x;
            Y = y;
            Grid = grid;
        }

        public ExpansionLayoutScore Score { get; }
        public int X { get; }
        public int Y { get; }
        public JObject Grid { get; }
    }

}
