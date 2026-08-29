using Mono.Cecil;
using Mono.Cecil.Cil;

namespace Loopstructor.AutoPlayer.Tests;

public sealed class OpeningEventHandoffRuntimeContractTests
{
    [Fact]
    public void ClosingLegacyOpeningEvent_RearmsNormalEventProbeBeforeCoreReadinessGate()
    {
        using AssemblyDefinition assembly = AssemblyDefinition.ReadAssembly(PluginPath());
        TypeDefinition controller = assembly.MainModule.Types.Single(type =>
            type.FullName == "Loopstructor.AutoPlayer.Plugin.AutoPlayController");
        MethodDefinition handler = controller.Methods.Single(method =>
            method.Name == "TryHandleOpeningWaveFunctionUi");

        FieldReference[] writtenFields = handler.Body.Instructions
            .Where(instruction => instruction.OpCode.Code == Code.Stfld)
            .Select(instruction => instruction.Operand)
            .OfType<FieldReference>()
            .ToArray();

        Assert.Contains(writtenFields, field => field.Name == "_normalEventProbeRequired");
        Assert.Contains(handler.Body.Instructions, instruction =>
            instruction.OpCode.Code == Code.Ldstr &&
            instruction.Operand is string text &&
            text.Contains("连续出现的普通事件", StringComparison.Ordinal));
    }

    private static string PluginPath()
    {
        string path = Path.Combine(AppContext.BaseDirectory, "Loopstructor.AutoPlayer.Plugin.dll");
        Assert.True(File.Exists(path), "Plugin assembly was not copied to the test output: " + path);
        return path;
    }
}
