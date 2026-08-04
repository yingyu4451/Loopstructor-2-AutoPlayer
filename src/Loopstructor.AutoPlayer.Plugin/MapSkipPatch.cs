using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.Reflection;
using HarmonyLib;
using Loopstructor.AutoPlayer.Core;
using UnityEngine;

namespace Loopstructor.AutoPlayer.Plugin;

/// <summary>
/// 在地图跳关模式下开放当前已加载阶段的全部节点，并通过游戏原生地图流程完成跳转。
/// </summary>
internal static class MapSkipPatch
{
    private static readonly RuntimeFeatureFlag EnabledState = new();
    private static Action<string> _log = _ => { };
    private static Type? _mapNodeUiType;
    private static Type? _uiButtonType;
    private static Type? _roomMapUiType;
    private static Type? _roomMapSpawnerType;
    private static Type? _waveDurationControllerType;
    private static Type? _waveProgressControllerType;
    private static Type? _gameControllerType;
    private static Type? _guiSaveHandlerType;
    private static Vector2Int? _pendingTarget;
    private static int _pendingMapStage;
    private static int _activeMapInstanceId;
    private static bool _observedMap;
    private static string _lastDiagnostic = string.Empty;

    public static bool Installed { get; private set; }
    public static bool Enabled => EnabledState.Value;

    /// <summary>
    /// 安装地图节点左键拦截补丁。
    /// </summary>
    public static bool Install(Harmony harmony, Action<string> log)
    {
        if (Installed) return true;
        if (harmony == null) throw new ArgumentNullException(nameof(harmony));

        _log = log ?? (_ => { });
        _mapNodeUiType = AccessTools.TypeByName("MetroTD.RoomSystem.MapNodeUI");
        _uiButtonType = AccessTools.TypeByName("ActFramework_ByHZR.UI.UIButton");
        _roomMapUiType = AccessTools.TypeByName("MetroTD.RoomSystem.RoomMapUI");
        _roomMapSpawnerType = AccessTools.TypeByName("MetroTD.RoomSystem.RoomMapSpawner");
        _waveDurationControllerType = AccessTools.TypeByName("MetroTD.RoomSystem.WaveDurationController");
        _waveProgressControllerType = AccessTools.TypeByName("MetroTD.RoomSystem.WaveProgressController");
        _gameControllerType = AccessTools.TypeByName("MetroTD.GameController");
        _guiSaveHandlerType = AccessTools.TypeByName("GuiSaveHandler");

        if (_mapNodeUiType == null ||
            _uiButtonType == null ||
            _roomMapUiType == null ||
            _roomMapSpawnerType == null ||
            _waveDurationControllerType == null ||
            _waveProgressControllerType == null ||
            _gameControllerType == null ||
            _guiSaveHandlerType == null)
        {
            _log("无法安装地图跳关补丁：当前游戏版本缺少必要的地图或波次类型。");
            return false;
        }

        MethodInfo? leftDown = AccessTools.Method(_mapNodeUiType, "LeftDown", Type.EmptyTypes);
        MethodInfo? mapNodePrefix = AccessTools.Method(typeof(MapSkipPatch), nameof(BeforeMapNodeLeftDown));
        MethodInfo? buttonLeftDown = AccessTools.Method(_uiButtonType, "LeftPointDown", Type.EmptyTypes);
        MethodInfo? buttonPrefix = AccessTools.Method(typeof(MapSkipPatch), nameof(BeforeUiButtonLeftPointDown));
        MethodInfo? click = FindMethod(_mapNodeUiType, "Click");
        MethodInfo? readDataToUi = FindMethod(_roomMapSpawnerType, "ReadDataToUI", typeof(int), typeof(bool));
        MethodInfo? loadPath = FindMethod(_roomMapUiType, "LoadPath", typeof(List<Vector2Int>));
        MethodInfo? getNodeUi = FindMethod(_roomMapUiType, "GetNextNodeUI", typeof(Vector2Int));
        MethodInfo? updateLayer = FindMethod(
            _roomMapUiType,
            "UpdateCurrentLayer",
            _mapNodeUiType,
            typeof(int),
            typeof(int));
        PropertyInfo? canReady = AccessTools.Property(_mapNodeUiType, "CanReady");
        PropertyInfo? currentStage = AccessTools.Property(_roomMapSpawnerType, "CurrentStage");
        PropertyInfo? stageStep = AccessTools.Property(_roomMapSpawnerType, "StageStep");
        PropertyInfo? mapData = AccessTools.Property(_roomMapSpawnerType, "MapData");
        FieldInfo? layerUis = AccessTools.Field(_roomMapUiType, "m_layerUIs");
        FieldInfo? pathField = AccessTools.Field(_roomMapUiType, "path");
        if (leftDown == null ||
            mapNodePrefix == null ||
            buttonLeftDown == null ||
            buttonPrefix == null ||
            click == null ||
            readDataToUi == null ||
            loadPath == null ||
            getNodeUi == null ||
            updateLayer == null ||
            canReady?.CanWrite != true ||
            currentStage?.CanRead != true ||
            stageStep?.CanRead != true ||
            mapData?.CanRead != true ||
            layerUis == null ||
            pathField == null)
        {
            _log("无法安装地图跳关补丁：当前游戏版本缺少必要的地图显示、路径加载或节点选择入口。");
            return false;
        }

        try
        {
            harmony.Patch(leftDown, prefix: new HarmonyMethod(mapNodePrefix) { priority = Priority.First });
            harmony.Patch(buttonLeftDown, prefix: new HarmonyMethod(buttonPrefix) { priority = Priority.First });
            Installed = true;
            _log("地图跳关已接入地图节点和底层按钮的左键输入流程。");
            return true;
        }
        catch (Exception exception)
        {
            _log("无法安装地图跳关补丁：" + Unwrap(exception).Message);
            return false;
        }
    }

    /// <summary>
    /// 开启或关闭地图跳关模式。
    /// </summary>
    public static bool SetEnabled(bool enabled)
    {
        if (enabled && !Installed)
        {
            LogDiagnostic("地图跳关补丁尚未安装，无法开启该功能。");
            return false;
        }

        if (Enabled == enabled) return true;

        if (!enabled)
        {
            RestoreNormalReachability();
        }

        EnabledState.Set(enabled);
        _pendingTarget = null;
        _pendingMapStage = 0;
        _activeMapInstanceId = 0;
        _observedMap = false;
        _lastDiagnostic = string.Empty;
        _log(enabled ? "地图跳关已开启，可以选择当前地图界面中的任意节点。" : "地图跳关已关闭，地图可达性已恢复。");
        return true;
    }

    /// <summary>
    /// 在 Unity 主线程刷新可选节点，并执行已经捕获的跳转。
    /// </summary>
    public static void Tick()
    {
        if (!Enabled) return;

        object? mapUi = TryGetSingleton(_roomMapUiType);
        if (IsUnityNull(mapUi))
        {
            _pendingTarget = null;
            _pendingMapStage = 0;
            _activeMapInstanceId = 0;
            _observedMap = false;
            return;
        }

        int mapInstanceId = GetUnityInstanceId(mapUi!);
        if (_observedMap && _activeMapInstanceId != 0 && mapInstanceId != _activeMapInstanceId)
        {
            _pendingTarget = null;
            _pendingMapStage = 0;
        }

        _observedMap = true;
        _activeMapInstanceId = mapInstanceId;

        try
        {
            if (_pendingTarget.HasValue)
            {
                Vector2Int target = _pendingTarget.Value;
                int requestedMapStage = _pendingMapStage;
                _pendingTarget = null;
                _pendingMapStage = 0;

                object? spawner = TryGetSingleton(_roomMapSpawnerType);
                if (IsUnityNull(spawner) || GetIntMember(spawner!, "CurrentStage") != requestedMapStage)
                {
                    LogDiagnostic("地图阶段已经变化，旧的节点跳转请求已取消。");
                }
                else
                {
                    TryJumpTo(target);
                }

                mapUi = TryGetSingleton(_roomMapUiType);
            }

            if (!IsUnityNull(mapUi) && CanJumpNow(mapUi!))
            {
                RevealAllLoadedNodes(mapUi!);
            }
        }
        catch (Exception exception)
        {
            LogDiagnostic("地图跳关运行失败：" + Unwrap(exception).Message);
        }
    }

    /// <summary>
    /// 清理待跳转请求、恢复普通地图可达性并关闭模式。
    /// </summary>
    public static void Reset()
    {
        if (Enabled)
        {
            RestoreNormalReachability();
        }

        EnabledState.Set(false);
        _pendingTarget = null;
        _pendingMapStage = 0;
        _activeMapInstanceId = 0;
        _observedMap = false;
        _lastDiagnostic = string.Empty;
    }

    private static bool BeforeMapNodeLeftDown(object __instance)
    {
        if (!Enabled || IsUnityNull(__instance)) return true;

        try
        {
            return HandleMapNodeLeftDown(__instance, nativeInputCanRun: true);
        }
        catch (Exception exception)
        {
            LogDiagnostic("地图节点点击拦截失败：" + Unwrap(exception).Message);
            return false;
        }
    }

    private static bool BeforeUiButtonLeftPointDown(object __instance)
    {
        if (!Enabled || IsUnityNull(__instance)) return true;
        if (__instance is not Component buttonComponent || _mapNodeUiType == null) return true;

        Component? mapNode = buttonComponent.GetComponent(_mapNodeUiType);
        if (IsUnityNull(mapNode)) return true;

        try
        {
            bool buttonActive = GetMember(__instance, "BtnActive") is bool active && active;
            return HandleMapNodeLeftDown(mapNode!, nativeInputCanRun: buttonActive);
        }
        catch (Exception exception)
        {
            LogDiagnostic("地图节点底层按钮拦截失败：" + Unwrap(exception).Message);
            return false;
        }
    }

    private static bool HandleMapNodeLeftDown(object mapNode, bool nativeInputCanRun)
    {
        object? mapUi = TryGetSingleton(_roomMapUiType);
        if (IsUnityNull(mapUi) || !CanJumpNow(mapUi!))
        {
            return false;
        }

        if (!TryGetNodePosition(mapNode, out Vector2Int target))
        {
            return false;
        }

        // 游戏本来就允许的节点继续走原始长按/点击流程，避免无意义地重载地图。
        if (nativeInputCanRun &&
            GetMember(mapNode, "IsReady") is bool ready &&
            ready &&
            GetMember(mapUi, "CurrentReadyNodeList") is IList readyNodes &&
            readyNodes.Contains(mapNode))
        {
            return true;
        }

        object? spawner = TryGetSingleton(_roomMapSpawnerType);
        if (IsUnityNull(spawner))
        {
            return false;
        }

        _pendingTarget = target;
        _pendingMapStage = GetIntMember(spawner!, "CurrentStage");
        return false;
    }

    private static bool TryJumpTo(Vector2Int target)
    {
        object? mapUi = TryGetSingleton(_roomMapUiType);
        object? spawner = TryGetSingleton(_roomMapSpawnerType);
        if (IsUnityNull(mapUi) || IsUnityNull(spawner) || !CanJumpNow(mapUi!))
        {
            LogDiagnostic("当前不是安全的备战状态，地图跳关请求已取消。");
            return false;
        }

        int stageStep = GetIntMember(spawner!, "StageStep");
        object? mapData = GetMember(spawner, "MapData");
        if (stageStep <= 0 || GetMember(mapData, "datas") is not IList layers ||
            target.y < 0 || target.y >= layers.Count)
        {
            LogDiagnostic("目标节点超出当前已生成地图的范围。");
            return false;
        }

        if (!TryCreateJumpPlan(
                layers,
                target,
                stageStep,
                out int targetStage,
                out List<Vector2Int> path,
                out MapJumpPlanFailure planFailure))
        {
            LogDiagnostic($"无法规划地图节点 ({target.x}, {target.y}) 的跳转路径：{FormatPlanFailure(planFailure)}。");
            return false;
        }

        MethodInfo? readDataToUi = FindMethod(_roomMapSpawnerType, "ReadDataToUI", typeof(int), typeof(bool));
        MethodInfo? loadPath = FindMethod(_roomMapUiType, "LoadPath", typeof(List<Vector2Int>));
        MethodInfo? getNodeUi = FindMethod(_roomMapUiType, "GetNextNodeUI", typeof(Vector2Int));
        MethodInfo? click = FindMethod(_mapNodeUiType, "Click");
        if (readDataToUi == null || loadPath == null || getNodeUi == null || click == null)
        {
            LogDiagnostic("当前游戏版本缺少地图跳转所需的公开入口。");
            return false;
        }

        int originalStage = GetIntMember(spawner!, "CurrentStage");
        if (targetStage != originalStage)
        {
            LogDiagnostic("目标节点不属于当前显示的地图阶段，旧的点击请求已取消。");
            return false;
        }

        if (IsUnityNull(getNodeUi.Invoke(mapUi, new object[] { target })))
        {
            LogDiagnostic("目标节点已经不在当前地图界面中，点击请求已取消。");
            return false;
        }

        if (!TryCopyMapPath(mapUi!, out List<Vector2Int> originalPath))
        {
            LogDiagnostic("无法读取当前地图路径，跳转请求已取消。");
            return false;
        }

        bool mutationStarted = false;
        try
        {
            // 与游戏 JumpWaveOrder 保持一致：重载当前阶段，再用目标前置路径让目标进入 Ready。
            mutationStarted = true;
            readDataToUi.Invoke(spawner, new object[] { targetStage, false });
            mapUi = TryGetSingleton(_roomMapUiType);
            if (IsUnityNull(mapUi))
            {
                return FailAndRestoreJump(
                    "地图阶段重载后无法重新取得地图界面。",
                    spawner!,
                    originalStage,
                    originalPath,
                    readDataToUi,
                    loadPath);
            }

            loadPath.Invoke(mapUi, new object[] { path });
            mapUi = TryGetSingleton(_roomMapUiType);
            object? targetNodeUi = IsUnityNull(mapUi) ? null : getNodeUi.Invoke(mapUi, new object[] { target });
            if (IsUnityNull(targetNodeUi) ||
                GetMember(mapUi, "CurrentReadyNodeList") is not IList readyNodes ||
                !readyNodes.Contains(targetNodeUi))
            {
                return FailAndRestoreJump(
                    $"地图节点 ({target.x}, {target.y}) 重载后未进入可选状态。",
                    spawner!,
                    originalStage,
                    originalPath,
                    readDataToUi,
                    loadPath);
            }

            click.Invoke(targetNodeUi, null);
            RequestGameSave();
            _activeMapInstanceId = GetUnityInstanceId(mapUi!);
            _lastDiagnostic = string.Empty;
            _log($"已跳转并选择地图节点 ({target.x}, {target.y})。");
            return true;
        }
        catch (Exception exception)
        {
            string message = "地图节点跳转失败：" + Unwrap(exception).Message;
            return mutationStarted
                ? FailAndRestoreJump(
                    message,
                    spawner!,
                    originalStage,
                    originalPath,
                    readDataToUi,
                    loadPath)
                : LogJumpFailure(message);
        }
    }

    private static bool FailAndRestoreJump(
        string failureMessage,
        object spawner,
        int originalStage,
        List<Vector2Int> originalPath,
        MethodInfo readDataToUi,
        MethodInfo loadPath)
    {
        if (!TryRestoreJumpState(
                spawner,
                originalStage,
                originalPath,
                readDataToUi,
                loadPath,
                out string restoreError))
        {
            EnabledState.Set(false);
            _pendingTarget = null;
            _pendingMapStage = 0;
            _log($"{failureMessage} 回滚地图状态失败：{restoreError}。地图跳关已自动关闭。");
            return false;
        }

        LogDiagnostic(failureMessage + " 已恢复跳转前的地图阶段和路径。");
        return false;
    }

    private static bool TryRestoreJumpState(
        object spawner,
        int originalStage,
        List<Vector2Int> originalPath,
        MethodInfo readDataToUi,
        MethodInfo loadPath,
        out string error)
    {
        try
        {
            readDataToUi.Invoke(spawner, new object[] { originalStage, false });
            object? restoredMapUi = TryGetSingleton(_roomMapUiType);
            if (IsUnityNull(restoredMapUi))
            {
                error = "重载原地图阶段后无法取得地图界面";
                return false;
            }

            loadPath.Invoke(restoredMapUi, new object[] { originalPath });
            error = string.Empty;
            return true;
        }
        catch (Exception exception)
        {
            error = Unwrap(exception).Message;
            return false;
        }
    }

    private static bool TryCopyMapPath(object mapUi, out List<Vector2Int> path)
    {
        path = new List<Vector2Int>();
        if (GetMember(mapUi, "path") is not IList currentPath)
        {
            return false;
        }

        foreach (object? item in currentPath)
        {
            if (item is not Vector2Int coordinate)
            {
                path.Clear();
                return false;
            }

            path.Add(coordinate);
        }

        return true;
    }

    private static bool LogJumpFailure(string message)
    {
        LogDiagnostic(message);
        return false;
    }

    private static bool TryCreateJumpPlan(
        IList layers,
        Vector2Int target,
        int stageStep,
        out int targetStage,
        out List<Vector2Int> path,
        out MapJumpPlanFailure failure)
    {
        targetStage = 0;
        path = new List<Vector2Int>();
        List<IReadOnlyList<MapJumpNode>> plannerLayers = new(layers.Count);
        for (int layerIndex = 0; layerIndex < layers.Count; layerIndex++)
        {
            List<MapJumpNode> plannerNodes = new();
            if (GetMember(layers[layerIndex], "nodes") is IList nodes)
            {
                foreach (object? node in nodes)
                {
                    if (!TryGetPosition(node, out Vector2Int position)) continue;

                    List<MapJumpCoordinate> nextCoordinates = new();
                    if (GetMember(node, "nextPos") is IEnumerable nextPositions)
                    {
                        foreach (object? nextPosition in nextPositions)
                        {
                            if (nextPosition is Vector2Int next)
                            {
                                nextCoordinates.Add(new MapJumpCoordinate(next.x, next.y));
                            }
                        }
                    }

                    plannerNodes.Add(new MapJumpNode(
                        new MapJumpCoordinate(position.x, position.y),
                        nextCoordinates));
                }
            }

            plannerLayers.Add(plannerNodes);
        }

        if (!MapJumpPlanner.TryCreatePlan(
                plannerLayers,
                new MapJumpCoordinate(target.x, target.y),
                stageStep,
                out MapJumpPlan? plan,
                out failure) ||
            plan == null)
        {
            return false;
        }

        targetStage = plan.TargetStage;
        for (int index = 0; index < plan.PredecessorPath.Count; index++)
        {
            MapJumpCoordinate coordinate = plan.PredecessorPath[index];
            path.Add(new Vector2Int(coordinate.X, coordinate.Y));
        }

        return true;
    }

    private static void RevealAllLoadedNodes(object mapUi)
    {
        if (mapUi is not Component mapComponent || !mapComponent.gameObject.activeInHierarchy || _mapNodeUiType == null)
        {
            return;
        }

        bool changed = false;
        // UpdateCurrentLayer 会关闭已通过层的父对象；先恢复已加载层，单独激活节点才会真正可见。
        if (GetMember(mapUi, "m_layerUIs") is IDictionary layerUis)
        {
            foreach (DictionaryEntry entry in layerUis)
            {
                if (entry.Value is Component layerComponent && !layerComponent.gameObject.activeSelf)
                {
                    layerComponent.gameObject.SetActive(true);
                    changed = true;
                }
            }
        }

        Component[] nodes = mapComponent.GetComponentsInChildren(_mapNodeUiType, true);
        for (int index = 0; index < nodes.Length; index++)
        {
            Component node = nodes[index];
            if (!TryGetNodePosition(node, out _))
            {
                continue;
            }

            PropertyInfo? canReady = AccessTools.Property(node.GetType(), "CanReady");
            if (canReady?.CanWrite == true && canReady.GetValue(node, null) is bool current && !current)
            {
                canReady.SetValue(node, true, null);
                changed = true;
            }
        }

        if (changed)
        {
            FindMethod(_roomMapUiType, "ForceUpdateLine")?.Invoke(mapUi, null);
        }
    }

    private static bool CanJumpNow(object mapUi)
    {
        if (!IsUnityNull(GetMember(mapUi, "pendingSubLevelNode"))) return false;

        object? gameController = TryGetSingleton(_gameControllerType);
        if (IsUnityNull(gameController) || GetMember(gameController, "GameIsOver") is not bool gameIsOver || gameIsOver)
        {
            return false;
        }

        object? durationController = TryGetSingleton(_waveDurationControllerType);
        object? waveProgressController = TryGetSingleton(_waveProgressControllerType);
        object? waveState = GetMember(waveProgressController, "CurrentWaveState");
        object? inWavingValue = GetMember(durationController, "isInWaving");
        object? templateLockValue = GetMember(durationController, "templateLock");
        string waveType = GetMember(durationController, "waveType")?.ToString() ?? string.Empty;
        return !IsUnityNull(durationController)
               && !IsUnityNull(waveProgressController)
               && inWavingValue is bool inWaving
               && !inWaving
               && templateLockValue is bool templateLock
               && !templateLock
               && string.Equals(waveType, "afterWave", StringComparison.Ordinal)
               && waveState != null
               && GetMember(waveState, "CurrentRunningNode") == null;
    }

    private static string FormatPlanFailure(MapJumpPlanFailure failure) => failure switch
    {
        MapJumpPlanFailure.InvalidStageStep => "地图阶段步长无效",
        MapJumpPlanFailure.MapUnavailable => "地图数据为空",
        MapJumpPlanFailure.InvalidTargetCoordinate => "目标坐标无效",
        MapJumpPlanFailure.TargetLayerOutOfRange => "目标层超出已生成地图范围",
        MapJumpPlanFailure.TargetNodeNotFound => "目标节点不存在",
        MapJumpPlanFailure.PreviousLayerUnavailable => "目标节点的前一层不可用",
        MapJumpPlanFailure.ConnectedPredecessorNotFound => "找不到与目标相连的前置节点",
        _ => "未知规划错误"
    };

    private static void RestoreNormalReachability()
    {
        try
        {
            object? mapUi = TryGetSingleton(_roomMapUiType);
            if (IsUnityNull(mapUi) || _mapNodeUiType == null) return;

            MethodInfo? updateLayer = FindMethod(
                _roomMapUiType,
                "UpdateCurrentLayer",
                _mapNodeUiType,
                typeof(int),
                typeof(int));
            MethodInfo? getNodeUi = FindMethod(_roomMapUiType, "GetNextNodeUI", typeof(Vector2Int));
            if (updateLayer == null || getNodeUi == null) return;

            if (GetMember(mapUi, "path") is not IList path || path.Count == 0)
            {
                updateLayer.Invoke(mapUi, new object?[] { null, -1, 0 });
                return;
            }

            if (path[path.Count - 1] is not Vector2Int currentPosition) return;
            object? currentNodeUi = getNodeUi.Invoke(mapUi, new object[] { currentPosition });
            updateLayer.Invoke(mapUi, new[] { currentNodeUi, (object)currentPosition.y, currentPosition.y + 1 });
        }
        catch (Exception exception)
        {
            LogDiagnostic("恢复普通地图可达性失败：" + Unwrap(exception).Message);
        }
    }

    private static void RequestGameSave()
    {
        try
        {
            object? saveHandler = TryGetSingleton(_guiSaveHandlerType);
            MethodInfo? save = FindMethod(
                _guiSaveHandlerType,
                "SaveDurationInValidGameTick",
                typeof(string),
                typeof(string),
                typeof(int));
            if (IsUnityNull(saveHandler) || save == null) return;

            ParameterInfo[] parameters = save.GetParameters();
            object?[] arguments = new object?[parameters.Length];
            for (int index = 0; index < parameters.Length; index++)
            {
                ParameterInfo parameter = parameters[index];
                arguments[index] = parameter.HasDefaultValue
                    ? parameter.DefaultValue
                    : parameter.ParameterType.IsValueType
                        ? Activator.CreateInstance(parameter.ParameterType)
                        : null;
            }

            save.Invoke(saveHandler, arguments);
        }
        catch (Exception exception)
        {
            LogDiagnostic("地图跳转完成，但请求游戏存档失败：" + Unwrap(exception).Message);
        }
    }

    private static bool TryGetNodePosition(object nodeUi, out Vector2Int position) =>
        TryGetPosition(GetMember(nodeUi, "nodeData"), out position);

    private static bool TryGetPosition(object? nodeData, out Vector2Int position)
    {
        object? value = GetMember(nodeData, "pos");
        if (value is Vector2Int vector)
        {
            position = vector;
            return true;
        }

        position = default;
        return false;
    }

    private static object? TryGetSingleton(Type? type)
    {
        if (type == null) return null;
        try
        {
            PropertyInfo? property = type.GetProperty(
                "Instance",
                BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static | BindingFlags.FlattenHierarchy);
            if (property != null) return property.GetValue(null, null);

            FieldInfo? field = type.GetField(
                                   "Instance",
                                   BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static | BindingFlags.FlattenHierarchy)
                               ?? type.GetField(
                                   "instance",
                                   BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static | BindingFlags.FlattenHierarchy);
            return field?.GetValue(null);
        }
        catch
        {
            return null;
        }
    }

    private static object? GetMember(object? target, string name)
    {
        if (target == null) return null;
        Type type = target.GetType();
        const BindingFlags flags = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.FlattenHierarchy;
        PropertyInfo? property = type.GetProperty(name, flags);
        if (property != null) return property.GetValue(target, null);
        FieldInfo? field = type.GetField(name, flags);
        return field?.GetValue(target);
    }

    private static int GetIntMember(object target, string name)
    {
        object? value = GetMember(target, name);
        return value == null ? 0 : Convert.ToInt32(value, CultureInfo.InvariantCulture);
    }

    private static MethodInfo? FindMethod(Type? type, string name, params Type[] parameterTypes) =>
        type?.GetMethod(
            name,
            BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static | BindingFlags.FlattenHierarchy,
            null,
            parameterTypes,
            null);

    private static int GetUnityInstanceId(object value) =>
        value is UnityEngine.Object unityObject && unityObject != null ? unityObject.GetInstanceID() : 0;

    private static bool IsUnityNull(object? value) =>
        value == null || value is UnityEngine.Object unityObject && unityObject == null;

    private static Exception Unwrap(Exception exception) =>
        exception is TargetInvocationException target && target.InnerException != null
            ? target.InnerException
            : exception;

    private static void LogDiagnostic(string message)
    {
        if (string.Equals(_lastDiagnostic, message, StringComparison.Ordinal)) return;
        _lastDiagnostic = message;
        _log(message);
    }
}
