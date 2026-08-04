using Mono.Cecil;
using Mono.Cecil.Cil;

namespace Loopstructor.AutoPlayer.Tests;

public sealed class PluginRuntimeHostContractTests
{
    private const string BootstrapType = "Loopstructor.AutoPlayer.Plugin.AutoPlayerPlugin";
    private const string SessionType = "Loopstructor.AutoPlayer.Plugin.AutoPlayerRuntimeSession";
    private const string HostType = "Loopstructor.AutoPlayer.Plugin.AutoPlayerRuntimeHost";

    [Fact]
    public void Bootstrap_OnlyStartsSession_AndNeverOwnsRuntimeCleanup()
    {
        using AssemblyDefinition assembly = ReadPlugin();
        TypeDefinition bootstrap = RequireType(assembly, BootstrapType);

        Assert.Contains(Calls(RequireMethod(bootstrap, "Awake")), IsCall(SessionType, "TryStart"));
        Assert.DoesNotContain(bootstrap.Methods, method => method.Name is "Update" or "OnGUI");

        MethodReference[] allCalls = Calls(bootstrap.Methods).ToArray();
        Assert.DoesNotContain(allCalls, call => call.DeclaringType.FullName == "UnityEngine.Object"
                                                 && call.Name is "DontDestroyOnLoad" or "Destroy" or "DestroyImmediate");
        Assert.DoesNotContain(allCalls, IsCall("Loopstructor.AutoPlayer.Plugin.PipeControlServer", "Dispose"));
        Assert.DoesNotContain(allCalls, call => call.Name == "UnpatchSelf");
    }

    [Fact]
    public void EnsureHost_CreatesIndependentHiddenPersistentRoot()
    {
        using AssemblyDefinition assembly = ReadPlugin();
        TypeDefinition session = RequireType(assembly, SessionType);
        MethodDefinition ensureHost = RequireMethod(session, "EnsureHost");
        Instruction[] instructions = ensureHost.Body.Instructions.ToArray();

        int create = FindCall(instructions, "UnityEngine.GameObject", ".ctor", Code.Newobj);
        int hide = FindCall(instructions, "UnityEngine.Object", "set_hideFlags");
        int persist = FindCall(instructions, "UnityEngine.Object", "DontDestroyOnLoad");
        int addHost = FindGenericCall(instructions, "UnityEngine.GameObject", "AddComponent", HostType);
        int attach = FindCall(instructions, HostType, "Attach");

        Assert.True(create < hide && hide < persist && persist < addHost && addHost < attach);
        Assert.Equal(61, ReadLoadedInt32(instructions[hide - 1]));
        Assert.DoesNotContain(Calls(ensureHost), IsCall("UnityEngine.Transform", "SetParent"));
        Assert.Equal("System.Object", session.BaseType.FullName);
    }

    [Fact]
    public void HostLoss_DoesNotDisposeSession_AndEveryMainThreadWatchdogCanRebuild()
    {
        using AssemblyDefinition assembly = ReadPlugin();
        TypeDefinition session = RequireType(assembly, SessionType);
        TypeDefinition host = RequireType(assembly, HostType);

        MethodDefinition hostDestroy = RequireMethod(host, "OnDestroy");
        Assert.Contains(Calls(hostDestroy), IsCall(SessionType, "HostDestroyed"));
        Assert.DoesNotContain(Calls(hostDestroy), IsCall(SessionType, "Dispose"));

        MethodDefinition hostLost = RequireMethod(session, "HostDestroyed");
        MethodReference[] lossCalls = Calls(hostLost).ToArray();
        Assert.Contains(
            hostLost.Body.Instructions,
            instruction => instruction.OpCode.Code is Code.Beq or Code.Beq_S or Code.Ceq);
        Assert.Contains(lossCalls, IsCall("Loopstructor.AutoPlayer.Plugin.CheatController", "OnRuntimeHostLost"));
        Assert.DoesNotContain(lossCalls, call => call.Name is "Dispose" or "UnpatchSelf");
        Assert.DoesNotContain(lossCalls, call => call.DeclaringType.FullName
            == "Loopstructor.AutoPlayer.Plugin.PipeControlServer");

        Assert.Contains(Calls(RequireMethod(session, "OnSceneLoaded")), IsCall(SessionType, "RecoverHostIfNeeded"));
        Assert.Contains(Calls(RequireMethod(session, "OnActiveSceneChanged")), IsCall(SessionType, "RecoverHostIfNeeded"));
        Assert.Contains(Calls(RequireMethod(session, "OnBeforeRender")), IsCall(SessionType, "RecoverHostIfNeeded"));
        Assert.Contains(Calls(RequireMethod(session, "RecoverHostIfNeeded")), IsCall(SessionType, "EnsureHost"));
    }

    [Fact]
    public void LifecycleEvents_AreSymmetric_AndBothQuitSignalsStopSession()
    {
        using AssemblyDefinition assembly = ReadPlugin();
        TypeDefinition session = RequireType(assembly, SessionType);
        TypeDefinition host = RequireType(assembly, HostType);

        MethodReference[] attach = Calls(RequireMethod(session, "AttachLifecycleEvents")).ToArray();
        MethodReference[] detach = Calls(RequireMethod(session, "DetachLifecycleEvents")).ToArray();
        foreach (string eventName in new[] { "sceneLoaded", "activeSceneChanged", "onBeforeRender", "quitting" })
        {
            Assert.Contains(attach, call => call.Name == "add_" + eventName);
            Assert.Contains(detach, call => call.Name == "remove_" + eventName);
        }

        Assert.Contains(Calls(RequireMethod(session, "OnApplicationQuitting")), IsCall(SessionType, "BeginQuit"));
        Assert.Contains(Calls(RequireMethod(host, "OnApplicationQuit")), IsCall(SessionType, "BeginQuit"));
        Assert.Contains(Calls(RequireMethod(session, "BeginQuit")), IsCall(SessionType, "Dispose"));
    }

    [Fact]
    public void IndependentHost_OwnsUnityCallbacks_AndSessionOwnsFinalCleanup()
    {
        using AssemblyDefinition assembly = ReadPlugin();
        TypeDefinition session = RequireType(assembly, SessionType);
        TypeDefinition host = RequireType(assembly, HostType);

        Assert.Equal("UnityEngine.MonoBehaviour", host.BaseType.FullName);
        Assert.Contains(Calls(RequireMethod(host, "Update")), IsCall(SessionType, "PumpFrame"));
        Assert.Contains(Calls(RequireMethod(host, "OnGUI")), IsCall(SessionType, "DrawOverlay"));

        MethodReference[] cleanup = Calls(AllMethods(session)).ToArray();
        Assert.Contains(cleanup, IsCall(SessionType, "DetachLifecycleEvents"));
        Assert.Contains(cleanup, IsCall("Loopstructor.AutoPlayer.Plugin.PipeControlServer", "Dispose"));
        Assert.Contains(cleanup, IsCall("Loopstructor.AutoPlayer.Plugin.CheatController", "Dispose"));
        Assert.Contains(cleanup, IsCall("Loopstructor.AutoPlayer.Plugin.SpawnPointCaptureInputPatch", "Detach"));
        Assert.Contains(cleanup, IsCall("Loopstructor.AutoPlayer.Plugin.MapSkipPatch", "Reset"));
        Assert.Contains(cleanup, call => call.Name == "UnpatchSelf");
    }

    [Fact]
    public void DuplicateActivation_MustMatchExistingSecurityBinding_AndStartupFailureIsFailClosed()
    {
        using AssemblyDefinition assembly = ReadPlugin();
        TypeDefinition session = RequireType(assembly, SessionType);

        Assert.Contains(Calls(RequireMethod(session, "TryStart")), IsCall(SessionType, "MatchesActivation"));
        Assert.Contains(Calls(RequireMethod(session, "MatchesActivation")), IsCall(SessionType, "TokensEqual"));
        Assert.Contains(session.Fields, field => field.Name == "_startupFailure"
                                                && field.IsStatic
                                                && field.FieldType.FullName == "System.String");
    }

    [Fact]
    public void PipeServer_StartsOnlyAfterAListenerIsReady()
    {
        using AssemblyDefinition assembly = ReadPlugin();
        TypeDefinition server = RequireType(assembly, "Loopstructor.AutoPlayer.Plugin.PipeControlServer");

        MethodReference[] startCalls = Calls(RequireMethod(server, "Start")).ToArray();
        Assert.Contains(startCalls, IsCall("System.Threading.ManualResetEventSlim", "Wait"));
        Assert.Contains(startCalls, IsCall("Loopstructor.AutoPlayer.Plugin.PipeControlServer", "Dispose"));
        Assert.Contains(
            Calls(RequireMethod(server, "RegisterActiveServer")),
            IsCall("System.Threading.ManualResetEventSlim", "Set"));
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
        method.HasBody ? Calls(new[] { method }) : Enumerable.Empty<MethodReference>();

    private static IEnumerable<MethodReference> Calls(IEnumerable<MethodDefinition> methods) =>
        methods.Where(method => method.HasBody)
            .SelectMany(method => method.Body.Instructions)
            .Where(instruction => instruction.OpCode.Code
                is Code.Call or Code.Callvirt or Code.Newobj or Code.Ldftn or Code.Ldvirtftn)
            .Select(instruction => instruction.Operand)
            .OfType<MethodReference>();

    private static IEnumerable<MethodDefinition> AllMethods(TypeDefinition type) =>
        type.Methods.Concat(type.NestedTypes.SelectMany(AllMethods));

    private static Predicate<MethodReference> IsCall(string declaringType, string methodName) =>
        call => call.DeclaringType.FullName == declaringType && call.Name == methodName;

    private static int FindCall(
        IReadOnlyList<Instruction> instructions,
        string declaringType,
        string methodName,
        Code? requiredCode = null)
    {
        for (int index = 0; index < instructions.Count; index++)
        {
            Instruction instruction = instructions[index];
            if (instruction.Operand is MethodReference call
                && call.DeclaringType.FullName == declaringType
                && call.Name == methodName
                && (!requiredCode.HasValue || instruction.OpCode.Code == requiredCode.Value))
            {
                return index;
            }
        }

        return -1;
    }

    private static int FindGenericCall(
        IReadOnlyList<Instruction> instructions,
        string declaringType,
        string methodName,
        string genericArgument)
    {
        for (int index = 0; index < instructions.Count; index++)
        {
            if (instructions[index].Operand is GenericInstanceMethod call
                && call.DeclaringType.FullName == declaringType
                && call.Name == methodName
                && call.GenericArguments.Any(argument => argument.FullName == genericArgument))
            {
                return index;
            }
        }

        return -1;
    }

    private static int ReadLoadedInt32(Instruction instruction) => instruction.OpCode.Code switch
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
        Code.Ldc_I4_S => (sbyte)instruction.Operand,
        Code.Ldc_I4 => (int)instruction.Operand,
        _ => throw new Xunit.Sdk.XunitException("Expected an int32 load before set_hideFlags.")
    };
}
