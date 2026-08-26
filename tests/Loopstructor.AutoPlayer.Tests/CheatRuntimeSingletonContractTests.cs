using System.Reflection;

namespace Loopstructor.AutoPlayer.Tests;

public sealed class CheatRuntimeSingletonContractTests
{
    [Fact]
    public void TryGetSingleton_TreatsTransientNullReferenceAsNotReady()
    {
        MethodInfo method = ResolveTryGetSingleton();

        object? result = method.Invoke(null, new object[] { typeof(UninitializedSingleton) });

        Assert.Null(result);
    }

    [Fact]
    public void TryGetSingleton_ReturnsInitializedInstance()
    {
        MethodInfo method = ResolveTryGetSingleton();

        object? result = method.Invoke(null, new object[] { typeof(InitializedSingleton) });

        Assert.Same(InitializedSingleton.Instance, result);
    }

    private static MethodInfo ResolveTryGetSingleton()
    {
        Assembly plugin = Assembly.Load("Loopstructor.AutoPlayer.Plugin");
        Type bridge = plugin.GetType("Loopstructor.AutoPlayer.Plugin.CheatRuntimeBridge", throwOnError: true)!;
        return bridge.GetMethod("TryGetSingleton", BindingFlags.NonPublic | BindingFlags.Static)
               ?? throw new MissingMethodException(bridge.FullName, "TryGetSingleton");
    }

    private sealed class UninitializedSingleton
    {
        public static UninitializedSingleton Instance =>
            throw new NullReferenceException("Scene module is not initialized.");
    }

    private sealed class InitializedSingleton
    {
        public static InitializedSingleton Instance { get; } = new();
    }
}
