using System.Threading;

namespace Loopstructor.AutoPlayer.Core;

/// <summary>
/// 为跨线程运行时功能提供单一、原子的启用状态。
/// </summary>
public sealed class RuntimeFeatureFlag
{
    private int _value;

    public RuntimeFeatureFlag(bool initialValue = false)
    {
        _value = initialValue ? 1 : 0;
    }

    public bool Value => Volatile.Read(ref _value) != 0;

    /// <summary>
    /// 原子写入状态，并返回该次写入是否改变了值。
    /// </summary>
    public bool Set(bool value)
    {
        int next = value ? 1 : 0;
        return Interlocked.Exchange(ref _value, next) != next;
    }
}
