using Loopstructor.AutoPlayer.Core;
using Loopstructor.AutoPlayer.Manager.Models;
using Mono.Cecil;
using Mono.Cecil.Cil;

namespace Loopstructor.AutoPlayer.Tests;

public sealed class AutoPlayFaultPolicyContractTests
{
    private const string ControllerType = "Loopstructor.AutoPlayer.Plugin.AutoPlayController";
    private const string ResultInspectorType = "Loopstructor.AutoPlayer.Core.RuntimeResultInspector";

    [Fact]
    public void ProcessRestartFlag_HasOneExplicitAuditableWriter()
    {
        using AssemblyDefinition assembly = ReadPlugin();
        TypeDefinition controller = RequireType(assembly, ControllerType);
        MethodDefinition requireRestart = RequireMethod(controller, "RequireProcessRestart");

        MethodDefinition[] writers = controller.Methods
            .Where(method => method.HasBody)
            .Where(method => method.Body.Instructions.Any(
                instruction => StoresField("_needsProcessRestart")(instruction)))
            .ToArray();

        Assert.Equal(new[] { "RequireProcessRestart" }, writers.Select(method => method.Name));
        Assert.Contains(requireRestart.Body.Instructions, StoresField("_needsProcessRestart"));
        Assert.Contains(requireRestart.Body.Instructions, LoadsInt32(1));

        Assert.DoesNotContain(
            RequireMethod(controller, "CommitFault").Body.Instructions,
            StoresField("_needsProcessRestart"));
        Assert.DoesNotContain(
            RequireMethod(controller, "ArmFaultWhilePreviewReleaseRuns").Body.Instructions,
            StoresField("_needsProcessRestart"));
    }

    [Fact]
    public void ExpectedNonVictoryOutcomes_UseSoftFaults()
    {
        using AssemblyDefinition assembly = ReadPlugin();
        TypeDefinition controller = RequireType(assembly, ControllerType);

        AssertOutcomeRoutesToSoftFault(
            RequireMethod(controller, "Tick"),
            (int)AutomationOutcome.Timeout);
        AssertOutcomeRoutesToSoftFault(
            RequireMethod(controller, "TickInGame"),
            (int)AutomationOutcome.WaveLimit);
        AssertOutcomeRoutesToSoftFault(
            RequireMethod(controller, "TickSettlement"),
            (int)AutomationOutcome.Defeat);

        foreach (string methodName in new[] { "RegisterFailure", "CheckForStall" })
        {
            MethodDefinition method = RequireMethod(controller, methodName);
            Assert.Contains(Calls(method), IsControllerCall("Fault"));
            Assert.DoesNotContain(Calls(method), IsControllerCall("FaultRequiringProcessRestart"));
            Assert.DoesNotContain(method.Body.Instructions, StoresField("_needsProcessRestart"));
        }
    }

    [Fact]
    public void UnsafeWritesAndFailedPreviewCleanup_RequireRestart()
    {
        using AssemblyDefinition assembly = ReadPlugin();
        TypeDefinition controller = RequireType(assembly, ControllerType);
        MethodDefinition execute = RequireMethod(controller, "ExecuteWithResult");
        MethodDefinition hardFault = RequireMethod(controller, "FaultRequiringProcessRestart");
        MethodDefinition failedCleanup = RequireMethod(controller, "FailOwnedPreviewRelease");
        MethodDefinition completedCleanup = RequireMethod(controller, "CompleteOwnedPreviewRelease");

        Assert.Contains(Calls(execute), IsCall(ResultInspectorType, "Classify"));
        Assert.Contains(Calls(execute), IsControllerCall("FaultRequiringProcessRestart"));
        Assert.Contains(Calls(execute), IsControllerCall("RegisterFailure"));
        Assert.Contains(Calls(hardFault), IsControllerCall("RequireProcessRestart"));
        Assert.Contains(Calls(hardFault), IsControllerCall("Fault"));

        Assert.Contains(Calls(failedCleanup), IsControllerCall("RequireProcessRestart"));
        Assert.Contains(Calls(failedCleanup), IsControllerCall("CommitFault"));
        Assert.DoesNotContain(Calls(completedCleanup), IsControllerCall("RequireProcessRestart"));
        Assert.Contains(Calls(completedCleanup), IsControllerCall("CommitFault"));
    }

    [Fact]
    public void UnsafeWriteMessageExplainsThatReportsAndSavesAreNotCorrupted()
    {
        using AssemblyDefinition assembly = ReadPlugin();
        MethodDefinition message = RequireMethod(
            RequireType(assembly, ControllerType),
            "UnsafeWriteMessage");
        string[] text = message.Body.Instructions
            .Where(instruction => instruction.OpCode.Code == Code.Ldstr)
            .Select(instruction => instruction.Operand)
            .OfType<string>()
            .ToArray();

        Assert.Contains(text, value => value.Contains("游戏内写入一致性无法确认", StringComparison.Ordinal));
        Assert.Contains(text, value => value.Contains("不是报告文件或游戏存档损坏", StringComparison.Ordinal));
        Assert.Contains(text, value => value.Contains("防止重复写入", StringComparison.Ordinal));
    }

    [Fact]
    public void FrontEndCommitTimeout_UsesHardFault()
    {
        using AssemblyDefinition assembly = ReadPlugin();
        TypeDefinition controller = RequireType(assembly, ControllerType);
        MethodDefinition tickFrontEnd = RequireMethod(controller, "TickFrontEnd");

        Assert.Contains(Calls(tickFrontEnd), IsControllerCall("FaultRequiringProcessRestart"));
    }

    [Fact]
    public void PreviewCleanup_BlocksNewRunAndCheatModeUntilItFinishes()
    {
        using AssemblyDefinition assembly = ReadPlugin();
        TypeDefinition controller = RequireType(assembly, ControllerType);
        MethodDefinition start = RequireMethod(controller, "Start");
        MethodDefinition setCheatMode = RequireMethod(controller, "TrySetCheatMode");
        Instruction[] startInstructions = start.Body.Instructions.ToArray();

        int cleanupGuard = FindPreviewReleaseGuard(startInstructions);
        int cleanupReset = FindCall(startInstructions, ControllerType, "ResetOwnedPreviewReleaseState");

        Assert.True(cleanupGuard >= 0, "Start must inspect the owned-preview cleanup state.");
        Assert.True(
            cleanupReset < 0 || cleanupGuard < cleanupReset,
            "Start must reject an in-progress preview cleanup before resetting cleanup state.");
        Assert.True(
            FindPreviewReleaseGuard(setCheatMode.Body.Instructions.ToArray()) >= 0,
            "Cheat mode must not start while owned-preview cleanup is in progress.");
    }

    [Fact]
    public void Manager_AllowsRetryAfterSoftFaultButBlocksHardRestartGate()
    {
        RunControlAvailability softFault = RunControlAvailability.From(
            sessionTrusted: true,
            new AutoPlayerStatus
            {
                RunState = AutoPlayerRunState.Faulted,
                Outcome = AutomationOutcome.Error,
                NeedsProcessRestart = false
            });
        RunControlAvailability hardFault = RunControlAvailability.From(
            sessionTrusted: true,
            new AutoPlayerStatus
            {
                RunState = AutoPlayerRunState.Faulted,
                Outcome = AutomationOutcome.Error,
                NeedsProcessRestart = true
            });

        Assert.True(softFault.CanStart);
        Assert.False(hardFault.CanStart);
    }

    private static void AssertOutcomeRoutesToSoftFault(MethodDefinition method, int outcome)
    {
        Instruction[] instructions = method.Body.Instructions.ToArray();
        int outcomeStore = FindOutcomeStore(instructions, outcome);
        Assert.True(
            outcomeStore >= 0,
            $"{method.Name} must assign automation outcome {outcome}.");

        MethodReference? faultCall = instructions
            .Skip(outcomeStore + 1)
            .Where(IsCallInstruction)
            .Select(instruction => instruction.Operand)
            .OfType<MethodReference>()
            .FirstOrDefault(call =>
                call.DeclaringType.FullName == ControllerType &&
                call.Name is "Fault" or "FaultRequiringProcessRestart");

        Assert.NotNull(faultCall);
        Assert.Equal("Fault", faultCall!.Name);
    }

    private static int FindOutcomeStore(IReadOnlyList<Instruction> instructions, int outcome)
    {
        for (int index = 1; index < instructions.Count; index++)
        {
            if (!StoresField("_outcome")(instructions[index])) continue;
            if (ReadLoadedInt32(instructions[index - 1]) == outcome) return index;
        }

        return -1;
    }

    private static int FindPreviewReleaseGuard(IReadOnlyList<Instruction> instructions)
    {
        for (int index = 0; index < instructions.Count; index++)
        {
            Instruction instruction = instructions[index];
            if (LoadsField("_ownedPreviewReleaseOperation")(instruction)) return index;
            if (instruction.Operand is MethodReference call &&
                call.DeclaringType.FullName == ControllerType &&
                call.Name.Contains("OwnedPreviewRelease", StringComparison.Ordinal) &&
                call.Name != "ResetOwnedPreviewReleaseState")
            {
                return index;
            }
        }

        return -1;
    }

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
            .Where(IsCallInstruction)
            .Select(instruction => instruction.Operand)
            .OfType<MethodReference>();

    private static bool IsCallInstruction(Instruction instruction) =>
        instruction.OpCode.Code is Code.Call or Code.Callvirt;

    private static Predicate<MethodReference> IsControllerCall(string methodName) =>
        IsCall(ControllerType, methodName);

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

    private static Predicate<Instruction> LoadsInt32(int expected) =>
        instruction => ReadLoadedInt32(instruction) == expected;

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
        Code.Ldc_I4_S or Code.Ldc_I4 => Convert.ToInt32(instruction.Operand),
        _ => null
    };
}
