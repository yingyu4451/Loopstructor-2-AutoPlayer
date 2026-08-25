using Loopstructor.AutoPlayer.Core;
using Newtonsoft.Json.Linq;

namespace Loopstructor.AutoPlayer.Tests;

public sealed class RailRebuildTransactionPlannerTests
{
    private readonly RailRebuildTransactionPlanner _planner = new();

    [Fact]
    public void CaptureBuildsOriginDisconnectAndPlayerDrawActions()
    {
        JObject rails = Result(new
        {
            rails = new[]
            {
                new
                {
                    instanceId = 701,
                    railInternalId = 71,
                    isLegalPlayerLoop = true,
                    loopCycleSeconds = 8d,
                    trainIds = new[] { 2 },
                    orderedStations = new[]
                    {
                        new { linePointInstanceId = 11, isAttribute = true, grid = new { x = 0, y = 2 } },
                        new { linePointInstanceId = 12, isAttribute = false, grid = new { x = -2, y = -1 } },
                        new { linePointInstanceId = 13, isAttribute = false, grid = new { x = 2, y = -1 } }
                    }
                }
            }
        });
        JObject trains = Result(new
        {
            trains = new[]
            {
                new
                {
                    index = 2,
                    vehicles = new[]
                    {
                        new { instanceId = 101, vehicleId = 1001 },
                        new { instanceId = 102, vehicleId = 1002 }
                    }
                }
            }
        });

        RailRebuildSnapshot snapshot = Assert.IsType<RailRebuildSnapshot>(
            _planner.Capture(rails, 701, trains));
        Assert.Equal(new[] { 11, 12, 13 }, snapshot.OrderedLinePointInstanceIds);
        Assert.Equal(new[] { 101, 102 }, snapshot.VehicleInstanceIds);
        Assert.Equal(new[] { 1001, 1002 }, snapshot.VehicleBusinessIds);
        Assert.Equal("deleteLinePoint", _planner.BuildDisconnectAction(snapshot).Command);
        AutomationAction preview = _planner.BuildPreviewAction(snapshot);
        Assert.Equal("previewRailPath", preview.Command);
        Assert.Equal(101, preview.Arguments["vehicleInstanceId"]?.Value<int>());
        Assert.Equal("drawRailPath", _planner.BuildDrawAction(snapshot).Command);
    }

    [Fact]
    public void VerifyDisconnectRequiresNativeTrainCacheAndExactVehicles()
    {
        RailRebuildSnapshot snapshot = new()
        {
            RailInstanceId = 701,
            TrainInstanceIds = new[] { 2 },
            VehicleInstanceIds = new[] { 101, 102 },
            VehicleBusinessIds = new[] { 1001, 1002 }
        };
        JObject result = Result(new
        {
            railDeleted = true,
            statePolluted = false,
            deletionOutcome = new
            {
                trainStashed = true,
                stashedVehicles = new object[]
                {
                    new { vehicleId = 1001, vehicle = new { instanceId = 101 } },
                    new { vehicleId = 1002, vehicle = new { instanceId = 102 } }
                }
            }
        });

        Assert.True(_planner.VerifyDisconnect(result, snapshot).Verified);
        JObject wrong = (JObject)result.DeepClone();
        wrong.SelectToken("data.state.deletionOutcome.stashedVehicles")!.Replace(
            JArray.FromObject(new[] { new { vehicleId = 9999, vehicle = new { instanceId = 999 } } }));
        Assert.False(_planner.VerifyDisconnect(wrong, snapshot).Verified);
        JObject unknown = (JObject)result.DeepClone();
        unknown.SelectToken("data.state.deletionOutcome.stashedVehicles")!.Replace(
            JArray.FromObject(new[] { new { vehicleId = 1001, vehicle = (object?)null } }));
        Assert.False(_planner.VerifyDisconnect(unknown, snapshot).Verified);
    }

    [Fact]
    public void VerifyRestoredRequiresSameStationsAndTrainIdentity()
    {
        RailRebuildSnapshot snapshot = new()
        {
            OriginLinePointInstanceId = 11,
            OrderedLinePointInstanceIds = new[] { 11, 12, 13 },
            TrainInstanceIds = new[] { 2 },
            LoopCycleSeconds = 8d
        };
        JObject rails = Result(new
        {
            rails = new[]
            {
                new
                {
                    isLegalPlayerLoop = true,
                    isLoop = true,
                    loopCycleSeconds = 6d,
                    trainIds = new[] { 2 },
                    orderedStations = new[]
                    {
                        new { linePointInstanceId = 11 },
                        new { linePointInstanceId = 12 },
                        new { linePointInstanceId = 13 }
                    }
                }
            }
        });

        RailRebuildVerification verification = _planner.VerifyRestored(rails, snapshot);
        Assert.True(verification.Verified);
        Assert.True(verification.VehiclesRestored);
        Assert.Equal(6d, verification.LoopCycleSeconds);
    }

    [Fact]
    public void VerifyRestoredWaitsForExactOriginalVehicleIdentity()
    {
        RailRebuildSnapshot snapshot = new()
        {
            OriginLinePointInstanceId = 11,
            OrderedLinePointInstanceIds = new[] { 11, 12, 13 },
            TrainInstanceIds = new[] { 2 },
            VehicleInstanceIds = new[] { 101 },
            VehicleBusinessIds = new[] { 1001 },
            LoopCycleSeconds = 8d
        };
        JObject rails = Result(new
        {
            rails = new[]
            {
                new
                {
                    isLegalPlayerLoop = true,
                    isLoop = true,
                    loopCycleSeconds = 6d,
                    trainIds = new[] { 2 },
                    orderedStations = new[]
                    {
                        new { linePointInstanceId = 11 },
                        new { linePointInstanceId = 12 },
                        new { linePointInstanceId = 13 }
                    }
                }
            }
        });
        JObject trains = Result(new
        {
            trains = new[]
            {
                new { index = 2, vehicles = new[] { new { instanceId = 999, vehicleId = 1001 } } }
            }
        });

        RailRebuildVerification verification = _planner.VerifyRestored(rails, snapshot, trains);

        Assert.False(verification.Verified);
        Assert.True(verification.Pending);
        Assert.Contains("实例身份", verification.Detail);
    }

    [Fact]
    public void BuildSpecialInsertionCandidatesUsesUnlinkedMovableRuntimeSpecialAndPlayerOrder()
    {
        JObject rails = Result(new
        {
            rails = new[]
            {
                new
                {
                    instanceId = 701,
                    railInternalId = 71,
                    isLegalPlayerLoop = true,
                    loopCycleSeconds = 8d,
                    trainIds = new[] { 2 },
                    orderedStations = new[]
                    {
                        new { linePointInstanceId = 11, isAttribute = true, grid = new { x = 0, y = 0 } },
                        new { linePointInstanceId = 12, isAttribute = false, grid = new { x = 3, y = 0 } },
                        new { linePointInstanceId = 13, isAttribute = false, grid = new { x = 0, y = 3 } }
                    }
                }
            }
        });
        JObject trains = Result(new
        {
            trains = new[]
            {
                new { index = 2, vehicles = new[] { new { instanceId = 101, vehicleId = 1001 } } }
            }
        });
        JObject catapults = Result(new
        {
            catapults = new[]
            {
                new
                {
                    linePointInstanceId = 19,
                    active = true,
                    isAttribute = false,
                    isSpecial = true,
                    canMove = true,
                    canUseForNewRail = true,
                    railMembershipCount = 0,
                    grid = new { x = 0, y = -3 }
                }
            }
        });

        IReadOnlyList<RailRebuildSnapshot> candidates =
            _planner.BuildSpecialInsertionCandidates(rails, trains, catapults);

        Assert.Equal(3, candidates.Count);
        Assert.All(candidates, candidate =>
        {
            Assert.Equal(11, candidate.OrderedLinePointInstanceIds[0]);
            Assert.Contains(19, candidate.OrderedLinePointInstanceIds);
            Assert.Equal(4, candidate.OrderedLinePointInstanceIds.Count);
            Assert.Equal(101, candidate.VehicleInstanceIds.Single());
            Assert.Equal(new[] { 11, 12, 13 }, candidate.OriginalOrderedLinePointInstanceIds);
        });
    }

    private static JObject Result(object state) => JObject.FromObject(new
    {
        success = true,
        data = new { state }
    });
}
