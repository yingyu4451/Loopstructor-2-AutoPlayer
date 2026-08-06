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

/// <summary>
/// Chooses one ordinary player action from already queried runtime state.
/// This policy is deliberately stateless; the caller owns polling and the disposable phase transition.
/// </summary>
public sealed class BattleDecisionEngine
{
    // The supported game build uses two world units per logical rail grid cell.
    private const double WorldToGridScale = 0.5d;

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
        JObject? trainResult)
    {
        JObject threats = State(waveThreatsResult);
        JObject rails = State(railResult);
        JObject trains = State(trainResult);

        JObject? nest = (threats["nests"] as JArray)?
            .OfType<JObject>()
            .Where(item => item["active"]?.Value<bool>() != false && TryReadThreatVector(threats, item, out _, out _))
            .OrderByDescending(ThreatScore)
            .ThenBy(item => ReadInt(item["index"], int.MaxValue))
            .FirstOrDefault();
        if (nest == null)
        {
            return AutomationAction.Wait(AutomationStage.Battle, "当前没有可用于列车机动的活动巢穴。");
        }

        if (!TryReadThreatVector(threats, nest, out double threatX, out double threatY))
        {
            return AutomationAction.Wait(AutomationStage.Battle, "巢穴缺少相对主基地的有效方向，暂不移动列车。");
        }

        JObject? train = (trains["trains"] as JArray)?
            .OfType<JObject>()
            .Where(IsMovableTrain)
            .OrderByDescending(TrainVehicleCount)
            .ThenBy(item => ReadInt(item["index"], int.MaxValue))
            .FirstOrDefault();
        if (train == null)
        {
            return AutomationAction.Wait(AutomationStage.Battle, "当前没有可移动的既有车列。");
        }

        JArray? railItems = rails["rails"] as JArray;
        if (railItems == null || railItems.Count == 0)
        {
            return AutomationAction.Wait(AutomationStage.Battle, "当前没有可用于列车机动的轨道。");
        }

        if (threatX * threatX + threatY * threatY <= 0.000001d)
        {
            return AutomationAction.Wait(AutomationStage.Battle, "主威胁巢穴与基地位置重合，无法判断防守方向。");
        }

        double targetGridX = threatX * WorldToGridScale;
        double targetGridY = threatY * WorldToGridScale;
        int? sourceRailId = ReadNullableInt(train["railId"]);
        string currentLineName = train["line"]?.Value<string>() ?? string.Empty;
        LineCandidate? target = EnumerateLineCandidates(
                railItems,
                sourceRailId,
                currentLineName,
                targetGridX,
                targetGridY)
            .OrderBy(candidate => candidate.DistanceToThreatSquared)
            .ThenByDescending(candidate => candidate.IsCurrentLine)
            .ThenBy(candidate => candidate.LineInstanceId)
            .FirstOrDefault();
        if (target == null)
        {
            return AutomationAction.Wait(AutomationStage.Battle, "主威胁方向上没有空闲且合法的轨道线段。");
        }

        if (target.IsCurrentLine)
        {
            return AutomationAction.Wait(AutomationStage.Battle, "当前车列已经位于最接近主威胁的合法线段，无需重复调度。");
        }

        int trainIndex = ReadInt(train["index"], -1);
        return new AutomationAction(
            "moveTrainToLine",
            JObject.FromObject(new
            {
                trainIndex,
                lineInstanceId = target.LineInstanceId,
                forward = target.ForwardTowardThreat
            }),
            AutomationStage.Battle,
            $"把车列 {trainIndex} 调往最接近主威胁巢穴的合法线段，并朝巢穴方向行驶。");
    }

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
        string confirmKind = ResolveConfirmKind(item);
        string effectKind = item.SelectToken("effectFacts.effectKind")?.Value<string>() ?? string.Empty;
        bool safeEffect = effectKind is
            "vehicleBuff" or
            "targetBuff" or
            "createStationWithBuiltInBuff" or
            "createStationWithLegacyBuff";
        return safeEffect &&
               (confirmKind is "none" or "grid" or "world" or "positionRaycast");
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
        string currentLineName,
        double targetX,
        double targetY)
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

                double midpointX = (fromX + toX) / 2d;
                double midpointY = (fromY + toY) / 2d;
                double directionProjection = midpointX * targetX + midpointY * targetY;
                if (directionProjection <= 0d)
                {
                    continue;
                }

                yield return new LineCandidate(
                    lineInstanceId,
                    DistancePointToSegmentSquared(targetX, targetY, fromX, fromY, toX, toY),
                    isCurrentLine,
                    DistanceSquared(toX, toY, targetX, targetY) <=
                    DistanceSquared(fromX, fromY, targetX, targetY));
            }
        }
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
        if (HasTarget(arguments))
        {
            return true;
        }

        JObject? candidate = (disposable["targetCandidates"] as JArray)?
            .OfType<JObject>()
            .FirstOrDefault(item => item["conditionPass"]?.Value<bool>() == true && HasIdentity(item));
        if (candidate == null)
        {
            return false;
        }

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

    private static bool HasTarget(JObject arguments) =>
        ReadInt(arguments["targetInstanceId"], ReadInt(arguments["instanceId"], 0)) != 0
        || !string.IsNullOrWhiteSpace(arguments["path"]?.Value<string>())
        || HasObject(arguments, "world")
        || HasObject(arguments, "grid");

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

    private sealed class LineCandidate
    {
        public LineCandidate(
            int lineInstanceId,
            double distanceToThreatSquared,
            bool isCurrentLine,
            bool forwardTowardThreat)
        {
            LineInstanceId = lineInstanceId;
            DistanceToThreatSquared = distanceToThreatSquared;
            IsCurrentLine = isCurrentLine;
            ForwardTowardThreat = forwardTowardThreat;
        }

        public int LineInstanceId { get; }
        public double DistanceToThreatSquared { get; }
        public bool IsCurrentLine { get; }
        public bool ForwardTowardThreat { get; }
    }
}
