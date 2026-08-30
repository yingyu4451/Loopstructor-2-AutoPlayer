using Mono.Cecil;
using Mono.Cecil.Cil;

namespace Loopstructor.AutoPlayer.Tests;

public sealed class OpeningDefenseGridProbeContractTests
{
    private const string ProbeType = "Loopstructor.AutoPlayer.Plugin.IncrementalAttributePlacementGridProbe";

    [Fact]
    public void ProbeUsesCandidatePoolsAndSingleGridValidatorInsteadOfFullMapQueryCommand()
    {
        using AssemblyDefinition assembly = ReadPlugin();
        TypeDefinition probe = RequireType(assembly);
        MethodDefinition resolve = RequireMethod(probe, "TryResolveContract");
        MethodDefinition initialize = RequireMethod(probe, "TryInitialize");
        MethodDefinition next = RequireMethod(probe, "ProbeNext");

        Assert.Contains("MapPosManager", LoadedStrings(resolve));
        Assert.Contains("CatapultRingPosition", LoadedStrings(resolve));
        Assert.Contains("EnergyCatapultRingPosition", LoadedStrings(resolve));
        Assert.Contains("minDisAwayStation", LoadedStrings(resolve));
        Assert.Contains("minEnergyDisAwayStation", LoadedStrings(resolve));
        Assert.Contains("TryValidateDisposableGridOption", AllMethods(probe).SelectMany(LoadedStrings));
        Assert.Contains(Calls(initialize), call =>
            call.DeclaringType.FullName == "Loopstructor.AutoPlayer.Core.DefenseStationGridRanker" &&
            call.Name == "RankPlacement");
        Assert.Contains(Calls(next), call =>
            call.DeclaringType.FullName == "System.Reflection.MethodBase" &&
            call.Name == "Invoke");

        Assert.DoesNotContain(
            AllMethods(probe).SelectMany(LoadedStrings),
            value => value == "QueryDisposableGridOptions" || value == "queryDisposableGridOptions");
        Assert.DoesNotContain(
            AllMethods(probe).SelectMany(Calls),
            call => call.DeclaringType.FullName == "Loopstructor.AutoPlayer.Plugin.RuntimeBridge" &&
                    call.Name == "Invoke");
    }

    [Fact]
    public void ProbeHasHardCandidateCountAndPerSliceTimeBoundaries()
    {
        using AssemblyDefinition assembly = ReadPlugin();
        TypeDefinition probe = RequireType(assembly);

        Assert.Equal(240, Constant<int>(probe, "MaximumValidationCount"));
        Assert.Equal(8, Constant<int>(probe, "MaximumValidationsPerSlice"));
        Assert.Equal(3d, Constant<double>(probe, "SliceBudgetMilliseconds"));

        MethodDefinition next = RequireMethod(probe, "ProbeNext");
        Assert.Contains(Calls(next), call =>
            call.DeclaringType.FullName == "System.Diagnostics.Stopwatch" &&
            call.Name == "StartNew");
        Assert.Contains(Calls(next), call =>
            call.DeclaringType.FullName == "System.Diagnostics.Stopwatch" &&
            call.Name == "get_Elapsed");
    }

    [Fact]
    public void ProbeContractIsReadOnlyAndDoesNotSetGameMembers()
    {
        using AssemblyDefinition assembly = ReadPlugin();
        TypeDefinition probe = RequireType(assembly);
        MethodReference[] calls = AllMethods(probe).SelectMany(Calls).ToArray();

        Assert.DoesNotContain(calls, call => call.Name == "SetValue");
        Assert.DoesNotContain(calls, call =>
            call.DeclaringType.FullName == "UnityEngine.Object" &&
            call.Name is "Destroy" or "DestroyImmediate");
        Assert.DoesNotContain(AllMethods(probe).SelectMany(LoadedStrings), value => value is
            "statePolluted" or "needsReset" or "Assembly-CSharp.dll");
    }

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

    private static TypeDefinition RequireType(AssemblyDefinition assembly) =>
        assembly.MainModule.Types.Single(type => type.FullName == ProbeType);

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
