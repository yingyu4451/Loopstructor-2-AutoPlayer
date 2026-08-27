using System;
using System.Collections;
using System.Reflection;
using HarmonyLib;
using UnityEngine;

namespace Loopstructor.AutoPlayer.Plugin;

/// <summary>
/// The shipped game command still enforces the retired one-train-per-rail rule. In independent
/// vehicle mode the gameplay layer uses RailDriversHandler.driverMaxCount as the live authority,
/// so permit the command to create another one-vehicle driver while that capacity remains.
/// </summary>
internal static class IndependentVehiclePlacementPatch
{
    public static bool Applied { get; private set; }

    public static bool Install(Harmony harmony, Action<string> log)
    {
        Type? runtimeType = AccessTools.TypeByName("GuiGameAutomation.Runtime.GuiGameMcpVehicleRuntime");
        MethodInfo? railCheck = runtimeType == null
            ? null
            : AccessTools.Method(runtimeType, "TryBuildPlaceVehicleOnLineRailCheck");
        MethodInfo? railCheckPrefix = AccessTools.Method(
            typeof(IndependentVehiclePlacementPatch),
            nameof(AllowLiveIndependentRailCapacity));
        Type? operationType = AccessTools.TypeByName("MetroTD.Interaction.VehicleOperationProcessor");
        MethodInfo? useVehicleOnLine = operationType == null
            ? null
            : AccessTools.Method(operationType, "UseMainRazorToLine");
        MethodInfo? deploymentPrefix = AccessTools.Method(
            typeof(IndependentVehiclePlacementPatch),
            nameof(RouteIndependentVehicleThroughEnergyPoint));
        if (railCheck == null || railCheckPrefix == null || useVehicleOnLine == null || deploymentPrefix == null)
        {
            log("无法接入完整的独立战车玩家投放流程；将保留游戏原命令行为。");
            return false;
        }

        harmony.Patch(railCheck, prefix: new HarmonyMethod(railCheckPrefix) { priority = Priority.First });
        harmony.Patch(useVehicleOnLine, prefix: new HarmonyMethod(deploymentPrefix) { priority = Priority.First });
        Applied = true;
        log("已按独立战车模式修正玩家放车校验，并通过始发站投放；轨道车列上限使用实时 driverMaxCount。");
        return true;
    }

    internal static bool ShouldOverrideLegacySingleTrainGate(
        bool independentVehicleMode,
        bool isLoop,
        int driverCount,
        int driverMaxCount,
        bool isDriverReachToMax) =>
        independentVehicleMode &&
        isLoop &&
        driverCount > 0 &&
        driverMaxCount > driverCount &&
        !isDriverReachToMax;

    private static bool AllowLiveIndependentRailCapacity(
        object vehicle,
        object line,
        ref object railPlacementCheck,
        ref string rejectSuggestion,
        ref bool __result)
    {
        try
        {
            Type? configType = AccessTools.TypeByName("MetroTD.LineSystem.TrainConfigSO");
            object? config = ReadStaticMember(configType, "Instance");
            bool independentVehicleMode = ReadBool(config, "independentVehicleMode");
            object? rail = ReadMember(line, "rail");
            object? driversHandler = ReadMember(rail, "driversHandler");
            int driverCount = ReadMember(driversHandler, "drivers") is ICollection drivers
                ? drivers.Count
                : 0;
            int driverMaxCount = ReadInt(driversHandler, "driverMaxCount");
            bool isLoop = ReadBool(rail, "isLoop");
            bool isDriverReachToMax = ReadBool(driversHandler, "isDriverReachToMax");
            if (!ShouldOverrideLegacySingleTrainGate(
                    independentVehicleMode,
                    isLoop,
                    driverCount,
                    driverMaxCount,
                    isDriverReachToMax))
            {
                return true;
            }

            int railInternalId = ReadInt(rail, "ID");
            int railDisplayId = ReadInt(rail, "railID");
            int lineInstanceId = line is UnityEngine.Object lineObject ? lineObject.GetInstanceID() : 0;
            int vehicleInstanceId = vehicle is UnityEngine.Object vehicleObject ? vehicleObject.GetInstanceID() : 0;
            railPlacementCheck = new
            {
                canPlace = true,
                reason = "independentVehicleRailCapacityAvailable",
                wouldCreateIndependentTrain = true,
                lineInstanceId,
                railInternalId,
                railDisplayId,
                isLoop,
                driverCount,
                driverMaxCount,
                hasDriver = driverCount > 0,
                isDriverReachToMax = false,
                vehicle = new { instanceId = vehicleInstanceId }
            };
            rejectSuggestion = null!;
            __result = true;
            return false;
        }
        catch
        {
            return true;
        }
    }

    private static bool RouteIndependentVehicleThroughEnergyPoint(object __instance, object line)
    {
        try
        {
            Type? configType = AccessTools.TypeByName("MetroTD.LineSystem.TrainConfigSO");
            object? config = ReadStaticMember(configType, "Instance");
            if (!ReadBool(config, "independentVehicleMode"))
            {
                return true;
            }

            object? rail = ReadMember(line, "rail");
            object? attributeCatapult = ReadMember(rail, "attributeCatapult");
            Type? linePointType = AccessTools.TypeByName("MetroTD.LineSystem.LinePoint");
            object? linePoint = attributeCatapult is Component component && linePointType != null
                ? component.GetComponent(linePointType)
                : null;
            MethodInfo? useEnergyPoint = AccessTools.Method(
                __instance.GetType(),
                "UseMainRazorToEnergyPoint",
                linePointType == null ? null : new[] { linePointType });
            if (linePoint == null || useEnergyPoint == null)
            {
                return true;
            }

            useEnergyPoint.Invoke(__instance, new[] { linePoint });
            return false;
        }
        catch
        {
            return true;
        }
    }

    private static object? ReadStaticMember(Type? type, string name)
    {
        if (type == null) return null;
        return AccessTools.Property(type, name)?.GetValue(null, null) ??
               AccessTools.Field(type, name)?.GetValue(null);
    }

    private static object? ReadMember(object? instance, string name)
    {
        if (instance == null) return null;
        Type type = instance.GetType();
        return AccessTools.Property(type, name)?.GetValue(instance, null) ??
               AccessTools.Field(type, name)?.GetValue(instance);
    }

    private static int ReadInt(object? instance, string name)
    {
        object? value = ReadMember(instance, name);
        return value is int number ? number : 0;
    }

    private static bool ReadBool(object? instance, string name)
    {
        object? value = ReadMember(instance, name);
        return value is bool flag && flag;
    }
}
