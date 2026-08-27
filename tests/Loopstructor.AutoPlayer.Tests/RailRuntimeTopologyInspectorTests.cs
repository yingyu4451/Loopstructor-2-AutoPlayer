using Loopstructor.AutoPlayer.Core;
using Newtonsoft.Json.Linq;

namespace Loopstructor.AutoPlayer.Tests;

public sealed class RailRuntimeTopologyInspectorTests
{
    [Fact]
    public void Inspect_UsesActualSegmentsInsteadOfGameLoopFlags()
    {
        JObject state = State(
            new[] { Station(1, true, -3, 3), Station(2, false, 3, -3), Station(3, false, -3, -3), Station(4, false, 3, 3) },
            new[] { Line(-3, 3, 3, -3), Line(3, -3, -3, -3), Line(-3, -3, 3, 3), Line(3, 3, -3, 3) });

        RailRuntimeTopologyInspection result = RailRuntimeTopologyInspector.Inspect(state);

        Assert.False(result.AllValid);
        Assert.True(result.Rails[0].Loop.SelfIntersectionCount > 0);
    }

    [Fact]
    public void Inspect_AcceptsExactSimpleRingAndBuildsStableFingerprint()
    {
        JObject state = State(
            new[] { Station(1, true, 0, 3), Station(2, false, 3, 0), Station(3, false, 0, -3), Station(4, false, -3, 0) },
            new[] { Line(0, 3, 3, 0), Line(3, 0, 0, -3), Line(0, -3, -3, 0), Line(-3, 0, 0, 3) });

        RailRuntimeTopologyInspection first = RailRuntimeTopologyInspector.Inspect(state);
        RailRuntimeTopologyInspection second = RailRuntimeTopologyInspector.Inspect(state);

        Assert.True(first.AllValid, first.Detail);
        Assert.Equal(first.Fingerprint, second.Fingerprint);
    }

    [Fact]
    public void Inspect_AcceptsLiveOpeningTriangleWithPointIdZero()
    {
        JObject state = State(
            new[] { Station(1, false, -2, -2), Station(2, true, 3, 0), Station(0, false, -2, 2) },
            new[] { Line(-2, -2, -2, 2), Line(3, 0, -2, -2), Line(-2, 2, 3, 0) });

        RailRuntimeTopologyInspection result = RailRuntimeTopologyInspector.Inspect(state);

        Assert.True(result.AllValid, result.Detail);
        Assert.Contains("0:-2,2", result.Fingerprint);
    }

    [Fact]
    public void Inspect_RejectsDisconnectedLineEndpoint()
    {
        JObject state = State(
            new[] { Station(1, true, 0, 3), Station(2, false, 3, 0), Station(3, false, 0, -3), Station(4, false, -3, 0) },
            new[] { Line(0, 3, 3, 0), Line(3, 0, 0, -3), Line(0, -3, 99, 99), Line(-3, 0, 0, 3) });

        RailRuntimeTopologyInspection result = RailRuntimeTopologyInspector.Inspect(state);

        Assert.False(result.AllValid);
        Assert.Contains("无法映射", result.Detail);
    }

    private static JObject State(IEnumerable<JObject> stations, IEnumerable<JObject> lines) => new()
    {
        ["rails"] = new JArray(new JObject
        {
            ["instanceId"] = 70,
            ["isLoop"] = true,
            ["isLegalPlayerLoop"] = true,
            ["orderedStations"] = new JArray(stations),
            ["lines"] = new JArray(lines)
        })
    };

    private static JObject Station(int id, bool attribute, int x, int y) => new()
    {
        ["pointId"] = id,
        ["linePointInstanceId"] = id + 100,
        ["isAttribute"] = attribute,
        ["grid"] = new JObject { ["x"] = x, ["y"] = y }
    };

    private static JObject Line(int fromX, int fromY, int toX, int toY) => new()
    {
        ["from"] = new JObject { ["x"] = fromX, ["y"] = fromY },
        ["to"] = new JObject { ["x"] = toX, ["y"] = toY }
    };
}
