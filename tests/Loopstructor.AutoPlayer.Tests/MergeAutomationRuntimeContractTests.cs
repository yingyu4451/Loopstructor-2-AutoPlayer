using Mono.Cecil;
using Mono.Cecil.Cil;

namespace Loopstructor.AutoPlayer.Tests;

public sealed class MergeAutomationRuntimeContractTests
{
    private const string ControllerType = "Loopstructor.AutoPlayer.Plugin.AutoPlayController";
    private const string BridgeType = "Loopstructor.AutoPlayer.Plugin.RuntimeBridge";
    private const string PlannerType = "Loopstructor.AutoPlayer.Core.MergeAutomationPlanner";
    private const string MergeFallbackType = "Loopstructor.AutoPlayer.Plugin.MergeUiRuntimeFallback";

    private static readonly string[] MergeCommands =
    {
        "openMergePanel",
        "queryMergeState",
        "selectMergeVehicle",
        "submitMergeSelection",
        "chooseMergeFetter",
        "queryMergeUiState",
        "closeMergePanel",
        "confirmMergeSettlement"
    };

    [Fact]
    public void Controller_UsesMergePlannerAndRequiresTheCompleteMergeCommandSet()
    {
        using AssemblyDefinition assembly = ReadPlugin();
        TypeDefinition controller = RequireType(assembly, ControllerType);
        MethodDefinition maintainDefense = RequireMethod(controller, "TryMaintainDefense");
        MethodDefinition runMerge = RequireMethod(controller, "RunMergeAutomationStep");
        MethodDefinition mergeContract = RequireMethod(controller, "HasMergeAutomationContract");

        FieldDefinition planner = RequireField(controller, "_mergeAutomationPlanner");
        Assert.Equal(PlannerType, planner.FieldType.FullName);
        Assert.Contains(Calls(maintainDefense), IsCall(PlannerType, "HasPotentialMergeCandidate"));
        Assert.Contains(Calls(maintainDefense), IsCall(ControllerType, "HasMergeAutomationContract"));
        Assert.Contains(Calls(runMerge), IsCall(PlannerType, "Decide"));

        HashSet<string> requiredCommands = LoadedStrings(mergeContract)
            .Where(MergeCommands.Contains)
            .ToHashSet(StringComparer.Ordinal);
        Assert.Equal(MergeCommands.Length, requiredCommands.Count);
        Assert.All(MergeCommands, command => Assert.Contains(command, requiredCommands));

        Assert.Contains(Calls(maintainDefense), IsCall(ControllerType, "RunMergeAutomationStep"));
        Assert.Contains(Calls(maintainDefense), IsCall(ControllerType, "ObserveMergeSettlement"));
        Assert.Contains(Calls(maintainDefense), IsCall(ControllerType, "ConfirmMergeSettlement"));
        Assert.Contains(Calls(maintainDefense), IsCall(ControllerType, "CloseMergePanel"));
    }

    [Fact]
    public void Settlement_IsObservedForOnePointFiveSecondsAndMaintenanceStopsAfterEightPasses()
    {
        using AssemblyDefinition assembly = ReadPlugin();
        TypeDefinition controller = RequireType(assembly, ControllerType);
        MethodDefinition observeSettlement = RequireMethod(controller, "ObserveMergeSettlement");
        MethodDefinition maintainDefense = RequireMethod(controller, "TryMaintainDefense");
        MethodDefinition confirmSettlement = RequireMethod(controller, "ConfirmMergeSettlement");

        FieldDefinition observationSeconds = RequireField(controller, "MergeSettlementObservationSeconds");
        Assert.True(observationSeconds.HasConstant);
        Assert.Equal(1.25f, Convert.ToSingle(observationSeconds.Constant));

        Instruction[] observationInstructions = observeSettlement.Body.Instructions.ToArray();
        int firstObservationWrite = FindFieldInstruction(
            observationInstructions,
            Code.Stfld,
            "_mergeSettlementObservedAt");
        int observedTimestampRead = FindFieldInstruction(
            observationInstructions,
            Code.Ldfld,
            "_mergeSettlementObservedAt",
            firstObservationWrite + 1);
        int delayLoad = FindFloatLoad(observationInstructions, 1.25f, observedTimestampRead + 1);
        Assert.True(
            firstObservationWrite >= 0 &&
            firstObservationWrite < observedTimestampRead &&
            observedTimestampRead < delayLoad,
            "The 1.25 second delay must be calculated from the first observed settlement timestamp.");
        Assert.Contains(Calls(observeSettlement), IsCall("System.Math", "Max"));

        FieldDefinition maxPasses = RequireField(controller, "MaxMergePassesPerMaintenance");
        Assert.True(maxPasses.HasConstant);
        Assert.Equal(8, Convert.ToInt32(maxPasses.Constant));
        Assert.Contains(maintainDefense.Body.Instructions, instruction => ReadLoadedInt32(instruction) == 8);

        Assert.Contains(confirmSettlement.Body.Instructions, instruction =>
            instruction.OpCode.Code == Code.Ldfld &&
            instruction.Operand is FieldReference field &&
            field.Name == "_mergePassCount");
        Assert.Contains(confirmSettlement.Body.Instructions, instruction => instruction.OpCode.Code == Code.Add);
        Assert.Contains(confirmSettlement.Body.Instructions, instruction =>
            instruction.OpCode.Code == Code.Stfld &&
            instruction.Operand is FieldReference field &&
            field.Name == "_mergePassCount");
    }

    [Fact]
    public void MergeFailures_UseRecoverablePauseWithoutRequestingAProcessRestart()
    {
        using AssemblyDefinition assembly = ReadPlugin();
        TypeDefinition controller = RequireType(assembly, ControllerType);
        MethodDefinition recoverablePause = RequireMethod(controller, "PauseForRecoverableRuntimeState");
        MethodDefinition reconcile = RequireMethod(controller, "ReconcileMergeState");
        MethodDefinition beginReconcile = RequireMethod(controller, "BeginMergeReconciliation");
        MethodDefinition[] mergeFailurePaths =
        {
            RequireMethod(controller, "RunMergeAutomationStep"),
            RequireMethod(controller, "ObserveMergeSettlement"),
            RequireMethod(controller, "ConfirmMergeSettlement"),
            RequireMethod(controller, "CloseMergePanel")
        };

        Assert.All(mergeFailurePaths, method =>
        {
            Assert.Contains(
                Calls(method),
                call => IsCall(ControllerType, "PauseForRecoverableRuntimeState")(call) ||
                        IsCall(ControllerType, "BeginMergeReconciliation")(call) ||
                        IsCall(ControllerType, "RecoverFromClosedMergePanel")(call));
            Assert.DoesNotContain(Calls(method), IsCall(ControllerType, "Fault"));
            Assert.DoesNotContain(Calls(method), IsCall(ControllerType, "CommitFault"));
            Assert.DoesNotContain(method.Body.Instructions, StoresField("_needsProcessRestart"));
        });

        Assert.Contains(recoverablePause.Body.Instructions, StoresField("_runState"));
        Assert.DoesNotContain(recoverablePause.Body.Instructions, StoresField("_needsProcessRestart"));
        Assert.DoesNotContain(Calls(recoverablePause), IsCall(ControllerType, "Fault"));
        Assert.DoesNotContain(Calls(recoverablePause), IsCall(ControllerType, "CommitFault"));
        Assert.Contains(Calls(reconcile), IsCall(ControllerType, "PauseForRecoverableRuntimeState"));
        Assert.DoesNotContain(Calls(reconcile), IsCall(ControllerType, "Fault"));
        Assert.DoesNotContain(reconcile.Body.Instructions, StoresField("_needsProcessRestart"));
        Assert.DoesNotContain(beginReconcile.Body.Instructions, StoresField("_needsProcessRestart"));
        MethodDefinition maintainDefense = RequireMethod(controller, "TryMaintainDefense");
        Assert.DoesNotContain(maintainDefense.Body.Instructions, StoresField("_needsProcessRestart"));
    }

    [Fact]
    public void RuntimeBridge_RegistersFiveNativeMergeCommandsAndRoutesFivePluginFallbacks()
    {
        using AssemblyDefinition assembly = ReadPlugin();
        TypeDefinition bridge = RequireType(assembly, BridgeType);
        MethodDefinition staticConstructor = RequireMethod(bridge, ".cctor");
        MethodDefinition initialize = RequireMethod(bridge, "Initialize");
        MethodDefinition hasCommand = RequireMethod(bridge, "HasCommand");
        MethodDefinition invoke = RequireMethod(bridge, "Invoke");

        HashSet<string> registrationStrings = LoadedStrings(staticConstructor).ToHashSet(StringComparer.Ordinal);
        string runtimeType = "GuiGameAutomation.Runtime.GuiGameMcpRebuildSellRuntime";
        (string Command, string Method)[] nativeCommands =
        {
            ("openMergePanel", "OpenMergePanel"),
            ("queryMergeState", "QueryMergeState"),
            ("selectMergeVehicle", "SelectMergeVehicle"),
            ("submitMergeSelection", "SubmitMergeSelection"),
            ("chooseMergeFetter", "ChooseMergeFetter")
        };

        Assert.Contains(runtimeType, registrationStrings);
        Assert.All(nativeCommands, entry =>
        {
            Assert.Contains(entry.Command, registrationStrings);
            Assert.Contains(entry.Method, registrationStrings);
        });
        Assert.Contains(initialize.Body.Instructions, LoadsField("OptionalBattleContract"));
        Assert.Contains(
            Calls(initialize),
            call => call.Name == "set_Item" &&
                    call.DeclaringType.FullName.StartsWith(
                        "System.Collections.Generic.Dictionary`2",
                        StringComparison.Ordinal));

        (string Command, string Route, string Method)[] exclusiveFallbackCommands =
        {
            ("queryMergeState", "InvokeLightweightMergeQuery", "TryQueryAutomationState"),
            ("selectMergeVehicle", "InvokeLightweightMergeSelection", "TrySelectMergeVehicle")
        };
        (string Command, string Method)[] fallbackCommands =
        {
            ("queryMergeUiState", "TryQueryState"),
            ("closeMergePanel", "TryClosePanel"),
            ("confirmMergeSettlement", "TryConfirmSettlement")
        };
        HashSet<string> recognizedFallbacks = LoadedStrings(hasCommand).ToHashSet(StringComparer.Ordinal);
        HashSet<string> routedFallbacks = LoadedStrings(invoke).ToHashSet(StringComparer.Ordinal);
        Assert.All(exclusiveFallbackCommands, entry =>
        {
            Assert.Contains(entry.Command, routedFallbacks);
            Assert.Contains(Calls(invoke), IsCall(BridgeType, entry.Route));
            MethodDefinition route = RequireMethod(bridge, entry.Route);
            Assert.Contains(Calls(route), IsCall(MergeFallbackType, entry.Method));
            Assert.Contains(Calls(route), IsCall(BridgeType, "LightweightContractUnavailable"));
            Assert.DoesNotContain(Calls(route), call => call.Name == "TryGetValue");
        });
        Assert.All(fallbackCommands, entry =>
        {
            Assert.Contains(entry.Command, routedFallbacks);
            Assert.Contains(Calls(invoke), IsCall(MergeFallbackType, entry.Method));
        });
        Assert.All(fallbackCommands, entry =>
            Assert.Contains(entry.Command, recognizedFallbacks));
    }

    private static AssemblyDefinition ReadPlugin()
    {
        string path = Path.Combine(AppContext.BaseDirectory, "Loopstructor.AutoPlayer.Plugin.dll");
        Assert.True(File.Exists(path), "Plugin assembly was not copied to the test output: " + path);
        return AssemblyDefinition.ReadAssembly(path);
    }

    private static TypeDefinition RequireType(AssemblyDefinition assembly, string fullName) =>
        assembly.MainModule.Types.Single(type => type.FullName == fullName);

    private static MethodDefinition RequireMethod(TypeDefinition type, string name) =>
        type.Methods.Single(method => method.Name == name);

    private static FieldDefinition RequireField(TypeDefinition type, string name) =>
        type.Fields.Single(field => field.Name == name);

    private static IEnumerable<MethodReference> Calls(MethodDefinition method) =>
        method.Body.Instructions
            .Where(instruction => instruction.OpCode.Code
                is Code.Call or Code.Callvirt or Code.Newobj or Code.Ldftn or Code.Ldvirtftn)
            .Select(instruction => instruction.Operand)
            .OfType<MethodReference>();

    private static IEnumerable<string> LoadedStrings(MethodDefinition method) =>
        method.Body.Instructions
            .Where(instruction => instruction.OpCode.Code == Code.Ldstr)
            .Select(instruction => instruction.Operand)
            .OfType<string>();

    private static Predicate<MethodReference> IsCall(string declaringType, string methodName) =>
        call => call.DeclaringType.FullName == declaringType && call.Name == methodName;

    private static Predicate<Instruction> LoadsField(string fieldName) =>
        instruction => instruction.OpCode.Code is Code.Ldfld or Code.Ldsfld &&
                       instruction.Operand is FieldReference field &&
                       field.Name == fieldName;

    private static Predicate<Instruction> StoresField(string fieldName) =>
        instruction => instruction.OpCode.Code is Code.Stfld or Code.Stsfld &&
                       instruction.Operand is FieldReference field &&
                       field.Name == fieldName;

    private static int FindFieldInstruction(
        IReadOnlyList<Instruction> instructions,
        Code code,
        string fieldName,
        int startIndex = 0)
    {
        for (int index = Math.Max(0, startIndex); index < instructions.Count; index++)
        {
            if (instructions[index].OpCode.Code == code &&
                instructions[index].Operand is FieldReference field &&
                field.Name == fieldName)
            {
                return index;
            }
        }

        return -1;
    }

    private static int FindFloatLoad(
        IReadOnlyList<Instruction> instructions,
        float value,
        int startIndex = 0)
    {
        for (int index = Math.Max(0, startIndex); index < instructions.Count; index++)
        {
            if (instructions[index].OpCode.Code == Code.Ldc_R4 &&
                Math.Abs((float)instructions[index].Operand - value) < 0.001f)
            {
                return index;
            }
        }

        return -1;
    }

    private static int? ReadLoadedInt32(Instruction instruction) => instruction.OpCode.Code switch
    {
        Code.Ldc_I4_M1 => -1,
        Code.Ldc_I4_0 => 0,
        Code.Ldc_I4_1 => 1,
        Code.Ldc_I4_2 => 2,
        Code.Ldc_I4_3 => 3,
        Code.Ldc_I4_4 => 4,
        Code.Ldc_I4_5 => 5,
        Code.Ldc_I4_6 => 6,
        Code.Ldc_I4_7 => 7,
        Code.Ldc_I4_8 => 8,
        Code.Ldc_I4_S => (sbyte)instruction.Operand,
        Code.Ldc_I4 => (int)instruction.Operand,
        _ => null
    };
}
