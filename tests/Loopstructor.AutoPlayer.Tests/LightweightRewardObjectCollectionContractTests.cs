using Mono.Cecil;
using Mono.Cecil.Cil;

namespace Loopstructor.AutoPlayer.Tests;

public sealed class LightweightRewardObjectCollectionContractTests
{
    private const string FallbackType = "Loopstructor.AutoPlayer.Plugin.RewardUiRuntimeFallback";
    private const string ContractType = FallbackType + "/ReflectionContract";
    private const string CollectionSnapshotType = FallbackType + "/RewardObjectCollectionSnapshot";

    [Fact]
    public void CollectionTargetsOneActiveSceneValidComponentFromTheRegisteredSpawner()
    {
        using AssemblyDefinition assembly = ReadPlugin();
        TypeDefinition fallback = RequireType(assembly, FallbackType);
        MethodDefinition collect = RequireMethod(fallback, "TryCollectRewardObject");
        MethodDefinition snapshot = RequireMethod(fallback, "BuildRewardObjectCollectionSnapshot");
        MethodDefinition readIdentity = RequireMethod(fallback, "TryReadRewardObjectIdentity");
        MethodDefinition contract = RequireMethod(fallback, "TryGetContract");
        Instruction[] collectInstructions = collect.Body.Instructions.ToArray();

        int read = FindCall(collectInstructions, FallbackType, "TryReadRewardObjectIdentity");
        int capture = FindCall(collectInstructions, FallbackType, "BuildRewardObjectCollectionSnapshot");
        int click = FindCall(collectInstructions, "System.Reflection.MethodBase", "Invoke");

        Assert.True(read >= 0 && read < capture);
        Assert.True(capture >= 0 && capture < click);
        Assert.Contains("instanceId", LoadedStrings(readIdentity));
        Assert.Contains("m_rewardObjects", LoadedStrings(contract));
        Assert.Contains("matchingRewardObjectCount", LoadedStrings(snapshot));
        Assert.Contains(Calls(snapshot), IsCall(FallbackType, "TryGetRegisteredObject"));
        Assert.Contains(Calls(snapshot), call => call.DeclaringType.FullName == "UnityEngine.SceneManagement.Scene" && call.Name == "IsValid");
        Assert.Contains(Calls(snapshot), call => call.DeclaringType.FullName == "UnityEngine.GameObject" && call.Name == "get_activeInHierarchy");
        Assert.Contains(Calls(snapshot), call => call.DeclaringType.FullName == "UnityEngine.GameObject" && call.Name == "GetComponent");
        Assert.Contains(Calls(snapshot), call => call.DeclaringType.FullName == "UnityEngine.Object" && call.Name == "GetInstanceID");
        Assert.True(Calls(collect).Count(call =>
            call.DeclaringType.FullName == CollectionSnapshotType && call.Name == "get_MatchCount") >= 2);
        Assert.Contains(Calls(collect), IsCall(CollectionSnapshotType, "get_Target"));
    }

    [Fact]
    public void CollectionInvokesOnlyThePlayerClickChainInOrder()
    {
        using AssemblyDefinition assembly = ReadPlugin();
        TypeDefinition fallback = RequireType(assembly, FallbackType);
        MethodDefinition collect = RequireMethod(fallback, "TryCollectRewardObject");
        MethodDefinition contract = RequireMethod(fallback, "TryGetContract");
        Instruction[] instructions = collect.Body.Instructions.ToArray();
        int[] invocations = FindCalls(instructions, "System.Reflection.MethodBase", "Invoke").ToArray();

        int pointEnter = FindLastCallBefore(
            instructions, invocations[0], ContractType, "get_RewardButtonPointEnter");
        int leftDown = FindLastCallBefore(
            instructions, invocations[1], ContractType, "get_RewardButtonLeftPointDown");
        int leftUp = FindLastCallBefore(
            instructions, invocations[2], ContractType, "get_RewardButtonLeftPointUp");

        Assert.Equal(3, invocations.Length);
        Assert.True(pointEnter >= 0 && pointEnter < invocations[0]);
        Assert.True(invocations[0] < leftDown && leftDown < invocations[1]);
        Assert.True(invocations[1] < leftUp && leftUp < invocations[2]);
        Assert.Contains("Btn", LoadedStrings(contract));
        Assert.Contains("PointEnter", LoadedStrings(contract));
        Assert.Contains("LeftPointDown", LoadedStrings(contract));
        Assert.Contains("LeftPointUp", LoadedStrings(contract));
        Assert.DoesNotContain("Get", LoadedStrings(contract));
        Assert.DoesNotContain(Calls(collect), call => call.Name == "Get" && call.DeclaringType.Name.Contains("RewardObject", StringComparison.Ordinal));
    }

    [Fact]
    public void CollectionReturnsPendingWithoutAnyPostInvocationFullSnapshot()
    {
        using AssemblyDefinition assembly = ReadPlugin();
        MethodDefinition collect = RequireMethod(RequireType(assembly, FallbackType), "TryCollectRewardObject");
        string[] strings = LoadedStrings(collect).ToArray();

        Assert.Contains("invocationStarted", strings);
        Assert.Contains("targetInstanceId", strings);
        Assert.Contains("pending", strings);
        Assert.Contains("needsPolling", strings);
        Assert.Contains("outcomeUnknown", strings);
        Assert.Contains("needsReconciliation", strings);
        Assert.Contains("uncertaintyOrigin", strings);
        Assert.Contains("rewardObjectClickException", strings);
        Assert.DoesNotContain("statePolluted", strings);
        Assert.DoesNotContain("needsReset", strings);
        Assert.DoesNotContain(Calls(collect), IsCall(FallbackType, "BuildSnapshot"));
        Assert.DoesNotContain(Calls(collect), IsCall(FallbackType, "BuildRewardObjects"));
    }

    [Fact]
    public void CollectionPathNeverFallsBackToSceneResourceScanning()
    {
        using AssemblyDefinition assembly = ReadPlugin();
        TypeDefinition fallback = RequireType(assembly, FallbackType);
        MethodDefinition collect = RequireMethod(fallback, "TryCollectRewardObject");
        MethodDefinition snapshot = RequireMethod(fallback, "BuildRewardObjectCollectionSnapshot");

        MethodReference[] calls = Calls(collect).Concat(Calls(snapshot)).ToArray();
        string[] strings = LoadedStrings(collect).Concat(LoadedStrings(snapshot)).ToArray();

        Assert.DoesNotContain(calls, call => call.DeclaringType.FullName == "UnityEngine.Resources");
        Assert.DoesNotContain(strings, value => value.Contains("FindObjectsOfTypeAll", StringComparison.Ordinal));
        Assert.DoesNotContain(strings, value => value.Contains("BuildRewardState", StringComparison.Ordinal));
    }

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

    private static int FindCall(Instruction[] instructions, string declaringType, string methodName) =>
        Array.FindIndex(
            instructions,
            instruction => instruction.Operand is MethodReference call &&
                           call.DeclaringType.FullName == declaringType &&
                           call.Name == methodName);

    private static IEnumerable<int> FindCalls(
        IReadOnlyList<Instruction> instructions,
        string declaringType,
        string methodName)
    {
        for (int index = 0; index < instructions.Count; index++)
        {
            if (instructions[index].Operand is MethodReference call &&
                call.DeclaringType.FullName == declaringType &&
                call.Name == methodName)
            {
                yield return index;
            }
        }
    }

    private static int FindLastCallBefore(
        IReadOnlyList<Instruction> instructions,
        int exclusiveEnd,
        string declaringType,
        string methodName)
    {
        for (int index = exclusiveEnd - 1; index >= 0; index--)
        {
            if (instructions[index].Operand is MethodReference call &&
                call.DeclaringType.FullName == declaringType &&
                call.Name == methodName)
            {
                return index;
            }
        }

        return -1;
    }

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
}
