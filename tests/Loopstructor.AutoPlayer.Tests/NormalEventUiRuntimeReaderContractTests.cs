using Mono.Cecil;

namespace Loopstructor.AutoPlayer.Tests;

public sealed class NormalEventUiRuntimeReaderContractTests
{
    [Fact]
    public void Reader_UsesCachedReadOnlyNormalEventStateContract()
    {
        string pluginPath = Path.Combine(AppContext.BaseDirectory, "Loopstructor.AutoPlayer.Plugin.dll");
        Assert.True(File.Exists(pluginPath), $"Missing plugin assembly: {pluginPath}");
        using AssemblyDefinition assembly = AssemblyDefinition.ReadAssembly(pluginPath);
        TypeDefinition reader = assembly.MainModule.Types.Single(type =>
            type.FullName == "Loopstructor.AutoPlayer.Plugin.NormalEventUiRuntimeReader");

        HashSet<string> strings = reader.Methods
            .SelectMany(method => method.Body?.Instructions ?? Enumerable.Empty<Mono.Cecil.Cil.Instruction>())
            .Where(instruction => instruction.OpCode == Mono.Cecil.Cil.OpCodes.Ldstr)
            .Select(instruction => instruction.Operand as string)
            .Where(value => value != null)
            .Cast<string>()
            .ToHashSet(StringComparer.Ordinal);

        Assert.Contains("MetroTD.UISystem.WaveFunctionNormalUI", strings);
        Assert.Contains("IsOpen", strings);
        Assert.Contains("m_isTypingStory", strings);
        Assert.Contains("m_currentTypingStage", strings);
        Assert.Contains("m_currentChooseItem", strings);
        Assert.Contains("m_currentStoryIndex", strings);
        Assert.Contains("m_currentStoryList", strings);
        Assert.Contains("m_currentItemStoryIndex", strings);
        Assert.Contains("m_currentItemStoryList", strings);

        MethodDefinition tryRead = reader.Methods.Single(method => method.Name == "TryRead");
        Assert.DoesNotContain(tryRead.Body.Instructions, instruction =>
            instruction.Operand is MethodReference method &&
            method.DeclaringType.FullName == "System.Reflection.FieldInfo" &&
            method.Name == "SetValue");
    }
}
