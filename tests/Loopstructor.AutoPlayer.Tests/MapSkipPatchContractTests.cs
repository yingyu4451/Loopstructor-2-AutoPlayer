using Mono.Cecil;
using Mono.Cecil.Cil;

namespace Loopstructor.AutoPlayer.Tests;

public sealed class MapSkipPatchContractTests
{
    private const string PatchType = "Loopstructor.AutoPlayer.Plugin.MapSkipPatch";
    private const string VisibilityPolicyType = "Loopstructor.AutoPlayer.Core.MapJumpVisibilityPolicy";

    [Fact]
    public void Tick_UsesFutureOnlyVisibilityPolicy()
    {
        using AssemblyDefinition assembly = ReadPlugin();
        TypeDefinition patch = assembly.MainModule.Types.Single(type => type.FullName == PatchType);

        Assert.DoesNotContain(patch.Methods, method => method.Name == "RevealAllLoadedNodes");
        Assert.Contains(Calls(RequireMethod(patch, "Tick")), IsCall(PatchType, "RevealFutureLoadedNodes"));
        Assert.Contains(
            Calls(RequireMethod(patch, "RevealFutureLoadedNodes")),
            IsCall(VisibilityPolicyType, "ShouldExposeForFreeJump"));
        Assert.Contains(
            Calls(RequireMethod(patch, "TryResolveCurrentProgressLayer")),
            IsCall(VisibilityPolicyType, "ResolveCurrentLayer"));
    }

    private static AssemblyDefinition ReadPlugin()
    {
        string path = Path.Combine(AppContext.BaseDirectory, "Loopstructor.AutoPlayer.Plugin.dll");
        Assert.True(File.Exists(path), "Plugin assembly was not copied to the test output: " + path);
        return AssemblyDefinition.ReadAssembly(path);
    }

    private static MethodDefinition RequireMethod(TypeDefinition type, string name) =>
        type.Methods.Single(method => method.Name == name);

    private static IEnumerable<MethodReference> Calls(MethodDefinition method) =>
        method.Body.Instructions
            .Where(instruction => instruction.OpCode.Code is Code.Call or Code.Callvirt)
            .Select(instruction => instruction.Operand)
            .OfType<MethodReference>();

    private static Predicate<MethodReference> IsCall(string declaringType, string methodName) =>
        call => call.DeclaringType.FullName == declaringType && call.Name == methodName;
}
