using Mono.Cecil;
using Mono.Cecil.Cil;
using Newtonsoft.Json.Linq;

namespace Loopstructor.AutoPlayer.Tests;

public sealed class SkipRewardPopupRuntimeContractTests
{
    private const string BridgeType = "Loopstructor.AutoPlayer.Plugin.CheatRuntimeBridge";
    private const string ControllerType = "Loopstructor.AutoPlayer.Plugin.CheatController";

    [Fact]
    public void Controller_DispatchesAuditedRewardSkipMutation()
    {
        using AssemblyDefinition assembly = ReadPlugin();
        MethodDefinition executeCore = RequireMethod(RequireType(assembly, ControllerType), "ExecuteCore");

        Assert.Contains("cheat.skiprewardpopup", LoadedStrings(executeCore));
        Assert.Contains(Calls(executeCore), IsCall(BridgeType, "SkipRewardPopup"));
    }

    [Fact]
    public void RuntimeSkip_UsesNativeQueueProgression_AndVerifiesPostcondition()
    {
        using AssemblyDefinition assembly = ReadPlugin();
        TypeDefinition bridge = RequireType(assembly, BridgeType);
        MethodDefinition skip = RequireMethod(bridge, "SkipRewardPopup");
        string[] strings = LoadedStrings(skip).ToArray();

        Assert.Contains("MetroTD.RewardSystem.RewardUIPanel", strings);
        Assert.Contains("SkipHandle", strings);
        Assert.Contains("UseCurrent", strings);
        Assert.Contains(Calls(skip), IsCall(BridgeType, "FindMethodByParameterCount"));
        Assert.Contains("UpdateImmediately", strings);
        Assert.Contains("m_currentQueueItem", strings);
        Assert.Contains("m_currentRewardQueneItems", strings);
        Assert.Contains("queueAdvanced", strings);
        Assert.Contains(Calls(skip), IsCall(BridgeType, "DispatchRewardJump"));
        Assert.Contains(Calls(skip), IsCall(BridgeType, "RequestGameSave"));
        Assert.DoesNotContain("CloseHandle", strings);
        Assert.DoesNotContain("CloseSelf", strings);
    }

    [Fact]
    public void MandatoryReward_AdvancesNativeQueue_AndPersistsSkip()
    {
        Type runtimeBridge = LoadRuntimeBridgeType();
        object bridge = System.Runtime.CompilerServices.RuntimeHelpers.GetUninitializedObject(runtimeBridge);
        SetField(runtimeBridge, bridge, "<IsAvailable>k__BackingField", true);
        SetField(runtimeBridge, bridge, "_guiSaveHandlerType", typeof(GuiSaveHandler));
        MetroTD.RewardSystem.RewardUIPanel.Reset(mandatory: true, includeNext: true);
        GuiSaveHandler.Reset();
        MetroTD.RewardSystem.RewardJumpEventHandler.Reset();

        object result = runtimeBridge.GetMethod("SkipRewardPopup")!.Invoke(bridge, null)!;
        Type resultType = result.GetType();
        Assert.True((bool)resultType.GetProperty("Success")!.GetValue(result)!);
        JObject data = Assert.IsType<JObject>(resultType.GetProperty("Data")!.GetValue(result));
        Assert.True(data.Value<bool>("queueAdvanced"));
        Assert.False(data.Value<bool>("panelClosed"));
        Assert.Equal("Disposable", MetroTD.RewardSystem.RewardUIPanel.Instance.CurrentItemType);
        Assert.True(GuiSaveHandler.WasSaved);
        Assert.Equal(
            MetroTD.RewardSystem.FakeRewardItemType.SuperModule,
            MetroTD.RewardSystem.RewardJumpEventHandler.LastItemType);
    }

    private static AssemblyDefinition ReadPlugin()
    {
        string path = Path.Combine(AppContext.BaseDirectory, "Loopstructor.AutoPlayer.Plugin.dll");
        Assert.True(File.Exists(path), "Plugin assembly was not copied to the test output: " + path);
        return AssemblyDefinition.ReadAssembly(path);
    }

    private static Type LoadRuntimeBridgeType()
    {
        string path = Path.Combine(AppContext.BaseDirectory, "Loopstructor.AutoPlayer.Plugin.dll");
        return System.Reflection.Assembly.LoadFrom(path)
            .GetType(BridgeType, throwOnError: true)!;
    }

    private static void SetField(Type type, object target, string name, object value) =>
        type.GetField(name, System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!
            .SetValue(target, value);

    private static TypeDefinition RequireType(AssemblyDefinition assembly, string fullName) =>
        assembly.MainModule.Types.Single(type => type.FullName == fullName);

    private static MethodDefinition RequireMethod(TypeDefinition type, string name) =>
        type.Methods.Single(method => method.Name == name);

    private static IEnumerable<MethodReference> Calls(MethodDefinition method) =>
        method.Body.Instructions
            .Where(instruction => instruction.OpCode.Code
                is Code.Call or Code.Callvirt or Code.Newobj or Code.Ldftn or Code.Ldvirtftn)
            .Select(instruction => instruction.Operand)
            .OfType<MethodReference>();

    private static IEnumerable<string> LoadedStrings(MethodDefinition method) =>
        method.Body.Instructions
            .Where(instruction => instruction.OpCode.Code == Code.Ldstr)
            .Select(instruction => instruction.Operand)
            .OfType<string>();

    private static Predicate<MethodReference> IsCall(string declaringType, string methodName) =>
        call => call.DeclaringType.FullName == declaringType && call.Name == methodName;
}
