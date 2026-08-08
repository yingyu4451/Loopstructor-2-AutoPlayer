using Loopstructor.AutoPlayer.Core;
using Mono.Cecil;
using Mono.Cecil.Cil;

namespace Loopstructor.AutoPlayer.Tests;

public sealed class RewardSettlementFrameIsolationContractTests
{
    private const string ControllerType = "Loopstructor.AutoPlayer.Plugin.AutoPlayController";
    private const string SelectionGuardType = "Loopstructor.AutoPlayer.Core.RewardSelectionSettlementGuard";
    private const string ObjectGuardType = "Loopstructor.AutoPlayer.Core.RewardObjectSettlementGuard";

    [Fact]
    public void TransientEmptyRewardOptions_AreRejectedBeforeSettlementObservationKeepsTheLock()
    {
        using AssemblyDefinition assembly = ReadPlugin();
        TypeDefinition controller = RequireType(assembly, ControllerType);
        MethodDefinition stable = RequireMethod(controller, "IsStableRewardSelectionObservation");
        MethodDefinition settle = RequireMethod(controller, "HandleRewardSelectionSettlement");
        Instruction[] stableInstructions = stable.Body.Instructions.ToArray();
        Instruction[] settleInstructions = settle.Body.Instructions.ToArray();

        Assert.Contains("options", LoadedStrings(stable));
        Assert.Contains("pending", LoadedStrings(stable));
        Assert.Contains("needsPolling", LoadedStrings(stable));
        Assert.Contains("busy", LoadedStrings(stable));
        Assert.Contains("refresh", LoadedStrings(stable));
        Assert.Contains("finished", LoadedStrings(stable));
        Assert.Contains(stableInstructions, instruction =>
            instruction.Operand is MethodReference call && call.Name == "get_Count");

        int stableGate = FindCall(settleInstructions, ControllerType, "IsStableRewardSelectionObservation");
        int observe = FindCall(settleInstructions, SelectionGuardType, "Observe", stableGate + 1);
        int complete = FindCall(settleInstructions, ControllerType, "CompleteRewardSelectionSettlement", observe + 1);

        Assert.True(stableGate >= 0 && stableGate < observe);
        Assert.Contains(settleInstructions[(stableGate + 1)..observe], instruction =>
            instruction.OpCode.Code == Code.Ret ||
            (instruction.OpCode.FlowControl == FlowControl.Branch &&
             instruction.Operand is Instruction target &&
             !IsReachable(settleInstructions, target, settleInstructions[observe])));
        Assert.True(complete > observe);
        Assert.DoesNotContain(settleInstructions[..observe], instruction =>
            IsCall(instruction, SelectionGuardType, "Reset"));
    }

    [Fact]
    public void UnavailableSpawnerWithEmptyRewardObjects_RemainsWaitingInsteadOfProvingSettlement()
    {
        RewardObjectSettlementGuard guard = new();
        Assert.True(guard.TryArm(101, 10f));

        RewardObjectSettlementStatus status = guard.Observe(
            activeRewardObjectInstanceIds: null,
            rewardPanelOrOptionsVisible: false,
            rewardBlockerVisible: true,
            now: 11f,
            timeoutSeconds: 20f);

        Assert.Equal(RewardObjectSettlementStatus.Waiting, status);
        Assert.True(guard.IsArmed);
        Assert.Equal(101, guard.RewardObjectInstanceId);

        using AssemblyDefinition assembly = ReadPlugin();
        MethodDefinition settle = RequireMethod(
            RequireType(assembly, ControllerType),
            "HandleRewardObjectSettlement");
        Instruction[] instructions = settle.Body.Instructions.ToArray();
        int spawnerAvailable = FindLoadedString(instructions, "spawnerAvailable");
        int rewardObjects = FindLoadedString(instructions, "rewardObjects", spawnerAvailable + 1);
        int observe = FindCall(instructions, ObjectGuardType, "Observe", rewardObjects + 1);

        Assert.True(spawnerAvailable >= 0 && spawnerAvailable < rewardObjects);
        Assert.True(rewardObjects < observe);
        Assert.Contains(instructions[(spawnerAvailable + 1)..observe], BranchesToNull);
    }

    [Fact]
    public void SameNamedSceneWithDifferentHandle_EntersCleanupAndClearsGuardsAndDeferredActions()
    {
        using AssemblyDefinition assembly = ReadPlugin();
        MethodDefinition observeScene = RequireMethod(
            RequireType(assembly, ControllerType),
            "ObserveActiveScene");
        Instruction[] instructions = observeScene.Body.Instructions.ToArray();

        int getHandle = FindCall(instructions, "UnityEngine.SceneManagement.Scene", "get_handle");
        int compareName = FindCall(instructions, "System.String", "Equals", getHandle + 1);
        int sceneHandleStore = FindField(instructions, Code.Stfld, "_sceneHandle", compareName + 1);
        int storedHandleRead = FindField(instructions, Code.Ldfld, "_sceneHandle", compareName + 1);
        int handleEquality = FindCode(instructions, Code.Ceq, storedHandleRead + 1, sceneHandleStore);
        int mismatchCleanupBranch = handleEquality >= 0
            ? FindInstruction(
                instructions,
                handleEquality + 1,
                sceneHandleStore,
                instruction => instruction.OpCode.Code is Code.Brfalse or Code.Brfalse_S)
            : FindInstruction(
                instructions,
                storedHandleRead + 1,
                sceneHandleStore,
                instruction => instruction.OpCode.Code is Code.Bne_Un or Code.Bne_Un_S);
        Assert.True(getHandle >= 0 && getHandle < compareName);
        Assert.True(compareName < storedHandleRead && storedHandleRead < mismatchCleanupBranch);
        Assert.True(mismatchCleanupBranch < sceneHandleStore);
        Instruction cleanupTarget = Assert.IsType<Instruction>(instructions[mismatchCleanupBranch].Operand);
        Assert.True(IsReachable(instructions, cleanupTarget, instructions[sceneHandleStore]));

        Assert.True(FindCall(instructions, SelectionGuardType, "Reset", sceneHandleStore + 1) > sceneHandleStore);
        Assert.True(FindCall(instructions, ObjectGuardType, "Reset", sceneHandleStore + 1) > sceneHandleStore);
        Assert.True(FindCall(instructions, ControllerType, "ResetRewardOptionObservation", sceneHandleStore + 1) > sceneHandleStore);

        foreach (string field in new[]
                 {
                     "_deferredFrontEndAction",
                     "_deferredNormalEventAction",
                     "_deferredNormalEventChoosingOption",
                     "_deferredRewardAction",
                     "_deferredSettlementAction"
                 })
        {
            Assert.True(
                FindField(instructions, Code.Stfld, field, sceneHandleStore + 1) > sceneHandleStore,
                field + " must be cleared when the scene instance changes.");
        }
    }

    [Fact]
    public void RewardQueryAndNonWaitRewardWrite_AreSeparatedByADeferredTickBoundary()
    {
        using AssemblyDefinition assembly = ReadPlugin();
        MethodDefinition tick = RequireMethod(RequireType(assembly, ControllerType), "TickInGame");
        Instruction[] instructions = tick.Body.Instructions.ToArray();

        int queryReward = FindLoadedString(instructions, "queryReward");
        int decide = FindCall(instructions, ControllerType, "DecideObservedReward", queryReward + 1);
        int deferredStore = FindField(instructions, Code.Stfld, "_deferredRewardAction", decide + 1);

        Assert.True(queryReward >= 0 && queryReward < decide);
        Assert.True(decide < deferredStore);
        Assert.False(IsAnyReachable(instructions, instructions[deferredStore], instruction =>
            IsCall(instruction, ControllerType, "Execute") ||
            IsCall(instruction, ControllerType, "ExecuteWithResult")));

        int deferredLoad = FindField(instructions, Code.Ldfld, "_deferredRewardAction");
        int deferredExecute = FindCall(instructions, ControllerType, "Execute", deferredLoad + 1);
        Assert.True(deferredLoad >= 0 && deferredLoad < deferredExecute);
        Assert.True(deferredExecute < queryReward);
        Assert.False(IsReachable(instructions, instructions[deferredExecute], instructions[queryReward]));
    }

    [Fact]
    public void RewardAppearanceGraceCompletesBeforeAFullRecordingObservationDelayStarts()
    {
        using AssemblyDefinition assembly = ReadPlugin();
        MethodDefinition wait = RequireMethod(
            RequireType(assembly, ControllerType),
            "TryWaitForRewardOptions");
        Instruction[] instructions = wait.Body.Instructions.ToArray();

        int grace = FindFloat(instructions, 1.25f);
        int appearanceReadyStore = FindField(
            instructions,
            Code.Stfld,
            "_rewardObjectsAppearanceReadyAt",
            grace + 1);
        int recordingDelay = FindFloat(instructions, 1.5f, appearanceReadyStore + 1);
        int observationReadyStore = FindField(
            instructions,
            Code.Stfld,
            "_rewardObjectsReadyAt",
            recordingDelay + 1);

        Assert.True(grace >= 0 && grace < appearanceReadyStore);
        Assert.Contains(instructions[(grace + 1)..appearanceReadyStore], instruction =>
            instruction.OpCode.Code == Code.Add);
        Assert.False(IsReachable(
            instructions,
            instructions[appearanceReadyStore],
            instructions[recordingDelay]));
        Assert.Contains(instructions[(appearanceReadyStore + 1)..recordingDelay], instruction =>
            IsField(instruction, Code.Ldfld, "_rewardObjectsAppearanceReadyAt"));
        Assert.True(recordingDelay < observationReadyStore);
        Assert.Contains(instructions[(recordingDelay + 1)..observationReadyStore], instruction =>
            instruction.OpCode.Code == Code.Add);
        Assert.Contains(LoadedStrings(wait), value => value.Contains("1.5", StringComparison.Ordinal));
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

    private static int FindCall(
        IReadOnlyList<Instruction> instructions,
        string declaringType,
        string methodName,
        int start = 0) =>
        FindInstruction(
            instructions,
            start,
            instructions.Count,
            instruction => IsCall(instruction, declaringType, methodName));

    private static int FindLoadedString(
        IReadOnlyList<Instruction> instructions,
        string expected,
        int start = 0) =>
        FindInstruction(
            instructions,
            start,
            instructions.Count,
            instruction => instruction.OpCode.Code == Code.Ldstr && Equals(instruction.Operand, expected));

    private static int FindField(
        IReadOnlyList<Instruction> instructions,
        Code code,
        string fieldName,
        int start = 0) =>
        FindInstruction(
            instructions,
            start,
            instructions.Count,
            instruction => IsField(instruction, code, fieldName));

    private static int FindCode(
        IReadOnlyList<Instruction> instructions,
        Code code,
        int start,
        int? end = null) =>
        FindInstruction(
            instructions,
            start,
            end ?? instructions.Count,
            instruction => instruction.OpCode.Code == code);

    private static int FindFloat(
        IReadOnlyList<Instruction> instructions,
        float expected,
        int start = 0) =>
        FindInstruction(
            instructions,
            start,
            instructions.Count,
            instruction => instruction.OpCode.Code == Code.Ldc_R4 &&
                           Math.Abs((float)instruction.Operand - expected) < 0.001f);

    private static int FindInstruction(
        IReadOnlyList<Instruction> instructions,
        int start,
        int end,
        Predicate<Instruction> match)
    {
        for (int index = Math.Max(0, start); index < Math.Min(end, instructions.Count); index++)
        {
            if (match(instructions[index]))
            {
                return index;
            }
        }

        return -1;
    }

    private static bool IsReachable(
        IReadOnlyList<Instruction> instructions,
        Instruction start,
        Instruction target) =>
        IsAnyReachable(instructions, start, instruction => ReferenceEquals(instruction, target));

    private static bool IsAnyReachable(
        IReadOnlyList<Instruction> instructions,
        Instruction start,
        Predicate<Instruction> match)
    {
        HashSet<Instruction> visited = new();
        Stack<Instruction> pending = new();
        pending.Push(start);
        while (pending.Count > 0)
        {
            Instruction current = pending.Pop();
            if (!visited.Add(current))
            {
                continue;
            }

            if (match(current))
            {
                return true;
            }

            foreach (Instruction successor in Successors(current))
            {
                pending.Push(successor);
            }
        }

        return false;
    }

    private static IEnumerable<Instruction> Successors(Instruction instruction)
    {
        if (instruction.OpCode.Code is Code.Ret or Code.Throw or Code.Rethrow or Code.Endfinally)
        {
            yield break;
        }

        if (instruction.Operand is Instruction target)
        {
            yield return target;
            if (instruction.OpCode.FlowControl == FlowControl.Branch)
            {
                yield break;
            }
        }
        else if (instruction.Operand is Instruction[] targets)
        {
            foreach (Instruction switchTarget in targets)
            {
                yield return switchTarget;
            }
        }

        if (instruction.Next != null)
        {
            yield return instruction.Next;
        }
    }

    private static bool BranchesToNull(Instruction instruction)
    {
        if (instruction.OpCode.FlowControl != FlowControl.Cond_Branch ||
            instruction.Operand is not Instruction target)
        {
            return false;
        }

        while (target.OpCode.Code == Code.Nop && target.Next != null)
        {
            target = target.Next;
        }

        return target.OpCode.Code == Code.Ldnull;
    }

    private static bool IsCall(Instruction instruction, string declaringType, string methodName) =>
        instruction.Operand is MethodReference call &&
        call.DeclaringType.FullName == declaringType &&
        call.Name == methodName;

    private static bool IsField(Instruction instruction, Code code, string fieldName) =>
        instruction.OpCode.Code == code &&
        instruction.Operand is FieldReference field &&
        field.Name == fieldName;

    private static IEnumerable<string> LoadedStrings(MethodDefinition method) =>
        method.Body.Instructions
            .Where(instruction => instruction.OpCode.Code == Code.Ldstr)
            .Select(instruction => instruction.Operand)
            .OfType<string>();
}
