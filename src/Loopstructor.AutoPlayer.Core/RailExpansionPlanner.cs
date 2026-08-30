using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Newtonsoft.Json.Linq;

namespace Loopstructor.AutoPlayer.Core;

public sealed class RailInsertionCandidate
{
    public int RailInstanceId { get; set; }
    public int RailInternalId { get; set; }
    public int LineInstanceId { get; set; }
    public int StationLinePointInstanceId { get; set; }
    public int StationCatapultInstanceId { get; set; }
    public int StationGameObjectInstanceId { get; set; }
    public string StationPath { get; set; } = string.Empty;
    public string StationName { get; set; } = string.Empty;
    public string StationDisposableEnum { get; set; } = string.Empty;
    public int OriginalGridX { get; set; }
    public int OriginalGridY { get; set; }
    public bool StationIsSpecial { get; set; }
    public bool StationCanMove { get; set; }
    public int StationCount { get; set; }
    public double CurrentLoopCycleSeconds { get; set; }
    public double CurrentRailLength { get; set; }
    public double PredictedRailLength { get; set; }
    public double VehicleThroughputScore { get; set; }
    public double PredictedVehicleThroughputScore { get; set; }
    public IReadOnlyList<AutoPlayerGrid> OrderedStationGrids { get; set; } = Array.Empty<AutoPlayerGrid>();
    public IReadOnlyList<AutoPlayerGrid> PredictedStationGrids { get; set; } = Array.Empty<AutoPlayerGrid>();
    public JObject PreviewArguments { get; set; } = new();

    public string Identity => string.Join(
        ":",
        RailInstanceId.ToString(CultureInfo.InvariantCulture),
        LineInstanceId.ToString(CultureInfo.InvariantCulture),
        StationLinePointInstanceId.ToString(CultureInfo.InvariantCulture));
}

public sealed class RailInsertionPreviewScore
{
    public RailInsertionCandidate Candidate { get; set; } = new();
    public double PredictedLoopCycleSeconds { get; set; }
    public double BaselineRailLength { get; set; }
    public double PredictedRailLength { get; set; }
    public double BaselineTriggerRate { get; set; }
    public double PredictedTriggerRate { get; set; }
    public double TriggerRateGain { get; set; }
    public double RelativeGain { get; set; }
    public double BaselineEffectiveAttackRate { get; set; }
    public double PredictedEffectiveAttackRate { get; set; }
    public double EffectiveAttackRateGain { get; set; }
    public RailLayoutScore? BaselineLayout { get; set; }
    public RailLayoutScore? PredictedLayout { get; set; }
    public bool IsBeneficial => TriggerRateGain > 0.000001d;
}

public sealed class RailInsertionVerification
{
    public bool Verified { get; set; }
    public bool Beneficial { get; set; }
    public bool MoveObserved { get; set; }
    public bool StructureValid { get; set; }
    public bool Pending { get; set; }
    public string Detail { get; set; } = string.Empty;
    public double ObservedLoopCycleSeconds { get; set; }
    public double ObservedRailLength { get; set; }
}

public sealed class RailStationMoveCandidate
{
    public int RailInstanceId { get; set; }
    public int RailInternalId { get; set; }
    public int StationCount { get; set; }
    public double CurrentLoopCycleSeconds { get; set; }
    public double RailLength { get; set; }
    public int StationCatapultInstanceId { get; set; }
    public int StationGameObjectInstanceId { get; set; }
    public int StationLinePointInstanceId { get; set; }
    /// <summary>
    /// Game-stable LinePoint.ID. Unlike Unity instance ids, this survives CatapultCreator.MoveCatapult.
    /// </summary>
    public int StationPointId { get; set; }
    public string StationPath { get; set; } = string.Empty;
    public string StationName { get; set; } = string.Empty;
    public string StationDisposableEnum { get; set; } = string.Empty;
    public string StationFingerprint { get; set; } = string.Empty;
    public bool StationIsAttribute { get; set; }
    public StationSpacingRules SpacingRules { get; set; }
    public AutoPlayerGrid CurrentGrid { get; set; }
    public IReadOnlyList<AutoPlayerGrid> NeighborGrids { get; set; } = Array.Empty<AutoPlayerGrid>();
    public IReadOnlyList<AutoPlayerGrid> OrderedStationGrids { get; set; } = Array.Empty<AutoPlayerGrid>();
    public IReadOnlyList<bool> OrderedStationKinds { get; set; } = Array.Empty<bool>();
    public IReadOnlyList<int> OrderedStationPointIds { get; set; } = Array.Empty<int>();
}

/// <summary>
/// Plans existing-loop expansion from runtime snapshots. Throughput is the number of station
/// triggers per independently moving vehicle. Each vehicle contributes base output multiplied by
/// its own speed, station count and inverse rail length; personal and former group enchantments are
/// intentionally excluded by the runtime base-output reader.
/// </summary>
public sealed class RailExpansionPlanner
{
    public IReadOnlyList<RailInsertionCandidate> BuildCandidates(
        JObject? railResult,
        JObject? catapultResult,
        JObject? vehicleStateResult)
    {
        JObject railState = State(railResult);
        JObject catapultState = State(catapultResult);
        JObject vehicleState = State(vehicleStateResult);
        List<JObject> stations = (catapultState["catapults"] as JArray)?
            .OfType<JObject>()
            .Where(IsAvailableCommonStation)
            .ToList() ?? new List<JObject>();
        if (stations.Count == 0)
        {
            return Array.Empty<RailInsertionCandidate>();
        }

        List<JObject> vehicles = (vehicleState["vehicles"] as JArray)?
            .OfType<JObject>()
            .ToList() ?? new List<JObject>();
        List<RailInsertionCandidate> candidates = new();
        foreach (JObject rail in (railState["rails"] as JArray)?.OfType<JObject>()
                 ?? Enumerable.Empty<JObject>())
        {
            if (rail["isLegalPlayerLoop"]?.Value<bool>() != true ||
                rail["isLoop"]?.Value<bool>() != true ||
                rail["isOnField"]?.Value<bool>() == false)
            {
                continue;
            }

            int railInstanceId = ReadInt(rail["instanceId"], 0);
            int railInternalId = ReadInt(rail["railInternalId"], ReadInt(rail["id"], 0));
            int stationCount = ReadInt(rail["stationCount"], ReadInt(rail["pointCount"], 0));
            List<AutoPlayerGrid> orderedStationGrids = ReadRailGeometryGrids(rail);
            if (railInstanceId == 0 || railInternalId == 0 || stationCount < 3)
            {
                continue;
            }

            double railLength = rail["railLength"]?.Value<double?>() ?? 0d;
            if (!(railLength > 0d)) continue;
            double cycleSeconds = TryReadPositiveDouble(rail["loopCycleSeconds"], out double observedCycle)
                ? observedCycle
                : railLength;
            double vehicleThroughputScore = ReadVehicleThroughputScore(
                vehicles,
                railInternalId,
                railLength,
                stationCount);
            if (!(vehicleThroughputScore > 0d)) continue;

            foreach (JObject line in (rail["lines"] as JArray)?.OfType<JObject>()
                     ?? Enumerable.Empty<JObject>())
            {
                int lineInstanceId = ReadInt(line["lineInstanceId"], ReadInt(line["instanceId"], 0));
                if (lineInstanceId == 0)
                {
                    continue;
                }
                if (!TryReadGrid(line["from"], out AutoPlayerGrid lineFrom) ||
                    !TryReadGrid(line["to"], out AutoPlayerGrid lineTo))
                {
                    continue;
                }

                foreach (JObject station in stations)
                {
                    int pointInstanceId = ReadInt(station["linePointInstanceId"], 0);
                    int catapultInstanceId = ReadInt(
                        station["catapultInstanceId"],
                        ReadInt(station["instanceId"], 0));
                    if (pointInstanceId == 0 || catapultInstanceId == 0)
                    {
                        continue;
                    }
                    if (!TryReadGrid(station["grid"], out AutoPlayerGrid stationGrid))
                    {
                        continue;
                    }
                    double predictedRailLength = railLength - Distance(lineFrom, lineTo) +
                                                 Distance(lineFrom, stationGrid) +
                                                 Distance(stationGrid, lineTo);
                    IReadOnlyList<AutoPlayerGrid> predictedStationGrids = BuildInsertedStationGrids(
                        orderedStationGrids,
                        lineFrom,
                        lineTo,
                        stationGrid);
                    if (!(predictedRailLength > 0d) || !IsFinite(predictedRailLength))
                    {
                        continue;
                    }
                    if (predictedStationGrids.Count != stationCount + 1)
                    {
                        continue;
                    }
                    double predictedVehicleThroughputScore = vehicleThroughputScore *
                                                             (stationCount + 1d) / stationCount *
                                                             railLength / predictedRailLength;

                    JObject arguments = new()
                    {
                        ["lineInstanceId"] = lineInstanceId,
                        ["station"] = new JObject { ["instanceId"] = pointInstanceId }
                    };
                    candidates.Add(new RailInsertionCandidate
                    {
                        RailInstanceId = railInstanceId,
                        RailInternalId = railInternalId,
                        LineInstanceId = lineInstanceId,
                        StationLinePointInstanceId = pointInstanceId,
                        StationCatapultInstanceId = catapultInstanceId,
                        StationGameObjectInstanceId = ReadInt(station["gameObjectInstanceId"], 0),
                        StationPath = station["path"]?.Value<string>() ?? string.Empty,
                        StationName = station["name"]?.Value<string>() ?? string.Empty,
                        StationDisposableEnum = station["recycleDisposableEnum"]?.Value<string>() ?? string.Empty,
                        OriginalGridX = ReadInt(station.SelectToken("grid.x"), 0),
                        OriginalGridY = ReadInt(station.SelectToken("grid.y"), 0),
                        StationIsSpecial = station["isSpecial"]?.Value<bool>() == true,
                        StationCanMove = station["canMove"]?.Value<bool>() == true,
                        StationCount = stationCount,
                        CurrentLoopCycleSeconds = cycleSeconds,
                        CurrentRailLength = railLength,
                        PredictedRailLength = predictedRailLength,
                        VehicleThroughputScore = vehicleThroughputScore,
                        PredictedVehicleThroughputScore = predictedVehicleThroughputScore,
                        OrderedStationGrids = orderedStationGrids,
                        PredictedStationGrids = predictedStationGrids,
                        PreviewArguments = arguments
                    });
                }
            }
        }

        return candidates
            .GroupBy(candidate => candidate.Identity, StringComparer.Ordinal)
            .Select(group => group.Single())
            .OrderByDescending(candidate =>
                candidate.VehicleThroughputScore)
            .ThenBy(candidate => candidate.RailInstanceId)
            .ThenBy(candidate => candidate.LineInstanceId)
            .ThenBy(candidate => candidate.StationLinePointInstanceId)
            .ToArray();
    }

    public IReadOnlyList<RailStationMoveCandidate> BuildExistingSpecialMoveCandidates(
        JObject? railResult,
        JObject? catapultResult,
        StationSpacingRules spacingRules = default)
    {
        JObject railState = State(railResult);
        List<JObject> rails = (railState["rails"] as JArray)?.OfType<JObject>().ToList()
                              ?? new List<JObject>();
        List<RailStationMoveCandidate> result = new();
        foreach (JObject station in (State(catapultResult)["catapults"] as JArray)?.OfType<JObject>()
                 ?? Enumerable.Empty<JObject>())
        {
            bool isAttribute = station["isAttribute"]?.Value<bool>() == true;
            bool isSpecial = station["isSpecial"]?.Value<bool>() == true;
            string disposableEnum = station["recycleDisposableEnum"]?.Value<string>() ?? string.Empty;
            bool isSupportedMovableStation = isAttribute ||
                                             (isSpecial && !string.Equals(
                                                 disposableEnum,
                                                 "FreePoint",
                                                 StringComparison.OrdinalIgnoreCase));
            if (station["canMove"]?.Value<bool>() != true ||
                ReadInt(station["railMembershipCount"], 0) != 1 ||
                !isSupportedMovableStation ||
                !TryReadGrid(station["grid"], out AutoPlayerGrid currentGrid))
            {
                continue;
            }

            int linePointInstanceId = ReadInt(station["linePointInstanceId"], 0);
            List<JObject> matchingRails = rails.Where(item => RailContainsPoint(item, linePointInstanceId)).ToList();
            if (matchingRails.Count != 1)
            {
                continue;
            }

            JObject rail = matchingRails[0];
            if (
                rail["isLegalPlayerLoop"]?.Value<bool>() != true ||
                !TryReadPositiveDouble(rail["loopCycleSeconds"], out double cycle) ||
                !TryReadPositiveDouble(rail["railLength"], out double railLength))
            {
                continue;
            }

            List<AutoPlayerGrid> neighbors = new();
            foreach (JObject line in (rail["lines"] as JArray)?.OfType<JObject>()
                     ?? Enumerable.Empty<JObject>())
            {
                if (!TryReadGrid(line["from"], out AutoPlayerGrid from) ||
                    !TryReadGrid(line["to"], out AutoPlayerGrid to))
                {
                    continue;
                }

                if (from.Equals(currentGrid)) neighbors.Add(to);
                else if (to.Equals(currentGrid)) neighbors.Add(from);
            }

            neighbors = neighbors.Distinct().ToList();
            if (neighbors.Count < 2)
            {
                continue;
            }

            List<AutoPlayerGrid> orderedStationGrids = ReadRailGeometryGrids(rail);
            if (orderedStationGrids.Count < 3)
            {
                continue;
            }
            IReadOnlyList<bool> orderedStationKinds = ReadRailStationKinds(rail);
            if (orderedStationKinds.Count != orderedStationGrids.Count)
            {
                orderedStationKinds = orderedStationGrids
                    .Select((grid, index) => index == 0)
                    .ToArray();
            }
            IReadOnlyList<int> orderedStationPointIds = ReadRailStablePointIds(rail);
            if (!TryReadStablePointId(rail, linePointInstanceId, out int stablePointId) ||
                orderedStationPointIds.Count != orderedStationGrids.Count)
            {
                continue;
            }

            result.Add(new RailStationMoveCandidate
            {
                RailInstanceId = ReadInt(rail["instanceId"], 0),
                RailInternalId = ReadInt(rail["railInternalId"], ReadInt(rail["id"], 0)),
                StationCount = ReadInt(rail["stationCount"], ReadInt(rail["pointCount"], 0)),
                CurrentLoopCycleSeconds = cycle,
                RailLength = railLength,
                StationCatapultInstanceId = ReadInt(
                    station["catapultInstanceId"],
                    ReadInt(station["instanceId"], 0)),
                StationGameObjectInstanceId = ReadInt(station["gameObjectInstanceId"], 0),
                StationLinePointInstanceId = linePointInstanceId,
                StationPointId = stablePointId,
                StationPath = station["path"]?.Value<string>() ?? string.Empty,
                StationName = station["name"]?.Value<string>() ?? string.Empty,
                StationDisposableEnum = station["recycleDisposableEnum"]?.Value<string>() ?? string.Empty,
                StationFingerprint = BuildStationFingerprint(station),
                StationIsAttribute = isAttribute,
                SpacingRules = spacingRules,
                CurrentGrid = currentGrid,
                NeighborGrids = neighbors,
                OrderedStationGrids = orderedStationGrids,
                OrderedStationKinds = orderedStationKinds,
                OrderedStationPointIds = orderedStationPointIds
            });
        }

        return result
            .Where(candidate => candidate.RailInstanceId != 0 &&
                                candidate.RailInternalId != 0 &&
                                candidate.StationCatapultInstanceId != 0 &&
                                 candidate.StationGameObjectInstanceId != 0 &&
                                 candidate.StationLinePointInstanceId != 0 &&
                                !string.IsNullOrWhiteSpace(candidate.StationPath) &&
                                !string.IsNullOrWhiteSpace(candidate.StationFingerprint))
            .OrderByDescending(candidate => candidate.StationCount / candidate.CurrentLoopCycleSeconds)
            .ThenBy(candidate => candidate.RailInstanceId)
            .ThenBy(candidate => candidate.StationPointId)
            .ToArray();
    }

    public double PredictCycleAfterMove(RailStationMoveCandidate candidate, AutoPlayerGrid targetGrid)
    {
        double oldAdjacentLength = candidate.NeighborGrids.Sum(neighbor => Distance(candidate.CurrentGrid, neighbor));
        double newAdjacentLength = candidate.NeighborGrids.Sum(neighbor => Distance(targetGrid, neighbor));
        double predictedLength = candidate.RailLength - oldAdjacentLength + newAdjacentLength;
        return predictedLength > 0d && candidate.RailLength > 0d
            ? candidate.CurrentLoopCycleSeconds * predictedLength / candidate.RailLength
            : double.PositiveInfinity;
    }

    public RailLayoutScore ScoreCurrentLayout(RailStationMoveCandidate candidate) =>
        RailLayoutStrategyPlanner.EvaluateWithSpacing(
            candidate.OrderedStationGrids.Select(ToLayoutPoint),
            candidate.OrderedStationKinds,
            candidate.StationCount,
            candidate.CurrentLoopCycleSeconds,
            candidate.SpacingRules);

    public RailLayoutScore ScoreMovedLayout(RailStationMoveCandidate candidate, AutoPlayerGrid targetGrid)
    {
        double predictedCycle = PredictCycleAfterMove(candidate, targetGrid);
        IReadOnlyList<AutoPlayerGrid> moved = candidate.OrderedStationGrids
            .Select(grid => grid.Equals(candidate.CurrentGrid) ? targetGrid : grid)
            .ToArray();
        return RailLayoutStrategyPlanner.EvaluateWithSpacing(
            moved.Select(ToLayoutPoint),
            candidate.OrderedStationKinds,
            candidate.StationCount,
            predictedCycle,
            candidate.SpacingRules);
    }

    public RailLayoutScore ScoreObservedJointLayout(
        RailJointLayoutPlan? plan,
        JObject? railResult)
    {
        if (plan == null) return new RailLayoutScore();
        JObject? rail = (State(railResult)["rails"] as JArray)?.OfType<JObject>()
            .SingleOrDefault(item =>
                ReadInt(item["railInternalId"], ReadInt(item["id"], 0)) == plan.RailInternalId);
        if (rail == null || rail["isLegalPlayerLoop"]?.Value<bool>() != true ||
            !TryReadPositiveDouble(rail["loopCycleSeconds"], out double cycle)) return new RailLayoutScore();
        List<AutoPlayerGrid> grids = ReadRailGeometryGrids(rail);
        IReadOnlyList<bool> kinds = ReadRailStationKinds(rail);
        StationSpacingRules rules = plan.Targets.FirstOrDefault()?.Candidate.SpacingRules ?? default;
        return kinds.Count == grids.Count
            ? RailLayoutStrategyPlanner.EvaluateWithSpacing(
                grids.Select(ToLayoutPoint), kinds, grids.Count, cycle, rules)
            : RailLayoutStrategyPlanner.Evaluate(grids.Select(ToLayoutPoint), grids.Count, cycle);
    }

    public RailLayoutScore ScorePlannedJointLayout(
        RailJointLayoutPlan? plan,
        double loopCycleSeconds)
    {
        if (plan == null || !TryReadPositiveDouble(new JValue(loopCycleSeconds), out double cycle) ||
            plan.OrderedTargetGrids.Count < 3) return new RailLayoutScore();
        RailStationMoveCandidate? canonical = plan.Targets.FirstOrDefault()?.Candidate;
        if (canonical == null || canonical.OrderedStationKinds.Count != plan.OrderedTargetGrids.Count)
            return new RailLayoutScore();
        return RailLayoutStrategyPlanner.EvaluateWithSpacing(
            plan.OrderedTargetGrids.Select(ToLayoutPoint),
            canonical.OrderedStationKinds,
            plan.OrderedTargetGrids.Count,
            cycle,
            canonical.SpacingRules);
    }

    public bool TryRefreshJointMoveCandidate(
        JObject? railResult,
        JObject? catapultResult,
        RailJointMoveTarget? target,
        out RailStationMoveCandidate refreshed)
    {
        refreshed = new RailStationMoveCandidate();
        if (target == null) return false;
        StationSpacingRules spacingRules = target.Candidate.SpacingRules;
        RailStationMoveCandidate[] candidates = BuildExistingSpecialMoveCandidates(
                railResult,
                catapultResult,
                spacingRules)
            .Where(candidate => candidate.StationPointId == target.StablePointId)
            .ToArray();
        if (candidates.Length != 1) return false;
        refreshed = candidates[0];
        return true;
    }

    public bool IsJointMoveObserved(
        JObject? railResult,
        JObject? catapultResult,
        RailJointMoveTarget? target,
        out RailStationMoveCandidate refreshed)
    {
        refreshed = new RailStationMoveCandidate();
        if (target == null) return false;
        JObject[] rails = (State(railResult)["rails"] as JArray)?.OfType<JObject>()
            .Where(item => ReadInt(item["railInternalId"], ReadInt(item["id"], 0)) ==
                           target.Candidate.RailInternalId)
            .ToArray() ?? Array.Empty<JObject>();
        int expectedLinePointInstanceId = 0;
        if (rails.Length > 0)
        {
            JObject[] stableStations = rails
                .SelectMany(item => (item["orderedStations"] as JArray)?.OfType<JObject>() ??
                                    Enumerable.Empty<JObject>())
                .Where(item =>
                    item["pointId"]?.Type == JTokenType.Integer &&
                    item["pointId"]!.Value<int>() == target.StablePointId &&
                    item.SelectToken("grid.x")?.Value<int?>() == target.TargetGrid.X &&
                    item.SelectToken("grid.y")?.Value<int?>() == target.TargetGrid.Y)
                .ToArray();
            if (stableStations.Length != 1) return false;
            expectedLinePointInstanceId = ReadInt(stableStations[0]["linePointInstanceId"], 0);
            if (expectedLinePointInstanceId == 0) return false;
        }
        JObject[] matches = (State(catapultResult)["catapults"] as JArray)?.OfType<JObject>()
            .Where(item =>
                item["active"]?.Value<bool>() != false &&
                item.SelectToken("grid.x")?.Value<int?>() == target.TargetGrid.X &&
                item.SelectToken("grid.y")?.Value<int?>() == target.TargetGrid.Y &&
                (item["isAttribute"]?.Value<bool>() == true) == target.Candidate.StationIsAttribute &&
                (expectedLinePointInstanceId == 0 ||
                 ReadInt(item["linePointInstanceId"], 0) == expectedLinePointInstanceId) &&
                string.Equals(BuildStationFingerprint(item), target.Candidate.StationFingerprint, StringComparison.Ordinal))
            .ToArray() ?? Array.Empty<JObject>();
        if (matches.Length != 1) return false;
        JObject station = matches[0];
        refreshed = CloneWithRuntimeIdentity(target.Candidate, station, target.TargetGrid);
        return refreshed.StationCatapultInstanceId != 0 && refreshed.StationLinePointInstanceId != 0;
    }

    /// <summary>
    /// A move is useful when it repairs a weaker coverage tier, or improves N/T without reducing
    /// all-direction coverage. This lets one-sided layouts recover before cycle optimization.
    /// </summary>
    public bool IsBeneficialMove(RailStationMoveCandidate candidate, AutoPlayerGrid targetGrid)
    {
        RailLayoutScore baseline = ScoreCurrentLayout(candidate);
        RailLayoutScore predicted = ScoreMovedLayout(candidate, targetGrid);
        return RailLayoutStrategyPlanner.IsStrictDefenseImprovement(baseline, predicted);
    }

    public bool IsFreshMovableSpecial(
        JObject? catapultResult,
        JObject? movableStateResult,
        RailStationMoveCandidate? candidate)
    {
        if (candidate == null) return false;
        List<JObject> catapults = (State(catapultResult)["catapults"] as JArray)?.OfType<JObject>()
            .Where(item =>
                ReadInt(item["catapultInstanceId"], ReadInt(item["instanceId"], 0)) ==
                candidate.StationCatapultInstanceId)
            .ToList() ?? new List<JObject>();
        List<JObject> stations = (State(movableStateResult)["stations"] as JArray)?.OfType<JObject>()
            .Where(item => ReadInt(item["instanceId"], 0) == candidate.StationCatapultInstanceId)
            .ToList() ?? new List<JObject>();
        if (catapults.Count != 1 || stations.Count != 1)
        {
            return false;
        }

        JObject catapult = catapults[0];
        JObject station = stations[0];
        return
               MatchesMovableStationKind(catapult, candidate) &&
               catapult["canMove"]?.Value<bool>() == true &&
               station["canMove"]?.Value<bool>() == true &&
               ReadInt(catapult["gameObjectInstanceId"], 0) == candidate.StationGameObjectInstanceId &&
               ReadInt(station["gameObjectInstanceId"], 0) == candidate.StationGameObjectInstanceId &&
               ReadInt(catapult["linePointInstanceId"], 0) == candidate.StationLinePointInstanceId &&
               string.Equals(catapult["path"]?.Value<string>(), candidate.StationPath, StringComparison.Ordinal) &&
               string.Equals(station["path"]?.Value<string>(), candidate.StationPath, StringComparison.Ordinal) &&
               ReadInt(catapult["railMembershipCount"], 0) == 1 &&
               ReadStationRailId(catapult) == candidate.RailInternalId &&
               string.Equals(BuildStationFingerprint(catapult), candidate.StationFingerprint, StringComparison.Ordinal) &&
               string.Equals(
                   catapult["recycleDisposableEnum"]?.Value<string>(),
                   candidate.StationDisposableEnum,
                   StringComparison.Ordinal);
    }

    public bool IsOwnedMoveInteraction(
        JObject? movableStateResult,
        RailStationMoveCandidate? candidate,
        int expectedInteractionInstanceId = 0)
    {
        if (candidate == null) return false;
        JObject? interaction = State(movableStateResult)["currentMoveInteraction"] as JObject;
        int interactionId = ReadInt(interaction?["interactionInstanceId"], 0);
        int targetId = ReadInt(interaction?.SelectToken("target.instanceId"), 0);
        return interaction?["active"]?.Value<bool>() == true &&
               interactionId != 0 &&
               (expectedInteractionInstanceId == 0 || expectedInteractionInstanceId == interactionId) &&
               (candidate.StationGameObjectInstanceId == 0 || targetId == candidate.StationGameObjectInstanceId) &&
               string.Equals(
                   interaction?.SelectToken("target.path")?.Value<string>(),
                   candidate.StationPath,
               StringComparison.Ordinal);
    }

    public int ReadMoveInteractionInstanceId(JObject? movableStateResult) =>
        ReadInt(State(movableStateResult).SelectToken("currentMoveInteraction.interactionInstanceId"), 0);

    public RailInsertionVerification VerifyMove(
        JObject? baselineResult,
        JObject? currentResult,
        JObject? catapultResult,
        RailStationMoveCandidate? candidate,
        JObject? expectedGrid)
    {
        if (candidate == null ||
            !TryReadRailMap(baselineResult, out Dictionary<int, JObject> baselineRails) ||
            !TryReadRailMap(currentResult, out Dictionary<int, JObject> currentRails) ||
            !baselineRails.Keys.OrderBy(id => id).SequenceEqual(currentRails.Keys.OrderBy(id => id)))
        {
            return Failure("移动特殊弹射点前后轨道集合无法安全对账。");
        }

        int? x = expectedGrid?["x"]?.Value<int?>();
        int? y = expectedGrid?["y"]?.Value<int?>();
        if (!x.HasValue || !y.HasValue)
        {
            return Failure("缺少特殊弹射点目标格身份。");
        }

        List<JObject> targetStations = (State(catapultResult)["catapults"] as JArray)?.OfType<JObject>()
            .Where(item => MatchesMovableStationKind(item, candidate) &&
                           string.Equals(item["name"]?.Value<string>(), candidate.StationName, StringComparison.Ordinal) &&
                           string.Equals(
                               item["recycleDisposableEnum"]?.Value<string>(),
                               candidate.StationDisposableEnum,
                               StringComparison.Ordinal) &&
                           string.Equals(
                               BuildStationFingerprint(item),
                               candidate.StationFingerprint,
                               StringComparison.Ordinal) &&
                           ReadInt(item["railMembershipCount"], 0) == 1 &&
                           ReadStationRailId(item) == candidate.RailInternalId &&
                           item.SelectToken("grid.x")?.Value<int?>() == x.Value &&
                           item.SelectToken("grid.y")?.Value<int?>() == y.Value)
            .ToList() ?? new List<JObject>();
        JObject currentRail = currentRails[candidate.RailInstanceId];
        JObject baselineRail = baselineRails[candidate.RailInstanceId];
        int currentCount = ReadInt(currentRail["stationCount"], ReadInt(currentRail["pointCount"], -1));
        if (targetStations.Count == 0 && currentCount == candidate.StationCount)
        {
            return new RailInsertionVerification { Pending = true, Detail = "尚未观察到特殊弹射点移动到目标格。" };
        }
        if (targetStations.Count == 1 && SameRailStructure(baselineRail, currentRail))
        {
            return new RailInsertionVerification
            {
                Pending = true,
                Detail = "已看到站点到达目标格，但轨道快照仍停留在移动前一帧；继续只读复核。"
            };
        }

        bool sourceGridStillOccupied = (State(catapultResult)["catapults"] as JArray)?.OfType<JObject>()
            .Any(item =>
                item.SelectToken("grid.x")?.Value<int?>() == candidate.CurrentGrid.X &&
                item.SelectToken("grid.y")?.Value<int?>() == candidate.CurrentGrid.Y &&
                string.Equals(BuildStationFingerprint(item), candidate.StationFingerprint, StringComparison.Ordinal)) == true;
        JObject? targetStation = targetStations.Count == 1 ? targetStations[0] : null;
        RailLayoutScore baselineLayout = ScoreCurrentLayout(candidate);
        List<AutoPlayerGrid> observedGrids = ReadRailGeometryGrids(currentRail);
        RailLayoutScore observedLayout = RailLayoutStrategyPlanner.Evaluate(
            observedGrids.Select(ToLayoutPoint),
            currentCount,
            TryReadPositiveDouble(currentRail["loopCycleSeconds"], out double candidateCycle)
                ? candidateCycle
                : 0d);
        bool coverageImproved = RailLayoutStrategyPlanner.CompareCoverage(observedLayout, baselineLayout) < 0;
        bool sameCoverageAndFaster =
            RailLayoutStrategyPlanner.CompareCoverage(observedLayout, baselineLayout) == 0 &&
            observedLayout.TriggerRate > baselineLayout.TriggerRate + 0.000001d;
        bool moveObserved = targetStation != null &&
                            !sourceGridStillOccupied &&
                            ReadInt(targetStation["catapultInstanceId"], ReadInt(targetStation["instanceId"], 0)) !=
                            candidate.StationCatapultInstanceId &&
                            ReadInt(targetStation["gameObjectInstanceId"], 0) != candidate.StationGameObjectInstanceId &&
                            ReadInt(targetStation["linePointInstanceId"], 0) != candidate.StationLinePointInstanceId &&
                            currentCount == candidate.StationCount;
        double observedCycle = 0d;
        bool structureValid = moveObserved &&
                              currentRail["isLegalPlayerLoop"]?.Value<bool>() == true &&
                              TryReadPositiveDouble(currentRail["loopCycleSeconds"], out observedCycle);
        bool beneficial = coverageImproved || sameCoverageAndFaster;
        if (!structureValid || !beneficial)
        {
            return new RailInsertionVerification
            {
                MoveObserved = moveObserved,
                StructureValid = structureValid,
                Beneficial = false,
                Detail = !structureValid
                    ? "已提交弹射点移动，但尚未证明站点保持在同一合法闭环。"
                    : "弹射点移动已经落地，但四向覆盖与真实触发率没有达到预测收益。",
                ObservedLoopCycleSeconds = structureValid ? observedCycle : 0d
            };
        }

        return new RailInsertionVerification
        {
            Verified = true,
            Beneficial = true,
            MoveObserved = true,
            StructureValid = true,
            Detail = "已验证弹射点仍属于同一合法闭环，且四向覆盖或真实触发率已改善。",
            ObservedLoopCycleSeconds = observedCycle
        };
    }

    public RailInsertionVerification VerifyMoveCancellationRollback(
        JObject? baselineResult,
        JObject? currentResult,
        JObject? catapultResult,
        RailStationMoveCandidate? candidate)
    {
        if (candidate == null ||
            !TryReadRailMap(baselineResult, out Dictionary<int, JObject> baselineRails) ||
            !baselineRails.TryGetValue(candidate.RailInstanceId, out JObject? baselineRail))
        {
            return Failure("取消特殊弹射点移动时缺少可信的轨道基线。");
        }

        if (!TryReadRailMap(currentResult, out Dictionary<int, JObject> currentRails) ||
            !baselineRails.Keys.OrderBy(id => id).SequenceEqual(currentRails.Keys.OrderBy(id => id)) ||
            !currentRails.TryGetValue(candidate.RailInstanceId, out JObject? currentRail))
        {
            return new RailInsertionVerification
            {
                Pending = true,
                Detail = "取消移动后的轨道集合尚未恢复到启动前基线。"
            };
        }

        int baselineCount = ReadInt(
            baselineRail["stationCount"],
            ReadInt(baselineRail["pointCount"], -1));
        int currentCount = ReadInt(
            currentRail["stationCount"],
            ReadInt(currentRail["pointCount"], -1));
        if (baselineCount != candidate.StationCount ||
            currentCount != baselineCount ||
            ReadInt(baselineRail["railInternalId"], ReadInt(baselineRail["id"], 0)) !=
            candidate.RailInternalId ||
            ReadInt(currentRail["railInternalId"], ReadInt(currentRail["id"], 0)) !=
            candidate.RailInternalId ||
            baselineRail["isLegalPlayerLoop"]?.Value<bool>() != true ||
            currentRail["isLegalPlayerLoop"]?.Value<bool>() != true ||
            baselineRail["isLoop"]?.Value<bool>() != true ||
            currentRail["isLoop"]?.Value<bool>() != true ||
            baselineRail["isOnField"]?.Value<bool>() == false ||
            currentRail["isOnField"]?.Value<bool>() == false ||
            !TryReadPointIdentitySequence(baselineRail, out int[] baselinePointIds) ||
            !TryReadPointIdentitySequence(currentRail, out int[] currentPointIds) ||
            !baselinePointIds.SequenceEqual(currentPointIds) ||
            baselinePointIds.Count(id => id == candidate.StationLinePointInstanceId) != 1 ||
            !TryReadPositiveDouble(baselineRail["railLength"], out double baselineLength) ||
            !TryReadPositiveDouble(currentRail["railLength"], out double currentLength) ||
            !ApproximatelyEqual(baselineLength, currentLength, 0.001d))
        {
            return new RailInsertionVerification
            {
                Pending = true,
                Detail = "取消移动后的目标轨道站点顺序或长度尚未恢复到启动前基线。"
            };
        }

        List<JObject> sourceStations = (State(catapultResult)["catapults"] as JArray)?
            .OfType<JObject>()
            .Where(item =>
                ReadInt(item["catapultInstanceId"], ReadInt(item["instanceId"], 0)) ==
                candidate.StationCatapultInstanceId)
            .ToList() ?? new List<JObject>();
        if (sourceStations.Count != 1)
        {
            return new RailInsertionVerification
            {
                Pending = true,
                Detail = "取消移动后尚未重新观察到原特殊弹射点实例。"
            };
        }

        JObject sourceStation = sourceStations[0];
        if (sourceStation["active"]?.Value<bool>() == false ||
            !MatchesMovableStationKind(sourceStation, candidate) ||
            ReadInt(sourceStation["gameObjectInstanceId"], 0) != candidate.StationGameObjectInstanceId ||
            ReadInt(sourceStation["linePointInstanceId"], 0) != candidate.StationLinePointInstanceId ||
            !string.Equals(sourceStation["path"]?.Value<string>(), candidate.StationPath, StringComparison.Ordinal) ||
            !string.Equals(sourceStation["name"]?.Value<string>(), candidate.StationName, StringComparison.Ordinal) ||
            !string.Equals(
                sourceStation["recycleDisposableEnum"]?.Value<string>(),
                candidate.StationDisposableEnum,
                StringComparison.Ordinal) ||
            !string.Equals(
                BuildStationFingerprint(sourceStation),
                candidate.StationFingerprint,
                StringComparison.Ordinal) ||
            ReadInt(sourceStation["railMembershipCount"], 0) != 1 ||
            ReadStationRailId(sourceStation) != candidate.RailInternalId ||
            !TryReadGrid(sourceStation["grid"], out AutoPlayerGrid sourceGrid) ||
            !sourceGrid.Equals(candidate.CurrentGrid))
        {
            return new RailInsertionVerification
            {
                Pending = true,
                Detail = "取消移动后的特殊弹射点身份、原格或轨道归属尚未恢复到启动前基线。"
            };
        }

        return new RailInsertionVerification
        {
            Verified = true,
            Detail = "已验证移动预览退出，原特殊弹射点与轨道结构均恢复到启动前基线。",
            ObservedLoopCycleSeconds = TryReadPositiveDouble(
                currentRail["loopCycleSeconds"],
                out double observedCycle)
                ? observedCycle
                : 0d
        };
    }

    public bool TryScorePreview(
        RailInsertionCandidate? candidate,
        JObject? previewResult,
        out RailInsertionPreviewScore score)
    {
        score = new RailInsertionPreviewScore();
        if (candidate == null || candidate.StationCount < 1 ||
            candidate.CurrentRailLength <= 0d || candidate.PredictedRailLength <= 0d ||
            candidate.VehicleThroughputScore <= 0d || candidate.PredictedVehicleThroughputScore <= 0d)
        {
            return false;
        }

        JObject state = State(previewResult);
        if (state["wouldBeLegal"]?.Value<bool>() != true ||
            state["sideEffectCheckPassed"]?.Value<bool>() != true ||
            state["statePolluted"]?.Value<bool>() == true ||
            ReadInt(state["beforeRailCount"], -1) != ReadInt(state["afterRailCount"], -2) ||
            ReadInt(state["affectedRailId"], 0) != candidate.RailInternalId)
        {
            return false;
        }

        double predictedLength = TryReadPositiveDouble(state["predictedRailLength"], out double runtimeLength)
            ? runtimeLength
            : candidate.PredictedRailLength;
        double baselineRate = candidate.StationCount / candidate.CurrentRailLength;
        double predictedRate = (candidate.StationCount + 1d) / predictedLength;
        double baselineOutput = candidate.VehicleThroughputScore;
        double predictedOutput = baselineOutput * predictedRate / baselineRate;
        RailLayoutScore? baselineLayout = TryBuildCandidateLayout(
            candidate,
            null,
            candidate.CurrentRailLength);
        RailLayoutScore? predictedLayout = TryBuildCandidateLayout(
            candidate,
            previewResult,
            predictedLength);
        score = new RailInsertionPreviewScore
        {
            Candidate = candidate,
            PredictedLoopCycleSeconds = 0d,
            BaselineRailLength = candidate.CurrentRailLength,
            PredictedRailLength = predictedLength,
            BaselineTriggerRate = baselineRate,
            PredictedTriggerRate = predictedRate,
            TriggerRateGain = predictedRate - baselineRate,
            RelativeGain = predictedRate / baselineRate - 1d,
            BaselineEffectiveAttackRate = baselineOutput,
            PredictedEffectiveAttackRate = predictedOutput,
            EffectiveAttackRateGain = predictedOutput - baselineOutput,
            BaselineLayout = baselineLayout,
            PredictedLayout = predictedLayout
        };
        return IsFinite(score.TriggerRateGain) && IsFinite(score.RelativeGain);
    }

    public RailInsertionPreviewScore? SelectBest(IEnumerable<RailInsertionPreviewScore>? scores) =>
        scores?
            .Where(score => score != null &&
                            score.BaselineLayout != null &&
                            score.PredictedLayout != null &&
                            RailLayoutStrategyPlanner.IsStrictDefenseImprovement(
                                score.BaselineLayout,
                                score.PredictedLayout))
            .OrderBy(
                score => score.PredictedLayout,
                Comparer<RailLayoutScore?>.Create(RailLayoutStrategyPlanner.CompareCoverage))
            .ThenByDescending(score => score.PredictedEffectiveAttackRate)
            .ThenByDescending(score => score.EffectiveAttackRateGain)
            .ThenByDescending(score => score.TriggerRateGain)
            .ThenBy(
                score => score.PredictedLayout,
                Comparer<RailLayoutScore?>.Create(RailLayoutStrategyPlanner.CompareForDefense))
            .ThenByDescending(score => score.RelativeGain)
            .ThenBy(score => score.PredictedRailLength)
            .ThenBy(score => score.Candidate.RailInstanceId)
            .ThenBy(score => score.Candidate.LineInstanceId)
            .ThenBy(score => score.Candidate.StationLinePointInstanceId)
            .FirstOrDefault();

    /// <summary>
    /// Selects a legal insertion when a map reward has produced an unassigned station that must
    /// be reconciled before progression. A required map station is ranked by the resulting
    /// coverage, but it must not be rejected merely because inserting that fixed-position point
    /// lowers a coverage tie-breaker. The runtime preview remains the authority for loop legality;
    /// otherwise a valid map station can remain outside the loop forever and becomes frozen as
    /// soon as the next wave starts.
    /// </summary>
    public RailInsertionPreviewScore? SelectBestRequiredTopology(
        IEnumerable<RailInsertionPreviewScore>? scores) =>
        scores?
            .Where(score => score != null &&
                            score.BaselineLayout != null &&
                            score.PredictedLayout != null)
            .OrderBy(
                score => score.PredictedLayout,
                Comparer<RailLayoutScore?>.Create(RailLayoutStrategyPlanner.CompareCoverage))
            .ThenByDescending(score => score.PredictedEffectiveAttackRate)
            .ThenByDescending(score => score.PredictedTriggerRate)
            .ThenBy(score => score.PredictedRailLength)
            .ThenBy(score => score.Candidate.RailInstanceId)
            .ThenBy(score => score.Candidate.LineInstanceId)
            .ThenBy(score => score.Candidate.StationLinePointInstanceId)
            .FirstOrDefault();

    public RailInsertionPreviewScore? SelectMovableSpecialForReposition(
        IEnumerable<RailInsertionPreviewScore>? scores) =>
        scores?
            .Where(score => score != null &&
                            !score.IsBeneficial &&
                            score.Candidate.StationIsSpecial &&
                            score.Candidate.StationCanMove &&
                            !string.Equals(
                                score.Candidate.StationDisposableEnum,
                                "FreePoint",
                                StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(score => score.RelativeGain)
            .ThenBy(score => score.Candidate.RailInstanceId)
            .ThenBy(score => score.Candidate.LineInstanceId)
            .FirstOrDefault();

    public AutomationAction BuildInsertAction(RailInsertionPreviewScore score)
    {
        if (score == null)
        {
            throw new ArgumentNullException(nameof(score));
        }

        return new AutomationAction(
            "insertPointFromLine",
            (JObject)score.Candidate.PreviewArguments.DeepClone(),
            AutomationStage.PreparingDefense,
            $"扩充轨道 {score.Candidate.RailInternalId}：逐车基础输出与独立速度聚合收益由 " +
            $"{score.BaselineEffectiveAttackRate:0.###} 变为 {score.PredictedEffectiveAttackRate:0.###}，" +
            $"轨道长度由 {score.BaselineRailLength:0.###} 变为 {score.PredictedRailLength:0.###}。");
    }

    public RailInsertionVerification VerifyInsertion(
        JObject? baselineResult,
        JObject? currentResult,
        RailInsertionPreviewScore? selected)
    {
        if (selected == null ||
            !TryReadRailMap(baselineResult, out Dictionary<int, JObject> baselineRails) ||
            !TryReadRailMap(currentResult, out Dictionary<int, JObject> currentRails))
        {
            return Failure("扩轨前后轨道快照缺少唯一实例身份。");
        }

        if (!baselineRails.Keys.OrderBy(id => id).SequenceEqual(currentRails.Keys.OrderBy(id => id)))
        {
            return Failure("扩轨期间轨道集合发生了非预期变化。");
        }

        RailInsertionCandidate candidate = selected.Candidate;
        JObject baseline = baselineRails[candidate.RailInstanceId];
        JObject current = currentRails[candidate.RailInstanceId];
        int baselineCount = ReadInt(baseline["stationCount"], ReadInt(baseline["pointCount"], -1));
        int currentCount = ReadInt(current["stationCount"], ReadInt(current["pointCount"], -1));
        bool containsTarget = RailContainsPoint(current, candidate.StationLinePointInstanceId);
        if (currentCount == baselineCount && !containsTarget)
        {
            return new RailInsertionVerification
            {
                Pending = true,
                Detail = "尚未观察到选定站点加入目标轨道。"
            };
        }

        if (baselineCount != candidate.StationCount ||
            currentCount != baselineCount + 1 ||
            !containsTarget ||
            current["isLegalPlayerLoop"]?.Value<bool>() != true ||
            current["isLoop"]?.Value<bool>() != true ||
            current["isOnField"]?.Value<bool>() == false ||
            !TryReadPositiveDouble(current["railLength"], out double observedLength))
        {
            return Failure("目标轨道未形成仅新增一个指定站点的合法闭环。");
        }

        double baselineLength = TryReadPositiveDouble(baseline["railLength"], out double readBaselineLength)
            ? readBaselineLength
            : selected.BaselineRailLength;
        RailLayoutScore baselineLayout = ScoreObservedOrPlannedLayout(
            baseline,
            baselineCount,
            baselineLength,
            selected.BaselineLayout);
        RailLayoutScore currentLayout = ScoreObservedOrPlannedLayout(
            current,
            currentCount,
            observedLength,
            selected.PredictedLayout);
        bool beneficial = RailLayoutStrategyPlanner.IsStrictDefenseImprovement(
            baselineLayout,
            currentLayout);

        return new RailInsertionVerification
        {
            Verified = true,
            Beneficial = beneficial,
            Detail = beneficial
                ? "已验证指定站点加入原轨道，且轨道仍为合法玩家闭环。"
                : "已验证指定站点加入原轨道且结构合法，但实测布局收益未达到预期。",
            ObservedRailLength = observedLength
        };
    }

    public bool IsFreshMovableSpecial(
        JObject? catapultResult,
        JObject? movableStateResult,
        RailInsertionCandidate? candidate)
    {
        if (candidate == null ||
            !candidate.StationIsSpecial ||
            !candidate.StationCanMove ||
            string.Equals(candidate.StationDisposableEnum, "FreePoint", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        JObject? catapult = (State(catapultResult)["catapults"] as JArray)?
            .OfType<JObject>()
            .SingleOrDefault(item =>
                ReadInt(item["catapultInstanceId"], ReadInt(item["instanceId"], 0)) ==
                candidate.StationCatapultInstanceId);
        JObject? station = (State(movableStateResult)["stations"] as JArray)?
            .OfType<JObject>()
            .SingleOrDefault(item => ReadInt(item["instanceId"], 0) == candidate.StationCatapultInstanceId);
        return catapult != null &&
               station != null &&
               catapult["isSpecial"]?.Value<bool>() == true &&
               catapult["isAttribute"]?.Value<bool>() != true &&
               catapult["canMove"]?.Value<bool>() == true &&
               station["canMove"]?.Value<bool>() == true &&
               string.Equals(
                   catapult["recycleDisposableEnum"]?.Value<string>(),
                   candidate.StationDisposableEnum,
                   StringComparison.Ordinal) &&
               ReadInt(catapult["railMembershipCount"], 0) == 0;
    }

    public bool IsOwnedMoveInteraction(
        JObject? movableStateResult,
        RailInsertionCandidate? candidate,
        int expectedInteractionInstanceId = 0)
    {
        JObject state = State(movableStateResult);
        JObject? interaction = state["currentMoveInteraction"] as JObject;
        string targetPath = interaction?.SelectToken("target.path")?.Value<string>() ?? string.Empty;
        int interactionInstanceId = ReadInt(interaction?["interactionInstanceId"], 0);
        int targetInstanceId = ReadInt(interaction?.SelectToken("target.instanceId"), 0);
        return candidate != null &&
               interaction?["active"]?.Value<bool>() == true &&
               interactionInstanceId != 0 &&
               (expectedInteractionInstanceId == 0 ||
                interactionInstanceId == expectedInteractionInstanceId) &&
               (candidate.StationGameObjectInstanceId == 0 ||
                targetInstanceId == candidate.StationGameObjectInstanceId) &&
               !string.IsNullOrWhiteSpace(candidate.StationPath) &&
               string.Equals(targetPath, candidate.StationPath, StringComparison.Ordinal);
    }

    public bool IsMovedSpecialAtGrid(
        JObject? catapultResult,
        RailInsertionCandidate? candidate,
        JObject? expectedGrid)
    {
        int? x = expectedGrid?["x"]?.Value<int?>();
        int? y = expectedGrid?["y"]?.Value<int?>();
        if (candidate == null || !x.HasValue || !y.HasValue)
        {
            return false;
        }

        List<JObject> matches = (State(catapultResult)["catapults"] as JArray)?.OfType<JObject>().Where(item =>
            item["isSpecial"]?.Value<bool>() == true &&
            item["isAttribute"]?.Value<bool>() != true &&
            string.Equals(
                item["recycleDisposableEnum"]?.Value<string>(),
                candidate.StationDisposableEnum,
                StringComparison.Ordinal) &&
            string.Equals(item["name"]?.Value<string>(), candidate.StationName, StringComparison.Ordinal) &&
            item.SelectToken("grid.x")?.Value<int?>() == x.Value &&
            item.SelectToken("grid.y")?.Value<int?>() == y.Value).ToList()
            ?? new List<JObject>();
        return matches.Count == 1 &&
               ReadInt(matches[0]["catapultInstanceId"], ReadInt(matches[0]["instanceId"], 0)) !=
               candidate.StationCatapultInstanceId;
    }

    private static double ReadVehicleThroughputScore(
        IEnumerable<JObject> vehicles,
        int railInternalId,
        double railLength,
        int stationCount) =>
        vehicles
            .Where(vehicle => ReadInt(vehicle["railId"], 0) == railInternalId)
            .Where(vehicle => vehicle["running"]?.Value<bool>() == true ||
                              vehicle["queued"]?.Value<bool>() == true)
            .Where(vehicle => ReadInt(vehicle["instanceId"], 0) != 0)
            .GroupBy(vehicle => ReadInt(vehicle["instanceId"], 0))
            .Select(group => group.First())
            .Sum(vehicle =>
            {
                int instanceId = ReadInt(vehicle["instanceId"], 1);
                double baseOutput = ReadDouble(vehicle["baseCombatPower"], 0d);
                if (!(baseOutput > 0d))
                {
                    baseOutput = ReadDouble(
                        vehicle.SelectToken("attackFacts.damage"),
                        ReadDouble(vehicle["damage"], ReadDouble(vehicle["attackDamage"], 0d)));
                }
                if (!(baseOutput > 0d))
                {
                    baseOutput = ReadDouble(
                        vehicle.SelectToken("attackFacts.range"),
                        ReadDouble(vehicle["attackRange"], 0d));
                }
                if (!(baseOutput > 0d)) baseOutput = 1d / (1d + Math.Abs((double)instanceId));

                double speed = ReadDouble(
                    vehicle["configuredSpeed"],
                    ReadDouble(vehicle["currentSpeed"], 0d));
                if (!(speed > 0d)) speed = 1d / (1d + Math.Abs((double)instanceId));
                return baseOutput * speed * Math.Max(1, stationCount) / railLength;
            });

    private static List<AutoPlayerGrid> ReadRailGeometryGrids(JObject rail)
    {
        List<AutoPlayerGrid> ordered = ((rail["orderedStations"] as JArray) ?? (rail["points"] as JArray))?
            .OfType<JObject>()
            .Select(item => TryReadGrid(item["grid"], out AutoPlayerGrid grid)
                ? (AutoPlayerGrid?)grid
                : null)
            .Where(grid => grid.HasValue)
            .Select(grid => grid!.Value)
            .Distinct()
            .ToList() ?? new List<AutoPlayerGrid>();
        if (ordered.Count >= 3)
        {
            return ordered;
        }

        List<AutoPlayerGrid> endpoints = new();
        foreach (JObject line in (rail["lines"] as JArray)?.OfType<JObject>() ?? Enumerable.Empty<JObject>())
        {
            if (TryReadGrid(line["from"], out AutoPlayerGrid from)) endpoints.Add(from);
            if (TryReadGrid(line["to"], out AutoPlayerGrid to)) endpoints.Add(to);
        }
        return endpoints.Distinct().ToList();
    }

    private static IReadOnlyList<bool> ReadRailStationKinds(JObject rail) =>
        (((rail["orderedStations"] as JArray) ?? (rail["points"] as JArray))?
            .OfType<JObject>()
            .Select(item => item["isAttribute"]?.Value<bool>() == true)
            .ToArray()) ?? Array.Empty<bool>();

    private static RailLayoutScore? TryBuildCandidateLayout(
        RailInsertionCandidate candidate,
        JObject? previewResult,
        double cycleSeconds)
    {
        JObject state = State(previewResult);
        JArray? grids = state["predictedStationGrids"] as JArray;
        RailLayoutPoint[] baselinePoints = candidate.OrderedStationGrids
            .Select(ToLayoutPoint)
            .ToArray();
        if (previewResult == null)
        {
            return baselinePoints.Length < 3
                ? null
                : RailLayoutStrategyPlanner.Evaluate(
                    baselinePoints,
                    candidate.StationCount,
                    cycleSeconds);
        }

        RailLayoutPoint[] points;
        if (grids != null && grids.Count >= 3)
        {
            points = grids
                .OfType<JObject>()
                .Where(item => item["x"]?.Type is JTokenType.Integer or JTokenType.Float &&
                               item["y"]?.Type is JTokenType.Integer or JTokenType.Float)
                .Select(item => new RailLayoutPoint(item["x"]!.Value<double>(), item["y"]!.Value<double>()))
                .ToArray();
        }
        else
        {
            points = candidate.PredictedStationGrids
                .Select(ToLayoutPoint)
                .ToArray();
        }
        return points.Length < 3
            ? null
            : RailLayoutStrategyPlanner.Evaluate(
                points,
                candidate.StationCount + (previewResult == null ? 0 : 1),
                cycleSeconds);
    }

    private static RailLayoutPoint ToLayoutPoint(AutoPlayerGrid grid) => new(grid.X, grid.Y);

    private static IReadOnlyList<AutoPlayerGrid> BuildInsertedStationGrids(
        IReadOnlyList<AutoPlayerGrid> ordered,
        AutoPlayerGrid lineFrom,
        AutoPlayerGrid lineTo,
        AutoPlayerGrid station)
    {
        if (ordered.Count < 3 || ordered.Contains(station))
        {
            return Array.Empty<AutoPlayerGrid>();
        }

        for (int index = 0; index < ordered.Count; index++)
        {
            int nextIndex = (index + 1) % ordered.Count;
            bool matches = ordered[index].Equals(lineFrom) && ordered[nextIndex].Equals(lineTo) ||
                           ordered[index].Equals(lineTo) && ordered[nextIndex].Equals(lineFrom);
            if (!matches) continue;

            List<AutoPlayerGrid> result = new(ordered.Count + 1);
            for (int itemIndex = 0; itemIndex < ordered.Count; itemIndex++)
            {
                result.Add(ordered[itemIndex]);
                if (itemIndex == index) result.Add(station);
            }
            return result;
        }

        return Array.Empty<AutoPlayerGrid>();
    }

    private static RailLayoutScore ScoreObservedOrPlannedLayout(
        JObject rail,
        int stationCount,
        double cycleSeconds,
        RailLayoutScore? planned)
    {
        List<AutoPlayerGrid> grids = ReadRailGeometryGrids(rail);
        if (grids.Count >= 3)
        {
            return RailLayoutStrategyPlanner.Evaluate(
                grids.Select(ToLayoutPoint),
                stationCount,
                cycleSeconds);
        }

        if (planned?.IsValid != true || stationCount < 1 || cycleSeconds <= 0d)
        {
            return new RailLayoutScore();
        }

        return new RailLayoutScore
        {
            IsValid = planned.IsValid,
            EncirclesBase = planned.EncirclesBase,
            CoveredQuadrants = planned.CoveredQuadrants,
            AngularCoverageDegrees = planned.AngularCoverageDegrees,
            MaxAngularGapDegrees = planned.MaxAngularGapDegrees,
            AverageRadius = planned.AverageRadius,
            RadiusVariance = planned.RadiusVariance,
            LoopLength = planned.LoopLength,
            StationCount = stationCount,
            LoopCycleSeconds = cycleSeconds,
            TriggerRate = stationCount / cycleSeconds,
            DefenseUtility = planned.DefenseUtility
        };
    }

    private static int ReadStationRailId(JObject station) =>
        ReadInt(
            station["railId"],
            ReadInt(station["lastRailId"], ReadInt(station["currentRailId"], 0)));

    private static string BuildStationFingerprint(JObject station)
    {
        static string JoinValues(JToken? token) => token is JArray values
            ? string.Join(",", values.Values<string>().OrderBy(value => value, StringComparer.Ordinal))
            : string.Empty;

        return string.Join(
            "|",
            station["name"]?.Value<string>() ?? string.Empty,
            station["recycleDisposableEnum"]?.Value<string>() ?? string.Empty,
            station["specialSource"]?.Value<string>() ?? string.Empty,
            station["effectEnum"]?.Value<string>() ?? station["specialEffectEnum"]?.Value<string>() ?? string.Empty,
            JoinValues(station["pointBuffFlags"]),
            JoinValues(station["runtimeBuffIdentities"]),
            JoinValues(station["effectTags"]));
    }

    private static bool MatchesMovableStationKind(JObject station, RailStationMoveCandidate candidate)
    {
        bool isAttribute = station["isAttribute"]?.Value<bool>() == true;
        if (candidate.StationIsAttribute)
        {
            return isAttribute;
        }

        return !isAttribute && station["isSpecial"]?.Value<bool>() == true;
    }

    private static RailStationMoveCandidate CloneWithRuntimeIdentity(
        RailStationMoveCandidate source,
        JObject station,
        AutoPlayerGrid currentGrid) => new()
    {
        RailInstanceId = source.RailInstanceId,
        RailInternalId = source.RailInternalId,
        StationCount = source.StationCount,
        CurrentLoopCycleSeconds = source.CurrentLoopCycleSeconds,
        RailLength = source.RailLength,
        StationCatapultInstanceId = ReadInt(station["catapultInstanceId"], ReadInt(station["instanceId"], 0)),
        StationGameObjectInstanceId = ReadInt(station["gameObjectInstanceId"], 0),
        StationLinePointInstanceId = ReadInt(station["linePointInstanceId"], 0),
        StationPointId = source.StationPointId,
        StationPath = station["path"]?.Value<string>() ?? source.StationPath,
        StationName = station["name"]?.Value<string>() ?? source.StationName,
        StationDisposableEnum = source.StationDisposableEnum,
        StationFingerprint = source.StationFingerprint,
        StationIsAttribute = source.StationIsAttribute,
        SpacingRules = source.SpacingRules,
        CurrentGrid = currentGrid,
        NeighborGrids = source.NeighborGrids,
        OrderedStationGrids = source.OrderedStationGrids,
        OrderedStationKinds = source.OrderedStationKinds,
        OrderedStationPointIds = source.OrderedStationPointIds
    };

    private static bool IsAvailableCommonStation(JObject item) =>
        item["active"]?.Value<bool>() != false &&
        item["canUseForNewRail"]?.Value<bool>() == true &&
        item["canPickLine"]?.Value<bool>() != false &&
        item["frozen"]?.Value<bool>() != true &&
        item["railReachMax"]?.Value<bool>() != true &&
        item["isAttribute"]?.Value<bool>() != true &&
        ReadInt(item["railMembershipCount"], 0) == 0;

    private static bool TryReadRailMap(JObject? result, out Dictionary<int, JObject> rails)
    {
        rails = new Dictionary<int, JObject>();
        List<JObject> items = (State(result)["rails"] as JArray)?.OfType<JObject>().ToList()
                              ?? new List<JObject>();
        foreach (JObject rail in items)
        {
            int id = ReadInt(rail["instanceId"], 0);
            if (id == 0 || rails.ContainsKey(id))
            {
                rails.Clear();
                return false;
            }

            rails[id] = rail;
        }

        return items.Count > 0;
    }

    private static bool RailContainsPoint(JObject rail, int pointInstanceId) =>
        ((rail["orderedStations"] as JArray) ?? (rail["points"] as JArray))?
        .OfType<JObject>()
        .Any(point => ReadInt(point["linePointInstanceId"], ReadInt(point["instanceId"], 0)) == pointInstanceId) == true;

    private static bool TryReadStablePointId(JObject rail, int linePointInstanceId, out int pointId)
    {
        pointId = 0;
        JObject[] matches = ((rail["orderedStations"] as JArray) ?? (rail["points"] as JArray))?
            .OfType<JObject>()
            .Where(point => ReadInt(point["linePointInstanceId"], ReadInt(point["instanceId"], 0)) == linePointInstanceId)
            .ToArray() ?? Array.Empty<JObject>();
        if (matches.Length != 1) return false;
        JToken? stableToken = matches[0]["pointId"];
        if (stableToken?.Type == JTokenType.Integer)
        {
            pointId = stableToken.Value<int>();
            return true;
        }
        pointId = ReadInt(matches[0]["linePointInstanceId"], ReadInt(matches[0]["instanceId"], 0));
        return pointId != 0;
    }

    private static IReadOnlyList<int> ReadRailStablePointIds(JObject rail) =>
        ((rail["orderedStations"] as JArray) ?? (rail["points"] as JArray))?
        .OfType<JObject>()
        .Select(point => point["pointId"]?.Type == JTokenType.Integer
            ? point["pointId"]!.Value<int>()
            : ReadInt(point["linePointInstanceId"], ReadInt(point["instanceId"], 0)))
        .ToArray() ?? Array.Empty<int>();

    private static bool TryReadPointIdentitySequence(JObject rail, out int[] pointIds)
    {
        pointIds = ((rail["orderedStations"] as JArray) ?? (rail["points"] as JArray))?
            .OfType<JObject>()
            .Select(point => ReadInt(point["linePointInstanceId"], ReadInt(point["instanceId"], 0)))
            .ToArray() ?? Array.Empty<int>();
        return pointIds.Length > 0 && pointIds.All(id => id != 0);
    }

    private static bool TryReadGrid(JToken? token, out AutoPlayerGrid grid)
    {
        grid = default;
        if (token is not JObject value ||
            value["x"]?.Type != JTokenType.Integer ||
            value["y"]?.Type != JTokenType.Integer)
        {
            return false;
        }

        grid = new AutoPlayerGrid(value["x"]!.Value<int>(), value["y"]!.Value<int>());
        return true;
    }

    private static double Distance(AutoPlayerGrid left, AutoPlayerGrid right)
    {
        double x = left.X - right.X;
        double y = left.Y - right.Y;
        return Math.Sqrt(x * x + y * y);
    }

    private static RailInsertionVerification Failure(string detail) => new() { Detail = detail };

    private static JObject State(JObject? result) =>
        result?.SelectToken("data.state") as JObject
        ?? result?["state"] as JObject
        ?? result
        ?? new JObject();

    private static int ReadInt(JToken? token, int fallback)
    {
        if (token?.Type == JTokenType.Integer)
        {
            return token.Value<int>();
        }

        return int.TryParse(token?.Value<string>(), out int value) ? value : fallback;
    }

    private static double ReadDouble(JToken? token, double fallback)
    {
        if (token?.Type is JTokenType.Integer or JTokenType.Float)
        {
            double value = token.Value<double>();
            return IsFinite(value) ? value : fallback;
        }

        return double.TryParse(
            token?.Value<string>(),
            NumberStyles.Float,
            CultureInfo.InvariantCulture,
            out double parsed) && IsFinite(parsed)
            ? parsed
            : fallback;
    }

    private static bool TryReadPositiveDouble(JToken? token, out double value)
    {
        value = 0d;
        if (token?.Type is JTokenType.Integer or JTokenType.Float)
        {
            value = token.Value<double>();
        }
        else if (!double.TryParse(
                     token?.Value<string>(),
                     NumberStyles.Float,
                     CultureInfo.InvariantCulture,
                     out value))
        {
            return false;
        }

        return value > 0d && IsFinite(value);
    }

    private static bool IsFinite(double value) => !double.IsNaN(value) && !double.IsInfinity(value);

    private static bool ApproximatelyEqual(double left, double right, double relativeTolerance)
    {
        double scale = Math.Max(Math.Abs(left), Math.Abs(right));
        return Math.Abs(left - right) <= Math.Max(0.0001d, scale * relativeTolerance);
    }

    private static bool SameRailStructure(JObject baseline, JObject current)
    {
        int baselineCount = ReadInt(baseline["stationCount"], ReadInt(baseline["pointCount"], -1));
        int currentCount = ReadInt(current["stationCount"], ReadInt(current["pointCount"], -1));
        if (baselineCount < 3 || currentCount != baselineCount ||
            !TryReadPointIdentitySequence(baseline, out int[] baselinePointIds) ||
            !TryReadPointIdentitySequence(current, out int[] currentPointIds) ||
            !baselinePointIds.SequenceEqual(currentPointIds))
        {
            return false;
        }

        List<AutoPlayerGrid> baselineGrids = ReadRailGeometryGrids(baseline);
        List<AutoPlayerGrid> currentGrids = ReadRailGeometryGrids(current);
        if (baselineGrids.Count >= 3 || currentGrids.Count >= 3)
        {
            return baselineGrids.SequenceEqual(currentGrids);
        }

        return TryReadPositiveDouble(baseline["railLength"], out double baselineLength) &&
               TryReadPositiveDouble(current["railLength"], out double currentLength) &&
               ApproximatelyEqual(baselineLength, currentLength, 0.001d);
    }
}

public static class DefenseStationGridRanker
{
    public static IReadOnlyList<AutoPlayerGrid> RankPlacement(
        string disposableEnum,
        IEnumerable<AutoPlayerGrid>? candidates,
        JObject? catapultResult,
        StationSpacingRules spacingRules = default,
        bool? placementIsAttribute = null)
    {
        List<AutoPlayerGrid> source = candidates?.Distinct().ToList() ?? new List<AutoPlayerGrid>();
        JObject state = catapultResult?.SelectToken("data.state") as JObject
                        ?? catapultResult?["state"] as JObject
                        ?? catapultResult
                        ?? new JObject();
        List<JObject> points = (state["catapults"] as JArray)?.OfType<JObject>().ToList()
                               ?? new List<JObject>();
        bool targetIsAttribute = placementIsAttribute ??
                                 string.Equals(disposableEnum, "FreePoint_Attribute", StringComparison.Ordinal);
        bool targetMustBeIncluded = !string.Equals(disposableEnum, "FreePoint", StringComparison.Ordinal) &&
                                    !string.Equals(disposableEnum, "FreePoint_Attribute", StringComparison.Ordinal);
        List<(double X, double Y)> anchors;
        if (!targetIsAttribute)
        {
            List<(double X, double Y)> attributes = points
                .Where(point => point["isAttribute"]?.Value<bool>() == true)
                .Where(IsAvailable)
                .Select(point => ReadGrid(point["grid"] as JObject))
                .Where(grid => grid.HasValue)
                .Select(grid => grid!.Value)
                .ToList();
            List<(double X, double Y)> commons = points
                .Where(point => point["isAttribute"]?.Value<bool>() != true)
                .Where(IsAvailable)
                .Select(point => ReadGrid(point["grid"] as JObject))
                .Where(grid => grid.HasValue)
                .Select(grid => grid!.Value)
                .ToList();
            anchors = attributes.Concat(commons).ToList();
            if (attributes.Count > 0)
            {
                return source
                    .Select(grid => new
                    {
                        Grid = grid,
                        Layout = ScoreProspectivePlayerLoop(
                            attributes,
                            commons,
                            grid,
                            targetMustBeIncluded),
                        FirstPointAngularPenalty = commons.Count == 0
                            ? attributes.Min(attribute => FirstCommonAngularPenalty(attribute, grid))
                            : 0d,
                        RadiusDelta = commons.Count == 0
                            ? attributes.Min(attribute => Math.Abs(
                                Radius(grid) - Math.Sqrt(attribute.X * attribute.X + attribute.Y * attribute.Y)))
                            : 0d,
                        RadiusRatio = commons.Count == 0
                            ? attributes.Max(attribute => RadiusRatio(
                                Radius(grid),
                                Math.Sqrt(attribute.X * attribute.X + attribute.Y * attribute.Y)))
                            : 1d,
                        Distance = anchors.Min(anchor =>
                            DistanceSquared(grid.X, grid.Y, anchor.X, anchor.Y))
                    })
                    .Where(item =>
                        item.RadiusRatio <= 2.5d + 0.000001d &&
                        (commons.Count == 0 || RailLayoutStrategyPlanner.IsBalancedDefenseRing(item.Layout)))
                    .OrderByDescending(item =>
                        item.Layout?.IsValid == true &&
                        item.Layout.IsSimpleCycle &&
                        item.Layout.EncirclesBase)
                    .ThenBy(
                        item => item.Layout,
                        Comparer<RailLayoutScore?>.Create(RailLayoutStrategyPlanner.CompareCoverage))
                    .ThenBy(item => item.RadiusDelta)
                    .ThenBy(item => item.FirstPointAngularPenalty)
                    .ThenBy(item => item.Distance)
                    .ThenBy(item => item.Grid.X)
                    .ThenBy(item => item.Grid.Y)
                    .Select(item => item.Grid)
                    .ToArray();
            }
        }
        else
        {
            anchors = points
                .Where(point => targetIsAttribute
                    ? point["isAttribute"]?.Value<bool>() != true && IsAvailable(point)
                    : point["active"]?.Value<bool>() != false)
                .Select(point => ReadGrid(point["grid"] as JObject))
                .Where(grid => grid.HasValue)
                .Select(grid => grid!.Value)
                .ToList();
        }

        // The first opening attribute station fixes the radius band available to every later
        // station. Choosing the nearest legal cell is unsafe because every confirmed placement
        // removes its spacing neighbourhood from MapPosManager's live candidate pools. Reserve
        // one full station-spacing band outside the innermost legal radius before any write.
        if (targetIsAttribute && anchors.Count == 0)
        {
            double targetRadius = OpeningRingTargetRadius(source, spacingRules);
            return source
                .Select(grid => new
                {
                    Grid = grid,
                    RadiusDelta = Math.Abs(Radius(grid) - targetRadius),
                    Radius = Radius(grid),
                    Angle = StablePolarAngle(grid)
                })
                .OrderBy(item => item.RadiusDelta)
                .ThenBy(item => item.Radius)
                .ThenBy(item => item.Angle)
                .ThenBy(item => item.Grid.X)
                .ThenBy(item => item.Grid.Y)
                .Select(item => item.Grid)
                .ToArray();
        }

        return source
            .Select(grid => new
            {
                Grid = grid,
                Layout = RailLayoutStrategyPlanner.EvaluateEstimated(
                    anchors.Select(anchor => new RailLayoutPoint(anchor.X, anchor.Y))
                        .Append(new RailLayoutPoint(grid.X, grid.Y))),
                SpacingSurplus = spacingRules.IsKnown
                    ? NearestLegalSpacingSurplus(grid, targetIsAttribute, points, spacingRules)
                    : 0d,
                Distance = anchors.Count == 0
                    ? (double)grid.X * grid.X + (double)grid.Y * grid.Y
                    : anchors.Min(anchor => DistanceSquared(grid.X, grid.Y, anchor.X, anchor.Y))
            })
            .Where(item => item.SpacingSurplus >= -0.000001d)
            .OrderBy(
                item => item.Layout,
                Comparer<RailLayoutScore>.Create(RailLayoutStrategyPlanner.CompareCoverage))
            .ThenBy(item => item.SpacingSurplus)
            .ThenBy(item => item.Distance)
            .ThenBy(item => item.Grid.X)
            .ThenBy(item => item.Grid.Y)
            .Select(item => item.Grid)
            .ToArray();
    }

    private static RailLayoutScore? ScoreProspectivePlayerLoop(
        IReadOnlyList<(double X, double Y)> attributes,
        IReadOnlyList<(double X, double Y)> commons,
        AutoPlayerGrid candidate,
        bool candidateMustBeIncluded)
    {
        if (attributes.Count == 0 || commons.Count == 0) return null;
        List<RailLoopPointCandidate> points = new();
        int identity = 1;
        foreach ((double x, double y) in attributes)
        {
            points.Add(new RailLoopPointCandidate
            {
                InstanceId = identity++,
                IsAttribute = true,
                Grid = new RailLayoutPoint(x, y)
            });
        }
        foreach ((double x, double y) in commons)
        {
            points.Add(new RailLoopPointCandidate
            {
                InstanceId = identity++,
                Grid = new RailLayoutPoint(x, y)
            });
        }
        points.Add(new RailLoopPointCandidate
        {
            InstanceId = identity,
            MustInclude = candidateMustBeIncluded,
            Grid = new RailLayoutPoint(candidate.X, candidate.Y)
        });
        return RailLayoutStrategyPlanner.PlanPlayerLoop(points)?.Score;
    }

    private static double FirstCommonAngularPenalty(
        (double X, double Y) attribute,
        AutoPlayerGrid candidate)
    {
        double attributeAngle = Math.Atan2(attribute.Y, attribute.X);
        double candidateAngle = Math.Atan2(candidate.Y, candidate.X);
        double separation = Math.Abs(attributeAngle - candidateAngle);
        if (separation > Math.PI) separation = Math.PI * 2d - separation;
        return Math.Abs(separation - Math.PI * 2d / 3d);
    }

    private static double OpeningRingTargetRadius(
        IReadOnlyCollection<AutoPlayerGrid> candidates,
        StationSpacingRules spacingRules)
    {
        if (candidates.Count == 0) return 0d;
        double innerLegalRadius = candidates.Min(Radius);
        double reservedSpacing = spacingRules.IsKnown
            ? Math.Max(spacingRules.OrdinaryMinimum, spacingRules.EnergyMinimum)
            : 2d;
        return innerLegalRadius + reservedSpacing;
    }

    private static double Radius(AutoPlayerGrid grid) =>
        Math.Sqrt((double)grid.X * grid.X + (double)grid.Y * grid.Y);

    private static double RadiusRatio(double left, double right)
    {
        double minimum = Math.Min(left, right);
        return minimum <= 0.000001d
            ? double.PositiveInfinity
            : Math.Max(left, right) / minimum;
    }

    private static double StablePolarAngle(AutoPlayerGrid grid)
    {
        double angle = Math.Atan2(grid.Y, grid.X);
        return angle < 0d ? angle + Math.PI * 2d : angle;
    }

    public static IReadOnlyList<AutoPlayerGrid> RankMove(
        IEnumerable<AutoPlayerGrid>? candidates,
        JObject? railResult,
        int lineInstanceId,
        AutoPlayerGrid currentGrid)
    {
        JObject state = railResult?.SelectToken("data.state") as JObject
                        ?? railResult?["state"] as JObject
                        ?? railResult
                        ?? new JObject();
        JObject? line = (state["rails"] as JArray)?.OfType<JObject>()
            .SelectMany(rail => (rail["lines"] as JArray)?.OfType<JObject>() ?? Enumerable.Empty<JObject>())
            .SingleOrDefault(item =>
                ReadInt(item["lineInstanceId"], ReadInt(item["instanceId"], 0)) == lineInstanceId);
        (double X, double Y)? from = ReadGrid(line?["from"] as JObject);
        (double X, double Y)? to = ReadGrid(line?["to"] as JObject);
        if (!from.HasValue || !to.HasValue)
        {
            return Array.Empty<AutoPlayerGrid>();
        }

        double currentDetour = Detour(currentGrid.X, currentGrid.Y, from.Value, to.Value);
        return (candidates ?? Enumerable.Empty<AutoPlayerGrid>())
            .Distinct()
            .Where(grid => !grid.Equals(currentGrid))
            .Select(grid => new
            {
                Grid = grid,
                Detour = Detour(grid.X, grid.Y, from.Value, to.Value)
            })
            .Where(item => item.Detour + 0.001d < currentDetour)
            .OrderBy(item => item.Detour)
            .ThenBy(item => item.Grid.X)
            .ThenBy(item => item.Grid.Y)
            .Select(item => item.Grid)
            .ToArray();
    }

    public static IReadOnlyList<AutoPlayerGrid> RankExistingStationMove(
        IEnumerable<AutoPlayerGrid>? candidates,
        RailStationMoveCandidate candidate)
    {
        RailExpansionPlanner planner = new();
        return (candidates ?? Enumerable.Empty<AutoPlayerGrid>())
            .Distinct()
            .Where(grid => !grid.Equals(candidate.CurrentGrid))
            .Select(grid => new
            {
                Grid = grid,
                Layout = planner.ScoreMovedLayout(candidate, grid)
            })
            .Where(item => planner.IsBeneficialMove(candidate, item.Grid))
            .OrderBy(
                item => item.Layout,
                Comparer<RailLayoutScore>.Create(RailLayoutStrategyPlanner.CompareForDefense))
            .ThenBy(item => item.Grid.X)
            .ThenBy(item => item.Grid.Y)
            .Select(item => item.Grid)
            .ToArray();
    }

    private static bool IsAvailable(JObject item) =>
        item["active"]?.Value<bool>() != false &&
        item["canUseForNewRail"]?.Value<bool>() == true &&
        item["frozen"]?.Value<bool>() != true &&
        item["railReachMax"]?.Value<bool>() != true &&
        ReadInt(item["railMembershipCount"], 0) == 0;

    private static (double X, double Y)? ReadGrid(JObject? grid)
    {
        if (grid?["x"]?.Type is not (JTokenType.Integer or JTokenType.Float) ||
            grid["y"]?.Type is not (JTokenType.Integer or JTokenType.Float))
        {
            return null;
        }

        return (grid["x"]!.Value<double>(), grid["y"]!.Value<double>());
    }

    private static double Detour(
        double x,
        double y,
        (double X, double Y) from,
        (double X, double Y) to) =>
        Math.Sqrt(DistanceSquared(from.X, from.Y, x, y)) +
        Math.Sqrt(DistanceSquared(x, y, to.X, to.Y)) -
        Math.Sqrt(DistanceSquared(from.X, from.Y, to.X, to.Y));

    private static double DistanceSquared(double x1, double y1, double x2, double y2)
    {
        double dx = x1 - x2;
        double dy = y1 - y2;
        return dx * dx + dy * dy;
    }

    private static double NearestLegalSpacingSurplus(
        AutoPlayerGrid grid,
        bool targetIsAttribute,
        IEnumerable<JObject> points,
        StationSpacingRules rules)
    {
        double best = double.PositiveInfinity;
        foreach (JObject point in points.Where(item => item["active"]?.Value<bool>() != false))
        {
            (double X, double Y)? existing = ReadGrid(point["grid"] as JObject);
            if (!existing.HasValue) continue;
            double minimum = rules.MinimumFor(targetIsAttribute, point["isAttribute"]?.Value<bool>() == true);
            double distance = Math.Sqrt(DistanceSquared(
                grid.X,
                grid.Y,
                existing.Value.X,
                existing.Value.Y));
            if (distance + 0.000001d < minimum) return -1d;
            best = Math.Min(best, distance - minimum);
        }
        return double.IsPositiveInfinity(best) ? 0d : best;
    }

    private static int ReadInt(JToken? token, int fallback) =>
        token?.Type == JTokenType.Integer && token.Value<int>() is int value ? value : fallback;
}

/// <summary>Write-once ledger for structural defense mutations reconciled by later read-only snapshots.</summary>
public sealed class PendingDefenseMutationGuard
{
    public bool IsArmed { get; private set; }
    public string Command { get; private set; } = string.Empty;
    public string MutationIdentity { get; private set; } = string.Empty;
    public float StartedAt { get; private set; } = -1f;
    public bool OutcomeUnknown { get; private set; }
    public bool InvocationIssued { get; private set; }

    public bool TryArm(AutomationAction? action, string? mutationIdentity, float now)
    {
        string command = action?.Command?.Trim() ?? string.Empty;
        if (IsArmed || action == null || string.IsNullOrWhiteSpace(command) ||
            string.IsNullOrWhiteSpace(mutationIdentity))
        {
            return false;
        }

        IsArmed = true;
        Command = command;
        MutationIdentity = mutationIdentity!.Trim();
        StartedAt = now;
        OutcomeUnknown = false;
        InvocationIssued = false;
        return true;
    }

    public bool TryAdvance(AutomationAction? action, string? mutationIdentity, float now)
    {
        string command = action?.Command?.Trim() ?? string.Empty;
        if (!IsArmed || !InvocationIssued || action == null || string.IsNullOrWhiteSpace(command) ||
            string.IsNullOrWhiteSpace(mutationIdentity))
        {
            return false;
        }

        Command = command;
        MutationIdentity = mutationIdentity!.Trim();
        StartedAt = now;
        OutcomeUnknown = false;
        InvocationIssued = false;
        return true;
    }

    public bool IsPreparedFor(AutomationAction action, string mutationIdentity) =>
        IsArmed &&
        !InvocationIssued &&
        string.Equals(Command, action.Command, StringComparison.Ordinal) &&
        string.Equals(MutationIdentity, mutationIdentity, StringComparison.Ordinal);

    public void MarkInvocationIssued()
    {
        if (IsArmed)
        {
            InvocationIssued = true;
        }
    }

    public void MarkOutcomeUnknown()
    {
        if (IsArmed)
        {
            OutcomeUnknown = true;
        }
    }

    public bool HasTimedOut(float now, float timeoutSeconds) =>
        IsArmed && now - StartedAt >= Math.Max(0.1f, timeoutSeconds);

    public void Reset()
    {
        IsArmed = false;
        Command = string.Empty;
        MutationIdentity = string.Empty;
        StartedAt = -1f;
        OutcomeUnknown = false;
        InvocationIssued = false;
    }
}
