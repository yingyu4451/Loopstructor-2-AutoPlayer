using Mono.Cecil;
using Mono.Cecil.Cil;

namespace Loopstructor.AutoPlayer.Tests;

public sealed class WaveFunctionUiRuntimeFallbackContractTests
{
    private const string FallbackType =
        "Loopstructor.AutoPlayer.Plugin.WaveFunctionUiRuntimeFallback";

    [Fact]
    public void Query_UsesRegisteredEventPanelAndReturnsACompleteIdentitySnapshot()
    {
        using AssemblyDefinition assembly = ReadPlugin();
        TypeDefinition fallback = RequireType(assembly, FallbackType);
        MethodDefinition query = RequireMethod(fallback, "TryQueryOptions");
        MethodDefinition findPanel = RequireMethod(fallback, "TryFindActiveEventPanel");
        MethodDefinition findRegisteredPanel = RequireMethod(fallback, "TryFindRegisteredEventPanel");
        MethodDefinition getItems = RequireMethod(fallback, "GetOrderedItems");
        MethodDefinition buildPanel = RequireMethod(fallback, "BuildPanelState");
        MethodDefinition buildOption = RequireMethod(fallback, "BuildOptionState");

        Assert.Contains(Calls(query), IsCall(FallbackType, "TryGetContract"));
        Assert.Contains(Calls(query), IsCall(FallbackType, "TryFindActiveEventPanel"));
        Assert.Contains(Calls(findPanel), IsCall(FallbackType, "TryFindRegisteredEventPanel"));
        Assert.Contains(Calls(findRegisteredPanel), IsCall("System.Reflection.PropertyInfo", "GetValue"));
        Assert.Contains(Calls(getItems), call =>
            call.DeclaringType.FullName == "UnityEngine.Component" &&
            call.Name == "GetComponentsInChildren");
        Assert.Contains(Calls(getItems), IsCall("UnityEngine.Transform", "IsChildOf"));

        foreach (string field in new[]
                 {
                     "panel",
                     "panelOpen",
                     "panelInstanceId",
                     "snapshotComplete",
                     "optionsGenerated",
                     "eventKey",
                     "eventTag",
                     "eventTagValue",
                     "shouldShowAppearanceAnimation",
                     "appearanceAnimationReadable",
                     "appearanceAnimationComplete",
                     "appearanceAnimationName",
                     "appearanceAnimationDurationSeconds",
                     "options"
                 })
        {
            Assert.Contains(field, LoadedStrings(buildPanel));
        }

        foreach (string field in new[]
                 {
                     "instanceId",
                     "buttonInstanceId",
                     "buttonActive",
                     "conditionPass",
                     "displayText",
                     "optionName",
                     "optionSide",
                     "optionSideValue",
                     "styleTag",
                     "tagEnums",
                     "tagValues",
                     "behaviourTypes",
                     "behaviourTypeIds",
                     "extraDataType"
                 })
        {
            Assert.Contains(field, LoadedStrings(buildOption));
        }

        MethodReference[] calls = fallback.Methods.SelectMany(Calls).ToArray();
        string[] strings = fallback.Methods.SelectMany(LoadedStrings).ToArray();
        Assert.DoesNotContain(calls, call => call.DeclaringType.FullName == "UnityEngine.Resources");
        Assert.DoesNotContain(strings, value => value.Contains("FindObjectsOfTypeAll", StringComparison.Ordinal));
        Assert.DoesNotContain(strings, value => value.Contains("GuiGameMcpObjectResolver", StringComparison.Ordinal));
    }

    [Fact]
    public void AppearanceState_UsesTheLiveSpineTrackAndGatesSelectionBeforeObservationStarts()
    {
        using AssemblyDefinition assembly = ReadPlugin();
        TypeDefinition fallback = RequireType(assembly, FallbackType);
        MethodDefinition appearance = RequireMethod(fallback, "BuildAppearanceAnimationState");
        MethodReference[] appearanceCalls = Calls(appearance).ToArray();

        Assert.Contains(appearanceCalls, call =>
            call.DeclaringType.FullName == "UnityEngine.Component" &&
            call.Name == "GetComponentsInChildren");
        Assert.Contains("Appear", LoadedStrings(appearance));
        Assert.Contains("readable", LoadedStrings(appearance));
        Assert.Contains("complete", LoadedStrings(appearance));

        TypeDefinition controller = RequireType(
            assembly,
            "Loopstructor.AutoPlayer.Plugin.AutoPlayController");
        MethodDefinition opening = RequireMethod(controller, "TryHandleOpeningWaveFunctionUi");
        MethodDefinition wait = RequireMethod(controller, "TryWaitForWaveFunctionAppearance");
        Instruction[] openingInstructions = opening.Body.Instructions.ToArray();
        int animationGate = FindCall(
            openingInstructions,
            controller.FullName,
            "TryWaitForWaveFunctionAppearance");
        int optionObservation = FindCall(
            openingInstructions,
            controller.FullName,
            "TryWaitForEventOptions");

        Assert.True(animationGate >= 0 && animationGate < optionObservation);
        foreach (string field in new[]
                 {
                     "appearanceAnimationReadable",
                     "appearanceAnimationComplete",
                     "appearanceAnimationName",
                     "appearanceAnimationDurationSeconds"
                 })
        {
            Assert.Contains(field, LoadedStrings(wait));
        }
        Assert.Contains(Calls(wait), call =>
            call.DeclaringType.FullName == controller.FullName &&
            call.Name == "ClearSelectionHighlight");
    }

    [Fact]
    public void ExpectedPanelQueryCanReturnACompleteClosedSnapshot()
    {
        using AssemblyDefinition assembly = ReadPlugin();
        TypeDefinition fallback = RequireType(assembly, FallbackType);
        MethodDefinition query = RequireMethod(fallback, "TryQueryPanelState");
        MethodDefinition closed = RequireMethod(fallback, "BuildClosedPanelState");

        Assert.Contains(Calls(query), IsCall(FallbackType, "TryQueryOptions"));
        Assert.Contains(Calls(query), IsCall(FallbackType, "TryFindRegisteredEventPanel"));
        foreach (string field in new[] { "eventPanel", "panelOpen", "snapshotComplete", "options" })
        {
            Assert.Contains(
                field,
                LoadedStrings(query).Concat(LoadedStrings(closed)));
        }
    }

    [Fact]
    public void Choose_RequiresFourfoldIdentityAndInvokesOnClickExactlyOnce()
    {
        using AssemblyDefinition assembly = ReadPlugin();
        TypeDefinition fallback = RequireType(assembly, FallbackType);
        MethodDefinition choose = RequireMethod(fallback, "TryChooseOption");
        Instruction[] instructions = choose.Body.Instructions.ToArray();
        int click = FindCall(instructions, "System.Reflection.MethodBase", "Invoke");

        Assert.True(click >= 0, "The validated EventUI option must invoke its native OnClick method.");
        Assert.Equal(1, Calls(choose).Count(call =>
            call.DeclaringType.FullName == "System.Reflection.MethodBase" && call.Name == "Invoke"));
        Assert.Equal(1, Calls(choose).Count(call =>
            call.DeclaringType.FullName == FallbackType && call.Name == "ReadIndex"));
        Assert.Equal(2, Calls(choose).Count(call =>
            call.DeclaringType.FullName == FallbackType && call.Name == "ReadIdentity"));
        Assert.True(FindLoadedString(instructions, "EventUI") < click);
        foreach (string identity in new[] { "panel", "panelInstanceId", "instanceId", "index" })
        {
            int identityRead = FindLoadedString(instructions, identity);
            Assert.True(identityRead >= 0 && identityRead < click, identity + " must be validated before OnClick.");
        }

        foreach (string availability in new[] { "snapshotComplete", "buttonActive", "conditionPass" })
        {
            int availabilityRead = FindLoadedString(instructions, availability);
            Assert.True(
                availabilityRead >= 0 && availabilityRead < click,
                availability + " must gate OnClick.");
        }

        int panelLookup = FindCall(instructions, FallbackType, "TryFindActiveEventPanel");
        int firstSnapshot = FindCall(instructions, FallbackType, "BuildPanelState");
        Assert.True(panelLookup >= 0 && firstSnapshot > panelLookup && click > firstSnapshot);
    }

    [Fact]
    public void ClickException_RequiresReadOnlyReconciliationWithoutPollutionFlags()
    {
        using AssemblyDefinition assembly = ReadPlugin();
        MethodDefinition choose = RequireMethod(RequireType(assembly, FallbackType), "TryChooseOption");
        Instruction[] instructions = choose.Body.Instructions.ToArray();
        int click = FindCall(instructions, "System.Reflection.MethodBase", "Invoke");

        foreach (string field in new[] { "outcomeUnknown", "needsReconciliation", "invocationStarted" })
        {
            int stateWrite = FindLoadedString(instructions, field, click + 1);
            Assert.True(stateWrite > click, field + " must be emitted only after OnClick starts.");
        }

        Assert.DoesNotContain("statePolluted", LoadedStrings(choose));
        Assert.DoesNotContain("needsReset", LoadedStrings(choose));
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

    private static int FindCall(
        IReadOnlyList<Instruction> instructions,
        string declaringType,
        string methodName) =>
        Array.FindIndex(
            instructions.ToArray(),
            instruction => instruction.Operand is MethodReference call &&
                           call.DeclaringType.FullName == declaringType &&
                           call.Name == methodName);

    private static int FindLoadedString(
        IReadOnlyList<Instruction> instructions,
        string value,
        int startIndex = 0)
    {
        for (int index = Math.Max(0, startIndex); index < instructions.Count; index++)
        {
            if (instructions[index].OpCode.Code == Code.Ldstr &&
                string.Equals(instructions[index].Operand as string, value, StringComparison.Ordinal))
            {
                return index;
            }
        }

        return -1;
    }
}
