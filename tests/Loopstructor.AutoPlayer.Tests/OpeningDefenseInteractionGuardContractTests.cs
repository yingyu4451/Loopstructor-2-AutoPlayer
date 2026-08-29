using Mono.Cecil;
using Mono.Cecil.Cil;

namespace Loopstructor.AutoPlayer.Tests;

public sealed class OpeningDefenseInteractionGuardContractTests
{
    private const string GuardType =
        "Loopstructor.AutoPlayer.Plugin.OpeningDefenseInteractionGuard";

    [Fact]
    public void GuardUsesCachedExecutorAndLastInteractionContractWithoutSceneScan()
    {
        using AssemblyDefinition assembly = ReadPlugin();
        TypeDefinition guard = RequireType(assembly);
        MethodDefinition query = RequireMethod(guard, "Query");
        MethodDefinition bind = RequireMethod(guard, "TryGetContract");
        string[] strings = AllMethods(guard).SelectMany(LoadedStrings).ToArray();
        MethodReference[] calls = AllMethods(guard).SelectMany(Calls).ToArray();

        Assert.Contains("MetroTD.DisposableSystem.DisposableInteractionExecutor", strings);
        Assert.Contains("MetroTD.DisposableSystem.DisposableInteraction", strings);
        Assert.Contains("MetroTD.DisposableSystem.GridChooseInteraction", strings);
        Assert.Contains("Instance", strings);
        Assert.Contains("isInPreview", strings);
        Assert.Contains("GetLastDisposableInteraction", strings);
        Assert.Contains("noActiveInteraction", strings);
        Assert.Contains("observationConsistent", strings);
        Assert.Contains(Calls(query), call =>
            call.DeclaringType.FullName == "System.Reflection.PropertyInfo" &&
            call.Name == "GetValue");
        Assert.Contains(Calls(query), call =>
            call.DeclaringType.FullName == "System.Reflection.MethodBase" &&
            call.Name == "Invoke");
        Assert.Contains(Calls(bind), call =>
            call.DeclaringType.FullName == "System.Type" &&
            call.Name == "GetProperty");
        Assert.Contains(guard.Fields, field =>
            field.Name == "_contract" &&
            field.FieldType.FullName.EndsWith("/ReflectionContract", StringComparison.Ordinal));

        Assert.DoesNotContain(calls, IsGlobalUnityObjectSearch);
        Assert.DoesNotContain(calls, call =>
            call.DeclaringType.FullName == "Loopstructor.AutoPlayer.Plugin.RuntimeBridge" &&
            call.Name == "Invoke");
    }

    [Fact]
    public void GuardOnlyReadsReflectionMembersAndNeverMutatesGameState()
    {
        using AssemblyDefinition assembly = ReadPlugin();
        TypeDefinition guard = RequireType(assembly);
        MethodReference[] calls = AllMethods(guard).SelectMany(Calls).ToArray();
        string[] strings = AllMethods(guard).SelectMany(LoadedStrings).ToArray();

        Assert.DoesNotContain(calls, call => call.Name == "SetValue");
        Assert.DoesNotContain(calls, call =>
            call.DeclaringType.FullName == "UnityEngine.Object" &&
            call.Name is "Destroy" or "DestroyImmediate");
        Assert.DoesNotContain(strings, value => value is
            "m_disposableInteractions" or
            "Clear" or
            "FinishCurrent" or
            "CancelInteraction" or
            "confirmDisposableGrid" or
            "useDisposable" or
            "cancelDisposable" or
            "statePolluted" or
            "needsReset");
    }

    [Fact]
    public void ControllerUsesLocalGuardBothAsPlannerReadAndImmediatelyBeforeConfirmation()
    {
        using AssemblyDefinition assembly = ReadPlugin();
        TypeDefinition controller = assembly.MainModule.Types.Single(type =>
            type.FullName == "Loopstructor.AutoPlayer.Plugin.AutoPlayController");
        MethodDefinition read = controller.Methods.Single(method =>
            method.Name == "ExecuteOpeningDefenseReadOnly");
        MethodDefinition confirmGuard = controller.Methods.Single(method =>
            method.Name == "CanConfirmOpeningDefensePlacementNow");
        MethodDefinition prepare = controller.Methods.Single(method =>
            method.Name == "PrepareOpeningDefenseIncrementally");

        Assert.Contains(controller.Fields, field =>
            field.Name == "_openingDefenseInteractionGuard" &&
            field.FieldType.FullName == GuardType);
        Assert.Contains(LoadedStrings(read), value =>
            value == "queryOpeningDefenseInteractionGuard");
        Assert.Contains(Calls(read), call =>
            call.DeclaringType.FullName == GuardType && call.Name == "Query");
        Assert.Contains(Calls(confirmGuard), call =>
            call.DeclaringType.FullName == GuardType && call.Name == "Query");
        Assert.Contains(Calls(prepare), call =>
            call.DeclaringType.FullName ==
                "Loopstructor.AutoPlayer.Plugin.AutoPlayController" &&
            call.Name == "CanConfirmOpeningDefensePlacementNow");
        Assert.Contains(controller.Fields, field =>
            field.Name == "_openingDefenseWaitingForForeignPreview" &&
            field.FieldType.FullName == "System.Boolean");
    }

    private static bool IsGlobalUnityObjectSearch(MethodReference call) =>
        call.DeclaringType.FullName == "UnityEngine.Resources" &&
        call.Name.Contains("FindObjectsOfTypeAll", StringComparison.Ordinal);

    private static AssemblyDefinition ReadPlugin()
    {
        string path = Path.Combine(AppContext.BaseDirectory, "Loopstructor.AutoPlayer.Plugin.dll");
        Assert.True(File.Exists(path), "Plugin assembly was not copied to the test output: " + path);
        return AssemblyDefinition.ReadAssembly(path);
    }

    private static TypeDefinition RequireType(AssemblyDefinition assembly) =>
        assembly.MainModule.Types.Single(type => type.FullName == GuardType);

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
