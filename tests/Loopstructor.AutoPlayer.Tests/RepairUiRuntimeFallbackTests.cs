using Mono.Cecil;
using Mono.Cecil.Cil;

namespace Loopstructor.AutoPlayer.Tests;

public sealed class RepairUiRuntimeFallbackTests
{
    private const string FallbackType = "Loopstructor.AutoPlayer.Plugin.RepairUiRuntimeFallback";

    [Fact]
    public void ExposesRuntimeBridgeCompatibleQueryAndChooseEntryPoints()
    {
        using AssemblyDefinition assembly = ReadPlugin();
        TypeDefinition fallback = RequireType(assembly);

        Assert.True(fallback.IsAbstract && fallback.IsSealed);
        MethodDefinition query = RequireMethod(fallback, "TryQueryOptions");
        MethodDefinition choose = RequireMethod(fallback, "TryChooseOption");

        Assert.True(query.IsAssembly && query.IsStatic);
        Assert.Equal("System.Boolean", query.ReturnType.FullName);
        Assert.Collection(
            query.Parameters,
            result => Assert.Equal("Newtonsoft.Json.Linq.JObject&", result.ParameterType.FullName));

        Assert.True(choose.IsAssembly && choose.IsStatic);
        Assert.Equal("System.Boolean", choose.ReturnType.FullName);
        Assert.Collection(
            choose.Parameters,
            arguments => Assert.Equal("Newtonsoft.Json.Linq.JObject", arguments.ParameterType.FullName),
            result => Assert.Equal("Newtonsoft.Json.Linq.JObject&", result.ParameterType.FullName));
    }

    [Fact]
    public void UsesRegisteredRepairPanelAndOrdersConfiguredChoicesBeforeChildFallbacks()
    {
        using AssemblyDefinition assembly = ReadPlugin();
        TypeDefinition fallback = RequireType(assembly);
        string[] strings = AllMethods(fallback).SelectMany(LoadedStrings).ToArray();
        MethodReference[] calls = AllMethods(fallback).SelectMany(Calls).ToArray();

        Assert.Contains("MetroTD.UISystem.RepairUI", strings);
        Assert.Contains("MetroTD.UISystem.UIItem_RepairChoose", strings);
        Assert.Contains("MetroTD.UISystem.WaveFunctionUI_Item_Behaviour", strings);
        Assert.Contains("constChoices", strings);
        Assert.Contains("itemBehaviour", strings);
        Assert.Contains("IsOpen", strings);
        Assert.Contains(calls, IsCall("System.Reflection.PropertyInfo", "GetValue"));
        Assert.DoesNotContain(calls, IsCall("UnityEngine.Resources", "FindObjectsOfTypeAll"));
        Assert.Contains(calls, IsCall("UnityEngine.Component", "GetComponentsInChildren"));
        Assert.Contains(calls, IsCall("UnityEngine.Transform", "IsChildOf"));
        Assert.Contains(calls, call => call.Name == "get_activeInHierarchy");
        Assert.Contains(calls, call => call.Name == "IsValid"
                                       && call.DeclaringType.FullName == "UnityEngine.SceneManagement.Scene");

        MethodDefinition ordered = RequireMethod(fallback, "GetOrderedItems");
        Instruction[] instructions = ordered.Body.Instructions.ToArray();
        int configuredChoices = Array.FindIndex(
            instructions,
            instruction => instruction.Operand is MethodReference call
                           && call.DeclaringType.FullName == "System.Reflection.FieldInfo"
                           && call.Name == "GetValue");
        int childFallbacks = Array.FindIndex(
            instructions,
            instruction => instruction.Operand is MethodReference call
                           && call.DeclaringType.FullName == "UnityEngine.Component"
                           && call.Name == "GetComponentsInChildren");
        Assert.True(configuredChoices >= 0 && configuredChoices < childFallbacks);
    }

    [Fact]
    public void ExpectedPanelQueryCanReturnACompleteClosedSnapshot()
    {
        using AssemblyDefinition assembly = ReadPlugin();
        TypeDefinition fallback = RequireType(assembly);
        MethodDefinition query = RequireMethod(fallback, "TryQueryPanelState");
        MethodDefinition closed = RequireMethod(fallback, "BuildClosedPanelState");

        Assert.Contains(Calls(query), IsCall(FallbackType, "TryQueryOptions"));
        Assert.Contains(Calls(query), IsCall(FallbackType, "TryFindRegisteredRepairPanel"));
        foreach (string field in new[] { "repairPanel", "panelOpen", "snapshotComplete", "options" })
        {
            Assert.Contains(
                field,
                LoadedStrings(query).Concat(LoadedStrings(closed)));
        }
    }

    [Fact]
    public void OptionStateContainsDecisionFieldsAndSelectionInvokesOnClick()
    {
        using AssemblyDefinition assembly = ReadPlugin();
        TypeDefinition fallback = RequireType(assembly);
        MethodDefinition buildState = RequireMethod(fallback, "BuildOptionState");
        MethodDefinition choose = RequireMethod(fallback, "TryChooseOption");
        string[] stateFields = LoadedStrings(buildState).ToArray();
        string[] allStrings = AllMethods(fallback).SelectMany(LoadedStrings).ToArray();

        foreach (string field in new[]
                 {
                     "panel", "index", "panelInstanceId", "instanceId", "buttonActive", "conditionPass", "displayText",
                     "currentItemType", "optionName", "behaviourTypes", "behaviourTypeIds", "behaviourNames",
                     "extraDataType", "source"
                 })
        {
            Assert.Contains(field, stateFields);
        }

        Assert.Contains("btn", allStrings);
        Assert.Contains("BtnActive", allStrings);
        Assert.Contains("contentText", allStrings);
        Assert.Contains("text", allStrings);
        Assert.Contains("currentItem", allStrings);
        Assert.Contains("IsConditionPass", allStrings);
        Assert.Contains("behaviourList", allStrings);
        Assert.Contains("OnClick", allStrings);
        Assert.Contains("pluginReflection:RepairUI", allStrings);
        Assert.Contains(
            Calls(choose),
            call => call.DeclaringType.FullName == "System.Reflection.MethodBase" && call.Name == "Invoke");

        MethodDefinition buildPanel = RequireMethod(fallback, "BuildPanelState");
        foreach (string field in new[] { "panelOpen", "panelInstanceId", "snapshotComplete", "options" })
        {
            Assert.Contains(field, LoadedStrings(buildPanel));
        }
    }

    [Fact]
    public void SelectionValidatesStableIdentityAndAvailabilityBeforeOnClick()
    {
        using AssemblyDefinition assembly = ReadPlugin();
        TypeDefinition fallback = RequireType(assembly);
        MethodDefinition choose = RequireMethod(fallback, "TryChooseOption");
        Instruction[] instructions = choose.Body.Instructions.ToArray();

        int identityReader = Array.FindIndex(
            instructions,
            instruction => instruction.Operand is MethodReference call &&
                           call.DeclaringType.FullName == FallbackType &&
                           call.Name == "ReadIdentity");
        int buildSnapshot = Array.FindIndex(
            instructions,
            instruction => instruction.Operand is MethodReference call &&
                           call.DeclaringType.FullName == FallbackType &&
                           call.Name == "BuildOptionState");
        int invoke = Array.FindIndex(
            instructions,
            instruction => instruction.Operand is MethodReference call &&
                           call.DeclaringType.FullName == "System.Reflection.MethodBase" &&
                           call.Name == "Invoke");

        Assert.True(identityReader >= 0 && identityReader < buildSnapshot && buildSnapshot < invoke);
        Assert.Contains("buttonActive", LoadedStrings(choose));
        Assert.Contains("conditionPass", LoadedStrings(choose));
        Assert.Contains("invocationStarted", LoadedStrings(choose));
    }

    [Fact]
    public void PostClickExceptionIsUnknownButNeverDeclaredPolluted()
    {
        using AssemblyDefinition assembly = ReadPlugin();
        MethodDefinition choose = RequireMethod(RequireType(assembly), "TryChooseOption");
        string[] fields = LoadedStrings(choose).ToArray();

        Assert.Contains("outcomeUnknown", fields);
        Assert.Contains("needsReconciliation", fields);
        Assert.Contains("invocationStarted", fields);
        Assert.DoesNotContain("statePolluted", fields);
        Assert.DoesNotContain("needsReset", fields);
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
}
