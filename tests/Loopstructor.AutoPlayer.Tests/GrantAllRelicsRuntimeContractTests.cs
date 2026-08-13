using Mono.Cecil;
using Mono.Cecil.Cil;

namespace Loopstructor.AutoPlayer.Tests;

public sealed class GrantAllRelicsRuntimeContractTests
{
    private const string ControllerType = "Loopstructor.AutoPlayer.Plugin.CheatController";
    private const string BridgeType = "Loopstructor.AutoPlayer.Plugin.CheatRuntimeBridge";
    private const string AutoPlayControllerType = "Loopstructor.AutoPlayer.Plugin.AutoPlayController";

    [Fact]
    public void Controller_DispatchesOneBatchCommand_AndTicksOnlyOutsideAutoPlay()
    {
        using AssemblyDefinition assembly = ReadPlugin();
        TypeDefinition controller = RequireType(assembly, ControllerType);

        MethodDefinition executeCore = RequireMethod(controller, "ExecuteCore");
        Assert.Contains(
            LoadedStrings(executeCore),
            value => value.Equals("cheat.grantallrelics", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(
            Calls(executeCore),
            IsCall(BridgeType, "StartGrantAllRelics"));

        MethodDefinition tick = RequireMethod(controller, "Tick");
        MethodReference[] calls = Calls(tick).ToArray();
        Assert.Contains(calls, IsCall(ControllerType, "get_Enabled"));
        Assert.Contains(calls, IsCall(AutoPlayControllerType, "get_IsAutoPlayActive"));
        Assert.Contains(calls, IsCall(BridgeType, "CancelGrantAllRelics"));
        Assert.Contains(calls, IsCall(BridgeType, "TickGrantAllRelics"));

        Instruction[] instructions = tick.Body.Instructions.ToArray();
        int autoPlay = FindCall(instructions, AutoPlayControllerType, "get_IsAutoPlayActive");
        int cancel = FindCall(instructions, BridgeType, "CancelGrantAllRelics", autoPlay + 1);
        int advance = FindCall(instructions, BridgeType, "TickGrantAllRelics");
        Assert.True(autoPlay >= 0 && autoPlay < cancel, "自动游玩状态必须在取消批量作业之前读取。");
        Assert.True(cancel < advance, "取消路径必须位于继续批量作业路径之前，避免自动游玩时继续写入。");
    }

    [Fact]
    public void RuntimeJob_UsesCompleteEnumPool_SkipsOwnedAndVerifiesEveryGrant()
    {
        using AssemblyDefinition assembly = ReadPlugin();
        TypeDefinition bridge = RequireType(assembly, BridgeType);
        MethodDefinition start = RequireMethod(bridge, "StartGrantAllRelics");
        MethodDefinition tick = RequireMethod(bridge, "TickGrantAllRelics");
        MethodDefinition configuredValues = RequireMethod(bridge, "AllEnumValues");
        MethodDefinition distinctValues = RequireMethod(bridge, "DistinctEnumValues");

        Assert.Contains(Calls(start), IsCall(BridgeType, "AllEnumValues"));
        Assert.Contains(
            Calls(start),
            call => call.DeclaringType.FullName == "Loopstructor.AutoPlayer.Plugin.CheatExecutionResult"
                    && call.Name == "Changed");
        Assert.Contains(
            Calls(configuredValues),
            IsCall(BridgeType, "DistinctEnumValues"));
        Assert.Contains(
            Calls(distinctValues),
            call => call.Name == "Add"
                    && call.DeclaringType.FullName.Contains("HashSet", StringComparison.Ordinal));

        MethodReference[] startCalls = Calls(start).ToArray();
        Assert.Contains(startCalls, IsCall(BridgeType, "GetDictionaryListCount"));
        Assert.Contains(LoadedStrings(start), value => value == "superModules");

        MethodReference[] tickCalls = Calls(tick).ToArray();
        Assert.Contains(tickCalls, IsCall(BridgeType, "GetDictionaryListCount"));
        Assert.True(
            tickCalls.Count(call => IsCall(BridgeType, "GetDictionaryListCount")(call)) >= 2,
            "每个遗物必须读取写入前后数量，不能把 void API 调用当作成功。");
        Assert.Contains(LoadedStrings(tick), value => value == "superModules");
        Assert.Contains(tickCalls, call => call.Name == "Invoke");

        FieldDefinition perFrameLimit = Assert.Single(
            bridge.Fields,
            field => field.HasConstant
                     && field.Constant is int value
                     && value == 1
                     && field.Name.Contains("GrantAllRelics", StringComparison.OrdinalIgnoreCase));
        Assert.True(perFrameLimit.IsStatic);
        Assert.True(perFrameLimit.IsLiteral);
    }

    [Fact]
    public void QueryOwnedState_AlwaysExposesBatchJobProgress()
    {
        using AssemblyDefinition assembly = ReadPlugin();
        TypeDefinition bridge = RequireType(assembly, BridgeType);
        MethodDefinition queryOwnedState = RequireMethod(bridge, "QueryOwnedState");

        Assert.Contains(LoadedStrings(queryOwnedState), value => value == "grantAllRelics");
        Assert.Contains(
            Calls(queryOwnedState),
            call => call.DeclaringType.FullName == BridgeType
                    && call.Name.Contains("GrantAllRelics", StringComparison.Ordinal));
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

    private static int FindCall(
        IReadOnlyList<Instruction> instructions,
        string declaringType,
        string methodName,
        int startIndex = 0)
    {
        for (int index = Math.Max(0, startIndex); index < instructions.Count; index++)
        {
            if (instructions[index].Operand is MethodReference call
                && call.DeclaringType.FullName == declaringType
                && call.Name == methodName)
            {
                return index;
            }
        }

        return -1;
    }
}
