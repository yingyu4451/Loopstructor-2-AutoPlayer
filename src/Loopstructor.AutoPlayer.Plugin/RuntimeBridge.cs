using System;
using System.Collections.Generic;
using System.Reflection;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

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
            return Error("Automation command is unavailable: " + command);
        }

        try
        {
            object? result = method.Invoke(null, new object[] { (arguments ?? new JObject()).ToString(Formatting.None) });
            return JObject.Parse(JsonConvert.SerializeObject(result));
        }
        catch (TargetInvocationException exception)
        {
            Exception inner = exception.InnerException ?? exception;
            return Error(inner.GetType().Name + ": " + inner.Message);
        }
        catch (Exception exception)
        {
            return Error(exception.GetType().Name + ": " + exception.Message);
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
            message = "Waiting for the game's front-end readiness contract.";
            return false;
        }

        try
        {
            object? globalInstance = globalInstanceProperty.GetValue(null, null);
            if (globalInstance == null)
            {
                message = "Waiting for the game's global module instance.";
                return false;
            }

            if (isLoadingProperty.GetValue(globalInstance, null) is not bool isLoading || isLoading)
            {
                message = "Waiting for the game's global modules to finish initialization.";
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
                message = "Waiting for the current game scene to finish initialization.";
                return false;
            }

            message = "Game front-end modules are initialized.";
            return true;
        }
        catch (Exception exception)
        {
            message = "Waiting for a readable game front-end state: " +
                      (exception is TargetInvocationException target && target.InnerException != null
                          ? target.InnerException.Message
                          : exception.Message);
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

    private static JObject Error(string message) => JObject.FromObject(new
    {
        success = false,
        message,
        suggestion = "Confirm that this game build contains the current GuiGameAutomation.Runtime contract.",
        data = new { state = new { } }
    });
}
