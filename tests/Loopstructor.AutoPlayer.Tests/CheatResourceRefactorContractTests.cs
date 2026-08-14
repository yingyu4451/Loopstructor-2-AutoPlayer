using Mono.Cecil;
using Mono.Cecil.Cil;

namespace Loopstructor.AutoPlayer.Tests;

public sealed class CheatResourceRefactorContractTests
{
    private const string BridgeType = "Loopstructor.AutoPlayer.Plugin.CheatRuntimeBridge";
    private const string ControllerType = "Loopstructor.AutoPlayer.Plugin.CheatController";
    private const string DisplayPatchType = "Loopstructor.AutoPlayer.Plugin.VehicleEnchantmentDisplayPatch";

    [Fact]
    public void CatalogV5_UsesRandomModeFixedVehicleAndFetterPools_AndPartitionsOtherCompleteEnums()
    {
        using AssemblyDefinition assembly = ReadPlugin();
        TypeDefinition bridge = RequireType(assembly, BridgeType);
        MethodDefinition catalog = RequireMethod(bridge, "QueryCatalog");
        MethodDefinition vehicle = RequireMethod(bridge, "BuildVehicleCatalogItem");
        MethodDefinition enchantment = RequireMethod(bridge, "BuildEnchantmentCatalogItem");
        MethodDefinition vehicleValues = RequireMethod(bridge, "AllVehicleValues");
        MethodDefinition enchantmentValues = RequireMethod(bridge, "AllEnchantmentValues");

        Assert.Contains(5, LoadedInts(catalog));
        Assert.Contains(Calls(catalog), IsCall(BridgeType, "AllVehicleValues"));
        Assert.Contains(Calls(catalog), IsCall(BridgeType, "AllEnchantmentValues"));
        Assert.Contains(Calls(vehicleValues), IsCall(BridgeType, "RandomModeFixedPoolValues"));
        Assert.Contains(Calls(enchantmentValues), IsCall(BridgeType, "RandomModeFixedPoolValues"));
        MethodDefinition fixedPools = RequireMethod(bridge, "RandomModeFixedPoolValues");
        Assert.Contains("GetRandomModeVehiclePool", LoadedStrings(vehicleValues));
        Assert.Contains("GetRandomModeBasicFetterPool", LoadedStrings(enchantmentValues));
        Assert.Contains(Calls(fixedPools), call => call.Name == "Invoke");
        Assert.Contains("随机模式固定", LoadedStrings(fixedPools));
        Assert.DoesNotContain(bridge.Methods, method => method.Name == "ConfiguredCheatValues");
        Assert.DoesNotContain("AllDisposableRewards", LoadedStrings(catalog));
        Assert.DoesNotContain("AllSuperModuleRewards", LoadedStrings(catalog));
        Assert.Contains(Calls(catalog), IsCall(BridgeType, "IsCatapultPoint"));
        Assert.Contains(Calls(vehicle), IsCall(BridgeType, "VehicleFamily"));
        Assert.Contains(Calls(vehicle), IsCall(BridgeType, "VehicleTypeOrder"));
        Assert.DoesNotContain(Calls(vehicle), IsCall(BridgeType, "VehicleFamilyOrder"));
        Assert.Contains(Calls(enchantment), IsCall(BridgeType, "EnchantmentVariantOrder"));
        foreach (string field in new[] { "groupKey", "groupName", "groupOrder", "itemOrder" })
        {
            Assert.Contains(field, LoadedStrings(RequireMethod(bridge, "ApplyGrouping")));
        }
        foreach (string field in new[] { "typeKey", "typeOrder", "familyKey", "familyOrder" })
        {
            Assert.Contains(field, LoadedStrings(vehicle));
        }

        MethodDefinition pointClassifier = RequireMethod(bridge, "IsCatapultPoint");
        Assert.Contains("弹射点", LoadedStrings(pointClassifier));
        Assert.Contains("站点", LoadedStrings(pointClassifier));
        Assert.Contains("始发站", LoadedStrings(pointClassifier));
        Assert.Contains("description", LoadedStrings(enchantment));
        Assert.Contains("description", LoadedStrings(RequireMethod(bridge, "BuildDisposableCatalogItem")));
        Assert.Contains("description", LoadedStrings(RequireMethod(bridge, "BuildRelicCatalogItem")));
    }

    [Fact]
    public void CatalogCoverage_UsesRandomModeFixedPoolsForVehicleAndEnchantments_WhileOtherResourcesStayEnumComplete()
    {
        using AssemblyDefinition assembly = ReadPlugin();
        TypeDefinition bridge = RequireType(assembly, BridgeType);
        MethodDefinition allValues = RequireMethod(bridge, "AllEnumValues");
        MethodDefinition grantDisposable = RequireMethod(bridge, "GrantDisposable");
        MethodDefinition grantPoint = RequireMethod(bridge, "GrantCatapultPoint");
        MethodDefinition grantRelic = RequireMethod(bridge, "GrantRelic");
        MethodDefinition grantVehicle = RequireMethod(bridge, "GrantVehicle");
        MethodDefinition editEnchantment = RequireMethod(bridge, "SetVehicleEnchantment");

        Assert.Contains(Calls(allValues), call => call.DeclaringType.FullName == "System.Enum" && call.Name == "GetValues");
        Assert.Contains(Calls(grantVehicle), IsCall(BridgeType, "AllVehicleValues"));
        Assert.Contains(Calls(grantVehicle), IsCall(BridgeType, "AllEnchantmentValues"));
        Assert.Contains(Calls(editEnchantment), IsCall(BridgeType, "AllEnchantmentValues"));
        Assert.DoesNotContain(Calls(RequireMethod(bridge, "AllVehicleValues")), IsCall(BridgeType, "AllEnumValues"));
        Assert.DoesNotContain(Calls(RequireMethod(bridge, "AllEnchantmentValues")), IsCall(BridgeType, "AllEnumValues"));
        Assert.DoesNotContain(Calls(grantDisposable), IsCall(BridgeType, "TryGetDisposableData"));
        Assert.DoesNotContain(Calls(grantPoint), IsCall(BridgeType, "TryGetDisposableData"));
        Assert.DoesNotContain(Calls(grantRelic), IsCall(BridgeType, "TryGetSuperModuleData"));
        Assert.DoesNotContain(LoadedStrings(grantDisposable), value => value.Contains("奖励配置", StringComparison.Ordinal));
        Assert.DoesNotContain(LoadedStrings(grantRelic), value => value.Contains("奖励配置", StringComparison.Ordinal));
    }

    [Fact]
    public void ConsumableGrant_UsesGameCapacityAndReturnsOwnedState()
    {
        using AssemblyDefinition assembly = ReadPlugin();
        TypeDefinition bridge = RequireType(assembly, BridgeType);
        MethodDefinition grant = RequireMethod(bridge, "GrantDisposable");
        MethodDefinition state = RequireMethod(bridge, "QueryOwnedState");

        Assert.Contains(Calls(grant), IsCall(BridgeType, "ReadDisposableCapacity"));
        Assert.Contains(Calls(grant), IsCall(BridgeType, "BuildOwnedConsumables"));
        Assert.Contains("ownedConsumables", LoadedStrings(state));
        Assert.Contains(5, LoadedInts(RequireMethod(bridge, "ReadDisposableCapacity")));
    }

    [Fact]
    public void Enchantments_HaveNoProductCountOrLevelLimit_AndDisplayPatchShowsAll()
    {
        using AssemblyDefinition assembly = ReadPlugin();
        TypeDefinition bridge = RequireType(assembly, BridgeType);
        MethodDefinition grant = RequireMethod(bridge, "GrantVehicle");
        MethodDefinition edit = RequireMethod(bridge, "SetVehicleEnchantment");
        TypeDefinition displayPatch = RequireType(assembly, DisplayPatchType);
        MethodDefinition prefix = RequireMethod(displayPatch, "Prefix");
        MethodDefinition postfix = RequireMethod(displayPatch, "Postfix");

        Assert.Contains(Calls(grant), IsCall(BridgeType, "PositiveInt"));
        Assert.Contains(Calls(edit), IsCall(BridgeType, "NonNegativeInt"));
        Assert.DoesNotContain(
            bridge.Fields,
            field => field.Name.Contains("Enchant", StringComparison.OrdinalIgnoreCase)
                     && field.Name.Contains("Max", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(Calls(prefix), call => call.Name == "get_Count");
        Assert.Contains(Calls(postfix), call => call.DeclaringType.FullName == "UnityEngine.Mathf" && call.Name == "Sqrt");
        Assert.Contains(Calls(postfix), call => call.DeclaringType.FullName == "UnityEngine.RectTransform" && call.Name == "SetSizeWithCurrentAnchors");
        Assert.Contains(Calls(RequireMethod(displayPatch, "SetEnabled")), IsCall(DisplayPatchType, "RestoreLayouts"));
    }

    [Fact]
    public void BulkDeletes_AreSeparated_AndRelicRemovalIsOneItemPerFrame()
    {
        using AssemblyDefinition assembly = ReadPlugin();
        TypeDefinition bridge = RequireType(assembly, BridgeType);
        MethodDefinition consumables = RequireMethod(bridge, "ClearConsumables");
        MethodDefinition backpackPoints = RequireMethod(bridge, "ClearBackpackCatapultPoints");
        MethodDefinition fieldPoints = RequireMethod(bridge, "ClearFieldCatapultPoints");
        MethodDefinition startRemoveRelics = RequireMethod(bridge, "StartRemoveAllRelics");
        MethodDefinition tickRemoveRelics = RequireMethod(bridge, "TickRemoveAllRelics");

        Assert.Contains(Calls(consumables), IsCall(BridgeType, "ClearBackpackItems"));
        Assert.Contains(Calls(backpackPoints), IsCall(BridgeType, "ClearBackpackItems"));
        Assert.Contains(Calls(fieldPoints), IsCall(BridgeType, "DeleteFieldCatapult"));
        Assert.Contains("TryRemoveSuperModule", LoadedStrings(startRemoveRelics));
        Assert.Contains(Calls(tickRemoveRelics), call => call.Name == "Invoke");

        FieldDefinition budget = Assert.Single(
            bridge.Fields,
            field => field.HasConstant
                     && field.Constant is int value
                     && value == 1
                     && field.Name.Contains("RemoveAllRelics", StringComparison.OrdinalIgnoreCase));
        Assert.True(budget.IsLiteral && budget.IsStatic);
    }

    [Fact]
    public void ClickDelete_ConsumesInput_UsesExactHoveredPoint_AndResetsWithTransientFeatures()
    {
        using AssemblyDefinition assembly = ReadPlugin();
        TypeDefinition bridge = RequireType(assembly, BridgeType);
        MethodDefinition tick = RequireMethod(bridge, "TickFieldCatapultDeleteInput");
        MethodDefinition find = RequireMethod(bridge, "FindHoveredFieldCatapult");
        MethodDefinition reset = RequireMethod(bridge, "ResetTransientFeatures");
        TypeDefinition controller = RequireType(assembly, ControllerType);

        Assert.Contains(LoadedStrings(tick), value => value == "Escape");
        Assert.Contains(LoadedStrings(tick), value => value == "left");
        Assert.Contains(LoadedStrings(tick), value => value == "UseInputOnly");
        Assert.Contains(Calls(tick), IsCall(BridgeType, "FindHoveredFieldCatapult"));
        Assert.Contains(Calls(tick), IsCall(BridgeType, "DeleteFieldCatapult"));
        Assert.Contains(AllCalls(find), call => call.Name == "OverlapPoint");
        Assert.Contains(Calls(find), call => call.Name == "GetComponentsInChildren");
        Assert.DoesNotContain(LoadedFloats(find), value => Math.Abs(value - 1.6f) < 0.001f);
        FieldDefinition mode = Assert.Single(bridge.Fields, field => field.Name == "_fieldCatapultDeleteMode");
        Assert.Contains(
            reset.Body.Instructions,
            instruction => instruction.OpCode.Code == Code.Stfld
                           && instruction.Operand is FieldReference field
                           && field.FullName == mode.FullName);

        MethodDefinition controllerTick = RequireMethod(controller, "Tick");
        Assert.Contains(Calls(controllerTick), IsCall(BridgeType, "SetFieldCatapultDeleteMode"));
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
            .Where(instruction => instruction.OpCode.Code is Code.Call or Code.Callvirt or Code.Newobj or Code.Ldftn or Code.Ldvirtftn)
            .Select(instruction => instruction.Operand)
            .OfType<MethodReference>();

    private static IEnumerable<MethodReference> AllCalls(MethodDefinition method)
    {
        foreach (MethodReference call in Calls(method)) yield return call;
        foreach (TypeDefinition nested in method.DeclaringType.NestedTypes)
        {
            foreach (MethodDefinition nestedMethod in nested.Methods.Where(candidate => candidate.HasBody))
            {
                foreach (MethodReference call in Calls(nestedMethod)) yield return call;
            }
        }
    }

    private static IEnumerable<float> LoadedFloats(MethodDefinition method) =>
        method.Body.Instructions
            .Where(instruction => instruction.OpCode.Code == Code.Ldc_R4)
            .Select(instruction => (float)instruction.Operand);

    private static IEnumerable<string> LoadedStrings(MethodDefinition method) =>
        method.Body.Instructions
            .Where(instruction => instruction.OpCode.Code == Code.Ldstr)
            .Select(instruction => instruction.Operand)
            .OfType<string>();

    private static IEnumerable<int> LoadedInts(MethodDefinition method) =>
        method.Body.Instructions
            .Where(instruction => instruction.OpCode.Code is Code.Ldc_I4 or Code.Ldc_I4_S
                or Code.Ldc_I4_0 or Code.Ldc_I4_1 or Code.Ldc_I4_2 or Code.Ldc_I4_3
                or Code.Ldc_I4_4 or Code.Ldc_I4_5 or Code.Ldc_I4_6 or Code.Ldc_I4_7 or Code.Ldc_I4_8)
            .Select(instruction => instruction.OpCode.Code switch
            {
                Code.Ldc_I4_0 => 0,
                Code.Ldc_I4_1 => 1,
                Code.Ldc_I4_2 => 2,
                Code.Ldc_I4_3 => 3,
                Code.Ldc_I4_4 => 4,
                Code.Ldc_I4_5 => 5,
                Code.Ldc_I4_6 => 6,
                Code.Ldc_I4_7 => 7,
                Code.Ldc_I4_8 => 8,
                _ => Convert.ToInt32(instruction.Operand)
            });

    private static Predicate<MethodReference> IsCall(string declaringType, string methodName) =>
        call => call.DeclaringType.FullName == declaringType && call.Name == methodName;
}
