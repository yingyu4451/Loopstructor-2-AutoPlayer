using Mono.Cecil;
using Mono.Cecil.Cil;

namespace Loopstructor.AutoPlayer.Tests;

public sealed class MergeUiRuntimeFallbackContractTests
{
    private const string FallbackType = "Loopstructor.AutoPlayer.Plugin.MergeUiRuntimeFallback";

    [Fact]
    public void ExposesBridgeCompatibleCloseAndSettlementEntryPoints()
    {
        using AssemblyDefinition assembly = ReadPlugin();
        TypeDefinition fallback = RequireType(assembly);

        Assert.True(fallback.IsAbstract && fallback.IsSealed);
        AssertEntryPoint(RequireMethod(fallback, "TryClosePanel"));
        AssertEntryPoint(RequireMethod(fallback, "TryConfirmSettlement"));
    }

    [Fact]
    public void DiscoversPrivateSettlementPanelFieldThroughTheBaseType()
    {
        using AssemblyDefinition assembly = ReadPlugin();
        TypeDefinition fallback = RequireType(assembly);
        MethodDefinition contractFactory = RequireMethod(fallback, "TryGetContract");
        MethodDefinition fieldFinder = RequireMethod(fallback, "FindInstanceField");
        MethodReference[] factoryCalls = Calls(contractFactory).ToArray();
        MethodReference[] finderCalls = Calls(fieldFinder).ToArray();

        Assert.Contains("MetroTD.UISystem.RebuildUI_MergeRebuildPanel", LoadedStrings(contractFactory));
        Assert.Contains("m_settlementPanel", LoadedStrings(contractFactory));
        Assert.Contains(factoryCalls, IsCall(FallbackType, "FindInstanceField"));
        Assert.Contains(finderCalls, IsCall("System.Type", "get_BaseType"));
        Assert.Contains(finderCalls, IsCall("System.Type", "GetField"));

        int[] loadedFlags = fieldFinder.Body.Instructions
            .Select(TryReadInt32)
            .Where(value => value.HasValue)
            .Select(value => value.GetValueOrDefault())
            .ToArray();
        Assert.Contains(
            loadedFlags,
            flags => (flags & (int)System.Reflection.BindingFlags.Instance) != 0
                     && (flags & (int)System.Reflection.BindingFlags.NonPublic) != 0
                     && (flags & (int)System.Reflection.BindingFlags.DeclaredOnly) != 0);
    }

    [Fact]
    public void SettlementVisibilityUsesActiveSelfRatherThanHierarchyVisibility()
    {
        using AssemblyDefinition assembly = ReadPlugin();
        TypeDefinition fallback = RequireType(assembly);
        MethodReference[] calls = Calls(RequireMethod(fallback, "IsSettlementVisible")).ToArray();

        Assert.Contains(calls, IsCall("UnityEngine.GameObject", "get_activeSelf"));
        Assert.DoesNotContain(calls, IsCall("UnityEngine.GameObject", "get_activeInHierarchy"));
    }

    [Fact]
    public void CloseAndConfirmInvokeOnlyTheirNativePanelCommands()
    {
        using AssemblyDefinition assembly = ReadPlugin();
        TypeDefinition fallback = RequireType(assembly);
        MethodDefinition close = RequireMethod(fallback, "TryClosePanel");
        MethodDefinition confirm = RequireMethod(fallback, "TryConfirmSettlement");
        MethodDefinition contractFactory = RequireMethod(fallback, "TryGetContract");

        Assert.Contains("CloseSelf", LoadedStrings(contractFactory));
        Assert.Contains("FinishCurrent", LoadedStrings(contractFactory));

        MethodReference[] closeCalls = Calls(close).ToArray();
        Assert.Contains(closeCalls, call => call.Name == "get_CloseSelf");
        Assert.DoesNotContain(closeCalls, call => call.Name == "get_FinishCurrent");
        Assert.Contains(closeCalls, IsCall("System.Reflection.MethodBase", "Invoke"));

        MethodReference[] confirmCalls = Calls(confirm).ToArray();
        Assert.Contains(confirmCalls, call => call.Name == "get_FinishCurrent");
        Assert.DoesNotContain(confirmCalls, call => call.Name == "get_CloseSelf");
        Assert.Contains(confirmCalls, IsCall("System.Reflection.MethodBase", "Invoke"));

        Assert.DoesNotContain(
            AllMethods(fallback).SelectMany(Calls),
            call => string.Equals(call.Name, "SetValue", StringComparison.Ordinal));
    }

    private static void AssertEntryPoint(MethodDefinition method)
    {
        Assert.True(method.IsAssembly && method.IsStatic);
        Assert.Equal("System.Boolean", method.ReturnType.FullName);
        Assert.Collection(
            method.Parameters,
            result => Assert.Equal("Newtonsoft.Json.Linq.JObject&", result.ParameterType.FullName));
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

    private static int? TryReadInt32(Instruction instruction) => instruction.OpCode.Code switch
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
        Code.Ldc_I4_S => Convert.ToInt32(instruction.Operand),
        Code.Ldc_I4 => (int)instruction.Operand,
        _ => null
    };
}
