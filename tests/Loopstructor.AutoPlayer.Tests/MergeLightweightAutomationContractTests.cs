using Mono.Cecil;
using Mono.Cecil.Cil;

namespace Loopstructor.AutoPlayer.Tests;

public sealed class MergeLightweightAutomationContractTests
{
    private const string FallbackType = "Loopstructor.AutoPlayer.Plugin.MergeUiRuntimeFallback";

    [Fact]
    public void ExposesLightweightQueryAndStrictSelectionEntryPoints()
    {
        using AssemblyDefinition assembly = ReadPlugin();
        TypeDefinition fallback = RequireType(assembly);
        MethodDefinition query = RequireMethod(fallback, "TryQueryAutomationState");
        MethodDefinition select = RequireMethod(fallback, "TrySelectMergeVehicle");

        Assert.True(query.IsAssembly && query.IsStatic);
        Assert.Equal("System.Boolean", query.ReturnType.FullName);
        Assert.Collection(
            query.Parameters,
            result => Assert.Equal("Newtonsoft.Json.Linq.JObject&", result.ParameterType.FullName));

        Assert.True(select.IsAssembly && select.IsStatic);
        Assert.Equal("System.Boolean", select.ReturnType.FullName);
        Assert.Collection(
            select.Parameters,
            arguments => Assert.Equal("Newtonsoft.Json.Linq.JObject", arguments.ParameterType.FullName),
            result => Assert.Equal("Newtonsoft.Json.Linq.JObject&", result.ParameterType.FullName));
    }

    [Fact]
    public void LightweightPathsNeverPerformGlobalUnityScanOrCallGameMcpPanelBuilder()
    {
        using AssemblyDefinition assembly = ReadPlugin();
        TypeDefinition fallback = RequireType(assembly);
        MethodDefinition[] allMethods = AllMethods(fallback).ToArray();

        foreach (string entryPoint in new[] { "TryQueryAutomationState", "TrySelectMergeVehicle" })
        {
            MethodDefinition[] reachable = Reachable(RequireMethod(fallback, entryPoint), allMethods).ToArray();
            MethodReference[] calls = reachable.SelectMany(Calls).ToArray();
            string[] strings = reachable.SelectMany(LoadedStrings).ToArray();

            Assert.DoesNotContain(
                calls,
                call => call.DeclaringType.FullName == "UnityEngine.Resources"
                        && call.Name.StartsWith("FindObjectsOfTypeAll", StringComparison.Ordinal));
            Assert.DoesNotContain(calls, call => call.Name == "BuildPanelState");
            Assert.DoesNotContain(strings, value => value.Contains("BuildPanelState", StringComparison.Ordinal));
            Assert.DoesNotContain(strings, value => value.Contains("GuiGameMcp", StringComparison.Ordinal));
        }
    }

    [Fact]
    public void QueryPathDoesNotReachVehicleClickMutation()
    {
        using AssemblyDefinition assembly = ReadPlugin();
        TypeDefinition fallback = RequireType(assembly);
        MethodDefinition[] allMethods = AllMethods(fallback).ToArray();
        MethodDefinition[] reachable = Reachable(
            RequireMethod(fallback, "TryQueryAutomationState"),
            allMethods).ToArray();
        MethodReference[] calls = reachable.SelectMany(Calls).ToArray();

        Assert.DoesNotContain(calls, call => call.Name == "get_VehicleClick");
        Assert.DoesNotContain(reachable, method => method.Name == "TrySelectMergeVehicle");
    }

    [Fact]
    public void SelectionRequiresFullIdentityAndOnlyPollutesAfterNativeInvocationStarts()
    {
        using AssemblyDefinition assembly = ReadPlugin();
        TypeDefinition fallback = RequireType(assembly);
        MethodDefinition[] allMethods = AllMethods(fallback).ToArray();
        MethodDefinition select = RequireMethod(fallback, "TrySelectMergeVehicle");
        MethodDefinition[] reachable = Reachable(select, allMethods).ToArray();
        string[] strings = reachable.SelectMany(LoadedStrings).ToArray();
        MethodReference[] directCalls = Calls(select).ToArray();

        foreach (string identityField in new[]
                 {
                     "panelInstanceId",
                     "rosterFingerprint",
                     "itemInstanceId",
                     "vehicleInstanceId",
                     "materialVehicleType",
                     "resultVehicleType",
                     "requiredVehicleCount",
                     "candidateVehicleIndexes",
                     "candidateItemInstanceIds",
                     "candidateVehicleInstanceIds"
                 })
        {
            Assert.Contains(identityField, strings);
        }

        Assert.Contains(directCalls, call => call.Name == "get_VehicleClick");
        Assert.Contains(directCalls, IsCall("System.Reflection.MethodBase", "Invoke"));
        Assert.Contains("statePolluted", LoadedStrings(select));
        Assert.Contains("needsReset", LoadedStrings(select));
        Assert.Contains("invocationStarted", LoadedStrings(select));
        Assert.Contains("outcomeUnknown", LoadedStrings(select));
        Assert.Contains("needsReconciliation", LoadedStrings(select));

        int nativeInvoke = select.Body.Instructions.FindIndex(instruction =>
            instruction.OpCode.Code is Code.Call or Code.Callvirt
            && instruction.Operand is MethodReference call
            && call.DeclaringType.FullName == "System.Reflection.MethodBase"
            && call.Name == "Invoke");
        int firstPollutionFlag = select.Body.Instructions.FindIndex(instruction =>
            instruction.OpCode.Code == Code.Ldstr
            && string.Equals(instruction.Operand as string, "statePolluted", StringComparison.Ordinal));
        Assert.True(nativeInvoke >= 0);
        Assert.True(firstPollutionFlag > nativeInvoke);

        int postconditionUnknown = select.Body.Instructions.FindIndex(instruction =>
            instruction.OpCode.Code == Code.Ldstr
            && string.Equals(instruction.Operand as string, "outcomeUnknown", StringComparison.Ordinal));
        int postconditionReconciliation = select.Body.Instructions.FindIndex(instruction =>
            instruction.OpCode.Code == Code.Ldstr
            && string.Equals(instruction.Operand as string, "needsReconciliation", StringComparison.Ordinal));
        Assert.True(postconditionUnknown > nativeInvoke);
        Assert.True(postconditionReconciliation > postconditionUnknown);
    }

    [Fact]
    public void MissingAutomationContractIsHandledWithoutNativeFallback()
    {
        using AssemblyDefinition assembly = ReadPlugin();
        TypeDefinition fallback = RequireType(assembly);
        MethodDefinition query = RequireMethod(fallback, "TryQueryAutomationState");
        MethodDefinition select = RequireMethod(fallback, "TrySelectMergeVehicle");
        MethodDefinition unavailable = RequireMethod(fallback, "AutomationContractUnavailable");

        Assert.Contains(Calls(query), IsCall(FallbackType, "AutomationContractUnavailable"));
        Assert.Contains(Calls(select), IsCall(FallbackType, "AutomationContractUnavailable"));
        Assert.Contains("nativeFallbackBlocked", LoadedStrings(unavailable));
        Assert.Contains("contractAvailable", LoadedStrings(unavailable));
        Assert.Contains("invocationStarted", LoadedStrings(unavailable));
    }

    private static AssemblyDefinition ReadPlugin()
    {
        string path = Path.Combine(AppContext.BaseDirectory, "Loopstructor.AutoPlayer.Plugin.dll");
        Assert.True(File.Exists(path), "Plugin assembly was not copied to the test output: " + path);
        return AssemblyDefinition.ReadAssembly(path);
    }

    private static TypeDefinition RequireType(AssemblyDefinition assembly) =>
        assembly.MainModule.Types.Single(type => type.FullName == FallbackType);

    private static MethodDefinition RequireMethod(TypeDefinition type, string name) =>
        type.Methods.Single(method => method.Name == name);

    private static IEnumerable<MethodDefinition> Reachable(
        MethodDefinition root,
        IReadOnlyCollection<MethodDefinition> allMethods)
    {
        Dictionary<string, MethodDefinition> methodsByName = allMethods
            .GroupBy(method => method.FullName, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.First(), StringComparer.Ordinal);
        HashSet<string> visited = new(StringComparer.Ordinal);
        Stack<MethodDefinition> pending = new();
        pending.Push(root);
        while (pending.Count > 0)
        {
            MethodDefinition current = pending.Pop();
            if (!visited.Add(current.FullName))
            {
                continue;
            }

            yield return current;
            foreach (MethodReference call in Calls(current))
            {
                if (methodsByName.TryGetValue(call.FullName, out MethodDefinition? target))
                {
                    pending.Push(target);
                }
            }
        }
    }

    private static IEnumerable<MethodDefinition> AllMethods(TypeDefinition type) =>
        type.Methods.Concat(type.NestedTypes.SelectMany(AllMethods));

    private static IEnumerable<MethodReference> Calls(MethodDefinition method) =>
        method.HasBody
            ? method.Body.Instructions
                .Where(instruction => instruction.OpCode.Code
                    is Code.Call or Code.Callvirt or Code.Newobj or Code.Ldftn or Code.Ldvirtftn)
                .Select(instruction => instruction.Operand)
                .OfType<MethodReference>()
            : Enumerable.Empty<MethodReference>();

    private static IEnumerable<string> LoadedStrings(MethodDefinition method) =>
        method.HasBody
            ? method.Body.Instructions
                .Where(instruction => instruction.OpCode.Code == Code.Ldstr)
                .Select(instruction => instruction.Operand)
                .OfType<string>()
            : Enumerable.Empty<string>();

    private static Predicate<MethodReference> IsCall(string declaringType, string methodName) =>
        call => call.DeclaringType.FullName == declaringType && call.Name == methodName;
}

internal static class CecilInstructionListExtensions
{
    internal static int FindIndex(this Mono.Collections.Generic.Collection<Instruction> instructions, Predicate<Instruction> match)
    {
        for (int index = 0; index < instructions.Count; index++)
        {
            if (match(instructions[index]))
            {
                return index;
            }
        }

        return -1;
    }
}
