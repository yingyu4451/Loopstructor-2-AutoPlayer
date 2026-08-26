using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Reflection;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using UnityEngine;

namespace Loopstructor.AutoPlayer.Plugin;

internal sealed class RuntimeBridge
{
    private const double SlowCommandThresholdMs = 33.0;
    private readonly Dictionary<string, MethodInfo> _commands = new(StringComparer.OrdinalIgnoreCase);
    private readonly LiveEnemyThreatReader _liveEnemyThreatReader = new();
    private Type? _resultType;
    private FieldInfo? _resultSuccess;
    private FieldInfo? _resultMessage;
    private FieldInfo? _resultData;
    private FieldInfo? _resultSuggestion;
    private PropertyInfo? _waveControllerInstance;
    private PropertyInfo? _waveIsInWaving;
    private FieldInfo? _waveRemainingEnemies;
    private PropertyInfo? _gameControllerInstance;
    private PropertyInfo? _gameIsOver;
    private PropertyInfo? _waveFunctionOptionFlowInstance;
    private PropertyInfo? _waveFunctionOptionFlowHasPendingFlow;
    private PropertyInfo? _waveFunctionOptionFlowDescription;
    private PropertyInfo? _roomMapUiInstance;
    private Animator? _roomMapAnimator;

    private static readonly (string Command, string Type, string Method)[] RequiredContract =
    {
        ("queryState", "GuiGameAutomation.Runtime.GuiGameMcpStateRuntime", "QueryState"),
        ("queryUiInteractables", "GuiGameAutomation.Runtime.GuiGameMcpStateRuntime", "QueryUiInteractables"),
        ("queryFrontend", "GuiGameAutomation.Runtime.GuiGameMcpStartFlowRuntime", "QueryFrontendState"),
        ("openCommonMode", "GuiGameAutomation.Runtime.GuiGameMcpStartFlowRuntime", "OpenCommonModePanel"),
        ("prepareCommonMode", "GuiGameAutomation.Runtime.GuiGameMcpStartFlowRuntime", "PrepareCommonMode"),
        ("submitCommonMode", "GuiGameAutomation.Runtime.GuiGameMcpStartFlowRuntime", "SubmitCommonMode"),
        ("continueGame", "GuiGameAutomation.Runtime.GuiGameMcpStartFlowRuntime", "ContinueGame"),
        ("enterRandomMode", "GuiGameAutomation.Runtime.GuiGameMcpStartFlowRuntime", "EnterRandomMode"),
        ("queryRandomMode", "GuiGameAutomation.Runtime.GuiGameMcpStartFlowRuntime", "QueryRandomModeOptions"),
        ("selectRandomVehicle", "GuiGameAutomation.Runtime.GuiGameMcpStartFlowRuntime", "SelectRandomVehicle"),
        ("selectRandomFetter", "GuiGameAutomation.Runtime.GuiGameMcpStartFlowRuntime", "SelectRandomFetter"),
        ("submitRandomMode", "GuiGameAutomation.Runtime.GuiGameMcpStartFlowRuntime", "SubmitRandomMode"),
        ("queryAffordances", "GuiGameAutomation.Runtime.GuiGameMcpAffordanceRuntime", "QueryPlayerAffordances"),
        ("queryWave", "GuiGameAutomation.Runtime.GuiGameMcpAffordanceRuntime", "QueryWaveRuntimeState"),
        ("queryReward", "GuiGameAutomation.Runtime.GuiGameMcpShopRewardRuntime", "QueryRewardState"),
        ("collectRewardObject", "GuiGameAutomation.Runtime.GuiGameMcpShopRewardRuntime", "CollectRewardObject"),
        ("chooseRewardOption", "GuiGameAutomation.Runtime.GuiGameMcpShopRewardRuntime", "ChooseRewardOption"),
        ("skipReward", "GuiGameAutomation.Runtime.GuiGameMcpShopRewardRuntime", "SkipReward"),
        ("closeShop", "GuiGameAutomation.Runtime.GuiGameMcpShopRewardRuntime", "CloseShop"),
        ("queryEventOptions", "GuiGameAutomation.Runtime.GuiGameMcpUiRuntime", "QueryEventOptions"),
        ("uiClick", "GuiGameAutomation.Runtime.GuiGameMcpUiRuntime", "UiClick"),
        ("chooseWaveFunctionOption", "GuiGameAutomation.Runtime.GuiGameMcpUiRuntime", "ChooseWaveFunctionOption"),
        ("submitPopOption", "GuiGameAutomation.Runtime.GuiGameMcpUiRuntime", "SubmitPopOption"),
        ("cancelInputJob", "GuiGameAutomation.Runtime.GuiGameMcpUiRuntime", "CancelInputJob"),
        ("cancelDisposable", "GuiGameAutomation.Runtime.GuiGameMcpDisposableRuntime", "CancelDisposable"),
        ("queryVehicle", "GuiGameAutomation.Runtime.GuiGameMcpVehicleRuntime", "QueryVehicleState"),
        ("cancelVehicleInteraction", "GuiGameAutomation.Runtime.GuiGameMcpVehicleRuntime", "CancelVehicleInteraction"),
        ("moveVehicleInTrain", "GuiGameAutomation.Runtime.GuiGameMcpVehicleRuntime", "MoveVehicleInTrain"),
        ("queryMap", "GuiGameAutomation.Runtime.GuiGameMcpMapRuntime", "QueryMapState"),
        ("uiClickMapButton", "GuiGameAutomation.Runtime.GuiGameMcpMapRuntime", "UiClickMapButton"),
        ("selectMapNode", "GuiGameAutomation.Runtime.GuiGameMcpMapRuntime", "SelectMapNode"),
        ("selectSublevel", "GuiGameAutomation.Runtime.GuiGameMcpMapRuntime", "SelectSublevel"),
        ("setTimeSpeed", "GuiGameAutomation.Runtime.GuiGameMcpMapRuntime", "SetTimeSpeed"),
        ("startWave", "GuiGameAutomation.Runtime.GuiGameMcpMapRuntime", "StartWave")
    };

    private static readonly (string Command, string Type, string Method)[] OptionalBattleContract =
    {
        ("queryWaveThreats", "GuiGameAutomation.Runtime.GuiGameMcpWaveThreatRuntime", "QueryWaveThreats"),
        ("queryDisposable", "GuiGameAutomation.Runtime.GuiGameMcpDisposableRuntime", "QueryDisposableState"),
        ("queryDisposableGridOptions", "GuiGameAutomation.Runtime.GuiGameMcpDisposableRuntime", "QueryDisposableGridOptions"),
        ("useDisposable", "GuiGameAutomation.Runtime.GuiGameMcpDisposableRuntime", "UseDisposable"),
        ("confirmDisposableGrid", "GuiGameAutomation.Runtime.GuiGameMcpDisposableRuntime", "ConfirmDisposableGrid"),
        ("confirmDisposableWorld", "GuiGameAutomation.Runtime.GuiGameMcpDisposableRuntime", "ConfirmDisposableWorld"),
        ("confirmDisposableTarget", "GuiGameAutomation.Runtime.GuiGameMcpDisposableRuntime", "ConfirmDisposableTarget"),
        ("queryTrain", "GuiGameAutomation.Runtime.GuiGameMcpVehicleRuntime", "QueryTrainState"),
        ("queryRail", "GuiGameAutomation.Runtime.GuiGameMcpLineRuntime", "QueryRailState"),
        ("queryCatapults", "GuiGameAutomation.Runtime.GuiGameMcpLineRuntime", "QueryCatapults"),
        ("previewRailPath", "GuiGameAutomation.Runtime.GuiGameMcpLineRuntime", "PreviewRailPath"),
        ("drawRailPath", "GuiGameAutomation.Runtime.GuiGameMcpLineRuntime", "DrawRailPath"),
        ("insertPointFromLine", "GuiGameAutomation.Runtime.GuiGameMcpLineRuntime", "InsertPointFromLine"),
        ("deleteLinePoint", "GuiGameAutomation.Runtime.GuiGameMcpLineRuntime", "DeleteLinePoint"),
        ("queryMovableStationState", "GuiGameAutomation.Runtime.GuiGameMcpStationRuntime", "QueryMovableStationState"),
        ("startStationMove", "GuiGameAutomation.Runtime.GuiGameMcpStationRuntime", "StartStationMove"),
        ("confirmStationMoveGrid", "GuiGameAutomation.Runtime.GuiGameMcpStationRuntime", "ConfirmStationMoveGrid"),
        ("placeVehicleOnLine", "GuiGameAutomation.Runtime.GuiGameMcpVehicleRuntime", "PlaceVehicleOnLine"),
        ("moveTrainToLine", "GuiGameAutomation.Runtime.GuiGameMcpVehicleRuntime", "MoveTrainToLine"),
        ("openMergePanel", "GuiGameAutomation.Runtime.GuiGameMcpRebuildSellRuntime", "OpenMergePanel"),
        ("queryMergeState", "GuiGameAutomation.Runtime.GuiGameMcpRebuildSellRuntime", "QueryMergeState"),
        ("selectMergeVehicle", "GuiGameAutomation.Runtime.GuiGameMcpRebuildSellRuntime", "SelectMergeVehicle"),
        ("submitMergeSelection", "GuiGameAutomation.Runtime.GuiGameMcpRebuildSellRuntime", "SubmitMergeSelection"),
        ("chooseMergeFetter", "GuiGameAutomation.Runtime.GuiGameMcpRebuildSellRuntime", "ChooseMergeFetter")
    };

    public bool IsAvailable { get; private set; }
    public IReadOnlyList<string> MissingMembers { get; private set; } = Array.Empty<string>();
    public IReadOnlyList<string> AvailableCommands => new List<string>(_commands.Keys);
    public string LastCommand { get; private set; } = string.Empty;
    public double LastCommandDurationMs { get; private set; }
    public string MaxCommand { get; private set; } = string.Empty;
    public double MaxCommandDurationMs { get; private set; }
    public int SlowCommandCount { get; private set; }
    public bool WavePulseAvailable =>
        _waveControllerInstance != null &&
        _waveIsInWaving != null &&
        _waveRemainingEnemies != null;

    public void ResetMetrics()
    {
        LastCommand = string.Empty;
        LastCommandDurationMs = 0;
        MaxCommand = string.Empty;
        MaxCommandDurationMs = 0;
        SlowCommandCount = 0;
    }

    public bool Initialize()
    {
        List<string> missing = new();
        _commands.Clear();
        foreach ((string command, string typeName, string methodName) in RequiredContract)
        {
            Type? type = FindType(typeName);
            MethodInfo? method = type?.GetMethod(
                methodName,
                BindingFlags.Public | BindingFlags.Static,
                null,
                new[] { typeof(string) },
                null);
            if (method == null)
            {
                missing.Add(typeName + "." + methodName);
                continue;
            }

            _commands[command] = method;
        }

        foreach ((string command, string typeName, string methodName) in OptionalBattleContract)
        {
            Type? type = FindType(typeName);
            MethodInfo? method = type?.GetMethod(
                methodName,
                BindingFlags.Public | BindingFlags.Static,
                null,
                new[] { typeof(string) },
                null);
            if (method != null) _commands[command] = method;
        }

        const string resultTypeName = "GuiGameAutomation.Runtime.GuiGameMcpResult";
        _resultType = FindType(resultTypeName);
        _resultSuccess = _resultType?.GetField("success", BindingFlags.Public | BindingFlags.Instance);
        _resultMessage = _resultType?.GetField("message", BindingFlags.Public | BindingFlags.Instance);
        _resultData = _resultType?.GetField("data", BindingFlags.Public | BindingFlags.Instance);
        _resultSuggestion = _resultType?.GetField("suggestion", BindingFlags.Public | BindingFlags.Instance);
        InitializeWavePulseContract();
        InitializeWaveFunctionOptionFlowContract();
        InitializeMapAnimationContract();
        _liveEnemyThreatReader.Initialize();
        MissingMembers = missing;
        IsAvailable = missing.Count == 0;
        return IsAvailable;
    }

    public bool HasCommand(string command) =>
        !string.IsNullOrWhiteSpace(command) &&
        (_commands.ContainsKey(command) ||
         string.Equals(command, "queryMergeUiState", StringComparison.OrdinalIgnoreCase) ||
         string.Equals(command, "closeMergePanel", StringComparison.OrdinalIgnoreCase) ||
         string.Equals(command, "confirmMergeSettlement", StringComparison.OrdinalIgnoreCase));

    public bool TryGetWavePulse(out bool inWave, out bool gameOver, out int remainingEnemies)
    {
        inWave = false;
        gameOver = false;
        remainingEnemies = -1;
        if (!WavePulseAvailable) return false;

        try
        {
            object? waveController = _waveControllerInstance!.GetValue(null, null);
            if (waveController == null || _waveIsInWaving!.GetValue(waveController, null) is not bool active)
            {
                return false;
            }

            inWave = active;
            if (_waveRemainingEnemies!.GetValue(waveController) is int remaining)
            {
                remainingEnemies = Math.Max(0, remaining);
            }

            object? gameController = _gameControllerInstance?.GetValue(null, null);
            if (gameController != null && _gameIsOver?.GetValue(gameController, null) is bool over)
            {
                gameOver = over;
            }

            return true;
        }
        catch
        {
            inWave = false;
            gameOver = false;
            remainingEnemies = -1;
            return false;
        }
    }

    public bool TryGetWaveFunctionOptionFlow(out bool pending, out string description)
    {
        pending = false;
        description = string.Empty;
        if (_waveFunctionOptionFlowInstance == null ||
            _waveFunctionOptionFlowHasPendingFlow == null ||
            _waveFunctionOptionFlowDescription == null)
        {
            return false;
        }

        try
        {
            object? runtime = _waveFunctionOptionFlowInstance.GetValue(null, null);
            if (runtime == null ||
                _waveFunctionOptionFlowHasPendingFlow.GetValue(runtime, null) is not bool hasPendingFlow)
            {
                return false;
            }

            pending = hasPendingFlow;
            description = _waveFunctionOptionFlowDescription.GetValue(runtime, null) as string ?? string.Empty;
            return true;
        }
        catch
        {
            pending = false;
            description = string.Empty;
            return false;
        }
    }

    public JObject Invoke(string command, JObject? arguments = null)
    {
        Stopwatch stopwatch = Stopwatch.StartNew();
        bool mutationInvocationStarted = false;
        try
        {
            if (string.Equals(command, "queryReward", StringComparison.OrdinalIgnoreCase))
            {
                return InvokeLightweightRewardQuery();
            }

            if (string.Equals(command, "selectRandomFetter", StringComparison.OrdinalIgnoreCase) &&
                (arguments?["targetInstanceId"]?.Value<int>() ?? 0) != 0)
            {
                return RandomModeVisibleFetterReader.SelectExact(arguments, out mutationInvocationStarted);
            }

            if (string.Equals(command, "chooseRewardOption", StringComparison.OrdinalIgnoreCase))
            {
                return InvokeLightweightRewardSelection(arguments);
            }

            if (string.Equals(command, "skipReward", StringComparison.OrdinalIgnoreCase))
            {
                return InvokeLightweightRewardSkip(arguments);
            }

            if (string.Equals(command, "collectRewardObject", StringComparison.OrdinalIgnoreCase))
            {
                if (RewardUiRuntimeFallback.TryCollectRewardObject(arguments, out JObject rewardCollection))
                {
                    return rewardCollection;
                }

                return LightweightContractUnavailable(
                    command,
                    "当前无法绑定已注册奖励物及其玩家点击链；已阻止回落到原生全场景扫描命令，请重新查询奖励状态。");
            }

            if (string.Equals(command, "queryMergeState", StringComparison.OrdinalIgnoreCase))
            {
                return InvokeLightweightMergeQuery();
            }

            if (string.Equals(command, "selectMergeVehicle", StringComparison.OrdinalIgnoreCase))
            {
                return InvokeLightweightMergeSelection(arguments);
            }

            if (string.Equals(command, "queryMergeUiState", StringComparison.OrdinalIgnoreCase) &&
                MergeUiRuntimeFallback.TryQueryState(out JObject mergeUiState))
            {
                return mergeUiState;
            }

            if (string.Equals(command, "closeMergePanel", StringComparison.OrdinalIgnoreCase) &&
                MergeUiRuntimeFallback.TryClosePanel(out JObject mergeClose))
            {
                return mergeClose;
            }

            if (string.Equals(command, "confirmMergeSettlement", StringComparison.OrdinalIgnoreCase) &&
                MergeUiRuntimeFallback.TryConfirmSettlement(out JObject mergeConfirmation))
            {
                return mergeConfirmation;
            }

            if (string.Equals(command, "chooseWaveFunctionOption", StringComparison.OrdinalIgnoreCase))
            {
                return InvokeLightweightWaveFunctionSelection(arguments);
            }

            if (string.Equals(command, "queryEventOptions", StringComparison.OrdinalIgnoreCase))
            {
                return InvokeLightweightWaveFunctionQuery(arguments);
            }

            if (!_commands.TryGetValue(command, out MethodInfo method))
            {
                return Error("自动游玩命令不可用：" + command);
            }

            string jsonArguments = arguments == null ? "{}" : arguments.ToString(Formatting.None);
            mutationInvocationStarted = IsMutatingCommand(command);
            object? result = method.Invoke(null, new object[] { jsonArguments });
            if (result == null)
            {
                return mutationInvocationStarted
                    ? UncertainMutationError("自动游玩写命令已经开始执行，但运行时返回了空结果：" + command)
                    : Error("自动游玩运行时返回了空结果：" + command);
            }

            JObject adapted = AdaptRuntimeResult(result);
            if (string.Equals(command, "queryWaveThreats", StringComparison.OrdinalIgnoreCase) &&
                adapted["success"]?.Value<bool>() != false)
            {
                bool pulseAvailable = TryGetWavePulse(
                    out bool inWave,
                    out _,
                    out int remainingEnemies);
                _liveEnemyThreatReader.TryEnrich(
                    adapted,
                    pulseAvailable,
                    inWave,
                    remainingEnemies);
            }

            return adapted;
        }
        catch (TargetInvocationException exception)
        {
            Exception inner = exception.InnerException ?? exception;
            string message = "调用自动游玩运行时失败（" + inner.GetType().Name + "）：" + inner.Message;
            return mutationInvocationStarted ? UncertainMutationError(message) : Error(message);
        }
        catch (Exception exception)
        {
            string message = "调用自动游玩运行时失败（" + exception.GetType().Name + "）：" + exception.Message;
            return mutationInvocationStarted ? UncertainMutationError(message) : Error(message);
        }
        finally
        {
            stopwatch.Stop();
            LastCommand = command;
            LastCommandDurationMs = stopwatch.Elapsed.TotalMilliseconds;
            if (LastCommandDurationMs > MaxCommandDurationMs)
            {
                MaxCommand = command;
                MaxCommandDurationMs = LastCommandDurationMs;
            }

            if (LastCommandDurationMs >= SlowCommandThresholdMs)
            {
                SlowCommandCount++;
            }
        }
    }

    public bool TryGetMapProgress(out int stage, out int layer)
    {
        stage = -1;
        layer = -1;
        try
        {
            Type? spawnerType = FindType("MetroTD.RoomSystem.RoomMapSpawner");
            PropertyInfo? spawnerInstanceProperty = spawnerType?.GetProperty(
                "Instance",
                BindingFlags.Public | BindingFlags.Static | BindingFlags.FlattenHierarchy);
            object? spawner = spawnerInstanceProperty?.GetValue(null, null);
            PropertyInfo? stageProperty = spawnerType?.GetProperty(
                "CurrentStage",
                BindingFlags.Public | BindingFlags.Instance);
            if (spawner == null || stageProperty?.GetValue(spawner, null) is not int currentStage)
            {
                return false;
            }

            stage = currentStage;
            Type? mapType = FindType("MetroTD.RoomSystem.RoomMapUI");
            PropertyInfo? mapInstanceProperty = mapType?.GetProperty(
                "Instance",
                BindingFlags.Public | BindingFlags.Static | BindingFlags.FlattenHierarchy);
            object? map = mapInstanceProperty?.GetValue(null, null);
            FieldInfo? pathField = mapType?.GetField(
                "path",
                BindingFlags.Public | BindingFlags.Instance);
            if (map != null && pathField?.GetValue(map) is IList path && path.Count > 0)
            {
                object? position = path[path.Count - 1];
                PropertyInfo? yProperty = position?.GetType().GetProperty(
                    "y",
                    BindingFlags.Public | BindingFlags.Instance);
                FieldInfo? yField = position?.GetType().GetField(
                    "y",
                    BindingFlags.Public | BindingFlags.Instance);
                object? y = yProperty?.GetValue(position, null) ?? yField?.GetValue(position);
                if (y != null)
                {
                    layer = Convert.ToInt32(y);
                }
            }

            return true;
        }
        catch
        {
            stage = -1;
            layer = -1;
            return false;
        }
    }

    public bool TryGetMapOpenAnimationProgress(
        out bool openAnimationObserved,
        out bool completed,
        out float normalizedTime)
    {
        openAnimationObserved = false;
        completed = false;
        normalizedTime = 0f;
        if (_roomMapUiInstance == null) return false;

        try
        {
            object? map = _roomMapUiInstance.GetValue(null, null);
            if (map is not Component component ||
                component.gameObject == null ||
                !component.gameObject.activeInHierarchy)
            {
                return false;
            }

            Animator? animator = _roomMapAnimator;
            if (animator == null || animator.gameObject != component.gameObject)
            {
                animator = component.GetComponent<Animator>();
                _roomMapAnimator = animator;
            }
            if (animator == null || !animator.isActiveAndEnabled || animator.layerCount <= 0)
            {
                return false;
            }

            int openStateHash = Animator.StringToHash("Open");
            bool inTransition = animator.IsInTransition(0);
            AnimatorStateInfo current = animator.GetCurrentAnimatorStateInfo(0);
            bool currentIsOpen = current.IsName("Open") || current.shortNameHash == openStateHash;
            AnimatorStateInfo next = inTransition
                ? animator.GetNextAnimatorStateInfo(0)
                : default;
            bool nextIsOpen = inTransition &&
                              (next.IsName("Open") || next.shortNameHash == openStateHash);

            openAnimationObserved = currentIsOpen || nextIsOpen;
            normalizedTime = nextIsOpen ? next.normalizedTime : current.normalizedTime;
            completed = currentIsOpen && !inTransition && current.normalizedTime >= 1f;
            return true;
        }
        catch
        {
            openAnimationObserved = false;
            completed = false;
            normalizedTime = 0f;
            return false;
        }
    }

    private void InitializeWavePulseContract()
    {
        Type? waveType = FindType("MetroTD.RoomSystem.WaveDurationController");
        _waveControllerInstance = waveType?.GetProperty(
            "Instance",
            BindingFlags.Public | BindingFlags.Static | BindingFlags.FlattenHierarchy);
        _waveIsInWaving = waveType?.GetProperty(
            "isInWaving",
            BindingFlags.Public | BindingFlags.Instance);
        _waveRemainingEnemies = waveType?.GetField(
            "m_lastEnemyCount",
            BindingFlags.NonPublic | BindingFlags.Instance);

        Type? gameType = FindType("MetroTD.GameController");
        _gameControllerInstance = gameType?.GetProperty(
            "Instance",
            BindingFlags.Public | BindingFlags.Static | BindingFlags.FlattenHierarchy);
        _gameIsOver = gameType?.GetProperty(
            "GameIsOver",
            BindingFlags.Public | BindingFlags.Instance);
    }

    private void InitializeWaveFunctionOptionFlowContract()
    {
        Type? runtimeType = FindType("MetroTD.UISystem.WaveFunctionOptionFlowRuntime");
        _waveFunctionOptionFlowInstance = runtimeType?.GetProperty(
            "Instance",
            BindingFlags.Public | BindingFlags.Static | BindingFlags.FlattenHierarchy);
        _waveFunctionOptionFlowHasPendingFlow = runtimeType?.GetProperty(
            "HasPendingFlow",
            BindingFlags.Public | BindingFlags.Instance);
        _waveFunctionOptionFlowDescription = runtimeType?.GetProperty(
            "PendingFlowDescription",
            BindingFlags.Public | BindingFlags.Instance);
    }

    private void InitializeMapAnimationContract()
    {
        _roomMapAnimator = null;
        Type? mapType = FindType("MetroTD.RoomSystem.RoomMapUI");
        _roomMapUiInstance = mapType?.GetProperty(
            "Instance",
            BindingFlags.Public | BindingFlags.Static | BindingFlags.FlattenHierarchy);
    }

    private JObject AdaptRuntimeResult(object result)
    {
        if (result is JObject json) return json;
        if (_resultType == null
            || !_resultType.IsInstanceOfType(result)
            || _resultSuccess == null
            || _resultMessage == null
            || _resultData == null
            || _resultSuggestion == null)
        {
            return JObject.FromObject(result);
        }

        return new JObject
        {
            ["success"] = ToJsonToken(_resultSuccess.GetValue(result)),
            ["message"] = ToJsonToken(_resultMessage.GetValue(result)),
            ["data"] = ToJsonToken(_resultData.GetValue(result)),
            ["suggestion"] = ToJsonToken(_resultSuggestion.GetValue(result))
        };
    }

    private static JToken ToJsonToken(object? value) => value switch
    {
        null => JValue.CreateNull(),
        JToken token => token,
        _ => JToken.FromObject(value)
    };

    public bool IsFrontEndInitializationComplete(out string message)
    {
        const string globalTypeName = "ActFramework_ByHZR.MainLoop.Global";
        Type? globalType = FindType(globalTypeName);
        PropertyInfo? globalInstanceProperty = globalType?.GetProperty(
            "gm",
            BindingFlags.Public | BindingFlags.Static);
        PropertyInfo? sceneInstanceProperty = globalType?.GetProperty(
            "sceneGm",
            BindingFlags.Public | BindingFlags.Static);
        PropertyInfo? isLoadingProperty = globalType?.GetProperty(
            "isLoading",
            BindingFlags.Public | BindingFlags.Instance);
        if (globalInstanceProperty == null || sceneInstanceProperty == null || isLoadingProperty == null)
        {
            message = "正在等待游戏前端就绪契约。";
            return false;
        }

        try
        {
            object? globalInstance = globalInstanceProperty.GetValue(null, null);
            if (globalInstance == null)
            {
                message = "正在等待游戏全局模块实例。";
                return false;
            }

            if (isLoadingProperty.GetValue(globalInstance, null) is not bool isLoading || isLoading)
            {
                message = "正在等待游戏全局模块完成初始化。";
                return false;
            }

            object? sceneInstance = sceneInstanceProperty.GetValue(null, null);
            PropertyInfo? sceneLoadingProperty = sceneInstance?.GetType().GetProperty(
                "isLoading",
                BindingFlags.Public | BindingFlags.Instance);
            if (sceneInstance == null ||
                sceneLoadingProperty?.GetValue(sceneInstance, null) is not bool sceneIsLoading ||
                sceneIsLoading)
            {
                message = "正在等待当前游戏场景完成初始化。";
                return false;
            }

            message = "游戏前端模块已完成初始化。";
            return true;
        }
        catch (Exception exception)
        {
            message = "正在等待可读取的游戏前端状态：" +
                      (exception is TargetInvocationException target && target.InnerException != null
                          ? target.InnerException.Message
                          : exception.Message);
            return false;
        }
    }

    public bool TryDisableCommonModeTutorial(out string message)
    {
        const string panelTypeName = "MetroTD.CharacterSystem.UI.CharacterChooseMainPanel";
        Type? panelType = FindType(panelTypeName);
        FieldInfo? toggleField = panelType?.GetField(
            "tutorToggle",
            BindingFlags.Public | BindingFlags.Instance);
        if (panelType == null || toggleField == null)
        {
            message = "普通模式教程开关契约不可用。";
            return false;
        }

        try
        {
            UnityEngine.Object[] panels = Resources.FindObjectsOfTypeAll(panelType);
            object? panel = panels
                .OfType<Component>()
                .FirstOrDefault(component => component.gameObject.scene.IsValid() && component.gameObject.activeInHierarchy)
                ?? panels.FirstOrDefault();
            object? toggle = panel == null ? null : toggleField.GetValue(panel);
            MethodInfo? setValue = toggle?.GetType().GetMethod(
                "SetValue",
                BindingFlags.Public | BindingFlags.Instance,
                null,
                new[] { typeof(bool) },
                null);
            PropertyInfo? value = toggle?.GetType().GetProperty(
                "Value",
                BindingFlags.Public | BindingFlags.Instance);
            if (toggle == null || setValue == null || value == null)
            {
                message = "正在等待普通模式教程开关初始化。";
                return false;
            }

            setValue.Invoke(toggle, new object[] { false });
            if (value.GetValue(toggle, null) is not bool enabled || enabled)
            {
                message = "无法验证普通模式教程开关已关闭。";
                return false;
            }

            message = "已关闭普通模式教程开关。";
            return true;
        }
        catch (Exception exception)
        {
            message = "无法关闭普通模式教程开关：" + Unwrap(exception).Message;
            return false;
        }
    }

    public bool TryGetGameMode(out string mode, out string message)
    {
        mode = string.Empty;
        Type? managerType = FindType("GameProgressManager");
        PropertyInfo? instanceProperty = managerType?.GetProperty(
            "Instance",
            BindingFlags.Public | BindingFlags.Static | BindingFlags.FlattenHierarchy);
        PropertyInfo? modeProperty = managerType?.GetProperty(
            "Mode",
            BindingFlags.Public | BindingFlags.Instance);
        if (instanceProperty == null || modeProperty == null)
        {
            message = "游戏模式验证契约不可用。";
            return false;
        }

        try
        {
            object? instance = instanceProperty.GetValue(null, null);
            object? value = instance == null ? null : modeProperty.GetValue(instance, null);
            if (value == null)
            {
                message = "正在等待游戏模式初始化。";
                return false;
            }

            mode = value.ToString() ?? string.Empty;
            message = "当前游戏模式为 " + mode + "。";
            return !string.IsNullOrEmpty(mode);
        }
        catch (Exception exception)
        {
            message = "无法验证当前游戏模式：" + Unwrap(exception).Message;
            return false;
        }
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

    private static Exception Unwrap(Exception exception) =>
        exception is TargetInvocationException target && target.InnerException != null
            ? target.InnerException
            : exception;

    private static bool IsMutatingCommand(string command) =>
        !command.StartsWith("query", StringComparison.OrdinalIgnoreCase) &&
        !string.Equals(command, "previewRailPath", StringComparison.OrdinalIgnoreCase);

    private static JObject InvokeLightweightRewardQuery() =>
        RewardUiRuntimeFallback.TryQueryState(out JObject result)
            ? result
            : LightweightContractUnavailable(
                "queryReward",
                "轻量奖励界面反射契约不可用；已阻止回落到会扫描整个场景的原生 MCP 查询。");

    private static JObject InvokeLightweightRewardSelection(JObject? arguments) =>
        RewardUiRuntimeFallback.TryChooseOption(arguments, out JObject result)
            ? result
            : LightweightContractUnavailable(
                "chooseRewardOption",
                "轻量奖励选择反射契约不可用；本次未点击，并已阻止回落到原生 MCP 写命令。");

    private static JObject InvokeLightweightRewardSkip(JObject? arguments) =>
        RewardUiRuntimeFallback.TrySkipCurrentOpportunity(arguments, out JObject result)
            ? result
            : LightweightContractUnavailable(
                "skipReward",
                "轻量奖励跳过反射契约不可用；本次未跳过，并已阻止回落到原生 MCP 写命令。");

    private static JObject InvokeLightweightMergeQuery() =>
        MergeUiRuntimeFallback.TryQueryAutomationState(out JObject result)
            ? result
            : LightweightContractUnavailable(
                "queryMergeState",
                "轻量合成面板反射契约不可用；已阻止回落到会扫描整个场景的原生 MCP 查询。");

    private static JObject InvokeLightweightMergeSelection(JObject? arguments) =>
        MergeUiRuntimeFallback.TrySelectMergeVehicle(arguments, out JObject result)
            ? result
            : LightweightContractUnavailable(
                "selectMergeVehicle",
                "轻量合成选车反射契约不可用；本次未点击，并已阻止回落到原生 MCP 写命令。");

    private static JObject InvokeLightweightWaveFunctionQuery(JObject? arguments)
    {
        string panel = arguments?["panel"]?.Value<string>() ?? string.Empty;
        if (string.Equals(panel, "RepairUI", StringComparison.OrdinalIgnoreCase))
        {
            return RepairUiRuntimeFallback.TryQueryPanelState(out JObject repairState)
                ? repairState
                : LightweightContractUnavailable(
                    "queryEventOptions",
                    "当前无法读取预期的修整面板状态；已阻止回落到原生全场景扫描查询。");
        }

        if (string.Equals(panel, "EventUI", StringComparison.OrdinalIgnoreCase))
        {
            return WaveFunctionUiRuntimeFallback.TryQueryPanelState(out JObject eventState)
                ? eventState
                : LightweightContractUnavailable(
                    "queryEventOptions",
                    "当前无法读取预期的轨神事件面板状态；已阻止回落到原生全场景扫描查询。");
        }

        if (RepairUiRuntimeFallback.TryQueryOptions(out JObject repairOptions))
        {
            return repairOptions;
        }

        if (WaveFunctionUiRuntimeFallback.TryQueryOptions(out JObject eventOptions))
        {
            return eventOptions;
        }

        return LightweightContractUnavailable(
            "queryEventOptions",
            "当前没有可通过轻量契约读取的修整或轨神事件面板；已阻止回落到原生全场景扫描查询。");
    }

    private static JObject InvokeLightweightWaveFunctionSelection(JObject? arguments)
    {
        string panel = arguments?["panel"]?.Value<string>() ?? string.Empty;
        if (string.Equals(panel, "RepairUI", StringComparison.OrdinalIgnoreCase))
        {
            return RepairUiRuntimeFallback.TryChooseOption(arguments, out JObject repairChoice)
                ? repairChoice
                : LightweightContractUnavailable(
                    "chooseWaveFunctionOption",
                    "当前无法绑定活动的修整面板；已阻止按索引回落到原生写命令，请重新查询修整选项。");
        }

        if (string.Equals(panel, "EventUI", StringComparison.Ordinal))
        {
            return WaveFunctionUiRuntimeFallback.TryChooseOption(arguments, out JObject eventChoice)
                ? eventChoice
                : LightweightContractUnavailable(
                    "chooseWaveFunctionOption",
                    "当前无法绑定活动的轨神事件面板；已阻止按索引回落到原生写命令，请重新查询轨神事件选项。");
        }

        return LightweightContractUnavailable(
            "chooseWaveFunctionOption",
            "事件选项写命令缺少有效的 EventUI 或 RepairUI 面板身份；本次未点击，也不会回落到原生索引写入。");
    }

    private static JObject LightweightContractUnavailable(string command, string message) => new()
    {
        ["success"] = false,
        ["message"] = message,
        ["suggestion"] = "当前构建缺少专用轻量契约；已安全停止该操作，不会调用原生全场景扫描实现。",
        ["data"] = new JObject
        {
            ["state"] = new JObject
            {
                ["command"] = command,
                ["contractAvailable"] = false,
                ["nativeFallbackBlocked"] = true,
                ["invocationStarted"] = false
            }
        }
    };

    private static JObject UncertainMutationError(string message) => JObject.FromObject(new
    {
        success = false,
        message,
        suggestion = "写操作的最终状态未知；禁止重放，并优先通过只读状态对账。若无法对账，再重新启动游戏进程。",
        data = new
        {
            state = new
            {
                outcomeUnknown = true,
                needsReconciliation = true,
                uncertaintyOrigin = "bridgeDispatchException",
                invocationStarted = true
            }
        }
    });

    private static JObject Error(string message) => JObject.FromObject(new
    {
        success = false,
        message,
        suggestion = "请确认当前游戏版本包含所需的 GuiGameAutomation.Runtime 契约。",
        data = new { state = new { } }
    });
}
