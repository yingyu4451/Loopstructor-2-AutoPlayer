using Loopstructor.AutoPlayer.Core;

namespace Loopstructor.AutoPlayer.Tests;

public sealed class FrameTimingSamplerTests
{
    [Fact]
    public void ConstantFrameTime_ReportsCurrentAndOnePercentLowFps()
    {
        FrameTimingSampler sampler = new(1000, 10.0);
        for (int index = 0; index < 600; index++) sampler.Record(1.0 / 60.0);

        FrameTimingSnapshot snapshot = sampler.Snapshot();

        Assert.InRange(snapshot.SampleCount, 599, 600);
        Assert.InRange(snapshot.CurrentFps, 59.99, 60.01);
        Assert.InRange(snapshot.OnePercentLowFps, 59.99, 60.01);
        Assert.InRange(snapshot.FrameTimeP99Ms, 16.66, 16.67);
        Assert.InRange(snapshot.WindowSeconds, 9.98, 10.01);
    }

    [Fact]
    public void OnePercentLow_UsesAverageOfSlowestOnePercentOfFrames()
    {
        FrameTimingSampler sampler = new(1000, 1000.0);
        for (int index = 0; index < 990; index++) sampler.Record(0.01);
        for (int index = 0; index < 10; index++) sampler.Record(0.1);

        FrameTimingSnapshot snapshot = sampler.Snapshot();

        Assert.Equal(1000, snapshot.SampleCount);
        Assert.InRange(snapshot.CurrentFps, 91.74, 91.75);
        Assert.InRange(snapshot.OnePercentLowFps, 9.99, 10.01);
        Assert.InRange(snapshot.FrameTimeP99Ms, 99.99, 100.01);
    }

    [Fact]
    public void FixedCapacity_OverwritesOldestSamples()
    {
        FrameTimingSampler sampler = new(4, 100.0);
        sampler.Record(0.01);
        sampler.Record(0.02);
        sampler.Record(0.03);
        sampler.Record(0.04);
        sampler.Record(0.05);

        FrameTimingSnapshot snapshot = sampler.Snapshot();

        Assert.Equal(4, snapshot.SampleCount);
        Assert.InRange(snapshot.CurrentFps, 28.57, 28.58);
        Assert.InRange(snapshot.OnePercentLowFps, 19.99, 20.01);
    }

    [Fact]
    public void Record_HasNoPerFrameManagedAllocation()
    {
        FrameTimingSampler sampler = new(64, 10.0);
        for (int index = 0; index < 1000; index++) sampler.Record(1.0 / 60.0);

        long before = GC.GetAllocatedBytesForCurrentThread();
        for (int index = 0; index < 10000; index++) sampler.Record(1.0 / 60.0);
        long after = GC.GetAllocatedBytesForCurrentThread();

        Assert.Equal(0, after - before);
    }

    [Fact]
    public void InvalidDurations_AreIgnored()
    {
        FrameTimingSampler sampler = new(8, 10.0);
        sampler.Record(0);
        sampler.Record(-1);
        sampler.Record(double.NaN);
        sampler.Record(double.PositiveInfinity);

        Assert.Equal(0, sampler.Snapshot().SampleCount);
    }
}
