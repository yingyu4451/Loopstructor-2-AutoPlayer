using System;
using System.Collections.Generic;
using System.Linq;
using Newtonsoft.Json.Linq;

namespace Loopstructor.AutoPlayer.Core;

public sealed class RailRebuildSnapshot
{
    public int RailInstanceId { get; set; }
    public int RailInternalId { get; set; }
    public int OriginLinePointInstanceId { get; set; }
    public int OriginPointId { get; set; }
    public IReadOnlyList<int> OriginalOrderedLinePointInstanceIds { get; set; } = Array.Empty<int>();
    public IReadOnlyList<int> OrderedLinePointInstanceIds { get; set; } = Array.Empty<int>();
    public IReadOnlyList<int> OrderedPointIds { get; set; } = Array.Empty<int>();
    public IReadOnlyList<int> RunningVehicleInstanceIds { get; set; } = Array.Empty<int>();
    public IReadOnlyList<int> WaitingVehicleInstanceIds { get; set; } = Array.Empty<int>();
    public IReadOnlyList<int> VehicleInstanceIds { get; set; } = Array.Empty<int>();
    public IReadOnlyList<int> VehicleBusinessIds { get; set; } = Array.Empty<int>();
    public double LoopCycleSeconds { get; set; }
    public double EstimatedDetour { get; set; }
}

public sealed class RailRebuildVerification
{
    public bool Verified { get; set; }
    public bool Pending { get; set; }
    public bool ExplicitStatePolluted { get; set; }
    public bool VehiclesRestored { get; set; }
    public double LoopCycleSeconds { get; set; }
    public string Detail { get; set; } = string.Empty;
}

/// <summary>
/// Pure transaction facts for player-equivalent origin disconnect and reconnect. The controller
/// owns all writes; this class only captures immutable identities and reconciles read-only state.
/// </summary>
public sealed class RailRebuildTransactionPlanner
{
    public RailRebuildSnapshot? Capture(JObject? railResult, int railInstanceId, JObject? vehicleStateResult = null)
    {
        JObject? rail = Rails(railResult).SingleOrDefault(item =>
            ReadInt(item["instanceId"]) == railInstanceId);
        if (rail == null || rail["isLegalPlayerLoop"]?.Value<bool>() != true) return null;

        JObject[] stations = ((rail["orderedStations"] as JArray) ?? (rail["points"] as JArray))?
            .OfType<JObject>().ToArray() ?? Array.Empty<JObject>();
        int[] ordered = stations.Select(item => ReadInt(item["linePointInstanceId"], ReadInt(item["instanceId"])))
            .Where(id => id != 0).ToArray();
        int origin = stations.Where(item => item["isAttribute"]?.Value<bool>() == true)
            .Select(item => ReadInt(item["linePointInstanceId"], ReadInt(item["instanceId"])))
            .FirstOrDefault();
        JObject? originStation = stations.FirstOrDefault(item => item["isAttribute"]?.Value<bool>() == true);
        int originPointId = originStation == null ? 0 : ReadStablePointId(originStation);
        int[] stablePointIds = stations.Select(ReadStablePointId).ToArray();
        if (origin == 0 || originStation == null || ordered.Length < 3 || ordered[0] != origin ||
            stablePointIds.Length != ordered.Length) return null;

        JObject independentState = State(vehicleStateResult);
        JObject? capacityRail = (independentState["rails"] as JArray)?.OfType<JObject>()
            .SingleOrDefault(item =>
                ReadInt(item["instanceId"], ReadInt(item["railInstanceId"])) == railInstanceId);
        int[] runningVehicles = (capacityRail?["runningVehicleIds"] as JArray)?.Values<int>().ToArray()
                                ?? Array.Empty<int>();
        int[] waitingVehicles = (capacityRail?["waitingVehicleIds"] as JArray)?.Values<int>().ToArray()
                                ?? Array.Empty<int>();
        int[] allVehicles = runningVehicles.Concat(waitingVehicles).Distinct().OrderBy(id => id).ToArray();
        return new RailRebuildSnapshot
        {
            RailInstanceId = railInstanceId,
            RailInternalId = ReadInt(rail["railInternalId"], ReadInt(rail["id"])),
            OriginLinePointInstanceId = origin,
            OriginPointId = originPointId,
            OriginalOrderedLinePointInstanceIds = ordered,
            OrderedLinePointInstanceIds = ordered,
            OrderedPointIds = stablePointIds,
            RunningVehicleInstanceIds = runningVehicles.Distinct().OrderBy(id => id).ToArray(),
            WaitingVehicleInstanceIds = waitingVehicles,
            VehicleInstanceIds = allVehicles,
            VehicleBusinessIds = ReadVehicleBusinessIds(independentState, allVehicles),
            LoopCycleSeconds = rail["loopCycleSeconds"]?.Value<double?>() ?? 0d
        };
    }

    public AutomationAction BuildDisconnectAction(RailRebuildSnapshot snapshot) => new(
        "deleteLinePoint",
        JObject.FromObject(new
        {
            instanceId = snapshot.OriginLinePointInstanceId,
            railInternalId = snapshot.RailInternalId
        }),
        AutomationStage.Battle,
        "通过始发站的玩家右键语义断开闭环，由游戏容量服务接管运行与等待中的独立战车。");

    public bool ApplyStablePointOrder(
        RailRebuildSnapshot snapshot,
        JObject? railResult,
        IReadOnlyList<int> orderedStablePointIds)
    {
        if (snapshot == null || orderedStablePointIds == null || orderedStablePointIds.Count < 3)
            return false;
        JObject? rail = Rails(railResult).SingleOrDefault(item =>
            ReadInt(item["instanceId"]) == snapshot.RailInstanceId);
        JObject[] stations = ((rail?["orderedStations"] as JArray) ?? (rail?["points"] as JArray))?
            .OfType<JObject>().ToArray() ?? Array.Empty<JObject>();
        Dictionary<int, int> instanceByStableId = stations
            .Select(item => new
            {
                StableId = ReadStablePointId(item),
                InstanceId = ReadInt(item["linePointInstanceId"], ReadInt(item["instanceId"]))
            })
            .Where(item => item.InstanceId != 0)
            .GroupBy(item => item.StableId)
            .ToDictionary(group => group.Key, group => group.First().InstanceId);
        if (orderedStablePointIds.Distinct().Count() != orderedStablePointIds.Count ||
            orderedStablePointIds.Any(id => !instanceByStableId.ContainsKey(id))) return false;
        int[] orderedInstances = orderedStablePointIds.Select(id => instanceByStableId[id]).ToArray();
        if (orderedInstances[0] != snapshot.OriginLinePointInstanceId) return false;
        snapshot.OrderedPointIds = orderedStablePointIds.ToArray();
        snapshot.OrderedLinePointInstanceIds = orderedInstances;
        if (rail?["lines"] is JArray lines && lines.Count == stations.Length)
        {
            RailRuntimeValidation baseline = RailRuntimeTopologyInspector.InspectRail(rail);
            if (!baseline.Loop.IsValid)
            {
                // A malformed baseline must never be restored after a failed repair attempt.
                snapshot.OriginalOrderedLinePointInstanceIds = orderedInstances;
            }
        }
        return true;
    }

    public AutomationAction BuildPreviewAction(RailRebuildSnapshot snapshot)
    {
        JObject arguments = new()
        {
            ["linePointInstanceIds"] = new JArray(snapshot.OrderedLinePointInstanceIds)
        };
        return new AutomationAction(
            "previewRailPath",
            arguments,
            AutomationStage.Battle,
            "只读预览从同一始发站重新闭环后的合法性与真实回转周期。");
    }

    public AutomationAction BuildDrawAction(RailRebuildSnapshot snapshot) => new(
        "drawRailPath",
        new JObject { ["linePointInstanceIds"] = new JArray(snapshot.OrderedLinePointInstanceIds) },
        AutomationStage.Battle,
        "从始发站依次连接全部目标站点并回到始发站，由游戏容量服务恢复独立战车。");

    public RailRebuildVerification VerifyDisconnect(JObject? deleteResult, RailRebuildSnapshot snapshot)
    {
        JObject state = State(deleteResult);
        bool polluted = state["statePolluted"]?.Value<bool>() == true ||
                        state.SelectToken("deletionOutcome.statePolluted")?.Value<bool>() == true;
        if (polluted) return Failure("始发站断环运行时明确报告状态污染。", true);
        if (state["railDeleted"]?.Value<bool>() != true)
            return new RailRebuildVerification { Pending = true, Detail = "尚未观察到始发站闭环已断开。" };

        JObject? outcome = state["deletionOutcome"] as JObject;
        JObject[] stashedStates = (outcome?["stashedVehicles"] as JArray)?.OfType<JObject>().ToArray()
                                  ?? Array.Empty<JObject>();
        int[] stashedVehicles = stashedStates
            .Select(item => ReadInt(item.SelectToken("vehicle.instanceId")))
            .Where(id => id != 0).Distinct().OrderBy(id => id).ToArray();
        int[] stashedBusinessIds = stashedStates
            .Select(item => ReadInt(item["vehicleId"]))
            .Where(id => id != 0).Distinct().OrderBy(id => id).ToArray();
        if (stashedBusinessIds.Except(snapshot.VehicleBusinessIds).Any() ||
            stashedVehicles.Except(snapshot.VehicleInstanceIds).Any())
            return Failure("始发站缓存出现断环前快照之外的战车身份。", false);

        return new RailRebuildVerification { Verified = true, Detail = "闭环已断开，独立战车身份由游戏容量服务接管。" };
    }

    public bool IsLegalPreview(JObject? previewResult, RailRebuildSnapshot snapshot, out double cycle)
    {
        JObject state = State(previewResult);
        cycle = state["predictedLoopCycleSeconds"]?.Value<double?>() ?? 0d;
        return state["wouldBeLegal"]?.Value<bool>() == true &&
               state["sideEffectCheckPassed"]?.Value<bool>() == true &&
               state["statePolluted"]?.Value<bool>() != true;
    }

    public void RefreshMovedStationIdentity(
        RailRebuildSnapshot snapshot,
        int oldLinePointInstanceId,
        int newLinePointInstanceId)
    {
        if (oldLinePointInstanceId == 0 || newLinePointInstanceId == 0) return;
        snapshot.OrderedLinePointInstanceIds = snapshot.OrderedLinePointInstanceIds
            .Select(id => id == oldLinePointInstanceId ? newLinePointInstanceId : id)
            .ToArray();
        snapshot.OriginalOrderedLinePointInstanceIds = snapshot.OriginalOrderedLinePointInstanceIds
            .Select(id => id == oldLinePointInstanceId ? newLinePointInstanceId : id)
            .ToArray();
        if (snapshot.OriginLinePointInstanceId == oldLinePointInstanceId)
            snapshot.OriginLinePointInstanceId = newLinePointInstanceId;
    }

    public RailRebuildSnapshot BuildInsertionSnapshot(
        RailRebuildSnapshot snapshot,
        int newLinePointInstanceId,
        int insertAfterIndex)
    {
        if (snapshot == null) throw new ArgumentNullException(nameof(snapshot));
        if (newLinePointInstanceId == 0) throw new ArgumentOutOfRangeException(nameof(newLinePointInstanceId));
        List<int> ordered = snapshot.OrderedLinePointInstanceIds.ToList();
        int insertionIndex = Math.Max(1, Math.Min(insertAfterIndex + 1, ordered.Count));
        ordered.Insert(insertionIndex, newLinePointInstanceId);
        return new RailRebuildSnapshot
        {
            RailInstanceId = snapshot.RailInstanceId,
            RailInternalId = snapshot.RailInternalId,
            OriginLinePointInstanceId = snapshot.OriginLinePointInstanceId,
            OriginPointId = snapshot.OriginPointId,
            OriginalOrderedLinePointInstanceIds = snapshot.OriginalOrderedLinePointInstanceIds,
            OrderedLinePointInstanceIds = ordered,
            OrderedPointIds = snapshot.OrderedPointIds,
            RunningVehicleInstanceIds = snapshot.RunningVehicleInstanceIds,
            WaitingVehicleInstanceIds = snapshot.WaitingVehicleInstanceIds,
            VehicleInstanceIds = snapshot.VehicleInstanceIds,
            VehicleBusinessIds = snapshot.VehicleBusinessIds,
            LoopCycleSeconds = snapshot.LoopCycleSeconds,
            EstimatedDetour = snapshot.EstimatedDetour
        };
    }

    public IReadOnlyList<RailRebuildSnapshot> BuildSpecialInsertionCandidates(
        JObject? railResult,
        JObject? independentVehicleState,
        JObject? catapultResult) =>
        BuildUnassignedInsertionCandidates(
            railResult,
            independentVehicleState,
            catapultResult,
            movableSpecialOnly: true);

    public IReadOnlyList<RailRebuildSnapshot> BuildUnassignedInsertionCandidates(
        JObject? railResult,
        JObject? independentVehicleState,
        JObject? catapultResult) =>
        BuildUnassignedInsertionCandidates(
            railResult,
            independentVehicleState,
            catapultResult,
            movableSpecialOnly: false);

    /// <summary>
    /// Reorders an invalid existing loop together with every compatible unassigned common station.
    /// This is used after a fixed-position map station cannot be inserted edge-by-edge without
    /// crossing the loop. The attribute/energy point remains the unique first point, while the
    /// game preview still decides whether the complete player-equivalent redraw is legal.
    /// </summary>
    public RailRebuildSnapshot? BuildFullTopologyRepair(
        JObject? railResult,
        JObject? independentVehicleState,
        JObject? catapultResult) =>
        BuildFullTopologyRepair(railResult, independentVehicleState, catapultResult, out _);

    public RailRebuildSnapshot? BuildFullTopologyRepair(
        JObject? railResult,
        JObject? independentVehicleState,
        JObject? catapultResult,
        out string detail)
    {
        List<string> diagnostics = new();
        JObject[] unassignedCommons = (State(catapultResult)["catapults"] as JArray)?
            .OfType<JObject>()
            .Where(item => item["active"]?.Value<bool>() != false &&
                           item["isAttribute"]?.Value<bool>() != true &&
                           item["canUseForNewRail"]?.Value<bool>() == true &&
                           item["canPickLine"]?.Value<bool>() != false &&
                           item["frozen"]?.Value<bool>() != true &&
                           item["railReachMax"]?.Value<bool>() != true &&
                           ReadInt(item["railMembershipCount"]) == 0 &&
                           ReadInt(item["linePointInstanceId"]) != 0)
            .OrderBy(item => ReadInt(item["linePointInstanceId"]))
            .ToArray() ?? Array.Empty<JObject>();
        int primaryRailInternalId = Rails(railResult)
            .Select(item => ReadInt(
                item["railInternalId"],
                ReadInt(item["id"], int.MaxValue)))
            .DefaultIfEmpty(int.MaxValue)
            .Min();

        List<(RailRebuildSnapshot Snapshot, double Length)> repairs = new();
        foreach (JObject rail in Rails(railResult)
                     .OrderBy(item => ReadInt(item["railInternalId"], ReadInt(item["id"], int.MaxValue))))
        {
            if (RailRuntimeTopologyInspector.InspectRail(rail).IsDefenseValid) continue;
            RailRebuildSnapshot? snapshot = Capture(
                railResult,
                ReadInt(rail["instanceId"]),
                independentVehicleState) ?? CaptureMalformedForFullRepair(rail, independentVehicleState);
            if (snapshot == null)
            {
                diagnostics.Add($"轨道 {ReadInt(rail["instanceId"])} 无法锁定唯一能量点或战车快照。");
                continue;
            }

            JObject[] existing = ((rail["orderedStations"] as JArray) ?? (rail["points"] as JArray))?
                .OfType<JObject>().ToArray() ?? Array.Empty<JObject>();
            int railInternalId = ReadInt(
                rail["railInternalId"],
                ReadInt(rail["id"], int.MaxValue));
            // Shape repair must not transfer surplus stations to a later outer rail. Only the
            // earliest surviving RailManager ID may absorb currently unassigned common stations;
            // outer rails are repaired from their own station set and movable-station planning.
            IEnumerable<JObject> compatibleUnassigned = railInternalId == primaryRailInternalId
                ? unassignedCommons
                : Array.Empty<JObject>();
            JObject[] combined = existing.Concat(compatibleUnassigned)
                .Where(item => ReadInt(item["linePointInstanceId"], ReadInt(item["instanceId"])) != 0)
                .GroupBy(item => ReadInt(item["linePointInstanceId"], ReadInt(item["instanceId"])))
                .Select(group => group.First())
                .ToArray();
            JObject[] attributes = combined.Where(item => item["isAttribute"]?.Value<bool>() == true).ToArray();
            if (attributes.Length != 1 || combined.Length < 3 ||
                combined.Any(item => !TryReadGrid(item["grid"], out _, out _)))
            {
                diagnostics.Add(
                    $"轨道 {snapshot.RailInstanceId} 的完整站点集无法验证：" +
                    $"站点 {combined.Length} 个、能量点 {attributes.Length} 个或存在缺失网格。");
                continue;
            }

            int attributeInstanceId = ReadInt(
                attributes[0]["linePointInstanceId"],
                ReadInt(attributes[0]["instanceId"]));
            Dictionary<int, JObject> stationByInstanceId = combined.ToDictionary(
                item => ReadInt(item["linePointInstanceId"], ReadInt(item["instanceId"])),
                item => item);
            IReadOnlyList<int> ordered = RailLayoutStrategyPlanner.OrderSimplePlayerLoop(
                combined.Select(item =>
                {
                    TryReadGrid(item["grid"], out double x, out double y);
                    return new RailLoopPointCandidate
                    {
                        InstanceId = ReadInt(item["linePointInstanceId"], ReadInt(item["instanceId"])),
                        IsAttribute = item["isAttribute"]?.Value<bool>() == true,
                        Grid = new RailLayoutPoint(x, y)
                    };
                }),
                attributeInstanceId);
            if (ordered.Count != combined.Length || ordered[0] != snapshot.OriginLinePointInstanceId)
            {
                diagnostics.Add(
                    $"轨道 {snapshot.RailInstanceId} 的稳定极角排序未保留唯一能量点作为起点。" );
                continue;
            }

            RailLoopValidationResult validation = RailLoopValidator.ValidateOrdered(
                ordered.Select(instanceId =>
                {
                    JObject station = stationByInstanceId[instanceId];
                    TryReadGrid(station["grid"], out double x, out double y);
                    return new RailLoopNode
                    {
                        Id = instanceId,
                        IsAttribute = station["isAttribute"]?.Value<bool>() == true,
                        Point = new RailLayoutPoint(x, y)
                    };
                }));
            RailLayoutScore repairedLayout = RailLayoutStrategyPlanner.EvaluateEstimated(
                ordered.Select(instanceId =>
                {
                    JObject station = stationByInstanceId[instanceId];
                    TryReadGrid(station["grid"], out double x, out double y);
                    return new RailLayoutPoint(x, y);
                }));
            if (!validation.IsValid ||
                !RailLayoutStrategyPlanner.IsBalancedDefenseRing(repairedLayout))
            {
                diagnostics.Add(
                    $"轨道 {snapshot.RailInstanceId} 的完整排序仍未形成均衡简单闭环：" +
                    (validation.IsValid
                        ? $"最大角缺口 {repairedLayout.MaxAngularGapDegrees:0.###}°，" +
                          $"半径比 {repairedLayout.RadiusRatio:0.###}。"
                        : string.Join("；", validation.Errors)));
                continue;
            }

            snapshot.OrderedLinePointInstanceIds = ordered.ToArray();
            snapshot.OriginalOrderedLinePointInstanceIds = ordered.ToArray();
            snapshot.OrderedPointIds = ordered
                .Select(instanceId => ReadStablePointId(stationByInstanceId[instanceId]))
                .ToArray();
            double length = 0d;
            for (int index = 0; index < ordered.Count; index++)
            {
                JObject left = stationByInstanceId[ordered[index]];
                JObject right = stationByInstanceId[ordered[(index + 1) % ordered.Count]];
                TryReadGrid(left["grid"], out double leftX, out double leftY);
                TryReadGrid(right["grid"], out double rightX, out double rightY);
                length += Distance(leftX, leftY, rightX, rightY);
            }
            repairs.Add((snapshot, length));
        }

        RailRebuildSnapshot? selected = repairs.OrderBy(item => item.Snapshot.RailInternalId)
            .ThenBy(item => item.Length)
            .ThenBy(item => item.Snapshot.RailInstanceId)
            .Select(item => item.Snapshot)
            .FirstOrDefault();
        detail = selected != null
            ? $"已把 {selected.OrderedLinePointInstanceIds.Count} 个固定站点重排为单能量点简单闭环。"
            : diagnostics.Count > 0
                ? string.Join(" ", diagnostics)
                : $"没有可重排的无效轨道；检测到 {unassignedCommons.Length} 个兼容未接入普通站点。";
        return selected;
    }

    private static RailRebuildSnapshot? CaptureMalformedForFullRepair(
        JObject rail,
        JObject? vehicleStateResult)
    {
        JObject[] stations = ((rail["orderedStations"] as JArray) ?? (rail["points"] as JArray))?
            .OfType<JObject>().ToArray() ?? Array.Empty<JObject>();
        JObject[] origins = stations.Where(item => item["isAttribute"]?.Value<bool>() == true).ToArray();
        if (origins.Length != 1 || stations.Length < 3) return null;
        int originInstanceId = ReadInt(
            origins[0]["linePointInstanceId"],
            ReadInt(origins[0]["instanceId"]));
        if (originInstanceId == 0) return null;

        int railInstanceId = ReadInt(rail["instanceId"]);
        JObject independentState = State(vehicleStateResult);
        JObject? capacityRail = (independentState["rails"] as JArray)?.OfType<JObject>()
            .SingleOrDefault(item =>
                ReadInt(item["instanceId"], ReadInt(item["railInstanceId"])) == railInstanceId);
        int[] runningVehicles = (capacityRail?["runningVehicleIds"] as JArray)?.Values<int>().ToArray()
                                ?? Array.Empty<int>();
        int[] waitingVehicles = (capacityRail?["waitingVehicleIds"] as JArray)?.Values<int>().ToArray()
                                ?? Array.Empty<int>();
        int[] allVehicles = runningVehicles.Concat(waitingVehicles).Distinct().OrderBy(id => id).ToArray();
        return new RailRebuildSnapshot
        {
            RailInstanceId = railInstanceId,
            RailInternalId = ReadInt(rail["railInternalId"], ReadInt(rail["id"])),
            OriginLinePointInstanceId = originInstanceId,
            OriginPointId = ReadStablePointId(origins[0]),
            RunningVehicleInstanceIds = runningVehicles.Distinct().OrderBy(id => id).ToArray(),
            WaitingVehicleInstanceIds = waitingVehicles,
            VehicleInstanceIds = allVehicles,
            VehicleBusinessIds = ReadVehicleBusinessIds(independentState, allVehicles),
            LoopCycleSeconds = rail["loopCycleSeconds"]?.Value<double?>() ?? 0d
        };
    }

    private IReadOnlyList<RailRebuildSnapshot> BuildUnassignedInsertionCandidates(
        JObject? railResult,
        JObject? independentVehicleState,
        JObject? catapultResult,
        bool movableSpecialOnly)
    {
        JObject[] availableStations = (State(catapultResult)["catapults"] as JArray)?.OfType<JObject>()
            .Where(item => item["active"]?.Value<bool>() != false &&
                           item["isAttribute"]?.Value<bool>() != true &&
                           (!movableSpecialOnly ||
                            item["isSpecial"]?.Value<bool>() == true &&
                            item["canMove"]?.Value<bool>() == true) &&
                           item["canUseForNewRail"]?.Value<bool>() == true &&
                           item["canPickLine"]?.Value<bool>() != false &&
                           item["frozen"]?.Value<bool>() != true &&
                           item["railReachMax"]?.Value<bool>() != true &&
                           ReadInt(item["railMembershipCount"]) == 0 &&
                           ReadInt(item["linePointInstanceId"]) != 0)
            .OrderBy(item => ReadInt(item["linePointInstanceId"]))
            .ToArray() ?? Array.Empty<JObject>();
        if (availableStations.Length == 0) return Array.Empty<RailRebuildSnapshot>();

        List<RailRebuildSnapshot> result = new();
        foreach (JObject rail in Rails(railResult).OrderBy(item => ReadInt(item["instanceId"])))
        {
            int railInstanceId = ReadInt(rail["instanceId"]);
            RailRebuildSnapshot? baseline = Capture(railResult, railInstanceId, independentVehicleState);
            if (baseline == null || baseline.VehicleInstanceIds.Count == 0) continue;
            foreach (JObject station in availableStations)
            {
                int pointId = ReadInt(station["linePointInstanceId"]);
                if (!TryReadGrid(station["grid"], out double stationX, out double stationY)) continue;
                JObject[] orderedStations = ((rail["orderedStations"] as JArray) ?? (rail["points"] as JArray))?
                    .OfType<JObject>().ToArray() ?? Array.Empty<JObject>();
                for (int edgeIndex = 0; edgeIndex < orderedStations.Length; edgeIndex++)
                {
                    int next = (edgeIndex + 1) % orderedStations.Length;
                    if (!TryReadGrid(orderedStations[edgeIndex]["grid"], out double fromX, out double fromY) ||
                        !TryReadGrid(orderedStations[next]["grid"], out double toX, out double toY)) continue;
                    RailRebuildSnapshot candidate = BuildInsertionSnapshot(baseline, pointId, edgeIndex);
                    double detour = Distance(fromX, fromY, stationX, stationY) +
                                     Distance(stationX, stationY, toX, toY) -
                                     Distance(fromX, fromY, toX, toY);
                    candidate.EstimatedDetour = detour;
                    result.Add(candidate);
                }
            }
        }
        return result.OrderBy(item => item.RailInternalId)
            .ThenBy(item => item.EstimatedDetour)
            .ThenBy(item => item.RailInstanceId)
            .ThenBy(item => string.Join(",", item.OrderedLinePointInstanceIds))
            .ToArray();
    }

    public void RestoreOriginalOrder(RailRebuildSnapshot snapshot)
    {
        if (snapshot.OriginalOrderedLinePointInstanceIds.Count > 0)
            snapshot.OrderedLinePointInstanceIds = snapshot.OriginalOrderedLinePointInstanceIds.ToArray();
    }

    public RailRebuildVerification VerifyRestored(
        JObject? railResult,
        RailRebuildSnapshot snapshot,
        JObject? vehicleStateResult = null)
    {
        JObject[] matches = Rails(railResult).Where(rail =>
        {
            JObject[] stations = ((rail["orderedStations"] as JArray) ?? (rail["points"] as JArray))?
                .OfType<JObject>().ToArray() ?? Array.Empty<JObject>();
            int[] stablePointIds = stations.Select(ReadStablePointId).Where(id => id != 0).ToArray();
            bool stableMatch = snapshot.OrderedPointIds.Count == snapshot.OrderedLinePointInstanceIds.Count &&
                               stablePointIds.Length == snapshot.OrderedPointIds.Count &&
                               stablePointIds.OrderBy(id => id).SequenceEqual(
                                   snapshot.OrderedPointIds.OrderBy(id => id));
            if (stableMatch) return true;

            int[] instanceIds = stations
                .Select(item => ReadInt(item["linePointInstanceId"], ReadInt(item["instanceId"])))
                .Where(id => id != 0).ToArray();
            return instanceIds.Length == snapshot.OrderedLinePointInstanceIds.Count &&
                   instanceIds.OrderBy(id => id).SequenceEqual(
                       snapshot.OrderedLinePointInstanceIds.OrderBy(id => id));
        }).ToArray();
        if (matches.Length == 0) return new RailRebuildVerification { Pending = true, Detail = "尚未观察到目标闭环。" };
        if (matches.Length != 1) return Failure("目标站点集合对应多个闭环，无法安全确认。", false);
        JObject rail = matches[0];
        if (rail["isLegalPlayerLoop"]?.Value<bool>() != true || rail["isLoop"]?.Value<bool>() != true)
            return Failure("目标轨道已经出现，但尚未形成合法闭环。", false);
        if ((rail["lines"] as JArray)?.Count > 0)
        {
            RailRuntimeValidation topology = RailRuntimeTopologyInspector.InspectRail(rail);
            if (!topology.Loop.IsValid)
                return Failure("目标轨道是伪闭环：" + string.Join("；", topology.Loop.Errors), false);
        }
        if (vehicleStateResult != null)
        {
            JObject independentState = State(vehicleStateResult);
            JObject? capacityRail = (independentState["rails"] as JArray)?.OfType<JObject>()
                .SingleOrDefault(item =>
                    ReadInt(item["instanceId"], ReadInt(item["railInstanceId"])) ==
                    ReadInt(rail["instanceId"]));
            if (capacityRail == null)
                return new RailRebuildVerification { Pending = true, Detail = "闭环已恢复，正在等待容量服务发布轨道状态。" };

            int[] running = (capacityRail["runningVehicleIds"] as JArray)?.Values<int>().ToArray()
                            ?? Array.Empty<int>();
            int[] waiting = (capacityRail["waitingVehicleIds"] as JArray)?.Values<int>().ToArray()
                            ?? Array.Empty<int>();
            HashSet<int> onRail = new(running.Concat(waiting));
            HashSet<int> bag = new((independentState["vehicles"] as JArray)?.OfType<JObject>()
                .Where(item => item["inBag"]?.Value<bool>() == true)
                .Select(item => ReadInt(item["instanceId"]))
                .Where(id => id != 0) ?? Enumerable.Empty<int>());
            if (snapshot.VehicleInstanceIds.Any(id => !onRail.Contains(id) && !bag.Contains(id)))
                return new RailRebuildVerification { Pending = true, Detail = "闭环已恢复，仍有独立战车身份尚未在轨道或背包中出现。" };
            if (!IsOrderedSubsequence(snapshot.WaitingVehicleInstanceIds, waiting))
                return Failure("闭环恢复后的 FIFO 等待顺序与断环前不一致。", false);
        }
        return new RailRebuildVerification
        {
            Verified = true,
            VehiclesRestored = true,
            LoopCycleSeconds = rail["loopCycleSeconds"]?.Value<double?>() ?? 0d,
            Detail = "闭环合法；原独立战车均已恢复运行、按原 FIFO 排队，或因容量收缩合法回到背包。"
        };
    }

    private static IEnumerable<JObject> Rails(JObject? result) =>
        (State(result)["rails"] as JArray)?.OfType<JObject>() ?? Enumerable.Empty<JObject>();

    private static JObject State(JObject? result) =>
        result?.SelectToken("data.state") as JObject ?? result?["state"] as JObject ?? result ?? new JObject();

    private static int ReadInt(JToken? token, int fallback = 0) =>
        token?.Type == JTokenType.Integer ? token.Value<int>() : fallback;

    private static int ReadStablePointId(JObject station) =>
        station["pointId"]?.Type == JTokenType.Integer
            ? station["pointId"]!.Value<int>()
            : ReadInt(station["linePointInstanceId"], ReadInt(station["instanceId"]));

    private static bool TryReadGrid(JToken? token, out double x, out double y)
    {
        x = token?["x"]?.Value<double?>() ?? 0d;
        y = token?["y"]?.Value<double?>() ?? 0d;
        return token?["x"]?.Type is JTokenType.Integer or JTokenType.Float &&
               token?["y"]?.Type is JTokenType.Integer or JTokenType.Float;
    }

    private static double Distance(double x1, double y1, double x2, double y2)
    {
        double x = x1 - x2;
        double y = y1 - y2;
        return Math.Sqrt(x * x + y * y);
    }

    private static IReadOnlyList<int> ReadVehicleBusinessIds(
        JObject state,
        IReadOnlyCollection<int> vehicleInstanceIds) =>
        ((state["vehicles"] as JArray)?.OfType<JObject>()
            .Where(vehicle => vehicleInstanceIds.Contains(ReadInt(vehicle["instanceId"])))
            .Select(vehicle => ReadInt(vehicle["gameVehicleId"], ReadInt(vehicle["vehicleId"])))
            .Where(id => id != 0)
            .Distinct()
            .OrderBy(id => id)
            .ToArray()) ?? Array.Empty<int>();

    private static bool IsOrderedSubsequence(IReadOnlyList<int> expected, IReadOnlyList<int> actual)
    {
        int cursor = 0;
        foreach (int id in actual)
        {
            while (cursor < expected.Count && expected[cursor] != id) cursor++;
            if (cursor >= expected.Count) return false;
            cursor++;
        }
        return true;
    }

    private static RailRebuildVerification Failure(string detail, bool polluted) => new()
    {
        ExplicitStatePolluted = polluted,
        Detail = detail
    };
}
