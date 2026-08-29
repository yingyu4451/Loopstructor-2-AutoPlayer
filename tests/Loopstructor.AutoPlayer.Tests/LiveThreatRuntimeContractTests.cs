using Mono.Cecil;
using Mono.Cecil.Cil;

namespace Loopstructor.AutoPlayer.Tests;

public sealed class LiveThreatRuntimeContractTests
{
    private const string BridgeType = "Loopstructor.AutoPlayer.Plugin.RuntimeBridge";
    private const string ReaderType = "Loopstructor.AutoPlayer.Plugin.LiveEnemyThreatReader";

    [Fact]
    public void RuntimeBridge_EnrichesOnlyAdaptedWaveThreatResults()
    {
        using AssemblyDefinition assembly = ReadPlugin();
        TypeDefinition bridge = RequireType(assembly, BridgeType);
        MethodDefinition initialize = RequireMethod(bridge, "Initialize");
        MethodDefinition invoke = RequireMethod(bridge, "Invoke");
        Instruction[] instructions = invoke.Body.Instructions.ToArray();

        Assert.Contains(Calls(initialize), IsCall(ReaderType, "Initialize"));
        Assert.Contains(LoadedStrings(invoke), value => value == "queryWaveThreats");

        int nativeDispatch = FindCall(instructions, BridgeType, "InvokeNative");
        int enrich = FindCall(instructions, ReaderType, "TryEnrich");
        Assert.True(nativeDispatch >= 0 && nativeDispatch < enrich);
        Assert.Contains(Calls(RequireMethod(bridge, "InvokeNative")), IsCall(BridgeType, "AdaptRuntimeResult"));
        Assert.Contains(Calls(invoke), IsCall(BridgeType, "TryGetWavePulse"));
    }

    [Fact]
    public void LiveReader_UsesCachedReadOnlyEnemyAgentContract()
    {
        using AssemblyDefinition assembly = ReadPlugin();
        TypeDefinition reader = RequireType(assembly, ReaderType);
        MethodDefinition initialize = RequireMethod(reader, "Initialize");
        MethodDefinition collect = RequireMethod(reader, "CollectLiveThreats");
        MethodDefinition enrich = RequireMethod(reader, "TryEnrich");

        Assert.Contains(LoadedStrings(initialize), value => value == "MetroTD.AISystem.AgentCreator");
        Assert.Contains(LoadedStrings(initialize), value => value == "enemyAgents");
        Assert.Contains(LoadedStrings(initialize), value => value == "BasicAI");
        Assert.Contains(Calls(collect), call =>
            call.DeclaringType.FullName == "UnityEngine.GameObject" && call.Name == "GetComponent");
        Assert.Contains(Calls(collect), call =>
            call.DeclaringType.FullName == "System.Reflection.FieldInfo" && call.Name == "GetValue");
        Assert.Contains(Calls(collect), call =>
            call.DeclaringType.FullName == "System.Reflection.PropertyInfo" && call.Name == "GetValue");
        Assert.DoesNotContain(Calls(collect), call =>
            call.Name is "GetProperty" or "GetField");
        Assert.Contains(Calls(enrich), IsCall(ReaderType, "CollectLiveThreats"));

        string[] forbidden = { "BattleAgents", "QueryEnemies", "statePolluted", "needsReset" };
        IEnumerable<string> allStrings = reader.Methods.SelectMany(LoadedStrings);
        foreach (string value in forbidden)
        {
            Assert.DoesNotContain(value, allStrings);
        }

        Assert.DoesNotContain(reader.Methods.SelectMany(Calls), call =>
            call.DeclaringType.FullName == "UnityEngine.Resources" &&
            call.Name == "FindObjectsOfTypeAll");
    }

    [Fact]
    public void LiveReader_FailOpenPathDoesNotConstructMutationErrors()
    {
        using AssemblyDefinition assembly = ReadPlugin();
        TypeDefinition reader = RequireType(assembly, ReaderType);
        MethodDefinition enrich = RequireMethod(reader, "TryEnrich");

        Assert.NotEmpty(enrich.Body.ExceptionHandlers);
        Assert.Contains(LoadedStrings(enrich), value => value == "liveThreatsAvailable");
        Assert.DoesNotContain(LoadedStrings(enrich), value => value is
            "statePolluted" or "needsReset" or "invocationStarted");
    }

    private static int FindCall(IReadOnlyList<Instruction> instructions, string declaringType, string methodName) =>
        Array.FindIndex(
            instructions.ToArray(),
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
