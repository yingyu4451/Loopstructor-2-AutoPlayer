using Mono.Cecil;
using Mono.Cecil.Cil;

namespace Loopstructor.AutoPlayer.Tests;

public sealed class RewardUiAutomationContractTests
{
    private const string BridgeType = "Loopstructor.AutoPlayer.Plugin.RuntimeBridge";
    private const string ControllerType = "Loopstructor.AutoPlayer.Plugin.AutoPlayController";
    private const string FallbackType = "Loopstructor.AutoPlayer.Plugin.RewardUiRuntimeFallback";
    private const string GuardType = "Loopstructor.AutoPlayer.Core.RewardSelectionSettlementGuard";

    [Fact]
    public void RuntimeBridge_RoutesRewardCommandsExclusivelyThroughFailClosedLightweightEntrypoints()
    {
        using AssemblyDefinition assembly = ReadPlugin();
        TypeDefinition bridge = RequireType(assembly, BridgeType);
        MethodDefinition invoke = RequireMethod(bridge, "Invoke");
        Instruction[] instructions = invoke.Body.Instructions.ToArray();

        int queryRoute = FindCall(instructions, BridgeType, "InvokeLightweightRewardQuery");
        int selectionRoute = FindCall(instructions, BridgeType, "InvokeLightweightRewardSelection");
        int skipRoute = FindCall(instructions, BridgeType, "InvokeLightweightRewardSkip");
        int collectionRoute = FindCall(instructions, FallbackType, "TryCollectRewardObject");
        int nativeLookup = FindCall(
            instructions,
            "System.Collections.Generic.Dictionary`2<System.String,System.Reflection.MethodInfo>",
            "TryGetValue");

        Assert.True(queryRoute >= 0 && queryRoute < nativeLookup);
        Assert.True(selectionRoute >= 0 && selectionRoute < nativeLookup);
        Assert.True(skipRoute >= 0 && skipRoute < nativeLookup);
        Assert.True(collectionRoute >= 0 && collectionRoute < nativeLookup);
        Assert.Contains("queryReward", LoadedStrings(invoke));
        Assert.Contains("chooseRewardOption", LoadedStrings(invoke));
        Assert.Contains("skipReward", LoadedStrings(invoke));
        Assert.Contains("collectRewardObject", LoadedStrings(invoke));
        Assert.Contains(Calls(invoke), IsCall(BridgeType, "LightweightContractUnavailable"));

        MethodDefinition query = RequireMethod(bridge, "InvokeLightweightRewardQuery");
        MethodDefinition selection = RequireMethod(bridge, "InvokeLightweightRewardSelection");
        MethodDefinition skip = RequireMethod(bridge, "InvokeLightweightRewardSkip");
        Assert.Contains(Calls(query), IsCall(FallbackType, "TryQueryState"));
        Assert.Contains(Calls(selection), IsCall(FallbackType, "TryChooseOption"));
        Assert.Contains(Calls(skip), IsCall(FallbackType, "TrySkipCurrentOpportunity"));
        Assert.Contains(Calls(query), IsCall(BridgeType, "LightweightContractUnavailable"));
        Assert.Contains(Calls(selection), IsCall(BridgeType, "LightweightContractUnavailable"));
        Assert.Contains(Calls(skip), IsCall(BridgeType, "LightweightContractUnavailable"));
        Assert.DoesNotContain(Calls(query), call => call.Name == "TryGetValue");
        Assert.DoesNotContain(Calls(selection), call => call.Name == "TryGetValue");
        Assert.DoesNotContain(Calls(skip), call => call.Name == "TryGetValue");

        MethodDefinition unavailable = RequireMethod(bridge, "LightweightContractUnavailable");
        string[] unavailableStrings = LoadedStrings(unavailable).ToArray();
        Assert.Contains("nativeFallbackBlocked", unavailableStrings);
        Assert.Contains("contractAvailable", unavailableStrings);
        Assert.Contains("invocationStarted", unavailableStrings);
    }

    [Fact]
    public void LightweightRewardPath_UsesRegisteredOwnersAndNeverScansOrSerializesTheScene()
    {
        using AssemblyDefinition assembly = ReadPlugin();
        TypeDefinition fallback = RequireType(assembly, FallbackType);
        MethodDefinition snapshot = RequireMethod(fallback, "BuildSnapshot");
        MethodDefinition options = RequireMethod(fallback, "GetRewardOptions");
        MethodDefinition rewardObjects = RequireMethod(fallback, "BuildRewardObjects");
        MethodDefinition contract = RequireMethod(fallback, "TryGetContract");

        Assert.Contains("m_rewardContent", LoadedStrings(contract));
        Assert.Contains("m_rewardObjects", LoadedStrings(contract));
        Assert.Contains("m_currentQueueItem", LoadedStrings(contract));
        Assert.Contains("m_refresh", LoadedStrings(contract));
        Assert.Contains("m_currentQueueItemFinished", LoadedStrings(contract));
        Assert.Contains("isInUsing", LoadedStrings(contract));
        Assert.Contains("m_instance", LoadedStrings(contract));
        Assert.Contains("m_reward", LoadedStrings(contract));
        Assert.Contains("initFetterModuleData", LoadedStrings(contract));
        Assert.Contains("isMandatory", LoadedStrings(contract));
        Assert.Contains("SkipHandle", LoadedStrings(contract));
        Assert.Contains("TryGetDisposableTemplate", LoadedStrings(contract));
        Assert.Contains("CanAdd", LoadedStrings(contract));
        Assert.Contains("GetCurrentAll", LoadedStrings(contract));
        Assert.Contains(Calls(snapshot), IsCall(FallbackType, "TryGetRegisteredComponent"));
        Assert.Contains(Calls(snapshot), IsCall(FallbackType, "TryGetRegisteredObject"));
        Assert.Contains(Calls(contract), IsCall(FallbackType, "FindStaticField"));
        Assert.Contains(Calls(options), call => call.Name == "GetChild");
        Assert.Contains(Calls(options), call => call.Name == "GetComponent");
        Assert.Contains(Calls(rewardObjects), call => call.Name == "GetComponent");

        string[] forbiddenStrings =
        {
            "FindObjectsOfTypeAll",
            "BuildRewardState",
            "GuiGameMcpObjectResolver",
            "JsonConvert"
        };
        MethodReference[] calls = fallback.Methods.SelectMany(Calls).ToArray();
        string[] strings = fallback.Methods.SelectMany(LoadedStrings).ToArray();
        Assert.DoesNotContain(calls, call => call.DeclaringType.FullName == "UnityEngine.Resources");
        Assert.DoesNotContain(calls, call => call.DeclaringType.FullName.Contains("JsonConvert", StringComparison.Ordinal));
        foreach (string forbidden in forbiddenStrings)
        {
            Assert.DoesNotContain(strings, value => value.Contains(forbidden, StringComparison.Ordinal));
        }
    }

    [Fact]
    public void RewardSelection_RequiresExactIdentityAndFailsClosedBeforeSingleClick()
    {
        using AssemblyDefinition assembly = ReadPlugin();
        TypeDefinition fallback = RequireType(assembly, FallbackType);
        MethodDefinition choose = RequireMethod(fallback, "TryChooseOption");
        MethodDefinition readSelection = RequireMethod(fallback, "TryReadSelection");
        Instruction[] instructions = choose.Body.Instructions.ToArray();
        int mutexAvailable = FindCall(instructions, FallbackType + "/RewardSnapshot", "get_MutexAvailable");
        int busy = FindCall(instructions, FallbackType + "/RewardSnapshot", "get_Busy");
        int canAcquire = FindCall(instructions, FallbackType + "/RewardOptionSnapshot", "get_CanAcquire");
        int click = FindCall(instructions, "System.Reflection.MethodBase", "Invoke");

        Assert.Contains("phaseToken", LoadedStrings(readSelection));
        Assert.Contains("itemInstanceId", LoadedStrings(readSelection));
        Assert.Contains("index", LoadedStrings(readSelection));
        Assert.True(mutexAvailable >= 0 && mutexAvailable < click);
        Assert.True(busy >= 0 && busy < click);
        Assert.True(canAcquire >= 0 && canAcquire < click);
        Assert.Equal(
            1,
            Calls(choose).Count(call =>
                call.DeclaringType.FullName == "System.Reflection.MethodBase" && call.Name == "Invoke"));

        foreach (string reconciliationFlag in new[]
                 {
                     "outcomeUnknown",
                     "needsReconciliation",
                     "uncertaintyOrigin",
                     "invocationStarted"
                 })
        {
            int flag = Array.FindIndex(
                instructions,
                click + 1,
                instruction => instruction.OpCode.Code == Code.Ldstr &&
                               Equals(instruction.Operand, reconciliationFlag));
            Assert.True(
                flag > click,
                reconciliationFlag + " must only be written after the click invocation starts.");
        }

        Assert.Contains("rewardClickException", LoadedStrings(choose));
        Assert.DoesNotContain("statePolluted", LoadedStrings(choose));
        Assert.DoesNotContain("needsReset", LoadedStrings(choose));
    }

    [Fact]
    public void RewardSkip_RequiresCurrentPhaseAndInvokesPanelOnce()
    {
        using AssemblyDefinition assembly = ReadPlugin();
        TypeDefinition fallback = RequireType(assembly, FallbackType);
        MethodDefinition skip = RequireMethod(fallback, "TrySkipCurrentOpportunity");
        MethodDefinition readPhase = RequireMethod(fallback, "TryReadPhaseToken");

        Assert.Contains("phaseToken", LoadedStrings(readPhase));
        Assert.Contains(Calls(skip), IsCall(FallbackType + "/RewardSnapshot", "get_CanSkip"));
        Assert.Equal(
            1,
            Calls(skip).Count(call =>
                call.DeclaringType.FullName == "System.Reflection.MethodBase" && call.Name == "Invoke"));
        Assert.Contains("rewardSkipException", LoadedStrings(skip));
    }

    [Fact]
    public void Controller_BindsIdentityWaitsForSettlementAndSoftFaultsOnTimeout()
    {
        using AssemblyDefinition assembly = ReadPlugin();
        TypeDefinition controller = RequireType(assembly, ControllerType);
        MethodDefinition tick = RequireMethod(controller, "TickInGame");
        MethodDefinition execute = RequireMethod(controller, "ExecuteWithResult");
        MethodDefinition bind = RequireMethod(controller, "BindRewardSelectionIdentity");
        MethodDefinition settle = RequireMethod(controller, "HandleRewardSelectionSettlement");
        Instruction[] tickInstructions = tick.Body.Instructions.ToArray();
        Instruction[] executeInstructions = execute.Body.Instructions.ToArray();

        int settlement = FindCall(tickInstructions, ControllerType, "HandleRewardSelectionSettlement");
        int firstObservation = FindCall(tickInstructions, ControllerType, "TryWaitForRewardOptions");
        Assert.True(settlement >= 0 && settlement < firstObservation);

        int armedGate = FindCall(executeInstructions, GuardType, "get_IsArmed");
        int bridgeInvoke = FindCall(executeInstructions, BridgeType, "Invoke");
        int arm = FindCall(executeInstructions, ControllerType, "TryArmRewardSelection");
        Assert.True(armedGate >= 0 && armedGate < bridgeInvoke);
        Assert.True(bridgeInvoke >= 0 && bridgeInvoke < arm);

        Assert.Contains("phaseToken", LoadedStrings(bind));
        Assert.Contains("itemInstanceId", LoadedStrings(bind));
        Assert.Contains(Calls(settle), IsCall(GuardType, "Observe"));
        Assert.Contains(Calls(settle), IsCall(ControllerType, "Fault"));
        Assert.DoesNotContain(Calls(settle), IsCall(ControllerType, "FaultRequiringProcessRestart"));
    }

    [Fact]
    public void InitialRewardObservation_WaitsForStableUiThenKeepsOneRecordingDelay()
    {
        using AssemblyDefinition assembly = ReadPlugin();
        MethodDefinition wait = RequireMethod(RequireType(assembly, ControllerType), "TryWaitForRewardOptions");
        string[] strings = LoadedStrings(wait).ToArray();

        Assert.Contains("busy", strings);
        Assert.Contains("refresh", strings);
        Assert.Contains("finished", strings);
        Assert.Contains(strings, value => value.Contains("0.75", StringComparison.Ordinal));
        Assert.Contains(wait.Body.Instructions, instruction =>
            instruction.OpCode.Code == Code.Ldc_R4 &&
            Math.Abs((float)instruction.Operand - 0.5f) < 0.001f);
        Assert.Contains(wait.Body.Instructions, instruction =>
            instruction.OpCode.Code == Code.Ldc_R4 &&
            Math.Abs((float)instruction.Operand - 0.75f) < 0.001f);
    }

    private static AssemblyDefinition ReadPlugin() => AssemblyDefinition.ReadAssembly(PluginPath());

    private static string PluginPath()
    {
        string path = Path.Combine(AppContext.BaseDirectory, "Loopstructor.AutoPlayer.Plugin.dll");
        Assert.True(File.Exists(path), "Plugin assembly was not copied to the test output: " + path);
        return path;
    }

    private static TypeDefinition RequireType(AssemblyDefinition assembly, string fullName) =>
        assembly.MainModule.Types.Single(type => type.FullName == fullName);

    private static MethodDefinition RequireMethod(TypeDefinition type, string name) =>
        type.Methods.Single(method => method.Name == name);

    private static int FindCall(Instruction[] instructions, string declaringType, string methodName) =>
        Array.FindIndex(
            instructions,
            instruction => instruction.Operand is MethodReference call &&
                           call.DeclaringType.FullName == declaringType &&
                           call.Name == methodName);

    private static IEnumerable<MethodReference> Calls(MethodDefinition method) =>
        method.Body.Instructions
            .Where(instruction => instruction.OpCode.Code is Code.Call or Code.Callvirt or Code.Newobj)
            .Select(instruction => instruction.Operand)
            .OfType<MethodReference>();

    private static IEnumerable<string> LoadedStrings(MethodDefinition method) =>
        method.Body.Instructions
            .Where(instruction => instruction.OpCode.Code == Code.Ldstr)
            .Select(instruction => instruction.Operand)
            .OfType<string>();

    private static Predicate<MethodReference> IsCall(string declaringType, string methodName) =>
        call => call.DeclaringType.FullName == declaringType && call.Name == methodName;
}
