using System;
using System.Threading;

namespace Loopstructor.AutoPlayer.Core;

public readonly struct FrameTimingSnapshot
{
    public FrameTimingSnapshot(
        double currentFps,
        double onePercentLowFps,
        double frameTimeP99Ms,
        int sampleCount,
        double windowSeconds)
    {
        CurrentFps = currentFps;
        OnePercentLowFps = onePercentLowFps;
        FrameTimeP99Ms = frameTimeP99Ms;
        SampleCount = sampleCount;
        WindowSeconds = windowSeconds;
    }

    public double CurrentFps { get; }
    public double OnePercentLowFps { get; }
    public double FrameTimeP99Ms { get; }
    public int SampleCount { get; }
    public double WindowSeconds { get; }
}

public sealed class FrameTimingSampler
{
    public const int DefaultCapacity = 4096;
    public const double DefaultWindowSeconds = 10.0;

    private readonly double[] _samples;
    private readonly double[] _scratch;
    private readonly double _windowSeconds;
    private readonly object _snapshotSync = new();
    private long _writerSequence;
    private long _publishedSequence;
    private long _writeVersion;

    public FrameTimingSampler(
        int capacity = DefaultCapacity,
        double windowSeconds = DefaultWindowSeconds)
    {
        if (capacity <= 0) throw new ArgumentOutOfRangeException(nameof(capacity));
        if (windowSeconds <= 0 || double.IsNaN(windowSeconds) || double.IsInfinity(windowSeconds))
        {
            throw new ArgumentOutOfRangeException(nameof(windowSeconds));
        }

        _samples = new double[capacity];
        _scratch = new double[capacity];
        _windowSeconds = windowSeconds;
    }

    public int Capacity => _samples.Length;

    public void Record(double frameSeconds)
    {
        if (frameSeconds <= 0 || double.IsNaN(frameSeconds) || double.IsInfinity(frameSeconds)) return;

        Interlocked.Increment(ref _writeVersion);
        long sequence = _writerSequence;
        int index = (int)(sequence % _samples.Length);
        Volatile.Write(ref _samples[index], frameSeconds);
        sequence++;
        _writerSequence = sequence;
        Volatile.Write(ref _publishedSequence, sequence);
        Interlocked.Increment(ref _writeVersion);
    }

    public FrameTimingSnapshot Snapshot()
    {
        lock (_snapshotSync)
        {
            int count = 0;
            double elapsedSeconds = 0;
            for (int attempt = 0; attempt < 3; attempt++)
            {
                long versionBefore = Volatile.Read(ref _writeVersion);
                long endSequence = Volatile.Read(ref _publishedSequence);
                count = CopyWindow(endSequence, out elapsedSeconds);
                long versionAfter = Volatile.Read(ref _writeVersion);
                if (versionBefore == versionAfter && (versionAfter & 1L) == 0) break;
            }

            if (count == 0 || elapsedSeconds <= 0) return default;

            double currentFps = count / elapsedSeconds;
            Array.Sort(_scratch, 0, count);
            int slowFrameCount = Math.Max(1, (int)Math.Ceiling(count * 0.01));
            int firstSlowFrame = count - slowFrameCount;
            double slowFrameSeconds = 0;
            for (int index = firstSlowFrame; index < count; index++)
            {
                slowFrameSeconds += _scratch[index];
            }

            double onePercentLowFps = slowFrameSeconds > 0
                ? slowFrameCount / slowFrameSeconds
                : 0;
            double frameTimeP99Ms = _scratch[firstSlowFrame] * 1000.0;
            return new FrameTimingSnapshot(
                currentFps,
                onePercentLowFps,
                frameTimeP99Ms,
                count,
                elapsedSeconds);
        }
    }

    private int CopyWindow(long endSequence, out double elapsedSeconds)
    {
        int available = (int)Math.Min(endSequence, _samples.Length);
        int count = 0;
        elapsedSeconds = 0;
        for (int offset = 1; offset <= available; offset++)
        {
            long sequence = endSequence - offset;
            int index = (int)(sequence % _samples.Length);
            double sample = Volatile.Read(ref _samples[index]);
            if (sample <= 0 || double.IsNaN(sample) || double.IsInfinity(sample)) continue;

            _scratch[count++] = sample;
            elapsedSeconds += sample;
            if (elapsedSeconds >= _windowSeconds) break;
        }

        return count;
    }
}
