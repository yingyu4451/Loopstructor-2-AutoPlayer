using Mono.Cecil;
using Mono.Cecil.Cil;

namespace Loopstructor.AutoPlayer.Tests;

public sealed class RailJointLayoutRuntimeContractTests
{
    private const string ControllerType = "Loopstructor.AutoPlayer.Plugin.AutoPlayController";
    private const string ProbeType = "Loopstructor.AutoPlayer.Plugin.IncrementalRailJointLayoutProbe";

    [Fact]
    public void ControllerPlansWholeLayoutAndTracksStablePointIdsAcrossMoves()
    {
        using AssemblyDefinition assembly = ReadPlugin();
        TypeDefinition controller = RequireType(assembly, ControllerType);
        TypeDefinition steps = controller.NestedTypes.Single(type => type.Name == "DefenseMaintenanceStep");
        MethodDefinition maintain = RequireMethod(controller, "TryMaintainDefense");
        MethodDefinition advance = RequireMethod(controller, "AdvanceJointLayoutAfterMove");

        Assert.Contains(steps.Fields, field => field.Name == "ProbeJointRailLayout");
        Assert.Contains(controller.Fields, field => field.Name == "_defenseJointLayoutPlan");
        Assert.Contains(controller.Fields, field => field.Name == "_defenseJointMovedPointIds");
        Assert.Contains(Calls(maintain), call => call.DeclaringType.FullName == ProbeType && call.Name == "TryInitialize");
        Assert.Contains(Calls(maintain), call => call.DeclaringType.FullName == ProbeType && call.Name == "ProbeNext");
        Assert.Contains(Calls(maintain), call => call.DeclaringType.FullName == ControllerType && call.Name == "AdvanceJointLayoutAfterMove");
        Assert.Contains(advance.Body.Instructions, instruction =>
            instruction.Operand is MethodReference call && call.Name == "get_StationPointId");
        Assert.Contains(LoadedStrings(advance), text => text.Contains("不会重新规划目标", StringComparison.Ordinal));
    }

    [Fact]
    public void JointProbeUsesTypedPoolsBeam512AndThreeMillisecondBudget()
    {
        using AssemblyDefinition assembly = ReadPlugin();
        TypeDefinition probe = RequireType(assembly, ProbeType);
        MethodDefinition initialize = RequireMethod(probe, "TryInitialize");
        MethodDefinition next = RequireMethod(probe, "ProbeNext");

        Assert.Contains(Calls(initialize), call =>
            call.DeclaringType.FullName == "Loopstructor.AutoPlayer.Plugin.RuntimeGridCandidatePoolReader" &&
            call.Name == "TryReadTyped");
        Assert.Contains(Calls(next), call =>
            call.DeclaringType.FullName == "Loopstructor.AutoPlayer.Core.RailJointLayoutSearch" &&
            call.Name == "ProbeNext");
        Assert.Equal(512, Constant<int>(probe, "BeamWidth"));
        Assert.Equal(3d, Constant<double>(probe, "SliceBudgetMilliseconds"));
    }

    [Fact]
    public void SuccessfulJointResultMarksStableInsteadOfContinuingGreedyOptimization()
    {
        using AssemblyDefinition assembly = ReadPlugin();
        TypeDefinition controller = RequireType(assembly, ControllerType);
        MethodDefinition complete = RequireMethod(controller, "CompleteJointLayoutWithoutRebuild");
        MethodDefinition resultTimeline = RequireMethod(controller, "BuildJointRailLayoutResultTimeline");

        Assert.Contains(Calls(complete), call => call.DeclaringType.FullName == ControllerType && call.Name == "MarkDefenseRailMaintenanceStable");
        Assert.Contains(Calls(complete), call => call.DeclaringType.FullName == ControllerType && call.Name == "FinishDefenseMaintenance");
        Assert.DoesNotContain(Calls(complete), call => call.DeclaringType.FullName == ControllerType && call.Name == "ContinueDefenseRailOptimization");
        Assert.Contains(LoadedStrings(resultTimeline), text => text.Contains("预测 T", StringComparison.Ordinal));
    }

    [Fact]
    public void JointFailuresUseCoordinateRollbackInsteadOfOnlyRestoringPointOrder()
    {
        using AssemblyDefinition assembly = ReadPlugin();
        TypeDefinition controller = RequireType(assembly, ControllerType);
        MethodDefinition failure = RequireMethod(controller, "HandleJointLayoutExecutionFailure");
        MethodDefinition rollback = RequireMethod(controller, "BeginJointLayoutRollback");

        Assert.Contains(Calls(failure), call =>
            call.DeclaringType.FullName == ControllerType && call.Name == "BeginJointLayoutRollback");
        Assert.Contains(Calls(rollback), call => call.Name == "Reverse");
        Assert.Contains(controller.Methods, method => method.Name == "CloneJointMoveCandidateAtGrid");
        Assert.Contains(controller.Fields, field => field.Name == "_defenseJointLayoutRestoring");
    }

    private static AssemblyDefinition ReadPlugin()
    {
        string path = Path.Combine(AppContext.BaseDirectory, "Loopstructor.AutoPlayer.Plugin.dll");
        return AssemblyDefinition.ReadAssembly(path);
    }

    private static TypeDefinition RequireType(AssemblyDefinition assembly, string fullName) =>
        assembly.MainModule.Types.Single(type => type.FullName == fullName);

    private static MethodDefinition RequireMethod(TypeDefinition type, string name) =>
        type.Methods.Single(method => method.Name == name);

    private static T Constant<T>(TypeDefinition type, string name)
    {
        FieldDefinition field = type.Fields.Single(field => field.Name == name);
        Assert.True(field.HasConstant);
        return (T)field.Constant;
    }

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
}
