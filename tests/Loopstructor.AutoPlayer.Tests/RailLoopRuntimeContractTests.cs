using Mono.Cecil;
using Mono.Cecil.Cil;

namespace Loopstructor.AutoPlayer.Tests;

public sealed class RailLoopRuntimeContractTests
{
    private const string ControllerType = "Loopstructor.AutoPlayer.Plugin.AutoPlayController";

    [Fact]
    public void PostMapObservationRequiresTwoStableStationReadsThenExactRailValidation()
    {
        using AssemblyDefinition assembly = ReadPlugin();
        TypeDefinition controller = RequireType(assembly, ControllerType);
        MethodDefinition observe = RequireMethod(controller, "TryObservePostMapTopology");

        Assert.Contains(controller.NestedTypes, type => type.Name == "PostMapTopologyObservationStep");
        Assert.Contains(LoadedStrings(observe), text => text == "queryCatapults");
        Assert.Contains(LoadedStrings(observe), text => text == "queryRail");
        Assert.Contains(Calls(observe), call =>
            call.DeclaringType.FullName == "Loopstructor.AutoPlayer.Core.RailRuntimeTopologyInspector" &&
            call.Name == "Inspect");
        Assert.Contains(Calls(observe), call =>
            call.DeclaringType.FullName == ControllerType && call.Name == "RequestDefenseMaintenance");
    }

    [Fact]
    public void PendingPostMapTopology_IsAProgressionGateUntilRuntimeVerificationClearsIt()
    {
        using AssemblyDefinition assembly = ReadPlugin();
        TypeDefinition controller = RequireType(assembly, ControllerType);
        MethodDefinition tick = RequireMethod(controller, "TickInGame");
        MethodDefinition decision = RequireMethod(controller, "ExecuteInGameDecision");
        MethodDefinition maintainGate = RequireMethod(controller, "TryRunRequiredRailTopologyMaintenance");
        MethodDefinition completion = RequireMethod(controller, "CompleteRequiredRailTopologyMaintenance");

        Assert.Contains(controller.Fields, field => field.Name == "_requiredRailTopologyMaintenance");
        Assert.Contains(Calls(tick), call =>
            call.DeclaringType.FullName == ControllerType && call.Name == maintainGate.Name);
        Assert.Contains(Calls(decision), call =>
            call.DeclaringType.FullName == ControllerType && call.Name == maintainGate.Name);
        Assert.Contains(Calls(completion), call =>
            call.DeclaringType.FullName == "Loopstructor.AutoPlayer.Core.RailRuntimeTopologyInspector" &&
            call.Name == "Inspect");
        Assert.Contains(Calls(completion), call =>
            call.DeclaringType.FullName == ControllerType && call.Name == "HasUnassignedActiveStation");
    }

    [Fact]
    public void DefeatCompletesAndNextStartUsesNativeAgainEntry()
    {
        using AssemblyDefinition assembly = ReadPlugin();
        TypeDefinition controller = RequireType(assembly, ControllerType);
        TypeDefinition bridge = RequireType(assembly, "Loopstructor.AutoPlayer.Plugin.RuntimeBridge");
        MethodDefinition settlement = RequireMethod(controller, "TickSettlement");
        MethodDefinition tick = RequireMethod(controller, "Tick");
        MethodDefinition restart = RequireMethod(bridge, "TryRestartAfterDefeat");
        MethodDefinition initializeRestart = RequireMethod(bridge, "InitializeSettlementRestartContract");

        Assert.Contains(Calls(settlement), call => call.DeclaringType.FullName == ControllerType && call.Name == "Complete");
        Assert.DoesNotContain(Calls(settlement), call => call.DeclaringType.FullName == ControllerType && call.Name == "FaultRequiringProcessRestart");
        Assert.Contains(Calls(tick), call => call.DeclaringType.FullName == bridge.FullName && call.Name == restart.Name);
        Assert.Contains(LoadedStrings(initializeRestart), text => text == "MetroTD.UISystem.SettlementUI");
        Assert.Contains(LoadedStrings(initializeRestart), text => text == "Again");
    }

    [Fact]
    public void VisualVerifierIsFingerprintGatedAndNeverIssuesInputCommands()
    {
        using AssemblyDefinition assembly = ReadPlugin();
        TypeDefinition verifier = RequireType(assembly, "Loopstructor.AutoPlayer.Plugin.RailVisualVerifier");
        MethodDefinition capture = RequireMethod(verifier, "CaptureIfChanged");

        Assert.Contains(verifier.Fields, field => field.Name == "_lastFingerprint");
        Assert.DoesNotContain(Calls(capture), call =>
            call.Name.Contains("Click", StringComparison.OrdinalIgnoreCase) ||
            call.Name.Contains("Invoke", StringComparison.OrdinalIgnoreCase));
    }

    private static AssemblyDefinition ReadPlugin() => AssemblyDefinition.ReadAssembly(
        Path.Combine(AppContext.BaseDirectory, "Loopstructor.AutoPlayer.Plugin.dll"));

    private static TypeDefinition RequireType(AssemblyDefinition assembly, string fullName) =>
        assembly.MainModule.Types.Single(type => type.FullName == fullName);

    private static MethodDefinition RequireMethod(TypeDefinition type, string name) =>
        type.Methods.Single(method => method.Name == name);

    private static IEnumerable<MethodReference> Calls(MethodDefinition method) =>
        method.Body.Instructions.Where(instruction => instruction.OpCode.Code is Code.Call or Code.Callvirt)
            .Select(instruction => instruction.Operand).OfType<MethodReference>();

    private static IEnumerable<string> LoadedStrings(MethodDefinition method) =>
        method.Body.Instructions.Where(instruction => instruction.OpCode.Code == Code.Ldstr)
            .Select(instruction => instruction.Operand).OfType<string>();
}
