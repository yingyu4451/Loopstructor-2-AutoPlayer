using Mono.Cecil;
using Mono.Cecil.Cil;

namespace Loopstructor.AutoPlayer.Tests;

public sealed class RepairOptionSettlementContractTests
{
    private const string ControllerType = "Loopstructor.AutoPlayer.Plugin.AutoPlayController";
    private const string BridgeType = "Loopstructor.AutoPlayer.Plugin.RuntimeBridge";
    private const string GuardType = "Loopstructor.AutoPlayer.Core.WaveFunctionOptionSettlementGuard";

    [Fact]
    public void UnsafeEventOrRepairChoiceArmsReadOnlyGuardBeforeGenericRestartFault()
    {
        using AssemblyDefinition assembly = ReadPlugin();
        MethodDefinition execute = RequireMethod(RequireType(assembly, ControllerType), "ExecuteWithResult");
        Instruction[] instructions = execute.Body.Instructions.ToArray();

        int arm = FindCall(instructions, ControllerType, "TryArmWaveFunctionOptionSettlement");
        int hardFault = FindCall(instructions, ControllerType, "FaultRequiringProcessRestart");
        Assert.True(arm >= 0 && arm < hardFault);
        Assert.Equal(
            2,
            Calls(execute).Count(call =>
                call.DeclaringType.FullName == ControllerType &&
                call.Name == "TryArmWaveFunctionOptionSettlement"));
        Assert.Contains(Calls(execute), IsCall(GuardType, "get_IsArmed"));
    }

    [Fact]
    public void ReconciliationTimeoutUsesSoftFaultAndSceneChangeResetsGuard()
    {
        using AssemblyDefinition assembly = ReadPlugin();
        TypeDefinition controller = RequireType(assembly, ControllerType);
        foreach (string methodName in new[]
                 {
                     "HandleWaveFunctionOptionSettlementFromOptions",
                     "HandleWaveFunctionOptionSettlementFromWaveState"
                 })
        {
            MethodDefinition method = RequireMethod(controller, methodName);
            Assert.Contains(Calls(method), IsCall(ControllerType, "Fault"));
            Assert.DoesNotContain(Calls(method), IsCall(ControllerType, "FaultRequiringProcessRestart"));
        }

        Assert.Contains(
            Calls(RequireMethod(controller, "ObserveActiveScene")),
            IsCall(GuardType, "Reset"));
    }

    [Fact]
    public void PendingMapWavePulseSettlesEventOrRepairLockBeforeWaveHandling()
    {
        using AssemblyDefinition assembly = ReadPlugin();
        MethodDefinition pendingMap = RequireMethod(
            RequireType(assembly, ControllerType),
            "TryHandlePendingMapSelection");
        Instruction[] instructions = pendingMap.Body.Instructions.ToArray();

        int settle = FindCall(
            instructions,
            ControllerType,
            "CompleteWaveFunctionOptionSettlementFromWavePulse");
        int handleWave = FindCall(instructions, ControllerType, "HandleWaveObservation");
        Assert.True(settle >= 0 && settle < handleWave);
    }

    [Fact]
    public void EventAndRepairWritesNeverFallBackToNativeIndexCommand()
    {
        using AssemblyDefinition assembly = ReadPlugin();
        TypeDefinition bridge = RequireType(assembly, BridgeType);
        MethodDefinition invoke = RequireMethod(bridge, "Invoke");
        MethodDefinition choose = RequireMethod(bridge, "InvokeLightweightWaveFunctionSelection");
        string[] strings = LoadedStrings(choose).ToArray();

        Assert.Contains("RepairUI", strings);
        Assert.Contains("EventUI", strings);
        Assert.Contains("当前无法绑定活动的修整面板；已阻止按索引回落到原生写命令，请重新查询修整选项。", strings);
        Assert.Contains("当前无法绑定活动的轨神事件面板；已阻止按索引回落到原生写命令，请重新查询轨神事件选项。", strings);
        Assert.Contains(Calls(invoke), IsCall(BridgeType, "InvokeLightweightWaveFunctionSelection"));
        Assert.Contains(
            Calls(choose),
            IsCall("Loopstructor.AutoPlayer.Plugin.RepairUiRuntimeFallback", "TryChooseOption"));
        Assert.Contains(
            Calls(choose),
            IsCall("Loopstructor.AutoPlayer.Plugin.WaveFunctionUiRuntimeFallback", "TryChooseOption"));
        Assert.Contains(Calls(choose), IsCall(BridgeType, "LightweightContractUnavailable"));
        Assert.DoesNotContain(Calls(choose), call => call.Name == "TryGetValue");
    }

    [Fact]
    public void EventQueryPrefersRepairThenEventAndNeverUsesNativeSceneQuery()
    {
        using AssemblyDefinition assembly = ReadPlugin();
        TypeDefinition bridge = RequireType(assembly, BridgeType);
        MethodDefinition invoke = RequireMethod(bridge, "Invoke");
        MethodDefinition query = RequireMethod(bridge, "InvokeLightweightWaveFunctionQuery");
        Instruction[] instructions = query.Body.Instructions.ToArray();

        int expectedRepair = FindCall(
            instructions,
            "Loopstructor.AutoPlayer.Plugin.RepairUiRuntimeFallback",
            "TryQueryPanelState");
        int expectedEvent = FindCall(
            instructions,
            "Loopstructor.AutoPlayer.Plugin.WaveFunctionUiRuntimeFallback",
            "TryQueryPanelState");
        int activeRepair = FindCall(
            instructions,
            "Loopstructor.AutoPlayer.Plugin.RepairUiRuntimeFallback",
            "TryQueryOptions");
        int activeEvent = FindCall(
            instructions,
            "Loopstructor.AutoPlayer.Plugin.WaveFunctionUiRuntimeFallback",
            "TryQueryOptions");
        int failClosed = FindCall(instructions, BridgeType, "LightweightContractUnavailable");
        Assert.True(expectedRepair >= 0 && expectedEvent > expectedRepair);
        Assert.True(activeRepair > expectedEvent && activeEvent > activeRepair);
        Assert.True(failClosed > expectedRepair);
        Assert.Contains(Calls(invoke), IsCall(BridgeType, "InvokeLightweightWaveFunctionQuery"));
        Assert.DoesNotContain(Calls(query), call => call.Name == "TryGetValue");

        MethodDefinition transition = RequireMethod(
            RequireType(assembly, ControllerType),
            "TryHandlePendingMapSelection");
        Assert.Contains(Calls(transition), call =>
            call.DeclaringType.FullName == "Newtonsoft.Json.Linq.JObject" &&
            call.Name == "FromObject");
        Assert.Contains(Calls(transition), call =>
            call.DeclaringType.FullName == BridgeType &&
            call.Name == "Invoke" &&
            call.Parameters.Count == 2);
    }

    [Fact]
    public void FullOptionSnapshotsReconcileBothPanelsAndWaveStateReadsBothBlockers()
    {
        using AssemblyDefinition assembly = ReadPlugin();
        TypeDefinition controller = RequireType(assembly, ControllerType);
        MethodDefinition options = RequireMethod(
            controller,
            "HandleWaveFunctionOptionSettlementFromOptions");
        MethodDefinition wave = RequireMethod(
            controller,
            "HandleWaveFunctionOptionSettlementFromWaveState");

        Assert.Contains(Calls(options), IsCall(GuardType, "ObserveOptions"));
        Assert.Contains("RepairUI", LoadedStrings(wave));
        Assert.Contains("EventUI", LoadedStrings(wave));
        Assert.Contains("repairBlocked", LoadedStrings(wave));
        Assert.Contains("eventBlocked", LoadedStrings(wave));
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

    private static int FindCall(
        IReadOnlyList<Instruction> instructions,
        string declaringType,
        string methodName)
    {
        for (int index = 0; index < instructions.Count; index++)
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

    private static IEnumerable<string> LoadedStrings(MethodDefinition method) =>
        method.Body.Instructions
            .Where(instruction => instruction.OpCode.Code == Code.Ldstr)
            .Select(instruction => instruction.Operand)
            .OfType<string>();

    private static Predicate<MethodReference> IsCall(string declaringType, string methodName) =>
        call => call.DeclaringType.FullName == declaringType && call.Name == methodName;
}
