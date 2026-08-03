using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using UnityEngine;

namespace Loopstructor.AutoPlayer.Plugin;

internal sealed class RuntimeBridge
{
    private readonly Dictionary<string, MethodInfo> _commands = new(StringComparer.OrdinalIgnoreCase);

    private static readonly (string Command, string Type, string Method)[] Contract =
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
        ("prepareDefaultDefense", "GuiGameAutomation.Runtime.GuiGameMcpDefaultDefenseRuntime", "PrepareDefaultDefense"),
        ("queryVehicle", "GuiGameAutomation.Runtime.GuiGameMcpVehicleRuntime", "QueryVehicleState"),
        ("cancelVehicleInteraction", "GuiGameAutomation.Runtime.GuiGameMcpVehicleRuntime", "CancelVehicleInteraction"),
        ("moveVehicleInTrain", "GuiGameAutomation.Runtime.GuiGameMcpVehicleRuntime", "MoveVehicleInTrain"),
        ("queryMap", "GuiGameAutomation.Runtime.GuiGameMcpMapRuntime", "QueryMapState"),
        ("selectMapNode", "GuiGameAutomation.Runtime.GuiGameMcpMapRuntime", "SelectMapNode"),
        ("selectSublevel", "GuiGameAutomation.Runtime.GuiGameMcpMapRuntime", "SelectSublevel"),
        ("setTimeSpeed", "GuiGameAutomation.Runtime.GuiGameMcpMapRuntime", "SetTimeSpeed"),
        ("startWave", "GuiGameAutomation.Runtime.GuiGameMcpMapRuntime", "StartWave")
    };

    public bool IsAvailable { get; private set; }
    public IReadOnlyList<string> MissingMembers { get; private set; } = Array.Empty<string>();
    public IReadOnlyList<string> AvailableCommands => new List<string>(_commands.Keys);

    public bool Initialize()
    {
        List<string> missing = new();
        _commands.Clear();
        foreach ((string command, string typeName, string methodName) in Contract)
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

        MissingMembers = missing;
        IsAvailable = missing.Count == 0;
        return IsAvailable;
    }

    public JObject Invoke(string command, JObject? arguments = null)
    {
        if (!_commands.TryGetValue(command, out MethodInfo method))
        {
            return Error("自动游玩命令不可用：" + command);
        }

        try
        {
            object? result = method.Invoke(null, new object[] { (arguments ?? new JObject()).ToString(Formatting.None) });
            return JObject.Parse(JsonConvert.SerializeObject(result));
        }
        catch (TargetInvocationException exception)
        {
            Exception inner = exception.InnerException ?? exception;
            return Error("调用自动游玩运行时失败（" + inner.GetType().Name + "）：" + inner.Message);
        }
        catch (Exception exception)
        {
            return Error("调用自动游玩运行时失败（" + exception.GetType().Name + "）：" + exception.Message);
        }
    }

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

    private static JObject Error(string message) => JObject.FromObject(new
    {
        success = false,
        message,
        suggestion = "请确认当前游戏版本包含所需的 GuiGameAutomation.Runtime 契约。",
        data = new { state = new { } }
    });
}
