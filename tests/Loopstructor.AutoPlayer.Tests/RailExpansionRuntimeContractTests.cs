using Mono.Cecil;
using Mono.Cecil.Cil;

namespace Loopstructor.AutoPlayer.Tests;

public sealed class RailExpansionRuntimeContractTests
{
    private const string ControllerType = "Loopstructor.AutoPlayer.Plugin.AutoPlayController";
    private const string BridgeType = "Loopstructor.AutoPlayer.Plugin.RuntimeBridge";
    private const string StructuralGuardType = "Loopstructor.AutoPlayer.Core.PendingDefenseMutationGuard";

    [Fact]
    public void RuntimeBridge_UsesFormalRailAndStationCommandsWithoutPseudoRightDragFallback()
    {
        using AssemblyDefinition assembly = ReadPlugin();
        TypeDefinition bridge = RequireType(assembly, BridgeType);
        MethodDefinition initializer = bridge.Methods.Single(method => method.Name == ".cctor");
        string[] values = LoadedStrings(initializer).ToArray();

        Assert.Contains("insertPointFromLine", values);
        Assert.Contains("InsertPointFromLine", values);
        Assert.Contains("queryMovableStationState", values);
        Assert.Contains("QueryMovableStationState", values);
        Assert.Contains("startStationMove", values);
        Assert.Contains("StartStationMove", values);
        Assert.Contains("confirmStationMoveGrid", values);
        Assert.Contains("ConfirmStationMoveGrid", values);
        Assert.DoesNotContain("startRightDragStationToGrid", values);
        Assert.DoesNotContain("StartRightDragStationToGrid", values);
        Assert.DoesNotContain("serviceFallback", values);
    }

    [Fact]
    public void Tick_ReconcilesArmedStructuralMutationBeforeOutcomeSettlement()
    {
        using AssemblyDefinition assembly = ReadPlugin();
        MethodDefinition tick = RequireMethod(RequireType(assembly, ControllerType), "Tick");
        Instruction[] instructions = tick.Body.Instructions.ToArray();

        int armed = FindCall(instructions, StructuralGuardType, "get_IsArmed");
        int reconcile = FindCall(instructions, ControllerType, "TryMaintainDefense", armed + 1);
        int outcome = FindCall(
            instructions,
            "Loopstructor.AutoPlayer.Plugin.GameOutcomeObserver",
            "get_Outcome",
            armed + 1);
        int settlement = FindCall(instructions, ControllerType, "TickSettlement", outcome + 1);

        Assert.True(armed >= 0 && reconcile > armed && outcome > reconcile && settlement > outcome);
    }

    [Fact]
    public void BattleTactics_StartAtMostOneSpecialStationMaintenancePathPerWave()
    {
        using AssemblyDefinition assembly = ReadPlugin();
        TypeDefinition controller = RequireType(assembly, ControllerType);
        TypeDefinition steps = controller.NestedTypes.Single(type => type.Name == "BattleTacticStep");
        MethodDefinition tactics = RequireMethod(controller, "RunBattleTacticStep");
        MethodDefinition begin = RequireMethod(controller, "TryBeginBattleSpecialStationMaintenance");
        MethodDefinition reset = RequireMethod(controller, "ResetBattleTactics");

        Assert.Contains(steps.Fields, field => field.Name == "RunSpecialStationMaintenance");
        Assert.Contains(Calls(tactics), call => call.DeclaringType.FullName == ControllerType &&
                                               call.Name == "TryBeginBattleSpecialStationMaintenance");
        Assert.Contains(Calls(tactics), call => call.DeclaringType.FullName == ControllerType &&
                                               call.Name == "TryMaintainDefense");
        Assert.Contains("startStationMove", LoadedStrings(begin));
        Assert.Contains("confirmStationMoveGrid", LoadedStrings(begin));
        Assert.Contains("queryMovableStationState", LoadedStrings(begin));
        Assert.Contains(
            begin.Body.Instructions,
            instruction => instruction.Operand is FieldReference field &&
                           field.Name == "_battleSpecialMoveAttemptedThisWave");
        Assert.Contains(
            reset.Body.Instructions,
            instruction => instruction.Operand is FieldReference field &&
                           field.Name == "_battleSpecialMoveAttemptedThisWave");
    }

    [Fact]
    public void StationMoveAutoCancellation_ReconcilesOriginalRailAndStationBeforeUnlocking()
    {
        using AssemblyDefinition assembly = ReadPlugin();
        TypeDefinition controller = RequireType(assembly, ControllerType);
        TypeDefinition steps = controller.NestedTypes.Single(type => type.Name == "DefenseMaintenanceStep");
        MethodDefinition maintain = RequireMethod(controller, "TryMaintainDefense");
        MethodDefinition beginRollback = RequireMethod(
            controller,
            "BeginSpecialStationMoveRollbackVerificationIfInactive");

        Assert.Contains(steps.Fields, field => field.Name == "VerifySpecialStationMoveRollbackRail");
        Assert.Contains(steps.Fields, field => field.Name == "VerifySpecialStationMoveRollbackResult");
        Assert.True(Calls(maintain).Count(call => call.DeclaringType.FullName == ControllerType &&
                                                   call.Name == beginRollback.Name) >= 3);
        Assert.Contains(
            Calls(maintain),
            call => call.DeclaringType.FullName == "Loopstructor.AutoPlayer.Core.RailExpansionPlanner" &&
                    call.Name == "VerifyMoveCancellationRollback");
        Assert.Contains("currentMoveInteraction.active", LoadedStrings(beginRollback));
        Assert.Contains("queryRail", LoadedStrings(maintain));
        Assert.Contains("queryCatapults", LoadedStrings(maintain));
    }

    [Theory]
    [InlineData("Start")]
    [InlineData("Pause")]
    [InlineData("Stop")]
    [InlineData("ApplyPause")]
    [InlineData("ApplyStop")]
    public void LifecycleTransitions_ReadStructuralGuardBeforeChangingState(string methodName)
    {
        using AssemblyDefinition assembly = ReadPlugin();
        MethodDefinition method = RequireMethod(RequireType(assembly, ControllerType), methodName);

        Assert.Contains(
            Calls(method),
            call => call.DeclaringType.FullName == StructuralGuardType && call.Name == "get_IsArmed");
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

    private static IEnumerable<MethodReference> Calls(MethodDefinition method) =>
        method.Body.Instructions
            .Where(instruction => instruction.OpCode.Code is Code.Call or Code.Callvirt)
            .Select(instruction => instruction.Operand)
            .OfType<MethodReference>();

    private static IEnumerable<string> LoadedStrings(MethodDefinition method) =>
        method.Body.Instructions
            .Where(instruction => instruction.OpCode.Code == Code.Ldstr)
            .Select(instruction => instruction.Operand)
            .OfType<string>();

    private static int FindCall(
        IReadOnlyList<Instruction> instructions,
        string declaringType,
        string methodName,
        int startIndex = 0)
    {
        for (int index = Math.Max(0, startIndex); index < instructions.Count; index++)
        {
            if (instructions[index].Operand is MethodReference call &&
                call.DeclaringType.FullName == declaringType &&
                call.Name == methodName)
            {
                return index;
            }
        }

        return -1;
    }
}
