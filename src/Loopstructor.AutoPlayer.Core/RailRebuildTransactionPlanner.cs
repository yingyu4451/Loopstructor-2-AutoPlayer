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
    public IReadOnlyList<int> TrainInstanceIds { get; set; } = Array.Empty<int>();
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
    public RailRebuildSnapshot? Capture(JObject? railResult, int railInstanceId, JObject? trainResult = null)
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

        int[] trains = (rail["trainIds"] as JArray)?.Values<int>().ToArray()
                       ?? Array.Empty<int>();
        return new RailRebuildSnapshot
        {
            RailInstanceId = railInstanceId,
            RailInternalId = ReadInt(rail["railInternalId"], ReadInt(rail["id"])),
            OriginLinePointInstanceId = origin,
            OriginPointId = originPointId,
            OriginalOrderedLinePointInstanceIds = ordered,
            OrderedLinePointInstanceIds = ordered,
            OrderedPointIds = stablePointIds,
            TrainInstanceIds = trains.Distinct().OrderBy(id => id).ToArray(),
            VehicleInstanceIds = ReadVehicleIds(trainResult, trains, "instanceId"),
            VehicleBusinessIds = ReadVehicleIds(trainResult, trains, "vehicleId"),
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
        "通过始发站的玩家右键语义断开闭环，并由游戏原生始发站缓存暂存车列。");

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
        int speedSourceVehicle = snapshot.VehicleInstanceIds.FirstOrDefault();
        if (speedSourceVehicle != 0)
        {
            arguments["vehicleInstanceId"] = speedSourceVehicle;
        }
        else
        {
            int speedSourceBusinessId = snapshot.VehicleBusinessIds.FirstOrDefault();
            if (speedSourceBusinessId != 0) arguments["vehicleId"] = speedSourceBusinessId;
        }
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
        "从始发站依次连接全部目标站点并回到始发站，使用玩家原生闭环流程恢复车列。");

    public RailRebuildVerification VerifyDisconnect(JObject? deleteResult, RailRebuildSnapshot snapshot)
    {
        JObject state = State(deleteResult);
        bool polluted = state["statePolluted"]?.Value<bool>() == true ||
                        state.SelectToken("deletionOutcome.statePolluted")?.Value<bool>() == true;
        if (polluted) return Failure("始发站断环运行时明确报告状态污染。", true);
        if (state["railDeleted"]?.Value<bool>() != true)
            return new RailRebuildVerification { Pending = true, Detail = "尚未观察到始发站闭环已断开。" };

        JObject? outcome = state["deletionOutcome"] as JObject;
        if (snapshot.TrainInstanceIds.Count > 0 && outcome?["trainStashed"]?.Value<bool>() != true)
            return Failure("断环完成，但游戏没有证明原车列已缓存到始发站。", false);

        JObject[] stashedStates = (outcome?["stashedVehicles"] as JArray)?.OfType<JObject>().ToArray()
                                  ?? Array.Empty<JObject>();
        int[] stashedVehicles = stashedStates
            .Select(item => ReadInt(item.SelectToken("vehicle.instanceId")))
            .Where(id => id != 0).Distinct().OrderBy(id => id).ToArray();
        int[] stashedBusinessIds = stashedStates
            .Select(item => ReadInt(item["vehicleId"]))
            .Where(id => id != 0).Distinct().OrderBy(id => id).ToArray();
        if (snapshot.VehicleBusinessIds.Count > 0 &&
            !snapshot.VehicleBusinessIds.SequenceEqual(stashedBusinessIds))
            return Failure("始发站缓存的战车业务身份与断环前快照不一致。", false);
        if (snapshot.VehicleInstanceIds.Count > 0 &&
            !snapshot.VehicleInstanceIds.SequenceEqual(stashedVehicles))
            return Failure("始发站缓存未能完整证明断环前的战车实例身份。", false);

        return new RailRebuildVerification { Verified = true, Detail = "闭环已断开，原车列已由始发站完整缓存。" };
    }

    public bool IsLegalPreview(JObject? previewResult, RailRebuildSnapshot snapshot, out double cycle)
    {
        JObject state = State(previewResult);
        cycle = state["predictedLoopCycleSeconds"]?.Value<double?>() ?? 0d;
        return state["wouldBeLegal"]?.Value<bool>() == true &&
               state["sideEffectCheckPassed"]?.Value<bool>() == true &&
               state["statePolluted"]?.Value<bool>() != true && cycle > 0d;
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
            TrainInstanceIds = snapshot.TrainInstanceIds,
            VehicleInstanceIds = snapshot.VehicleInstanceIds,
            VehicleBusinessIds = snapshot.VehicleBusinessIds,
            LoopCycleSeconds = snapshot.LoopCycleSeconds,
            EstimatedDetour = snapshot.EstimatedDetour
        };
    }

    public IReadOnlyList<RailRebuildSnapshot> BuildSpecialInsertionCandidates(
        JObject? railResult,
        JObject? trainResult,
        JObject? catapultResult) =>
        BuildUnassignedInsertionCandidates(
            railResult,
            trainResult,
            catapultResult,
            movableSpecialOnly: true);

    public IReadOnlyList<RailRebuildSnapshot> BuildUnassignedInsertionCandidates(
        JObject? railResult,
        JObject? trainResult,
        JObject? catapultResult) =>
        BuildUnassignedInsertionCandidates(
            railResult,
            trainResult,
            catapultResult,
            movableSpecialOnly: false);

    private IReadOnlyList<RailRebuildSnapshot> BuildUnassignedInsertionCandidates(
        JObject? railResult,
        JObject? trainResult,
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
            RailRebuildSnapshot? baseline = Capture(railResult, railInstanceId, trainResult);
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
        return result.OrderBy(item => item.EstimatedDetour)
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
        JObject? trainResult = null)
    {
        JObject[] matches = Rails(railResult).Where(rail =>
        {
            int[] points = ((rail["orderedStations"] as JArray) ?? (rail["points"] as JArray))?
                .OfType<JObject>()
                .Select(item => ReadInt(item["linePointInstanceId"], ReadInt(item["instanceId"])))
                .Where(id => id != 0).ToArray() ?? Array.Empty<int>();
            return points.Length == snapshot.OrderedLinePointInstanceIds.Count &&
                   points[0] == snapshot.OriginLinePointInstanceId &&
                   points.OrderBy(id => id).SequenceEqual(snapshot.OrderedLinePointInstanceIds.OrderBy(id => id));
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
        int[] trains = (rail["trainIds"] as JArray)?.Values<int>().Distinct().OrderBy(id => id).ToArray()
                       ?? Array.Empty<int>();
        bool vehiclesRestored = snapshot.VehicleInstanceIds.Count > 0 || snapshot.VehicleBusinessIds.Count > 0
            ? trains.Length > 0
            : snapshot.TrainInstanceIds.Count == 0 ||
              snapshot.TrainInstanceIds.OrderBy(id => id).SequenceEqual(trains.OrderBy(id => id));
        if (!vehiclesRestored) return new RailRebuildVerification { Pending = true, Detail = "闭环已恢复，正在等待始发站还原原车列身份。" };
        if (trainResult != null)
        {
            int[] restoredVehicleInstances = ReadRailVehicleIds(
                trainResult,
                snapshot.RailInternalId,
                "instanceId");
            int[] restoredVehicleBusinessIds = ReadRailVehicleIds(
                trainResult,
                snapshot.RailInternalId,
                "vehicleId");
            if (snapshot.VehicleInstanceIds.Count > 0 &&
                !snapshot.VehicleInstanceIds.SequenceEqual(restoredVehicleInstances))
                return new RailRebuildVerification { Pending = true, Detail = "闭环已恢复，原战车实例身份尚未完整回到车列。" };
            if (snapshot.VehicleBusinessIds.Count > 0 &&
                !snapshot.VehicleBusinessIds.SequenceEqual(restoredVehicleBusinessIds))
                return new RailRebuildVerification { Pending = true, Detail = "闭环已恢复，原战车业务身份尚未完整回到车列。" };
        }
        return new RailRebuildVerification
        {
            Verified = true,
            VehiclesRestored = true,
            LoopCycleSeconds = rail["loopCycleSeconds"]?.Value<double?>() ?? 0d,
            Detail = "闭环合法，始发站已恢复原车列身份。"
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

    private static IReadOnlyList<int> ReadVehicleIds(
        JObject? trainResult,
        IReadOnlyCollection<int> trainIndexes,
        string key)
    {
        if (trainIndexes.Count == 0) return Array.Empty<int>();
        return ((State(trainResult)["trains"] as JArray)?.OfType<JObject>()
                .Where(train => trainIndexes.Contains(ReadInt(train["index"])))
                .SelectMany(train => (train["vehicles"] as JArray)?.OfType<JObject>() ?? Enumerable.Empty<JObject>())
                .Select(vehicle => ReadInt(vehicle[key]))
                .Where(id => id != 0).Distinct().OrderBy(id => id).ToArray())
               ?? Array.Empty<int>();
    }

    private static int[] ReadRailVehicleIds(JObject? trainResult, int railInternalId, string key) =>
        ((State(trainResult)["trains"] as JArray)?.OfType<JObject>()
            .Where(train => ReadInt(train["railId"]) == railInternalId)
            .SelectMany(train => (train["vehicles"] as JArray)?.OfType<JObject>() ?? Enumerable.Empty<JObject>())
            .Select(vehicle => ReadInt(vehicle[key]))
            .Where(id => id != 0)
            .Distinct()
            .OrderBy(id => id)
            .ToArray()) ?? Array.Empty<int>();

    private static RailRebuildVerification Failure(string detail, bool polluted) => new()
    {
        ExplicitStatePolluted = polluted,
        Detail = detail
    };
}
