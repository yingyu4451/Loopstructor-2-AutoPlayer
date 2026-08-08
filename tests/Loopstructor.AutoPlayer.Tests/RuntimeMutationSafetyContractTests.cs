using System.Collections;
using System.Reflection;
using Loopstructor.AutoPlayer.Core;
using Mono.Cecil;
using Mono.Cecil.Cil;
using Newtonsoft.Json.Linq;

namespace Loopstructor.AutoPlayer.Tests;

public sealed class RuntimeMutationSafetyContractTests
{
    private const string BridgeType = "Loopstructor.AutoPlayer.Plugin.RuntimeBridge";
    private const string RepairFallbackType = "Loopstructor.AutoPlayer.Plugin.RepairUiRuntimeFallback";
    private const string MergeFallbackType = "Loopstructor.AutoPlayer.Plugin.MergeUiRuntimeFallback";

    [Theory]
    [InlineData("startWave", nameof(ThrowingRuntimeCommand))]
    [InlineData("startWave", nameof(NullRuntimeCommand))]
    public void MutatingRuntimeInvocationWithoutAResult_IsUnknownButNotDeclaredPolluted(
        string command,
        string stubName)
    {
        JObject result = InvokeBridge(command, stubName);

        Assert.Equal(RuntimeResultDisposition.Unsafe, RuntimeResultInspector.Classify(result));
        Assert.False(result.Value<bool>("success"));
        Assert.True(result.SelectToken("data.state.outcomeUnknown")?.Value<bool>());
        Assert.True(result.SelectToken("data.state.needsReconciliation")?.Value<bool>());
        Assert.Equal(
            "bridgeDispatchException",
            result.SelectToken("data.state.uncertaintyOrigin")?.Value<string>());
        Assert.NotEqual(true, result.SelectToken("data.state.statePolluted")?.Value<bool>());
        Assert.NotEqual(true, result.SelectToken("data.state.needsReset")?.Value<bool>());
        Assert.True(result.SelectToken("data.state.invocationStarted")?.Value<bool>());
    }

    [Theory]
    [InlineData("queryWave", nameof(ThrowingRuntimeCommand))]
    [InlineData("queryWave", nameof(NullRuntimeCommand))]
    [InlineData("previewRailPath", nameof(ThrowingRuntimeCommand))]
    [InlineData("previewRailPath", nameof(NullRuntimeCommand))]
    public void ReadOnlyRuntimeInvocationWithoutAResult_RemainsAnOrdinaryFailure(string command, string stubName)
    {
        JObject result = InvokeBridge(command, stubName);

        Assert.Equal(RuntimeResultDisposition.Failure, RuntimeResultInspector.Classify(result));
        Assert.False(result.Value<bool>("success"));
        Assert.NotEqual(true, result.SelectToken("data.state.statePolluted")?.Value<bool>());
        Assert.NotEqual(true, result.SelectToken("data.state.needsReset")?.Value<bool>());
        Assert.NotEqual(true, result.SelectToken("data.state.invocationStarted")?.Value<bool>());
    }

    [Fact]
    public void RuntimeBridge_ArmsMutationUncertaintyOnlyAfterCommandResolution()
    {
        using AssemblyDefinition assembly = ReadPlugin();
        TypeDefinition bridge = RequireType(assembly, BridgeType);
        MethodDefinition invoke = RequireMethod(bridge, "Invoke");
        Instruction[] instructions = invoke.Body.Instructions.ToArray();

        int commandLookup = FindCall(instructions, "System.Collections.Generic.Dictionary`2<System.String,System.Reflection.MethodInfo>", "TryGetValue");
        int mutationClassification = FindCall(instructions, BridgeType, "IsMutatingCommand");
        int runtimeInvoke = FindCall(instructions, "System.Reflection.MethodBase", "Invoke");

        Assert.True(commandLookup >= 0 && commandLookup < mutationClassification);
        Assert.True(mutationClassification < runtimeInvoke);
        Assert.True(Calls(invoke).Count(call => IsCall(BridgeType, "UncertainMutationError")(call)) >= 3);

        MethodDefinition classifier = RequireMethod(bridge, "IsMutatingCommand");
        Assert.Contains("query", LoadedStrings(classifier));
        Assert.Contains("previewRailPath", LoadedStrings(classifier));
        Assert.Contains(Calls(classifier), IsCall("System.String", "StartsWith"));
        Assert.Contains(Calls(classifier), IsCall("System.String", "Equals"));

        MethodDefinition uncertainError = RequireMethod(bridge, "UncertainMutationError");
        Assert.Contains(Calls(uncertainError), IsCall("Newtonsoft.Json.Linq.JObject", "FromObject"));
    }

    [Fact]
    public void RepairFallback_MarksOnlyPostClickExceptionsAsUncertain()
    {
        using AssemblyDefinition assembly = ReadPlugin();
        TypeDefinition fallback = RequireType(assembly, RepairFallbackType);

        AssertPostInvocationUnknownOutcomeGate(RequireMethod(fallback, "TryChooseOption"));
    }

    [Fact]
    public void MergeFallbacks_MarkOnlyPostCommandExceptionsAsUncertain()
    {
        using AssemblyDefinition assembly = ReadPlugin();
        TypeDefinition fallback = RequireType(assembly, MergeFallbackType);

        AssertPostInvocationUncertaintyGate(RequireMethod(fallback, "TryClosePanel"));
        AssertPostInvocationUncertaintyGate(RequireMethod(fallback, "TryConfirmSettlement"));
    }

    public static object ThrowingRuntimeCommand(string arguments) =>
        throw new InvalidOperationException("synthetic runtime failure after dispatch");

    public static object? NullRuntimeCommand(string arguments) => null;

    private static JObject InvokeBridge(string command, string stubName)
    {
        Assembly plugin = Assembly.LoadFrom(PluginPath());
        Type bridgeType = plugin.GetType(BridgeType, throwOnError: true)!;
        object bridge = Activator.CreateInstance(bridgeType, nonPublic: true)!;
        FieldInfo commandsField = bridgeType.GetField("_commands", BindingFlags.Instance | BindingFlags.NonPublic)!;
        IDictionary commands = Assert.IsAssignableFrom<IDictionary>(commandsField.GetValue(bridge));
        MethodInfo stub = typeof(RuntimeMutationSafetyContractTests).GetMethod(
            stubName,
            BindingFlags.Public | BindingFlags.Static)!;
        commands[command] = stub;

        MethodInfo invoke = bridgeType.GetMethod("Invoke", BindingFlags.Instance | BindingFlags.Public)!;
        return Assert.IsType<JObject>(invoke.Invoke(bridge, new object?[] { command, null }));
    }

    private static void AssertPostInvocationUncertaintyGate(MethodDefinition method)
    {
        Instruction[] instructions = method.Body.Instructions.ToArray();
        int invocation = FindCall(instructions, "System.Reflection.MethodBase", "Invoke");
        Assert.True(invocation >= 0, method.FullName + " must invoke the native command.");

        (int setTrueIndex, VariableDefinition? gate) = FindBooleanLocalAssignment(method, instructions, 0, invocation, true);
        Assert.True(setTrueIndex >= 0 && gate != null, method.FullName + " must arm an invocation-started gate before Invoke.");

        (int setFalseIndex, VariableDefinition? initialGate) = FindBooleanLocalAssignment(method, instructions, 0, setTrueIndex, false, gate);
        Assert.True(setFalseIndex >= 0 && initialGate == gate, method.FullName + " must initialize the gate as false.");

        int gateLoad = FindLocalLoad(method, instructions, invocation + 1, gate!);
        Assert.True(gateLoad > invocation, method.FullName + " must inspect the gate in its exception path.");
        Assert.Contains(
            instructions.Skip(gateLoad + 1).Take(4),
            instruction => instruction.OpCode.Code is Code.Brfalse or Code.Brfalse_S);

        foreach (string field in new[] { "statePolluted", "needsReset", "invocationStarted" })
        {
            int fieldIndex = Array.FindIndex(
                instructions,
                gateLoad + 1,
                instruction => instruction.OpCode.Code == Code.Ldstr && Equals(instruction.Operand, field));
            Assert.True(fieldIndex > gateLoad, method.FullName + " must set " + field + " only behind the post-invocation gate.");
        }
    }

    private static void AssertPostInvocationUnknownOutcomeGate(MethodDefinition method)
    {
        Instruction[] instructions = method.Body.Instructions.ToArray();
        int invocation = FindCall(instructions, "System.Reflection.MethodBase", "Invoke");
        Assert.True(invocation >= 0, method.FullName + " must invoke the native command.");

        (int setTrueIndex, VariableDefinition? gate) = FindBooleanLocalAssignment(method, instructions, 0, invocation, true);
        Assert.True(setTrueIndex >= 0 && gate != null, method.FullName + " must arm an invocation-started gate before Invoke.");

        int gateLoad = FindLocalLoad(method, instructions, invocation + 1, gate!);
        Assert.True(gateLoad > invocation, method.FullName + " must inspect the gate in its exception path.");
        foreach (string field in new[] { "outcomeUnknown", "needsReconciliation", "invocationStarted" })
        {
            int fieldIndex = Array.FindIndex(
                instructions,
                gateLoad + 1,
                instruction => instruction.OpCode.Code == Code.Ldstr && Equals(instruction.Operand, field));
            Assert.True(fieldIndex > gateLoad, method.FullName + " must set " + field + " only behind the post-invocation gate.");
        }

        Assert.DoesNotContain(
            instructions,
            instruction => instruction.OpCode.Code == Code.Ldstr &&
                           instruction.Operand is string field &&
                           field is "statePolluted" or "needsReset");
    }

    private static (int Index, VariableDefinition? Variable) FindBooleanLocalAssignment(
        MethodDefinition method,
        Instruction[] instructions,
        int start,
        int endExclusive,
        bool value,
        VariableDefinition? expected = null)
    {
        for (int index = endExclusive - 2; index >= start; index--)
        {
            if (!LoadsBoolean(instructions[index], value)) continue;
            VariableDefinition? variable = StoredLocal(method, instructions[index + 1]);
            if (variable != null && (expected == null || variable == expected)) return (index, variable);
        }

        return (-1, null);
    }

    private static int FindLocalLoad(
        MethodDefinition method,
        Instruction[] instructions,
        int start,
        VariableDefinition variable)
    {
        for (int index = start; index < instructions.Length; index++)
        {
            if (LoadedLocal(method, instructions[index]) == variable) return index;
        }

        return -1;
    }

    private static VariableDefinition? StoredLocal(MethodDefinition method, Instruction instruction) =>
        instruction.OpCode.Code switch
        {
            Code.Stloc_0 => method.Body.Variables[0],
            Code.Stloc_1 => method.Body.Variables[1],
            Code.Stloc_2 => method.Body.Variables[2],
            Code.Stloc_3 => method.Body.Variables[3],
            Code.Stloc or Code.Stloc_S => instruction.Operand as VariableDefinition,
            _ => null
        };

    private static VariableDefinition? LoadedLocal(MethodDefinition method, Instruction instruction) =>
        instruction.OpCode.Code switch
        {
            Code.Ldloc_0 => method.Body.Variables[0],
            Code.Ldloc_1 => method.Body.Variables[1],
            Code.Ldloc_2 => method.Body.Variables[2],
            Code.Ldloc_3 => method.Body.Variables[3],
            Code.Ldloc or Code.Ldloc_S => instruction.Operand as VariableDefinition,
            _ => null
        };

    private static bool LoadsBoolean(Instruction instruction, bool value) =>
        instruction.OpCode.Code == (value ? Code.Ldc_I4_1 : Code.Ldc_I4_0);

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
}
