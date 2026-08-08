using Loopstructor.AutoPlayer.Core;
using Newtonsoft.Json.Linq;

namespace Loopstructor.AutoPlayer.Tests;

public sealed class IncrementalGridProbePlanningTests
{
    [Fact]
    public void ExpansionRankerMatchesExistingSelectionPolicy()
    {
        JObject catapults = Result(new
        {
            catapults = new object[]
            {
                Catapult(201, false, 0, 0),
                Catapult(202, false, 10, 0),
                Catapult(203, false, 5, 20, railMembershipCount: 1)
            }
        });
        AutoPlayerGrid[] candidates =
        {
            new(0, 10),
            new(5, 0),
            new(5, 1),
            new(0, 10)
        };

        IReadOnlyList<AutoPlayerGrid> ranked =
            DefenseExpansionAttributeGridRanker.Rank(candidates, catapults);
        JObject options = Result(new
        {
            disposableEnum = "FreePoint_Attribute",
            validGrids = candidates.Select(candidate => new
            {
                grid = new { x = candidate.X, y = candidate.Y }
            })
        });
        JObject? selected = new BattleDecisionEngine().SelectExpansionAttributeGrid(options, catapults);

        Assert.NotEmpty(ranked);
        Assert.NotNull(selected);
        Assert.Equal(selected["x"]?.Value<int>(), ranked[0].X);
        Assert.Equal(selected["y"]?.Value<int>(), ranked[0].Y);
        Assert.Equal(new AutoPlayerGrid(5, 1), ranked[0]);
        Assert.DoesNotContain(new AutoPlayerGrid(5, 0), ranked);
        Assert.Equal(ranked.Count, ranked.Distinct().Count());
    }

    [Fact]
    public void ExpansionRankerPreservesOppositeSideCoverageTieBreak()
    {
        JObject catapults = Result(new
        {
            catapults = new object[]
            {
                Catapult(10, true, -10, 0, railMembershipCount: 1),
                Catapult(11, false, -11, -1, railMembershipCount: 1),
                Catapult(12, false, -11, 1, railMembershipCount: 1),
                Catapult(100, false, 5, -1),
                Catapult(101, false, 5, 2)
            }
        });
        AutoPlayerGrid[] candidates = { new(100, -1), new(4, 0) };

        IReadOnlyList<AutoPlayerGrid> ranked =
            DefenseExpansionAttributeGridRanker.Rank(candidates.Reverse(), catapults);

        Assert.Equal(new AutoPlayerGrid(4, 0), ranked[0]);
    }

    [Fact]
    public void ExpansionRankerMatchesExistingSelectorAcrossDeterministicLayouts()
    {
        Random random = new(4451);
        BattleDecisionEngine engine = new();
        for (int scenario = 0; scenario < 80; scenario++)
        {
            List<object> catapultItems = new();
            int commonCount = random.Next(2, 6);
            for (int index = 0; index < commonCount; index++)
            {
                catapultItems.Add(Catapult(
                    100 + index,
                    false,
                    random.Next(-12, 13),
                    random.Next(-12, 13)));
            }

            int occupiedCount = random.Next(0, 4);
            for (int index = 0; index < occupiedCount; index++)
            {
                catapultItems.Add(Catapult(
                    300 + index,
                    index == 0,
                    random.Next(-12, 13),
                    random.Next(-12, 13),
                    railMembershipCount: 1));
            }

            AutoPlayerGrid[] candidates = Enumerable.Range(0, random.Next(2, 12))
                .Select(_ => new AutoPlayerGrid(random.Next(-16, 17), random.Next(-16, 17)))
                .ToArray();
            JObject catapults = Result(new { catapults = catapultItems });
            JObject options = Result(new
            {
                disposableEnum = "FreePoint_Attribute",
                validGrids = candidates.Select(candidate => new
                {
                    grid = new { x = candidate.X, y = candidate.Y }
                })
            });

            IReadOnlyList<AutoPlayerGrid> ranked =
                DefenseExpansionAttributeGridRanker.Rank(candidates, catapults);
            JObject? selected = engine.SelectExpansionAttributeGrid(options, catapults);

            if (selected == null)
            {
                Assert.Empty(ranked);
            }
            else
            {
                Assert.NotEmpty(ranked);
                Assert.Equal(selected["x"]?.Value<int>(), ranked[0].X);
                Assert.Equal(selected["y"]?.Value<int>(), ranked[0].Y);
            }
        }
    }

    [Fact]
    public void ExpansionRankerFailsOpenWithNoUsableLayout()
    {
        JObject onlyOneCommonPoint = Result(new
        {
            catapults = new object[] { Catapult(201, false, 0, 0) }
        });

        IReadOnlyList<AutoPlayerGrid> ranked = DefenseExpansionAttributeGridRanker.Rank(
            new[] { new AutoPlayerGrid(2, 2) },
            onlyOneCommonPoint);

        Assert.Empty(ranked);
    }

    [Fact]
    public void BattleRankerOrdersUniqueCandidatesByThreatDistance()
    {
        AutoPlayerGrid[] candidates =
        {
            new(12, 10),
            new(11, 10),
            new(9, 10),
            new(11, 10),
            new(10, 13)
        };

        IReadOnlyList<AutoPlayerGrid> ranked = BattleDisposableGridRanker.Rank(
            candidates,
            new AutoPlayerGrid(10, 10));

        Assert.Equal(
            new[]
            {
                new AutoPlayerGrid(9, 10),
                new AutoPlayerGrid(11, 10),
                new AutoPlayerGrid(12, 10),
                new AutoPlayerGrid(10, 13)
            },
            ranked);
    }

    [Fact]
    public void ProbeResultNormalizesNegativeProgressAndNullDetail()
    {
        IncrementalGridProbeResult result = new(
            IncrementalGridProbeStatus.Unavailable,
            totalProbed: -1,
            detail: null!);

        Assert.Equal(IncrementalGridProbeStatus.Unavailable, result.Status);
        Assert.Equal(0, result.TotalProbed);
        Assert.Equal(string.Empty, result.Detail);
    }

    private static JObject Result(object state) => JObject.FromObject(new
    {
        success = true,
        data = new { state }
    });

    private static object Catapult(
        int instanceId,
        bool isAttribute,
        int x,
        int y,
        int railMembershipCount = 0) => new
    {
        active = true,
        canUseForNewRail = railMembershipCount == 0,
        canPickLine = true,
        frozen = false,
        railReachMax = false,
        isAttribute,
        linePointInstanceId = instanceId,
        railMembershipCount,
        grid = new { x, y }
    };
}
