using Mono.Cecil;
using Mono.Cecil.Cil;

namespace Loopstructor.AutoPlayer.Tests;

public sealed class ManagerVersionPlacementContractTests
{
    [Fact]
    public void MainWindow_ShowsVersionInHeaderButNotMechanicalFooter()
    {
        using AssemblyDefinition assembly = ReadManager();
        TypeDefinition mainForm = assembly.MainModule.Types.Single(
            type => type.FullName == "Loopstructor.AutoPlayer.Manager.UI.MainForm");
        MethodDefinition initializeWindow = mainForm.Methods.Single(method => method.Name == "InitializeWindow");
        MethodReference[] calls = initializeWindow.Body.Instructions
            .Where(instruction => instruction.OpCode.Code is Code.Call or Code.Callvirt)
            .Select(instruction => instruction.Operand)
            .OfType<MethodReference>()
            .ToArray();

        Assert.Contains(
            calls,
            call => call.DeclaringType.FullName == "Loopstructor.AutoPlayer.Manager.Models.ManagerProductInfo"
                    && call.Name == "get_DisplayText");
        Assert.Contains(
            calls,
            call => call.DeclaringType.FullName == "System.Windows.Controls.TextBlock"
                    && call.Name == "set_Text");
        Assert.DoesNotContain(
            calls,
            call => call.DeclaringType.FullName == "Loopstructor.AutoPlayer.Manager.UI.Controls.MechanicalShell"
                    && call.Name == "set_Subtitle");
    }

    private static AssemblyDefinition ReadManager()
    {
        string path = Path.Combine(AppContext.BaseDirectory, "Loopstructor.AutoPlayer.Manager.dll");
        Assert.True(File.Exists(path), "Manager assembly was not copied to the test output: " + path);
        return AssemblyDefinition.ReadAssembly(path);
    }
}
