using Mono.Cecil;
using Mono.Cecil.Cil;

namespace Loopstructor.AutoPlayer.Tests;

public sealed class RepairEventProgressContractTests
{
    private const string ControllerType = "Loopstructor.AutoPlayer.Plugin.AutoPlayController";

    [Fact]
    public void EmptyRepairPanelObservation_DoesNotRefreshTheStallWatchdog()
    {
        using AssemblyDefinition assembly = ReadPlugin();
        TypeDefinition controller = assembly.MainModule.Types.Single(type => type.FullName == ControllerType);
        MethodDefinition beginObservation = RequireMethod(controller, "BeginEventPanelObservation");
        MethodDefinition pendingMap = RequireMethod(controller, "TryHandlePendingMapSelection");
        MethodDefinition optionsWait = RequireMethod(controller, "TryWaitForEventOptions");

        Assert.DoesNotContain(Calls(beginObservation), IsMarkProgressCall);
        Assert.DoesNotContain(Calls(pendingMap), IsMarkProgressCall);
        Assert.Contains(Calls(optionsWait), IsMarkProgressCall);
    }

    private static bool IsMarkProgressCall(MethodReference call) =>
        call.DeclaringType.FullName == ControllerType && call.Name == "MarkProgress";

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
}
