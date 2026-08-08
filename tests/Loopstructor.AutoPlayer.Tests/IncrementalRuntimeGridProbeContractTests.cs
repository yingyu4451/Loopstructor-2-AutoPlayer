using Mono.Cecil;
using Mono.Cecil.Cil;

namespace Loopstructor.AutoPlayer.Tests;

public sealed class IncrementalRuntimeGridProbeContractTests
{
    private const string CandidateReaderType =
        "Loopstructor.AutoPlayer.Plugin.RuntimeGridCandidatePoolReader";
    private const string ExpansionProbeType =
        "Loopstructor.AutoPlayer.Plugin.IncrementalDefenseExpansionAttributeGridProbe";
    private const string BattleProbeType =
        "Loopstructor.AutoPlayer.Plugin.IncrementalBattleLiveDisposableGridProbe";

    [Fact]
    public void SharedCandidateReaderUsesCachedMapPoolsWithoutGlobalUnitySearch()
    {
        using AssemblyDefinition assembly = ReadPlugin();
        TypeDefinition reader = RequireType(assembly, CandidateReaderType);
        string[] strings = AllMethods(reader).SelectMany(LoadedStrings).ToArray();
        MethodReference[] calls = AllMethods(reader).SelectMany(Calls).ToArray();

        Assert.Contains("MapPosManager", strings);
        Assert.Contains("CatapultRingPosition", strings);
        Assert.Contains("EnergyCatapultRingPosition", strings);
        Assert.True(reader.Fields.Count(field => field.FieldType.FullName == "System.Reflection.PropertyInfo") >= 3);
        Assert.DoesNotContain(strings, value => value is "QueryDisposableGridOptions" or "queryDisposableGridOptions");
        Assert.DoesNotContain(calls, IsGlobalUnityObjectSearch);
        Assert.DoesNotContain(calls, call =>
            call.DeclaringType.FullName == "Loopstructor.AutoPlayer.Plugin.RuntimeBridge" &&
            call.Name == "Invoke");
    }

    [Fact]
    public void ExpansionProbeUsesPolicyRankingAndSingleGridTemplateValidation()
    {
        using AssemblyDefinition assembly = ReadPlugin();
        TypeDefinition probe = RequireType(assembly, ExpansionProbeType);
        MethodDefinition initialize = RequireMethod(probe, "TryInitialize");
        MethodDefinition next = RequireMethod(probe, "ProbeNext");
        MethodReference[] allCalls = AllMethods(probe).SelectMany(Calls).ToArray();
        string[] allStrings = AllMethods(probe).SelectMany(LoadedStrings).ToArray();

        Assert.Contains(Calls(initialize), call =>
            call.DeclaringType.FullName == "Loopstructor.AutoPlayer.Core.DefenseExpansionAttributeGridRanker" &&
            call.Name == "Rank");
        Assert.Contains("TryValidateDisposableGridOption", allStrings);
        Assert.Contains(Calls(next), IsReflectionInvoke);
        AssertBoundedSlice(probe, next);
        AssertCatchesExceptions(initialize);
        AssertFailOpen(next);
        AssertReadOnlyProbe(allCalls, allStrings);
    }

    [Fact]
    public void BattleProbeRanksAroundThreatAndUsesRestoringLiveSingleGridEvaluation()
    {
        using AssemblyDefinition assembly = ReadPlugin();
        TypeDefinition probe = RequireType(assembly, BattleProbeType);
        MethodDefinition initialize = RequireMethod(probe, "TryInitialize");
        MethodDefinition next = RequireMethod(probe, "ProbeNext");
        MethodReference[] allCalls = AllMethods(probe).SelectMany(Calls).ToArray();
        string[] allStrings = AllMethods(probe).SelectMany(LoadedStrings).ToArray();

        Assert.Contains(Calls(initialize), call =>
            call.DeclaringType.FullName == "Loopstructor.AutoPlayer.Core.BattleDisposableGridRanker" &&
            call.Name == "Rank");
        Assert.Contains(Calls(initialize), call =>
            call.DeclaringType.FullName == "UnityEngine.GridLayout" &&
            call.Name == "WorldToCell");
        Assert.Contains("ResolveLiveGridInteraction", allStrings);
        Assert.Contains("EvaluateLiveGrid", allStrings);
        Assert.DoesNotContain("TryPrepareLiveGrid", allStrings);
        Assert.Contains(probe.Fields, field =>
            field.Name == "_evaluateLiveGrid" &&
            field.FieldType.FullName == "System.Reflection.MethodInfo");
        Assert.DoesNotContain(probe.Fields, field => field.Name == "_tryPrepareLiveGrid");
        Assert.Contains(Calls(next), IsReflectionInvoke);
        AssertBoundedSlice(probe, next);
        AssertCatchesExceptions(initialize);
        AssertFailOpen(next);
        AssertReadOnlyProbe(allCalls, allStrings);
        Assert.DoesNotContain(allStrings, value => value is
            "confirmDisposableGrid" or "useDisposable" or "cancelDisposable");
    }

    [Fact]
    public void ProbeReflectionMetadataIsCachedAndNeverResolvedInsideSlices()
    {
        using AssemblyDefinition assembly = ReadPlugin();
        TypeDefinition expansion = RequireType(assembly, ExpansionProbeType);
        TypeDefinition battle = RequireType(assembly, BattleProbeType);

        Assert.Contains(expansion.Fields, field => field.FieldType.FullName == "System.Reflection.MethodInfo");
        Assert.True(battle.Fields.Count(field => field.FieldType.FullName == "System.Reflection.MethodInfo") >= 2);
        Assert.True(battle.Fields.Count(field => field.FieldType.FullName == "System.Reflection.PropertyInfo") >= 2);
        Assert.DoesNotContain(Calls(RequireMethod(expansion, "ProbeNext")), call => call.Name == "FindType");
        Assert.DoesNotContain(Calls(RequireMethod(battle, "ProbeNext")), call => call.Name == "FindType");
    }

    private static void AssertBoundedSlice(TypeDefinition probe, MethodDefinition next)
    {
        Assert.Equal(240, Constant<int>(probe, "MaximumValidationCount"));
        Assert.Equal(8, Constant<int>(probe, "MaximumValidationsPerSlice"));
        Assert.Equal(3d, Constant<double>(probe, "SliceBudgetMilliseconds"));
        Assert.Contains(Calls(next), call =>
            call.DeclaringType.FullName == "System.Diagnostics.Stopwatch" &&
            call.Name == "StartNew");
        Assert.Contains(Calls(next), call =>
            call.DeclaringType.FullName == "System.Diagnostics.Stopwatch" &&
            call.Name == "get_Elapsed");
    }

    private static void AssertFailOpen(MethodDefinition next)
    {
        AssertCatchesExceptions(next);
        Assert.Contains(Calls(next), call =>
            call.DeclaringType.FullName == "Loopstructor.AutoPlayer.Core.IncrementalGridProbeResult" &&
            call.Name == ".ctor");
    }

    private static void AssertCatchesExceptions(MethodDefinition method) =>
        Assert.Contains(method.Body.ExceptionHandlers, handler =>
            handler.HandlerType == ExceptionHandlerType.Catch &&
            handler.CatchType?.FullName == "System.Exception");

    private static void AssertReadOnlyProbe(
        IEnumerable<MethodReference> calls,
        IEnumerable<string> strings)
    {
        Assert.DoesNotContain(strings, value => value is "QueryDisposableGridOptions" or "queryDisposableGridOptions");
        Assert.DoesNotContain(calls, IsGlobalUnityObjectSearch);
        Assert.DoesNotContain(calls, call =>
            call.DeclaringType.FullName == "Loopstructor.AutoPlayer.Plugin.RuntimeBridge" &&
            call.Name == "Invoke");
        Assert.DoesNotContain(calls, call =>
            call.DeclaringType.FullName == "UnityEngine.Object" &&
            call.Name is "Destroy" or "DestroyImmediate");
    }

    private static bool IsGlobalUnityObjectSearch(MethodReference call) =>
        call.DeclaringType.FullName == "UnityEngine.Resources" &&
        call.Name.Contains("FindObjectsOfTypeAll", StringComparison.Ordinal);

    private static bool IsReflectionInvoke(MethodReference call) =>
        call.DeclaringType.FullName == "System.Reflection.MethodBase" && call.Name == "Invoke";

    private static T Constant<T>(TypeDefinition type, string name)
    {
        FieldDefinition field = type.Fields.Single(field => field.Name == name);
        Assert.True(field.HasConstant);
        return (T)field.Constant;
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

    private static IEnumerable<MethodDefinition> AllMethods(TypeDefinition type) =>
        type.Methods.Concat(type.NestedTypes.SelectMany(AllMethods));

    private static IEnumerable<MethodReference> Calls(MethodDefinition method) =>
        method.HasBody
            ? method.Body.Instructions
                .Where(instruction => instruction.OpCode.Code is Code.Call or Code.Callvirt or Code.Newobj)
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
}
