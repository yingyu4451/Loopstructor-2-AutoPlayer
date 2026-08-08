using Mono.Cecil;
using Mono.Cecil.Cil;

namespace Loopstructor.AutoPlayer.Tests;

public sealed class RewardObjectLedgerContractTests
{
    private const string ControllerType = "Loopstructor.AutoPlayer.Plugin.AutoPlayController";
    private const string GuardType = "Loopstructor.AutoPlayer.Core.RewardObjectSettlementGuard";

    [Fact]
    public void ProvenSettlementReleasesOnlyTheCompletedInstanceFromTheReplayLedger()
    {
        using AssemblyDefinition assembly = ReadPlugin();
        TypeDefinition controller = assembly.MainModule.Types.Single(type => type.FullName == ControllerType);
        MethodDefinition complete = controller.Methods.Single(method =>
            method.Name == "CompleteRewardObjectSettlement");
        Instruction[] instructions = complete.Body.Instructions.ToArray();

        int captureIdentity = FindCall(instructions, GuardType, "get_RewardObjectInstanceId");
        int resetGuard = FindCall(instructions, GuardType, "Reset");
        int removeLedger = FindCall(
            instructions,
            "System.Collections.Generic.HashSet`1<System.Int32>",
            "Remove");

        Assert.True(captureIdentity >= 0 && captureIdentity < resetGuard);
        Assert.True(resetGuard < removeLedger);
    }

    [Fact]
    public void IssuedInstanceIsRetainedWhileGuardIsArmed()
    {
        using AssemblyDefinition assembly = ReadPlugin();
        TypeDefinition controller = assembly.MainModule.Types.Single(type => type.FullName == ControllerType);
        MethodDefinition arm = controller.Methods.Single(method => method.Name == "TryArmRewardObjectSettlement");
        MethodDefinition execute = controller.Methods.Single(method => method.Name == "ExecuteWithResult");

        Assert.Contains(
            Calls(arm),
            call => call.DeclaringType.FullName ==
                    "System.Collections.Generic.HashSet`1<System.Int32>" &&
                    call.Name == "Add");
        Assert.Contains(
            Calls(execute),
            call => call.DeclaringType.FullName == GuardType && call.Name == "get_IsArmed");
        Assert.Contains(
            Calls(execute),
            call => call.DeclaringType.FullName ==
                    "System.Collections.Generic.HashSet`1<System.Int32>" &&
                    call.Name == "Contains");
    }

    private static int FindCall(
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
                return index;
            }
        }

        return -1;
    }

    private static IEnumerable<MethodReference> Calls(MethodDefinition method) =>
        method.Body.Instructions
            .Where(instruction => instruction.OpCode.Code is Code.Call or Code.Callvirt)
            .Select(instruction => instruction.Operand)
            .OfType<MethodReference>();

    private static AssemblyDefinition ReadPlugin()
    {
        string path = Path.Combine(AppContext.BaseDirectory, "Loopstructor.AutoPlayer.Plugin.dll");
        Assert.True(File.Exists(path), "Plugin assembly was not copied to the test output: " + path);
        return AssemblyDefinition.ReadAssembly(path);
    }
}
