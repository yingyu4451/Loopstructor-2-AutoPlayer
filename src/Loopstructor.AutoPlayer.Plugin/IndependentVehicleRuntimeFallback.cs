using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Newtonsoft.Json.Linq;
using UnityEngine;

namespace Loopstructor.AutoPlayer.Plugin;

/// <summary>
/// Binds the independent-vehicle gameplay service once and exposes a compact, identity-safe
/// state used by the automatic player. The game service remains the authority for capacity,
/// FIFO waiting vehicles and deployment settlement.
/// </summary>
internal static class IndependentVehicleRuntimeFallback
{
    private const string ResultSource = "pluginReflection:IndependentVehicle";
    private static readonly string[] DamageParameterNames =
    {
        "generalAttackDamage", "barrageHitDamage", "barragePenetrationDamage",
        "explosionDamage", "linkDamage", "projectileDamage", "shockWaveDamage",
        "siteDamage", "knifeDamage", "vehicleCollisionDamage", "meleeDamage", "remoteDamage"
    };
    private static readonly string[] IntervalParameterNames =
    {
        "shootingInterval", "damageInterval", "behaviourInterval", "vehicleCuttingInterval",
        "siteDamageInterval", "knifeDamageInterval", "bulletTouchDamageInterval"
    };
    private static readonly string[] CountParameterNames =
    {
        "attackCount", "chooseTargetCount", "bulletCount", "hitCount", "linkCount", "agentCount"
    };

    private static ReflectionContract? _contract;
    private static bool _baseConfigContractInitialized;
    private static Type? _vehicleDataManagerType;
    private static MethodInfo? _getMainVehicleComponent;

    internal static bool IsAvailable => TryGetContract(out _);

    internal static double ReadBaseCombatPower(Component vehicle) =>
        vehicle == null ? 0d : ComputeBaseCombatPower(vehicle, null);

    internal static bool TryBuildState(
        JObject railResult,
        JObject vehicleResult,
        out JObject result)
    {
        result = null!;
        if (!TryGetContract(out ReflectionContract contract))
        {
            return false;
        }

        try
        {
            object? service = contract.ServiceInstance.GetValue(null, null);
            if (service == null)
            {
                result = Error("独立战车容量服务尚未初始化。", false);
                return true;
            }

            JObject railState = State(railResult);
            JObject vehicleState = State(vehicleResult);
            JArray nativeRails = railState["rails"] as JArray ?? new JArray();
            JArray nativeVehicles = vehicleState["vehicles"] as JArray ?? new JArray();
            Dictionary<int, Component> liveVehicles = FindSceneComponents(contract.VehicleType)
                .ToDictionary(component => component.GetInstanceID(), component => component);
            Dictionary<int, Component> liveRails = FindSceneComponents(contract.RailType)
                .ToDictionary(component => component.GetInstanceID(), component => component);
            Dictionary<int, int> instanceByGameId = liveVehicles.Values
                .Select(component => new
                {
                    InstanceId = component.GetInstanceID(),
                    GameId = ReadInt(component, "ID")
                })
                .Where(item => item.GameId != 0)
                .GroupBy(item => item.GameId)
                .ToDictionary(group => group.Key, group => group.First().InstanceId);

            Dictionary<int, QueueSnapshot> queuesByRailId = ReadQueueSnapshots(
                contract,
                service,
                instanceByGameId);
            JArray vehicles = BuildVehicles(nativeVehicles, liveVehicles, queuesByRailId);
            JArray rails = BuildRails(contract, service, nativeRails, vehicles, queuesByRailId, liveRails);

            result = Success("已读取独立战车、轨道容量与等待队列。", new JObject
            {
                ["independentVehicleMode"] = ReadIndependentVehicleMode(contract),
                ["rails"] = rails,
                ["vehicles"] = vehicles,
                ["railCount"] = rails.Count,
                ["vehicleCount"] = vehicles.Count,
                ["source"] = ResultSource
            });
            return true;
        }
        catch (Exception exception)
        {
            result = Error("读取独立战车状态失败：" + Unwrap(exception).Message, false);
            return true;
        }
    }

    internal static bool TryDeploy(JObject? arguments, out JObject result)
    {
        result = null!;
        if (!TryGetContract(out ReflectionContract contract))
        {
            return false;
        }

        int vehicleInstanceId = arguments?["vehicleInstanceId"]?.Value<int?>() ?? 0;
        int energyPointInstanceId = arguments?["energyPointInstanceId"]?.Value<int?>() ?? 0;
        int expectedRailInstanceId = arguments?["railInstanceId"]?.Value<int?>() ?? 0;
        Component? vehicle = FindSceneComponents(contract.VehicleType)
            .FirstOrDefault(component => component.GetInstanceID() == vehicleInstanceId);
        Component? energyPoint = FindSceneComponents(contract.LinePointType)
            .FirstOrDefault(component => component.GetInstanceID() == energyPointInstanceId);
        object? service = contract.ServiceInstance.GetValue(null, null);
        if (service == null || vehicle == null || energyPoint == null)
        {
            result = Error("战车、能量点或容量服务身份已变化，未提交投放。", false);
            return true;
        }

        object? rail = ResolveRailFromPoint(energyPoint);
        int actualRailInstanceId = rail is UnityEngine.Object railObject ? railObject.GetInstanceID() : 0;
        if (expectedRailInstanceId == 0 || actualRailInstanceId != expectedRailInstanceId)
        {
            result = Error("能量点所属轨道已变化，未提交投放。", false);
            return true;
        }

        object? evaluation;
        try
        {
            evaluation = contract.EvaluateDeployment.Invoke(service, new object[] { energyPoint, vehicle });
        }
        catch (Exception exception)
        {
            result = Error("投放预检失败：" + Unwrap(exception).Message, false);
            return true;
        }

        string evaluationCode = ReadMember(evaluation, "Code")?.ToString() ?? "None";
        if (string.Equals(evaluationCode, "AlreadyQueued", StringComparison.Ordinal))
        {
            result = Success("战车已经位于该能量点等待队列中。", BuildDeploymentState(
                contract, service, vehicle, energyPoint, rail, evaluation, false, true));
            return true;
        }

        if (!string.Equals(evaluationCode, "Available", StringComparison.Ordinal))
        {
            JObject rejected = BuildDeploymentState(
                contract, service, vehicle, energyPoint, rail, evaluation, false, false);
            rejected["rejected"] = true;
            result = Error("战车投放被游戏容量服务拒绝：" + evaluationCode + "。", false, rejected);
            return true;
        }

        bool invocationStarted = false;
        try
        {
            invocationStarted = true;
            object? deployment = contract.TryDeployVehicle.Invoke(service, new object[] { energyPoint, vehicle });
            string code = ReadMember(deployment, "Code")?.ToString() ?? "None";
            JObject state = BuildDeploymentState(
                contract, service, vehicle, energyPoint, rail, deployment, true, false);
            bool settled = state["running"]?.Value<bool>() == true ||
                           state["queued"]?.Value<bool>() == true;
            if (string.Equals(code, "Accepted", StringComparison.Ordinal) && settled)
            {
                result = Success("游戏容量服务已接受战车投放。", state);
                return true;
            }

            if (string.Equals(code, "AlreadyQueued", StringComparison.Ordinal) &&
                state["queued"]?.Value<bool>() == true)
            {
                state["idempotent"] = true;
                result = Success("战车投放已通过等待队列幂等对账。", state);
                return true;
            }

            state["outcomeUnknown"] = string.Equals(code, "Accepted", StringComparison.Ordinal);
            state["needsReconciliation"] = state["outcomeUnknown"]!.Value<bool>();
            result = Error(
                string.Equals(code, "Accepted", StringComparison.Ordinal)
                    ? "投放已被接受，但尚未观察到运行或排队状态；已锁定写入并要求只读对账。"
                    : "战车投放未被接受：" + code + "。",
                invocationStarted,
                state);
            return true;
        }
        catch (Exception exception)
        {
            JObject state = BuildDeploymentState(
                contract, service, vehicle, energyPoint, rail, evaluation, invocationStarted, false);
            state["outcomeUnknown"] = invocationStarted;
            state["needsReconciliation"] = invocationStarted;
            result = Error("战车投放执行异常：" + Unwrap(exception).Message, invocationStarted, state);
            return true;
        }
    }

    private static JArray BuildVehicles(
        JArray nativeVehicles,
        IReadOnlyDictionary<int, Component> liveVehicles,
        IReadOnlyDictionary<int, QueueSnapshot> queuesByRailId)
    {
        Dictionary<int, (int RailId, int Index)> waiting = new();
        foreach (QueueSnapshot queue in queuesByRailId.Values)
        {
            for (int index = 0; index < queue.VehicleInstanceIds.Count; index++)
            {
                waiting[queue.VehicleInstanceIds[index]] = (queue.RailId, index);
            }
        }

        JArray result = new();
        HashSet<int> included = new();
        foreach (JObject native in nativeVehicles.OfType<JObject>())
        {
            JObject vehicle = (JObject)native.DeepClone();
            int instanceId = vehicle["instanceId"]?.Value<int?>() ?? 0;
            if (instanceId != 0) included.Add(instanceId);
            liveVehicles.TryGetValue(instanceId, out Component? component);
            int gameId = component == null ? 0 : ReadInt(component, "ID");
            bool queued = waiting.TryGetValue(instanceId, out (int RailId, int Index) queueInfo);
            object? motionDriver = component == null ? null : ReadMember(component, "motionDriver");
            object? ownerRail = ReadMember(motionDriver, "OwnerRail");
            int runningRailId = ReadInt(ownerRail, "ID");
            bool running = motionDriver != null && ownerRail != null;
            vehicle["gameVehicleId"] = gameId;
            vehicle["runState"] = running ? "running" : queued ? "queued" :
                vehicle["inBag"]?.Value<bool>() == true ? "bag" : "inactive";
            vehicle["running"] = running;
            vehicle["queued"] = queued;
            vehicle["waitingIndex"] = queued ? queueInfo.Index : -1;
            vehicle["railId"] = running ? runningRailId : queued ? queueInfo.RailId : null;
            vehicle["currentSpeed"] = ReadFloat(motionDriver, "CurrentSpeed");
            vehicle["configuredSpeed"] = ReadConfiguredSpeed(component, vehicle);
            vehicle["baseCombatPower"] = ComputeBaseCombatPower(component, vehicle);
            result.Add(vehicle);
        }

        foreach (Component component in liveVehicles.Values.Where(item => !included.Contains(item.GetInstanceID())))
        {
            int instanceId = component.GetInstanceID();
            int gameId = ReadInt(component, "ID");
            bool queued = waiting.TryGetValue(instanceId, out (int RailId, int Index) queueInfo);
            object? motionDriver = ReadMember(component, "motionDriver");
            object? ownerRail = ReadMember(motionDriver, "OwnerRail");
            int runningRailId = ReadInt(ownerRail, "ID");
            bool running = motionDriver != null && ownerRail != null;
            result.Add(new JObject
            {
                ["instanceId"] = instanceId,
                ["gameVehicleId"] = gameId,
                ["name"] = component.name,
                ["vehicleType"] = ReadMember(component, "vehicleType")?.ToString() ?? string.Empty,
                ["level"] = ReadInt(component, "level"),
                ["active"] = component.gameObject.activeInHierarchy,
                ["inBag"] = !component.gameObject.activeInHierarchy && !queued,
                ["runState"] = running ? "running" : queued ? "queued" : "bag",
                ["running"] = running,
                ["queued"] = queued,
                ["waitingIndex"] = queued ? queueInfo.Index : -1,
                ["railId"] = running ? runningRailId : queued ? queueInfo.RailId : null,
                ["currentSpeed"] = ReadFloat(motionDriver, "CurrentSpeed"),
                ["configuredSpeed"] = ReadConfiguredSpeed(component, null),
                ["baseCombatPower"] = ComputeBaseCombatPower(component, null)
            });
        }

        return new JArray(result.OfType<JObject>()
            .OrderBy(vehicle => vehicle["instanceId"]?.Value<int?>() ?? int.MaxValue));
    }

    private static JArray BuildRails(
        ReflectionContract contract,
        object service,
        JArray nativeRails,
        JArray vehicles,
        IReadOnlyDictionary<int, QueueSnapshot> queuesByRailId,
        IReadOnlyDictionary<int, Component> liveRails)
    {
        JArray result = new();
        foreach (JObject native in nativeRails.OfType<JObject>())
        {
            JObject rail = (JObject)native.DeepClone();
            rail.Remove("trainIds");
            rail.Remove("trains");
            int railId = rail["railInternalId"]?.Value<int?>() ?? rail["id"]?.Value<int?>() ?? 0;
            int runningCount = rail["driverCount"]?.Value<int?>() ?? 0;
            int capacity = Math.Max(0, rail["driverMaxCount"]?.Value<int?>() ?? 0);
            queuesByRailId.TryGetValue(railId, out QueueSnapshot? queue);
            int railInstanceId = rail["instanceId"]?.Value<int?>() ??
                                 rail["railInstanceId"]?.Value<int?>() ?? 0;
            int waitingCount = queue?.VehicleInstanceIds.Count ?? 0;
            if (liveRails.TryGetValue(railInstanceId, out Component? liveRail))
            {
                waitingCount = Math.Max(0, ConvertToInt(
                    contract.GetWaitingVehicleCount.Invoke(service, new object[] { liveRail })));
            }
            JArray runningIds = new(vehicles.OfType<JObject>()
                .Where(vehicle => vehicle["running"]?.Value<bool>() == true &&
                                  vehicle["railId"]?.Value<int?>() == railId)
                .Select(vehicle => new JValue(vehicle["instanceId"]?.Value<int?>() ?? 0)));
            JArray waitingIds = new((queue?.VehicleInstanceIds ?? Array.Empty<int>())
                .Select(instanceId => new JValue(instanceId)));
            JObject[] energyPoints = (rail["orderedStations"] as JArray)?.OfType<JObject>()
                .Where(point => point["isAttribute"]?.Value<bool>() == true)
                .ToArray() ?? Array.Empty<JObject>();
            int occupied = runningCount + waitingCount;
            rail["energyPointCount"] = energyPoints.Length;
            rail["energyPointInstanceId"] = energyPoints.Length == 1
                ? energyPoints[0]["linePointInstanceId"]?.Value<int?>() ??
                  energyPoints[0]["instanceId"]?.Value<int?>() ?? 0
                : 0;
            rail["runningCount"] = runningCount;
            rail["waitingCount"] = waitingCount;
            rail["occupiedCount"] = occupied;
            rail["capacity"] = capacity;
            rail["freeCapacity"] = Math.Max(0, capacity - occupied);
            rail["capacityFull"] = occupied >= capacity;
            rail["runningVehicleIds"] = runningIds;
            rail["waitingVehicleIds"] = waitingIds;
            rail["waitingFifoComplete"] = queue?.Complete ?? true;
            result.Add(rail);
        }

        return result;
    }

    private static Dictionary<int, QueueSnapshot> ReadQueueSnapshots(
        ReflectionContract contract,
        object service,
        IReadOnlyDictionary<int, int> instanceByGameId)
    {
        Dictionary<int, QueueSnapshot> result = new();
        object? saveData = contract.GetSaveData.Invoke(service, null);
        if (ReadMember(saveData, "items") is not IEnumerable items)
        {
            return result;
        }

        foreach (object? item in items)
        {
            int railId = ReadInt(item, "railId");
            int pointId = ReadInt(item, "pointId");
            if (railId == 0 && pointId == 0) continue;
            List<int> instanceIds = new();
            bool complete = true;
            if (ReadMember(item, "vehicleIds") is IEnumerable vehicleIds)
            {
                foreach (object? raw in vehicleIds)
                {
                    int gameId = ConvertToInt(raw);
                    if (gameId == 0 || !instanceByGameId.TryGetValue(gameId, out int instanceId))
                    {
                        complete = false;
                        continue;
                    }
                    instanceIds.Add(instanceId);
                }
            }
            result[railId] = new QueueSnapshot(railId, pointId, instanceIds, complete);
        }
        return result;
    }

    private static JObject BuildDeploymentState(
        ReflectionContract contract,
        object service,
        Component vehicle,
        Component energyPoint,
        object? rail,
        object? deployment,
        bool invocationStarted,
        bool idempotent)
    {
        int gameVehicleId = ReadInt(vehicle, "ID");
        int railId = ReadInt(rail, "ID");
        object? motionDriver = ReadMember(vehicle, "motionDriver");
        object? ownerRail = ReadMember(motionDriver, "OwnerRail");
        bool running = ownerRail != null && ReadInt(ownerRail, "ID") == railId;
        bool queued = IsQueued(contract, service, gameVehicleId, railId);
        return new JObject
        {
            ["code"] = ReadMember(deployment, "Code")?.ToString() ?? "None",
            ["vehicleInstanceId"] = vehicle.GetInstanceID(),
            ["gameVehicleId"] = gameVehicleId,
            ["energyPointInstanceId"] = energyPoint.GetInstanceID(),
            ["railInstanceId"] = rail is UnityEngine.Object railObject ? railObject.GetInstanceID() : 0,
            ["railId"] = railId,
            ["activeCount"] = ReadInt(deployment, "ActiveCount"),
            ["waitingCount"] = ReadInt(deployment, "WaitingCount"),
            ["capacity"] = ReadInt(deployment, "Capacity"),
            ["running"] = running,
            ["queued"] = queued,
            ["settled"] = running || queued,
            ["idempotent"] = idempotent,
            ["invocationStarted"] = invocationStarted,
            ["source"] = ResultSource
        };
    }

    private static bool IsQueued(
        ReflectionContract contract,
        object service,
        int gameVehicleId,
        int railId)
    {
        object? saveData = contract.GetSaveData.Invoke(service, null);
        if (ReadMember(saveData, "items") is not IEnumerable items) return false;
        foreach (object? item in items)
        {
            if (ReadInt(item, "railId") != railId || ReadMember(item, "vehicleIds") is not IEnumerable ids)
            {
                continue;
            }
            foreach (object? id in ids)
            {
                if (ConvertToInt(id) == gameVehicleId) return true;
            }
        }
        return false;
    }

    private static double ComputeBaseCombatPower(Component? vehicle, JObject? native)
    {
        object? basic = ResolveUnenchantedBaseComponent(vehicle) ??
                        (vehicle == null ? null : ReadMember(vehicle, "basicVehicleComponent"));
        object? parameters = ReadMember(basic, "allParameters");
        Dictionary<string, double> values = ReadNumericParameters(parameters);
        double damage = DamageParameterNames.Sum(name => values.TryGetValue(name, out double value)
            ? Math.Max(0d, value)
            : 0d);
        double interval = IntervalParameterNames
            .Where(values.ContainsKey)
            .Select(name => values[name])
            .Where(value => value > 0.01d)
            .DefaultIfEmpty(native?["cooldownSeconds"]?.Value<double?>() ?? 1d)
            .Min();
        double count = CountParameterNames
            .Where(values.ContainsKey)
            .Select(name => values[name])
            .Where(value => value > 0d)
            .DefaultIfEmpty(1d)
            .Max();
        if (damage > 0d)
        {
            return Math.Round(damage * Math.Max(1d, count) / Math.Max(0.05d, interval), 6);
        }
        double range = values.TryGetValue("attackRange", out double attackRange)
            ? attackRange
            : native?["attackRange"]?.Value<double?>() ?? 0d;
        return Math.Round(Math.Max(1d, range), 6);
    }

    private static object? ResolveUnenchantedBaseComponent(Component? vehicle)
    {
        if (vehicle == null) return null;
        if (!_baseConfigContractInitialized)
        {
            _vehicleDataManagerType = FindType("MetroTD.VehicleSystem.VehicleDataManager");
            _getMainVehicleComponent = _vehicleDataManagerType?.GetMethod(
                "GetMainRazorComponent",
                BindingFlags.Public | BindingFlags.Instance);
            _baseConfigContractInitialized = true;
        }
        if (_vehicleDataManagerType == null || _getMainVehicleComponent == null) return null;

        object? vehicleType = ReadMember(vehicle, "vehicleType");
        Component? manager = FindSceneComponents(_vehicleDataManagerType).FirstOrDefault();
        if (manager == null || vehicleType == null) return null;
        try
        {
            return _getMainVehicleComponent.Invoke(manager, new[] { vehicleType });
        }
        catch
        {
            return null;
        }
    }

    private static Dictionary<string, double> ReadNumericParameters(object? parameters)
    {
        Dictionary<string, double> result = new(StringComparer.Ordinal);
        if (parameters is not IDictionary dictionary) return result;
        foreach (DictionaryEntry entry in dictionary)
        {
            string key = entry.Key?.ToString() ?? string.Empty;
            if (key.Length == 0) continue;
            object? raw = ReadMember(entry.Value, "Value") ?? entry.Value;
            if (TryConvertDouble(raw, out double value)) result[key] = value;
        }
        return result;
    }

    private static double ReadConfiguredSpeed(Component? vehicle, JObject? native)
    {
        object? parameter = vehicle == null ? null : ReadMember(vehicle, "maxSpeed");
        object? value = ReadMember(parameter, "Value");
        if (TryConvertDouble(value, out double speed) && speed > 0d) return speed;
        return native?["driverMaxSpeed"]?.Value<double?>() ?? 0d;
    }

    private static bool ReadIndependentVehicleMode(ReflectionContract contract)
    {
        object? config = contract.TrainConfigInstance?.GetValue(null, null);
        return ReadMember(config, "independentVehicleMode") is bool enabled && enabled;
    }

    private static object? ResolveRailFromPoint(Component point)
    {
        object? rail = ReadMember(point, "LastRail");
        if (rail != null) return rail;
        object? next = ReadMember(point, "NextRail");
        return next;
    }

    private static IEnumerable<Component> FindSceneComponents(Type type) =>
        Resources.FindObjectsOfTypeAll(type)
            .OfType<Component>()
            .Where(component => component != null && component.gameObject != null && component.gameObject.scene.IsValid());

    private static JObject State(JObject result) =>
        result.SelectToken("data.state") as JObject ?? result["state"] as JObject ?? result;

    private static bool TryGetContract(out ReflectionContract contract)
    {
        if (_contract != null)
        {
            contract = _contract;
            return true;
        }

        Type? serviceType = FindType("MetroTD.CatapultSystem.EnergyCatapultTrainCacheService");
        Type? railType = FindType("MetroTD.LineSystem.Rail");
        Type? pointType = FindType("MetroTD.LineSystem.LinePoint");
        Type? vehicleType = FindType("MetroTD.VehicleSystem.VehicleController");
        Type? configType = FindType("MetroTD.LineSystem.TrainConfigSO");
        PropertyInfo? instance = serviceType?.GetProperty("Instance", BindingFlags.Public | BindingFlags.Static);
        MethodInfo? evaluate = serviceType?.GetMethod("EvaluateDeployment", BindingFlags.Public | BindingFlags.Instance);
        MethodInfo? deploy = serviceType?.GetMethod("TryDeployVehicle", BindingFlags.Public | BindingFlags.Instance);
        MethodInfo? waiting = serviceType?.GetMethod("GetWaitingVehicleCount", BindingFlags.Public | BindingFlags.Instance);
        MethodInfo? save = serviceType?.GetMethod("GetSaveData", BindingFlags.Public | BindingFlags.Instance);
        if (serviceType == null || railType == null || pointType == null || vehicleType == null ||
            instance == null || evaluate == null || deploy == null || waiting == null || save == null)
        {
            contract = null!;
            return false;
        }

        _contract = new ReflectionContract(
            instance,
            evaluate,
            deploy,
            waiting,
            save,
            railType,
            pointType,
            vehicleType,
            configType?.GetProperty("Instance", BindingFlags.Public | BindingFlags.Static));
        contract = _contract;
        return true;
    }

    private static Type? FindType(string fullName)
    {
        foreach (Assembly assembly in AppDomain.CurrentDomain.GetAssemblies())
        {
            Type? type = assembly.GetType(fullName, false);
            if (type != null) return type;
        }
        return null;
    }

    private static object? ReadMember(object? instance, string name)
    {
        if (instance == null) return null;
        const BindingFlags Flags = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance;
        Type type = instance.GetType();
        return type.GetProperty(name, Flags)?.GetValue(instance, null) ?? type.GetField(name, Flags)?.GetValue(instance);
    }

    private static int ReadInt(object? instance, string name) => ConvertToInt(ReadMember(instance, name));
    private static double ReadFloat(object? instance, string name) =>
        TryConvertDouble(ReadMember(instance, name), out double value) ? value : 0d;
    private static int ConvertToInt(object? value)
    {
        try { return value == null ? 0 : Convert.ToInt32(value); }
        catch { return 0; }
    }
    private static bool TryConvertDouble(object? value, out double number)
    {
        try
        {
            if (value == null) { number = 0d; return false; }
            number = Convert.ToDouble(value);
            return !double.IsNaN(number) && !double.IsInfinity(number);
        }
        catch { number = 0d; return false; }
    }

    private static Exception Unwrap(Exception exception) =>
        exception is TargetInvocationException target && target.InnerException != null
            ? target.InnerException
            : exception;

    private static JObject Success(string message, JObject state) => new()
    {
        ["success"] = true,
        ["message"] = message,
        ["suggestion"] = string.Empty,
        ["data"] = new JObject { ["state"] = state }
    };

    private static JObject Error(
        string message,
        bool invocationStarted,
        JObject? state = null)
    {
        JObject payload = state ?? new JObject();
        payload["invocationStarted"] = invocationStarted;
        payload["source"] = ResultSource;
        return new JObject
        {
            ["success"] = false,
            ["message"] = message,
            ["suggestion"] = invocationStarted
                ? "写入结果可能未知；禁止重放并通过独立战车状态只读对账。"
                : "重新读取轨道容量、能量点与战车身份后再规划。",
            ["data"] = new JObject { ["state"] = payload }
        };
    }

    private sealed class ReflectionContract
    {
        public ReflectionContract(
            PropertyInfo serviceInstance,
            MethodInfo evaluateDeployment,
            MethodInfo tryDeployVehicle,
            MethodInfo getWaitingVehicleCount,
            MethodInfo getSaveData,
            Type railType,
            Type linePointType,
            Type vehicleType,
            PropertyInfo? trainConfigInstance)
        {
            ServiceInstance = serviceInstance;
            EvaluateDeployment = evaluateDeployment;
            TryDeployVehicle = tryDeployVehicle;
            GetWaitingVehicleCount = getWaitingVehicleCount;
            GetSaveData = getSaveData;
            RailType = railType;
            LinePointType = linePointType;
            VehicleType = vehicleType;
            TrainConfigInstance = trainConfigInstance;
        }

        public PropertyInfo ServiceInstance { get; }
        public MethodInfo EvaluateDeployment { get; }
        public MethodInfo TryDeployVehicle { get; }
        public MethodInfo GetWaitingVehicleCount { get; }
        public MethodInfo GetSaveData { get; }
        public Type RailType { get; }
        public Type LinePointType { get; }
        public Type VehicleType { get; }
        public PropertyInfo? TrainConfigInstance { get; }
    }

    private sealed class QueueSnapshot
    {
        public QueueSnapshot(int railId, int pointId, IReadOnlyList<int> vehicleInstanceIds, bool complete)
        {
            RailId = railId;
            PointId = pointId;
            VehicleInstanceIds = vehicleInstanceIds;
            Complete = complete;
        }
        public int RailId { get; }
        public int PointId { get; }
        public IReadOnlyList<int> VehicleInstanceIds { get; }
        public bool Complete { get; }
    }
}
