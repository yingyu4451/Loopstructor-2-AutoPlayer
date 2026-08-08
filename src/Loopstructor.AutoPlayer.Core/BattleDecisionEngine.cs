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
}

public sealed class DefenseExpansionRailVerification
{
    public bool Verified { get; set; }
    public bool Pending { get; set; }
    public string Detail { get; set; } = string.Empty;
    public int RailInstanceId { get; set; }
    public JObject? Rail { get; set; }
}

/// <summary>
/// Chooses one ordinary player action from already queried runtime state.
/// This policy is deliberately stateless; the caller owns polling and the disposable phase transition.
/// </summary>
public sealed class BattleDecisionEngine
{
    // The supported game build uses two world units per logical rail grid cell.
    private const double WorldToGridScale = 0.5d;
    private const double MinimumTrainMovementImprovement = 0.01d;
    private const double MinimumTrainMovementRelativeImprovement = 0.01d;
    private const double LiveThreatUrgencyReserve = 0.6d;
    private const double LiveThreatUrgencyDistanceOffset = 0.5d;
    private const double LiveThreatUrgencyExponent = 4d;
    private const double LiveThreatUrgencyActivationRadius = 3d;
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
        JObject? trainResult,
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
            ? DecideVehicleReinforcement(State(trainResult), State(vehicleResult))
            : null;
    }

    public AutomationAction DecideTrainMovement(
        JObject? waveThreatsResult,
        JObject? railResult,
        JObject? trainResult,
        ISet<int>? excludedTrainIndexes = null)
    {
        JObject threats = State(waveThreatsResult);
        JObject rails = State(railResult);
        JObject trains = State(trainResult);

        List<ThreatCandidate> activeThreats = BuildThreatCandidates(threats);
        if (activeThreats.Count == 0)
        {
            return AutomationAction.Wait(AutomationStage.Battle, "当前没有可用于列车机动的活动威胁。");
        }

        List<JObject> allMovableTrains = ((trains["trains"] as JArray)?.OfType<JObject>()
                ?? Enumerable.Empty<JObject>())
            .Where(IsMovableTrain)
            .GroupBy(item => ReadInt(item["index"], -1))
            .Where(group => group.Count() == 1)
            .Select(group => group.Single())
            .ToList();
        if (allMovableTrains.Count == 0)
        {
            return AutomationAction.Wait(AutomationStage.Battle, "当前没有可移动的既有车列。");
        }

        List<JObject> candidateTrains = allMovableTrains
            .Where(train => excludedTrainIndexes?.Contains(TrainStableIdentity(train)) != true)
            .ToList();
        if (candidateTrains.Count == 0)
        {
            return AutomationAction.Wait(AutomationStage.Battle, "本波所有可移动车列都已经完成一次调度，不再重复移动。");
        }

        JArray? railItems = rails["rails"] as JArray;
        if (railItems == null || railItems.Count == 0)
        {
            return AutomationAction.Wait(AutomationStage.Battle, "当前没有可用于列车机动的轨道。");
        }

        HashSet<int> uniqueLineInstanceIds = new HashSet<int>(railItems
            .OfType<JObject>()
            .SelectMany(rail => (rail["lines"] as JArray)?.OfType<JObject>() ?? Enumerable.Empty<JObject>())
            .Select(line => ReadInt(line["lineInstanceId"], ReadInt(line["instanceId"], 0)))
            .Where(instanceId => instanceId != 0)
            .GroupBy(instanceId => instanceId)
            .Where(group => group.Count() == 1)
            .Select(group => group.Key));

        List<TrainCoveragePosition> currentPositions = new();
        Dictionary<int, List<LineCandidate>> candidatesByTrain = new();
        foreach (JObject train in allMovableTrains)
        {
            int trainIndex = ReadInt(train["index"], -1);
            int? sourceRailId = ReadNullableInt(train["railId"]);
            string currentLineName = train["line"]?.Value<string>() ?? string.Empty;
            List<LineCandidate> lineCandidates = EnumerateLineCandidates(
                    railItems,
                    sourceRailId,
                    currentLineName)
                .Where(candidate => uniqueLineInstanceIds.Contains(candidate.LineInstanceId))
                .ToList();
            candidatesByTrain[trainIndex] = lineCandidates;
            LineCandidate? currentLine = lineCandidates.Count(candidate => candidate.IsCurrentLine) == 1
                ? lineCandidates.Single(candidate => candidate.IsCurrentLine)
                : null;
            currentPositions.Add(new TrainCoveragePosition(
                trainIndex,
                TrainPowerScore(train),
                currentLine));
        }

        double baselineUtility = CoverageUtility(activeThreats, currentPositions, null);
        List<TrainMovementCandidate> improvements = new();
        List<TrainMovementCandidate> recoveryCandidates = new();

        foreach (JObject train in candidateTrains)
        {
            int trainIndex = ReadInt(train["index"], -1);
            int trainIdentity = TrainStableIdentity(train);
            int vehicleCount = TrainVehicleCount(train);
            double trainPower = TrainPowerScore(train);
            List<LineCandidate> lineCandidates = candidatesByTrain.TryGetValue(trainIndex, out List<LineCandidate>? value)
                ? value
                : new List<LineCandidate>();
            List<LineCandidate> currentLines = lineCandidates
                .Where(candidate => candidate.IsCurrentLine)
                .ToList();
            LineCandidate? currentLine = currentLines.Count == 1 ? currentLines[0] : null;

            foreach (LineCandidate target in lineCandidates.Where(candidate => !candidate.IsCurrentLine))
            {
                TrainCoveragePosition replacement = new(trainIndex, trainPower, target);
                double afterUtility = CoverageUtility(activeThreats, currentPositions, replacement);
                ThreatCandidate servedThreat = SelectServedThreat(
                    activeThreats,
                    currentPositions,
                    replacement);
                if (currentLine == null)
                {
                    recoveryCandidates.Add(new TrainMovementCandidate(
                        trainIndex,
                        trainIdentity,
                        vehicleCount,
                        trainPower,
                        target,
                        afterUtility,
                        servedThreat));
                    continue;
                }

                double improvement = afterUtility - baselineUtility;
                double relativeImprovement = improvement / Math.Max(Math.Abs(baselineUtility), 1d);
                if (improvement > MinimumTrainMovementImprovement &&
                    relativeImprovement >= MinimumTrainMovementRelativeImprovement)
                {
                    improvements.Add(new TrainMovementCandidate(
                        trainIndex,
                        trainIdentity,
                        vehicleCount,
                        trainPower,
                        target,
                        improvement,
                        servedThreat));
                }
            }
        }

        TrainMovementCandidate? selected = improvements
            .OrderByDescending(candidate => candidate.Improvement)
            .ThenByDescending(candidate => candidate.TrainPower)
            .ThenByDescending(candidate => candidate.VehicleCount)
            .ThenBy(candidate => candidate.TrainIndex)
            .ThenBy(candidate => candidate.Target.LineInstanceId)
            .FirstOrDefault();
        bool recoveringMissingPosition = false;
        if (selected == null)
        {
            selected = recoveryCandidates
                .OrderByDescending(candidate => candidate.Improvement)
                .ThenByDescending(candidate => candidate.TrainPower)
                .ThenByDescending(candidate => candidate.VehicleCount)
                .ThenBy(candidate => candidate.TrainIndex)
                .ThenBy(candidate => candidate.Target.LineInstanceId)
                .FirstOrDefault();
            recoveringMissingPosition = selected != null;
        }

        if (selected == null)
        {
            return AutomationAction.Wait(
                AutomationStage.Battle,
                "所有可定位车列都没有距离上的正改善，或主威胁方向上没有空闲合法目标，无需重复调度。");
        }

        return new AutomationAction(
                "moveTrainToLine",
            JObject.FromObject(new
            {
                trainIndex = selected.TrainIndex,
                trainIdentity = selected.TrainIdentity,
                lineInstanceId = selected.Target.LineInstanceId,
                forward = selected.Target.ForwardToward(selected.ServedThreat.X, selected.ServedThreat.Y)
            }),
            AutomationStage.Battle,
            recoveringMissingPosition
                ? $"车列 {selected.TrainIndex} 缺少可验证的当前线段；将其调往覆盖当前威胁收益最高的空闲合法线段（{BuildThreatSourceDetail(threats)}）。"
                : $"把车列 {selected.TrainIndex} 调往对当前威胁边际覆盖收益最高的空闲合法线段（{BuildThreatSourceDetail(threats)}）。"
        );
    }

    public bool NeedsDefenseExpansion(JObject? trainResult, JObject? vehicleResult)
    {
        JObject trainsState = State(trainResult);
        JObject vehiclesState = State(vehicleResult);
        JArray? trains = trainsState["trains"] as JArray;
        bool hasExistingTrain = trains?.OfType<JObject>().Any() == true;
        bool hasFreeCapacity = trains?.OfType<JObject>().Any(HasTrainCapacity) == true;
        bool hasBagVehicle = (vehiclesState["vehicles"] as JArray)?
            .OfType<JObject>()
            .Any(IsBagVehicle) == true;
        return hasExistingTrain && hasBagVehicle && !hasFreeCapacity;
    }

    public AutomationAction? DecideDefenseExpansion(
        JObject? trainResult,
        JObject? vehicleResult,
        JObject? catapultResult,
        ISet<string>? rejectedPathKeys = null)
    {
        if (!NeedsDefenseExpansion(trainResult, vehicleResult))
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
                    if (score.Area <= 0.000001d)
                    {
                        continue;
                    }
                    paths.Add(new ExpansionPathCandidate(score, key, ids));
                }
            }
        }

        JArray? linePointInstanceIds = paths
            .OrderBy(path => path.Score.Distance)
            .ThenBy(path => path.Score.HasCoverageContext ? path.Score.SideRank : 0)
            .ThenBy(path => path.Score.HasCoverageContext ? path.Score.DirectionCosine : 0d)
            .ThenByDescending(path => path.Score.Area)
            .ThenBy(path => path.Key, StringComparer.Ordinal)
            .Select(path => path.Ids)
            .FirstOrDefault();
        if (linePointInstanceIds == null)
        {
            return null;
        }

        JObject? previewVehicle = (State(vehicleResult)["vehicles"] as JArray)?
            .OfType<JObject>()
            .Where(IsBagVehicle)
            .OrderByDescending(item => ReadInt(item["level"], 0))
            .ThenBy(item => ReadInt(item["index"], int.MaxValue))
            .FirstOrDefault();
        JObject vehicleIdentity = previewVehicle == null
            ? new JObject()
            : BuildIdentity(previewVehicle, preferItemInstanceId: false);
        if (!vehicleIdentity.HasValues)
        {
            return null;
        }

        JObject expansionArguments = new()
        {
            ["linePointInstanceIds"] = linePointInstanceIds,
            ["vehicle"] = vehicleIdentity.DeepClone()
        };
        if (vehicleIdentity["instanceId"]?.Type == JTokenType.Integer)
        {
            expansionArguments["vehicleInstanceId"] = vehicleIdentity["instanceId"]!.DeepClone();
        }

        return new AutomationAction(
            "drawRailPath",
            expansionArguments,
            AutomationStage.PreparingDefense,
            occupiedPoints.Count > 0
                ? "现有车列已满且背包仍有战车；按玩家拖线规则创建一条优先补足现有防线相反方向的额外合法闭环。"
                : "现有车列已满且背包仍有战车；按玩家拖线规则创建一条最短的额外合法闭环。");
    }

    public static string BuildDefenseExpansionPathKey(JToken? linePointInstanceIds) =>
        linePointInstanceIds is JArray ids
            ? string.Join(":", ids.Values<int>())
            : string.Empty;

    public AutomationAction? DecideExpansionVehiclePlacement(JObject? vehicleResult, JObject? drawResult)
    {
        JObject vehiclesState = State(vehicleResult);
        JObject drawState = State(drawResult);
        JObject? vehicle = (vehiclesState["vehicles"] as JArray)?
            .OfType<JObject>()
            .Where(IsBagVehicle)
            .OrderByDescending(item => ReadInt(item["level"], 0))
            .ThenBy(item => ReadInt(item["index"], int.MaxValue))
            .FirstOrDefault();
        JObject? line = (drawState.SelectToken("rail.lines") as JArray)?
            .OfType<JObject>()
            .FirstOrDefault(item =>
                item["hasDriver"]?.Value<bool>() != true
                && ReadInt(item["driverCount"], 0) == 0
                && ReadInt(item["lineInstanceId"], ReadInt(item["instanceId"], 0)) != 0);
        if (vehicle == null || line == null)
        {
            return null;
        }

        JObject vehicleIdentity = BuildIdentity(vehicle, preferItemInstanceId: false);
        if (!vehicleIdentity.HasValues)
        {
            return null;
        }

        vehicleIdentity["lineInstanceId"] = ReadInt(
            line["lineInstanceId"],
            ReadInt(line["instanceId"], 0));
        vehicleIdentity["forward"] = true;
        string name = vehicle["name"]?.Value<string>()
                      ?? vehicle["vehicleType"]?.Value<string>()
                      ?? "未知战车";
        return new AutomationAction(
            "placeVehicleOnLine",
            vehicleIdentity,
            AutomationStage.PreparingDefense,
            $"新闭环尚无车列；按玩家放车流程把背包战车 {name} 放到新轨道并创建车列。");
    }

    public bool IsLegalDefenseExpansionPreview(JObject? previewResult)
    {
        JObject state = State(previewResult);
        return state["wouldBeLegal"]?.Value<bool>() == true
               && state["sideEffectCheckPassed"]?.Value<bool>() == true
               && state["statePolluted"]?.Value<bool>() != true
               && state["requiresSpeedSource"]?.Value<bool>() == false
               && TryReadDouble(state["predictedLoopCycleSeconds"], out double predictedCycle)
               && predictedCycle > 0d
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
            return RailVerificationFailure("唯一新增轨道的站点身份与本次选定三点不一致。");
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
                return RailVerificationFailure("drawResult.rail 的站点身份与本次选定三点不一致。");
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
        bool attribute = string.Equals(
            disposableEnum,
            ExpansionAttributeDisposableEnum,
            StringComparison.Ordinal);
        return AvailableExpansionPoints(catapultResult).Count(item =>
            attribute
                ? item["isAttribute"]?.Value<bool>() == true
                : item["isAttribute"]?.Value<bool>() != true &&
                  string.Equals(
                      item["recycleDisposableEnum"]?.Value<string>(),
                      disposableEnum,
                      StringComparison.Ordinal));
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
                    if (Math.Abs((bx - ax) * (cy - ay) - (by - ay) * (cx - ax)) > 0.000001d)
                    {
                        return string.Empty;
                    }
                }
            }
        }

        return ExpansionCommonDisposableEnum;
    }

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
        if (!IsExpansionStationDisposable(disposableEnum)) return null;
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

        JObject? selected = candidates
            .OrderBy(candidate => candidate.Score.Distance)
            .ThenBy(candidate => candidate.Score.HasCoverageContext ? candidate.Score.SideRank : 0)
            .ThenBy(candidate => candidate.Score.HasCoverageContext ? candidate.Score.DirectionCosine : 0d)
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
        if (!IsExpansionStationDisposable(disposableEnum)) return 0;
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
        if (!IsExpansionStationDisposable(disposableEnum)) return false;
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
        if (!IsExpansionStationDisposable(disposableEnum)) return null;
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

    private static bool IsExpansionStationDisposable(string disposableEnum) =>
        string.Equals(disposableEnum, ExpansionAttributeDisposableEnum, StringComparison.Ordinal) ||
        string.Equals(disposableEnum, ExpansionCommonDisposableEnum, StringComparison.Ordinal);

    private static AutomationAction? DecideDisposableUse(JObject disposable)
    {
        JObject? item = (disposable["items"] as JArray)?
            .OfType<JObject>()
            .Where(IsUsableDisposable)
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

    private static AutomationAction? DecideVehicleReinforcement(JObject trainsState, JObject vehiclesState)
    {
        JObject? vehicle = (vehiclesState["vehicles"] as JArray)?
            .OfType<JObject>()
            .Where(IsBagVehicle)
            .OrderByDescending(item => ReadInt(item["level"], 0))
            .ThenBy(item => ReadInt(item["index"], int.MaxValue))
            .FirstOrDefault();
        if (vehicle == null)
        {
            return null;
        }

        JObject? train = (trainsState["trains"] as JArray)?
            .OfType<JObject>()
            .Where(HasTrainCapacity)
            .OrderByDescending(RemainingTrainCapacity)
            .ThenBy(item => ReadInt(item["index"], int.MaxValue))
            .FirstOrDefault();
        JObject? relative = (train?["vehicles"] as JArray)?
            .OfType<JObject>()
            .Where(HasIdentity)
            .LastOrDefault();
        if (train == null || relative == null)
        {
            return null;
        }

        JObject vehicleIdentity = BuildIdentity(vehicle, preferItemInstanceId: false);
        JObject relativeIdentity = BuildIdentity(relative, preferItemInstanceId: false);
        if (!vehicleIdentity.HasValues || !relativeIdentity.HasValues)
        {
            return null;
        }

        JObject arguments = (JObject)vehicleIdentity.DeepClone();
        arguments["relative"] = relativeIdentity;
        string name = vehicle["name"]?.Value<string>()
                      ?? vehicle["vehicleType"]?.Value<string>()
                      ?? "未知战车";
        int level = ReadInt(vehicle["level"], 0);
        int trainIndex = ReadInt(train["index"], -1);
        return new AutomationAction(
            "moveVehicleInTrain",
            arguments,
            AutomationStage.PreparingDefense,
            $"把背包中等级最高的战车 {name}（等级 {level}）编入车列 {trainIndex}。");
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
            return new ExpansionLayoutScore(false, 1, 1d, double.MaxValue, 0d);
        }

        double distance = DistanceSquared(firstX, firstY, attributeX, attributeY)
                          + DistanceSquared(secondX, secondY, attributeX, attributeY)
                          + DistanceSquared(firstX, firstY, secondX, secondY);
        double area = Math.Abs(
            (firstX - attributeX) * (secondY - attributeY)
            - (firstY - attributeY) * (secondX - attributeX));
        JObject[] locatedOccupied = occupiedPoints
            .Where(item => TryReadPoint(item["grid"], out _, out _))
            .ToArray();
        if (locatedOccupied.Length == 0)
        {
            return new ExpansionLayoutScore(false, 0, 0d, distance, area);
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
        double candidateX = (attributeX + firstX + secondX) / 3d;
        double candidateY = (attributeY + firstY + secondY) / 3d;
        double existingMagnitude = Math.Sqrt(existingX * existingX + existingY * existingY);
        double candidateMagnitude = Math.Sqrt(candidateX * candidateX + candidateY * candidateY);
        if (existingMagnitude <= 0.000001d || candidateMagnitude <= 0.000001d)
        {
            return new ExpansionLayoutScore(false, 0, 0d, distance, area);
        }

        double cosine = (existingX * candidateX + existingY * candidateY)
                        / (existingMagnitude * candidateMagnitude);
        return new ExpansionLayoutScore(true, cosine <= 0d ? 0 : 1, cosine, distance, area);
    }

    private static bool HasTrainCapacity(JObject train)
    {
        int capacity = ReadInt(train["capacity"], -1);
        int count = ReadInt(train["realVehicleCount"], ReadInt(train["vehicleCount"], 0));
        return train["isOverCapacity"]?.Value<bool>() != true
               && capacity > count
               && (train["vehicles"] as JArray)?.OfType<JObject>().Any(HasIdentity) == true;
    }

    private static int RemainingTrainCapacity(JObject train) =>
        ReadInt(train["capacity"], 0)
        - ReadInt(train["realVehicleCount"], ReadInt(train["vehicleCount"], 0));

    private static IEnumerable<LineCandidate> EnumerateLineCandidates(
        JArray rails,
        int? sourceRailId,
        string currentLineName)
    {
        foreach (JObject rail in rails.OfType<JObject>())
        {
            if (rail["isLegalPlayerLoop"]?.Value<bool>() != true
                || rail["isLoop"]?.Value<bool>() != true
                || rail["isOnField"]?.Value<bool>() == false)
            {
                continue;
            }

            int? targetRailId = ReadNullableInt(rail["railInternalId"] ?? rail["id"]);
            bool sameRail = sourceRailId.HasValue && targetRailId.HasValue && sourceRailId.Value == targetRailId.Value;
            int driverCount = ReadInt(rail["driverCount"], 0);
            int driverMaxCount = ReadInt(rail["driverMaxCount"], 0);
            if (sameRail ? driverCount != 1 : driverCount != 0)
            {
                continue;
            }

            if (!sameRail
                && (rail["isDriverReachToMax"]?.Value<bool>() == true
                    || driverMaxCount > 0 && driverCount >= driverMaxCount))
            {
                continue;
            }

            foreach (JObject line in (rail["lines"] as JArray)?.OfType<JObject>() ?? Enumerable.Empty<JObject>())
            {
                int lineInstanceId = ReadInt(line["lineInstanceId"], ReadInt(line["instanceId"], 0));
                string lineName = line["name"]?.Value<string>() ?? string.Empty;
                bool isCurrentLine = sameRail &&
                                     !string.IsNullOrWhiteSpace(currentLineName) &&
                                     string.Equals(lineName, currentLineName, StringComparison.Ordinal);
                int lineDriverCount = ReadInt(line["driverCount"], 0);
                if (lineInstanceId == 0 ||
                    (isCurrentLine ? lineDriverCount != 1 : lineDriverCount != 0) ||
                    (!isCurrentLine && line["hasDriver"]?.Value<bool>() == true) ||
                    !TryReadPoint(line["from"], out double fromX, out double fromY)
                    || !TryReadPoint(line["to"], out double toX, out double toY))
                {
                    continue;
                }

                yield return new LineCandidate(
                    lineInstanceId,
                    isCurrentLine,
                    fromX,
                    fromY,
                    toX,
                    toY);
            }
        }
    }

    private static double CoverageUtility(
        IReadOnlyCollection<ThreatCandidate> threats,
        IReadOnlyCollection<TrainCoveragePosition> currentPositions,
        TrainCoveragePosition? replacement)
    {
        double utility = 0d;
        foreach (ThreatCandidate threat in threats)
        {
            utility += ThreatCoverageUtility(threat, currentPositions, replacement);
        }

        return utility;
    }

    private static double ThreatCoverageUtility(
        ThreatCandidate threat,
        IReadOnlyCollection<TrainCoveragePosition> currentPositions,
        TrainCoveragePosition? replacement)
    {
        List<double> contributions = new();
        foreach (TrainCoveragePosition position in currentPositions)
        {
            TrainCoveragePosition effective = replacement != null && replacement.TrainIndex == position.TrainIndex
                ? replacement
                : position;
            if (effective.Line == null)
            {
                continue;
            }

            double distance = Math.Sqrt(effective.Line.DistanceTo(threat.X, threat.Y));
            contributions.Add(effective.Power / (1d + distance));
        }

        if (replacement != null && currentPositions.All(position => position.TrainIndex != replacement.TrainIndex) && replacement.Line != null)
        {
            double distance = Math.Sqrt(replacement.Line.DistanceTo(threat.X, threat.Y));
            contributions.Add(replacement.Power / (1d + distance));
        }

        if (contributions.Count == 0)
        {
            return 0d;
        }

        double strongest = contributions.Max();
        double supporting = contributions.Sum() - strongest;
        return threat.Weight * (strongest + supporting * 0.25d);
    }

    private static ThreatCandidate SelectServedThreat(
        IReadOnlyCollection<ThreatCandidate> threats,
        IReadOnlyCollection<TrainCoveragePosition> currentPositions,
        TrainCoveragePosition replacement)
    {
        return threats
            .Select(threat => new
            {
                Threat = threat,
                Improvement = ThreatCoverageUtility(threat, currentPositions, replacement)
                              - ThreatCoverageUtility(threat, currentPositions, null),
                TargetCoverage = replacement.Line == null
                    ? 0d
                    : threat.Weight * replacement.Power /
                      (1d + Math.Sqrt(replacement.Line.DistanceTo(threat.X, threat.Y)))
            })
            .OrderByDescending(item => item.Improvement)
            .ThenByDescending(item => item.TargetCoverage)
            .ThenBy(item => item.Threat.Index)
            .Select(item => item.Threat)
            .First();
    }

    private static double TrainPowerScore(JObject train)
    {
        JObject[] combatVehicles = ((train["vehicles"] as JArray)?.OfType<JObject>()
                ?? Enumerable.Empty<JObject>())
            .Where(vehicle => vehicle["isFixedHead"]?.Value<bool>() != true
                              && vehicle["isTrainHead"]?.Value<bool>() != true)
            .ToArray();
        if (combatVehicles.Length > 0)
        {
            return combatVehicles.Sum(vehicle =>
            {
                int level = Math.Max(ReadInt(vehicle["level"], 1), 1);
                return (double)(level * level);
            });
        }

        return Math.Max(TrainVehicleCount(train) - 1, 1);
    }

    private static int TrainStableIdentity(JObject train)
    {
        int driverInstanceId = ReadInt(train["driverInstanceId"], 0);
        if (driverInstanceId != 0)
        {
            return driverInstanceId;
        }

        JObject? fixedHead = (train["vehicles"] as JArray)?.OfType<JObject>()
            .FirstOrDefault(vehicle => vehicle["isFixedHead"]?.Value<bool>() == true
                                       || vehicle["isTrainHead"]?.Value<bool>() == true);
        int headInstanceId = ReadInt(fixedHead?["instanceId"], 0);
        return headInstanceId != 0
            ? headInstanceId
            : ReadInt(train["index"], -1);
    }

    private static double DistancePointToSegmentSquared(
        double pointX,
        double pointY,
        double fromX,
        double fromY,
        double toX,
        double toY)
    {
        double segmentX = toX - fromX;
        double segmentY = toY - fromY;
        double lengthSquared = segmentX * segmentX + segmentY * segmentY;
        if (lengthSquared <= 0.000001d)
        {
            return DistanceSquared(pointX, pointY, fromX, fromY);
        }

        double projection = ((pointX - fromX) * segmentX + (pointY - fromY) * segmentY) / lengthSquared;
        projection = Math.Max(0d, Math.Min(1d, projection));
        return DistanceSquared(
            pointX,
            pointY,
            fromX + projection * segmentX,
            fromY + projection * segmentY);
    }

    private static double DistanceSquared(double x1, double y1, double x2, double y2)
    {
        double x = x1 - x2;
        double y = y1 - y2;
        return x * x + y * y;
    }

    private static bool IsMovableTrain(JObject train) =>
        ReadInt(train["index"], -1) >= 0
        && train["forward"]?.Type == JTokenType.Boolean
        && TrainVehicleCount(train) > 0;

    private static int TrainVehicleCount(JObject train) =>
        ReadInt(
            train["realVehicleCount"],
            ReadInt(train["vehicleCount"], (train["vehicles"] as JArray)?.Count ?? 0));

    private static long ThreatScore(JObject nest)
    {
        int level = Math.Max(ReadInt(nest.SelectToken("spawn.level"), 1), 1);
        int amount = Math.Max(ReadInt(nest.SelectToken("spawn.amount"), 1), 1);
        return (long)level * amount;
    }

    private static string BuildThreatSourceDetail(JObject threats)
    {
        if (threats["liveThreatsAvailable"]?.Value<bool>() != true)
        {
            return "使用巢穴生成先验";
        }

        int liveCount = ReadInt(threats["liveThreatCount"], 0);
        JObject? accounting = threats["enemyAccounting"] as JObject;
        if (accounting?["remainingReliable"]?.Value<bool>() == true)
        {
            int futureCount = Math.Max(ReadInt(accounting["estimatedFutureCount"], 0), 0);
            return $"实时活怪 {liveCount}，尚未生成约 {futureCount}";
        }

        return $"实时活怪 {liveCount}，剩余数暂不可可靠读取";
    }

    private static List<ThreatCandidate> BuildThreatCandidates(JObject threats)
    {
        if (threats["liveThreatsAvailable"]?.Value<bool>() != true ||
            threats["liveThreats"] is not JArray liveItems)
        {
            return BuildNestThreatCandidates(threats, null);
        }

        List<ThreatCandidate> liveThreats = BuildLiveThreatCandidates(threats, liveItems);
        double? futureMass = ReliableFutureThreatMass(threats);
        List<ThreatCandidate> nestThreats = BuildNestThreatCandidates(threats, futureMass);
        return ApplyLiveThreatUrgencyReserve(liveThreats, nestThreats);
    }

    private static List<ThreatCandidate> BuildLiveThreatCandidates(JObject threats, JArray liveItems)
    {
        List<ThreatCandidate> candidates = new();
        HashSet<ulong> runtimeIdentities = new();
        HashSet<int> instanceIdentities = new();
        foreach (JObject item in liveItems.OfType<JObject>())
        {
            int handleId = ReadInt(item["runtimeHandleId"], 0);
            int lifetimeVersion = ReadInt(item["lifetimeVersion"], 0);
            if (handleId > 0 && lifetimeVersion > 0)
            {
                ulong identity = ((ulong)(uint)handleId << 32) | (uint)lifetimeVersion;
                if (!runtimeIdentities.Add(identity)) continue;
            }
            else
            {
                int instanceId = ReadInt(item["instanceId"], 0);
                if (instanceId == 0 || !instanceIdentities.Add(instanceId)) continue;
            }

            if (!TryReadThreatVector(threats, item, out double x, out double y) ||
                !IsFinite(x) ||
                !IsFinite(y))
            {
                continue;
            }

            double factor = item["aiRunning"]?.Value<bool>() == false ? 0.5d : 1d;
            if (item["isBoss"]?.Value<bool>() == true) factor *= 4d;
            if (TryReadDouble(item["health"], out double health) &&
                TryReadDouble(item["healthMax"], out double healthMax) &&
                !double.IsNaN(health) &&
                !double.IsInfinity(health) &&
                !double.IsNaN(healthMax) &&
                !double.IsInfinity(healthMax) &&
                healthMax > 0d)
            {
                factor *= Math.Max(0.25d, Math.Min(1d, health / healthMax));
            }

            candidates.Add(new ThreatCandidate(
                handleId > 0 ? handleId : ReadInt(item["instanceId"], int.MaxValue),
                x * WorldToGridScale,
                y * WorldToGridScale,
                Math.Max(factor, 0.01d)));
        }

        return ScaleThreatCandidates(candidates, candidates.Count);
    }

    private static List<ThreatCandidate> ApplyLiveThreatUrgencyReserve(
        IReadOnlyCollection<ThreatCandidate> liveThreats,
        IReadOnlyCollection<ThreatCandidate> nestThreats)
    {
        List<ThreatCandidate> baseline = liveThreats
            .Concat(nestThreats)
            .ToList();
        if (liveThreats.Count == 0 || baseline.Count == 0)
        {
            return baseline
                .OrderByDescending(item => item.Weight)
                .ThenBy(item => item.Index)
                .ToList();
        }

        double totalMass = baseline.Sum(item => item.Weight);
        double urgencyMass = liveThreats.Sum(item => item.Weight * LiveThreatUrgencyScore(item));
        if (!IsFinite(totalMass) ||
            totalMass <= 0d ||
            !IsFinite(urgencyMass) ||
            urgencyMass <= 0d)
        {
            return baseline
                .OrderByDescending(item => item.Weight)
                .ThenBy(item => item.Index)
                .ToList();
        }

        double nearestLiveDistance = liveThreats.Min(item =>
            Math.Sqrt(item.X * item.X + item.Y * item.Y));
        double activation = 1d / (1d + Math.Pow(
            nearestLiveDistance / LiveThreatUrgencyActivationRadius,
            LiveThreatUrgencyExponent));
        double effectiveReserve = LiveThreatUrgencyReserve * activation;

        // This convex split preserves total threat mass. Every baseline threat keeps at least 40%
        // of its original weight. The urgency reserve rises continuously only as a live enemy enters
        // the base's three-grid danger radius, so distant live scouts do not erase a large future wave.
        double baselineShare = 1d - effectiveReserve;
        HashSet<ThreatCandidate> liveSet = new(liveThreats);
        return baseline
            .Select(item => new ThreatCandidate(
                item.Index,
                item.X,
                item.Y,
                item.Weight * baselineShare +
                (liveSet.Contains(item)
                    ? totalMass * effectiveReserve *
                      item.Weight * LiveThreatUrgencyScore(item) / urgencyMass
                    : 0d)))
            .OrderByDescending(item => item.Weight)
            .ThenBy(item => item.Index)
            .ToList();
    }

    private static double LiveThreatUrgencyScore(ThreatCandidate threat)
    {
        double distance = Math.Sqrt(threat.X * threat.X + threat.Y * threat.Y);
        return 1d / Math.Pow(
            LiveThreatUrgencyDistanceOffset + distance,
            LiveThreatUrgencyExponent);
    }

    private static List<ThreatCandidate> BuildNestThreatCandidates(JObject threats, double? totalMass)
    {
        if (totalMass.HasValue && totalMass.Value <= 0d)
        {
            return new List<ThreatCandidate>();
        }

        List<ThreatCandidate> candidates = ((threats["nests"] as JArray)?.OfType<JObject>()
                ?? Enumerable.Empty<JObject>())
            .Where(item => item["active"]?.Value<bool>() != false &&
                           TryReadThreatVector(threats, item, out _, out _))
            .Select(item =>
            {
                TryReadThreatVector(threats, item, out double x, out double y);
                return new ThreatCandidate(
                    ReadInt(item["index"], int.MaxValue),
                    x * WorldToGridScale,
                    y * WorldToGridScale,
                    Math.Max(ThreatScore(item), 1L));
            })
            .Where(item => item.X * item.X + item.Y * item.Y > 0.000001d)
            .ToList();
        return totalMass.HasValue
            ? ScaleThreatCandidates(candidates, totalMass.Value)
            : candidates
                .OrderByDescending(item => item.Weight)
                .ThenBy(item => item.Index)
                .ToList();
    }

    private static double? ReliableFutureThreatMass(JObject threats)
    {
        JObject? accounting = threats["enemyAccounting"] as JObject;
        if (accounting?["remainingReliable"]?.Value<bool>() != true)
        {
            return null;
        }

        int estimated = ReadInt(accounting["estimatedFutureCount"], -1);
        if (estimated >= 0 && estimated != int.MaxValue)
        {
            return estimated;
        }

        int remaining = ReadInt(accounting["globalRemaining"], -1);
        int accounted = ReadInt(threats["accountedLiveCount"], -1);
        if (remaining < 0 || remaining == int.MaxValue || accounted < 0)
        {
            return null;
        }

        return Math.Max(remaining - accounted, 0);
    }

    private static List<ThreatCandidate> ScaleThreatCandidates(
        IReadOnlyCollection<ThreatCandidate> candidates,
        double totalMass)
    {
        if (candidates.Count == 0 || totalMass <= 0d)
        {
            return new List<ThreatCandidate>();
        }

        double currentMass = candidates.Sum(item => item.Weight);
        if (currentMass <= 0d || double.IsNaN(currentMass) || double.IsInfinity(currentMass))
        {
            return new List<ThreatCandidate>();
        }

        double scale = totalMass / currentMass;
        return candidates
            .Select(item => new ThreatCandidate(
                item.Index,
                item.X,
                item.Y,
                item.Weight * scale))
            .OrderByDescending(item => item.Weight)
            .ThenBy(item => item.Index)
            .ToList();
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
            double area)
        {
            HasCoverageContext = hasCoverageContext;
            SideRank = sideRank;
            DirectionCosine = directionCosine;
            Distance = distance;
            Area = area;
        }

        public bool HasCoverageContext { get; }
        public int SideRank { get; }
        public double DirectionCosine { get; }
        public double Distance { get; }
        public double Area { get; }
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

    private sealed class LineCandidate
    {
        public LineCandidate(
            int lineInstanceId,
            bool isCurrentLine,
            double fromX,
            double fromY,
            double toX,
            double toY)
        {
            LineInstanceId = lineInstanceId;
            IsCurrentLine = isCurrentLine;
            FromX = fromX;
            FromY = fromY;
            ToX = toX;
            ToY = toY;
        }

        public int LineInstanceId { get; }
        public bool IsCurrentLine { get; }
        public double FromX { get; }
        public double FromY { get; }
        public double ToX { get; }
        public double ToY { get; }

        public double DistanceTo(double x, double y)
        {
            double midpointX = (FromX + ToX) / 2d;
            double midpointY = (FromY + ToY) / 2d;
            double lineLengthSquared = DistanceSquared(FromX, FromY, ToX, ToY);
            return DistanceSquared(x, y, midpointX, midpointY) + lineLengthSquared * 0.25d;
        }

        public bool ForwardToward(double x, double y) =>
            DistanceSquared(FromX, FromY, x, y) <= DistanceSquared(ToX, ToY, x, y);
    }

    private sealed class ThreatCandidate
    {
        public ThreatCandidate(int index, double x, double y, double weight)
        {
            Index = index;
            X = x;
            Y = y;
            Weight = weight;
        }

        public int Index { get; }
        public double X { get; }
        public double Y { get; }
        public double Weight { get; }
    }

    private sealed class TrainCoveragePosition
    {
        public TrainCoveragePosition(int trainIndex, double power, LineCandidate? line)
        {
            TrainIndex = trainIndex;
            Power = power;
            Line = line;
        }

        public int TrainIndex { get; }
        public double Power { get; }
        public LineCandidate? Line { get; }
    }

    private sealed class TrainMovementCandidate
    {
        public TrainMovementCandidate(
            int trainIndex,
            int trainIdentity,
            int vehicleCount,
            double trainPower,
            LineCandidate target,
            double improvement,
            ThreatCandidate servedThreat)
        {
            TrainIndex = trainIndex;
            TrainIdentity = trainIdentity;
            VehicleCount = vehicleCount;
            TrainPower = trainPower;
            Target = target;
            Improvement = improvement;
            ServedThreat = servedThreat;
        }

        public int TrainIndex { get; }
        public int TrainIdentity { get; }
        public int VehicleCount { get; }
        public double TrainPower { get; }
        public LineCandidate Target { get; }
        public double Improvement { get; }
        public ThreatCandidate ServedThreat { get; }
    }
}
