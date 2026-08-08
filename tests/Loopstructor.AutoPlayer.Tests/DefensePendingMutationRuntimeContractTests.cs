using Mono.Cecil;
using Mono.Cecil.Cil;

namespace Loopstructor.AutoPlayer.Tests;

public sealed class DefensePendingMutationRuntimeContractTests
{
    private const string ControllerType =
        "Loopstructor.AutoPlayer.Plugin.AutoPlayController";
    private const string BridgeType =
        "Loopstructor.AutoPlayer.Plugin.RuntimeBridge";
    private const string GuardType =
        "Loopstructor.AutoPlayer.Core.PendingDisposableMutationGuard";

    [Fact]
    public void DefenseMaintenance_ReconcilesPendingDisposableBeforeWaveExit()
    {
        using AssemblyDefinition assembly = ReadPlugin();
        TypeDefinition controller = RequireType(assembly, ControllerType);
        MethodDefinition maintain = RequireMethod(controller, "TryMaintainDefense");
        Instruction[] instructions = maintain.Body.Instructions.ToArray();

        int armed = FindCall(instructions, GuardType, "get_IsArmed");
        int reconcile = FindCall(
            instructions,
            ControllerType,
            "HandlePendingDefenseDisposableMutation",
            armed + 1);
        int wavePulse = FindCall(instructions, BridgeType, "TryGetWavePulse");
        int interruptedFinish = FindCall(
            instructions,
            ControllerType,
            "FinishDefenseMaintenance");

        Assert.True(
            armed >= 0 && reconcile > armed && wavePulse > reconcile,
            "An armed defense-disposable write must enter read-only reconciliation before the wave/game-over gate runs.");
        Assert.True(
            interruptedFinish > wavePulse,
            "Wave/game-over handling may finish maintenance only after the pending-write reconciliation gate.");
    }

    [Fact]
    public void DefenseMaintenanceReset_PreservesArmedDisposableTransaction()
    {
        using AssemblyDefinition assembly = ReadPlugin();
        TypeDefinition controller = RequireType(assembly, ControllerType);
        TypeDefinition steps = RequireNestedType(controller, "DefenseMaintenanceStep");
        MethodDefinition reset = RequireMethod(controller, "ResetDefenseMaintenanceState");
        Instruction[] instructions = reset.Body.Instructions.ToArray();

        int armed = FindCall(instructions, GuardType, "get_IsArmed");
        int waitState = FindEnumFieldStore(
            instructions,
            "_defenseMaintenanceStep",
            EnumValue(steps, "WaitForExpansionAttributeSettlement"),
            armed + 1);
        int clearPlacement = FindCall(
            instructions,
            ControllerType,
            "ClearDefenseAttributePlacementState");

        Assert.True(
            armed >= 0 && waitState > armed,
            "Reset must keep an armed defense-disposable transaction in its read-only settlement state.");
        Assert.True(
            clearPlacement > waitState,
            "The placement reset must remain on the non-pending branch after settlement-state preservation.");
        Assert.True(
            FindArmedBooleanBranchSkipping(instructions, armed, clearPlacement) >= 0,
            "The value returned by PendingDisposableMutationGuard.IsArmed must bypass placement clearing.");
        Assert.DoesNotContain(
            Calls(reset),
            IsCall(GuardType, "Reset"));
    }

    [Theory]
    [InlineData("Pause", "ApplyPause")]
    [InlineData("Stop", "ApplyStop")]
    public void UserLifecycleTransition_RejectsWhileDefenseDisposableIsPending(
        string transitionName,
        string applyName)
    {
        using AssemblyDefinition assembly = ReadPlugin();
        TypeDefinition controller = RequireType(assembly, ControllerType);
        MethodDefinition transition = RequireMethod(controller, transitionName);
        Instruction[] instructions = transition.Body.Instructions.ToArray();

        int armed = FindCall(instructions, GuardType, "get_IsArmed");
        int beginPreviewRelease = FindCall(
            instructions,
            ControllerType,
            "BeginOwnedPreviewRelease");
        int apply = FindCall(instructions, ControllerType, applyName);

        Assert.True(
            armed >= 0 && beginPreviewRelease > armed && apply > beginPreviewRelease,
            $"{transitionName} must reject an armed defense-disposable transaction before preview release or {applyName}.");
        Assert.True(
            ArmedPathTerminatesBefore(instructions, armed, beginPreviewRelease),
            $"The armed {transitionName} path must return false before any lifecycle transition starts.");
    }

    [Fact]
    public void ApplyStop_DefensivelyRefusesStandbyWhileDefenseDisposableIsPending()
    {
        using AssemblyDefinition assembly = ReadPlugin();
        TypeDefinition controller = RequireType(assembly, ControllerType);
        MethodDefinition applyStop = RequireMethod(controller, "ApplyStop");
        Instruction[] instructions = applyStop.Body.Instructions.ToArray();

        int armed = FindCall(instructions, GuardType, "get_IsArmed");
        int standbyStore = FindEnumFieldStore(
            instructions,
            "_runState",
            expectedValue: 0,
            startIndex: armed + 1);

        Assert.True(
            armed >= 0 && standbyStore > armed,
            "ApplyStop must read the pending defense-disposable guard before assigning Standby.");
        Assert.True(
            ArmedPathTerminatesBefore(instructions, armed, standbyStore),
            "ApplyStop's armed path must return without assigning Standby.");
    }

    private static AssemblyDefinition ReadPlugin()
    {
        string path = Path.Combine(AppContext.BaseDirectory, "Loopstructor.AutoPlayer.Plugin.dll");
        Assert.True(File.Exists(path), "Plugin assembly was not copied to the test output: " + path);
        return AssemblyDefinition.ReadAssembly(path);
    }

    private static TypeDefinition RequireType(AssemblyDefinition assembly, string fullName) =>
        assembly.MainModule.Types.Single(type => type.FullName == fullName);

    private static TypeDefinition RequireNestedType(TypeDefinition type, string name) =>
        type.NestedTypes.Single(nested => nested.Name == name);

    private static MethodDefinition RequireMethod(TypeDefinition type, string name) =>
        type.Methods.Single(method => method.Name == name);

    private static int EnumValue(TypeDefinition enumType, string name) =>
        Convert.ToInt32(enumType.Fields.Single(field => field.Name == name).Constant);

    private static IEnumerable<MethodReference> Calls(MethodDefinition method) =>
        method.Body.Instructions
            .Where(instruction => instruction.OpCode.Code is Code.Call or Code.Callvirt)
            .Select(instruction => instruction.Operand)
            .OfType<MethodReference>();

    private static Predicate<MethodReference> IsCall(string declaringType, string methodName) =>
        call => call.DeclaringType.FullName == declaringType && call.Name == methodName;

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

    private static int FindEnumFieldStore(
        IReadOnlyList<Instruction> instructions,
        string fieldName,
        int expectedValue,
        int startIndex = 0)
    {
        for (int index = Math.Max(0, startIndex); index < instructions.Count; index++)
        {
            if (instructions[index].OpCode.Code != Code.Stfld ||
                instructions[index].Operand is not FieldReference field ||
                field.Name != fieldName)
            {
                continue;
            }

            Instruction? valueLoad = PreviousMeaningfulInstruction(instructions, index);
            if (valueLoad != null && TryReadInt32(valueLoad, out int value) && value == expectedValue)
            {
                return index;
            }
        }

        return -1;
    }

    private static int FindArmedBooleanBranchSkipping(
        IReadOnlyList<Instruction> instructions,
        int armedCall,
        int skippedCall)
    {
        Dictionary<int, bool> booleanLocals = BuildGuardBooleanLocals(
            instructions,
            armedCall,
            skippedCall);
        for (int index = armedCall + 1; index < skippedCall; index++)
        {
            Instruction branch = instructions[index];
            if (branch.OpCode.Code is not (Code.Brfalse or Code.Brfalse_S or Code.Brtrue or Code.Brtrue_S) ||
                branch.Operand is not Instruction target ||
                IndexOf(instructions, target) <= skippedCall)
            {
                continue;
            }

            Instruction? condition = PreviousMeaningfulInstruction(instructions, index);
            if (TryResolveConditionWhenArmed(
                    condition,
                    instructions[armedCall],
                    booleanLocals,
                    out bool conditionWhenArmed) &&
                BranchTaken(branch, conditionWhenArmed))
            {
                return index;
            }
        }

        return -1;
    }

    private static bool ArmedPathTerminatesBefore(
        IReadOnlyList<Instruction> instructions,
        int armedCall,
        int forbiddenIndex)
    {
        Dictionary<int, bool> booleanLocals = BuildGuardBooleanLocals(
            instructions,
            armedCall,
            forbiddenIndex);
        for (int index = armedCall + 1; index < forbiddenIndex; index++)
        {
            Instruction branch = instructions[index];
            if (branch.OpCode.Code is not (Code.Brfalse or Code.Brfalse_S or Code.Brtrue or Code.Brtrue_S) ||
                branch.Operand is not Instruction target)
            {
                continue;
            }

            Instruction? condition = PreviousMeaningfulInstruction(instructions, index);
            if (!TryResolveConditionWhenArmed(
                    condition,
                    instructions[armedCall],
                    booleanLocals,
                    out bool conditionWhenArmed))
            {
                continue;
            }

            bool armedTakesBranch = BranchTaken(branch, conditionWhenArmed);
            int armedPathStart = armedTakesBranch
                ? IndexOf(instructions, target)
                : index + 1;
            if (armedPathStart < 0 || armedPathStart >= forbiddenIndex) return true;

            int armedBlockEnd = armedTakesBranch
                ? forbiddenIndex
                : Math.Min(IndexOf(instructions, target), forbiddenIndex);
            for (int pathIndex = armedPathStart; pathIndex < armedBlockEnd; pathIndex++)
            {
                Instruction pathInstruction = instructions[pathIndex];
                if (pathInstruction.OpCode.Code == Code.Ret) return true;
                if (pathInstruction.OpCode.FlowControl != FlowControl.Branch ||
                    pathInstruction.Operand is not Instruction exitTarget)
                {
                    continue;
                }

                int exitIndex = IndexOf(instructions, exitTarget);
                if (exitIndex < 0 || exitIndex > forbiddenIndex) return true;
            }

            return false;
        }

        return false;
    }

    private static Dictionary<int, bool> BuildGuardBooleanLocals(
        IReadOnlyList<Instruction> instructions,
        int armedCall,
        int exclusiveEnd)
    {
        Dictionary<int, bool> result = new();
        int? armedLocal = StoredLocalAfter(instructions, armedCall);
        if (!armedLocal.HasValue) return result;
        result[armedLocal.Value] = true;

        for (int index = armedCall + 1; index < Math.Min(exclusiveEnd, instructions.Count); index++)
        {
            int? storedLocal = StoredLocal(instructions[index]);
            if (!storedLocal.HasValue) continue;

            Instruction? equality = PreviousMeaningfulInstruction(instructions, index);
            int equalityIndex = equality == null ? -1 : IndexOf(instructions, equality);
            if (equality?.OpCode.Code != Code.Ceq || equalityIndex < 0) continue;

            Instruction? zero = PreviousMeaningfulInstruction(instructions, equalityIndex);
            int zeroIndex = zero == null ? -1 : IndexOf(instructions, zero);
            Instruction? source = zeroIndex < 0
                ? null
                : PreviousMeaningfulInstruction(instructions, zeroIndex);
            int? sourceLocal = source == null ? null : LoadedLocal(source);
            if (zero == null || !TryReadInt32(zero, out int zeroValue) || zeroValue != 0 ||
                !sourceLocal.HasValue || !result.TryGetValue(sourceLocal.Value, out bool sourceWhenArmed))
            {
                continue;
            }

            result[storedLocal.Value] = !sourceWhenArmed;
        }

        return result;
    }

    private static bool BranchTaken(Instruction branch, bool condition) =>
        branch.OpCode.Code is Code.Brtrue or Code.Brtrue_S ? condition : !condition;

    private static bool TryResolveConditionWhenArmed(
        Instruction? condition,
        Instruction armedCall,
        IReadOnlyDictionary<int, bool> booleanLocals,
        out bool value)
    {
        if (ReferenceEquals(condition, armedCall))
        {
            value = true;
            return true;
        }

        int? conditionLocal = condition == null ? null : LoadedLocal(condition);
        if (conditionLocal.HasValue &&
            booleanLocals.TryGetValue(conditionLocal.Value, out value))
        {
            return true;
        }

        value = false;
        return false;
    }

    private static int? StoredLocalAfter(
        IReadOnlyList<Instruction> instructions,
        int instructionIndex)
    {
        Instruction? next = NextMeaningfulInstruction(instructions, instructionIndex);
        return next == null ? null : StoredLocal(next);
    }

    private static int? StoredLocal(Instruction instruction) => instruction.OpCode.Code switch
    {
        Code.Stloc_0 => 0,
        Code.Stloc_1 => 1,
        Code.Stloc_2 => 2,
        Code.Stloc_3 => 3,
        Code.Stloc or Code.Stloc_S when instruction.Operand is VariableDefinition variable => variable.Index,
        _ => null
    };

    private static int? LoadedLocal(Instruction instruction) => instruction.OpCode.Code switch
    {
        Code.Ldloc_0 => 0,
        Code.Ldloc_1 => 1,
        Code.Ldloc_2 => 2,
        Code.Ldloc_3 => 3,
        Code.Ldloc or Code.Ldloc_S when instruction.Operand is VariableDefinition variable => variable.Index,
        _ => null
    };

    private static Instruction? NextMeaningfulInstruction(
        IReadOnlyList<Instruction> instructions,
        int index)
    {
        for (int next = index + 1; next < instructions.Count; next++)
        {
            if (instructions[next].OpCode.Code != Code.Nop) return instructions[next];
        }

        return null;
    }

    private static Instruction? PreviousMeaningfulInstruction(
        IReadOnlyList<Instruction> instructions,
        int index)
    {
        for (int previous = index - 1; previous >= 0; previous--)
        {
            if (instructions[previous].OpCode.Code != Code.Nop) return instructions[previous];
        }

        return null;
    }

    private static int IndexOf(IReadOnlyList<Instruction> instructions, Instruction target)
    {
        for (int index = 0; index < instructions.Count; index++)
        {
            if (ReferenceEquals(instructions[index], target)) return index;
        }

        return -1;
    }

    private static bool TryReadInt32(Instruction instruction, out int value)
    {
        switch (instruction.OpCode.Code)
        {
            case Code.Ldc_I4_M1: value = -1; return true;
            case Code.Ldc_I4_0: value = 0; return true;
            case Code.Ldc_I4_1: value = 1; return true;
            case Code.Ldc_I4_2: value = 2; return true;
            case Code.Ldc_I4_3: value = 3; return true;
            case Code.Ldc_I4_4: value = 4; return true;
            case Code.Ldc_I4_5: value = 5; return true;
            case Code.Ldc_I4_6: value = 6; return true;
            case Code.Ldc_I4_7: value = 7; return true;
            case Code.Ldc_I4_8: value = 8; return true;
            case Code.Ldc_I4_S: value = (sbyte)instruction.Operand; return true;
            case Code.Ldc_I4: value = (int)instruction.Operand; return true;
            default: value = 0; return false;
        }
    }
}
