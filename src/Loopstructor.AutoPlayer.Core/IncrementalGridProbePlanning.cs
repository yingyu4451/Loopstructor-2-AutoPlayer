using System;
using System.Collections.Generic;
using System.Linq;
using Newtonsoft.Json.Linq;

namespace Loopstructor.AutoPlayer.Core;

/// <summary>
/// A game-grid coordinate shared by incremental runtime probes without taking a Unity dependency.
/// </summary>
public readonly struct AutoPlayerGrid : IEquatable<AutoPlayerGrid>
{
    public AutoPlayerGrid(int x, int y)
    {
        X = x;
        Y = y;
    }

    public int X { get; }
    public int Y { get; }

    public bool Equals(AutoPlayerGrid other) => X == other.X && Y == other.Y;
    public override bool Equals(object? obj) => obj is AutoPlayerGrid other && Equals(other);
    public override int GetHashCode() => unchecked((X * 397) ^ Y);
    public override string ToString() => $"({X},{Y})";
}

public enum IncrementalGridProbeStatus
{
    Probing,
    Found,
    Exhausted,
    Unavailable
}

public sealed class IncrementalGridProbeResult
{
    public IncrementalGridProbeResult(
        IncrementalGridProbeStatus status,
        AutoPlayerGrid? grid = null,
        int totalProbed = 0,
        string detail = "")
    {
        Status = status;
        Grid = grid;
        TotalProbed = Math.Max(0, totalProbed);
        Detail = detail ?? string.Empty;
    }

    public IncrementalGridProbeStatus Status { get; }
    public AutoPlayerGrid? Grid { get; }
    public int TotalProbed { get; }
    public string Detail { get; }
}

public interface IDefenseExpansionAttributeGridProbe
{
    bool TryInitialize(JObject? catapultResult, out string error);
    IncrementalGridProbeResult ProbeNext();
    void Reset();
}

public interface IBattleLiveDisposableGridProbe
{
    bool TryInitialize(double threatWorldX, double threatWorldY, double threatWorldZ, out string error);
    IncrementalGridProbeResult ProbeNext();
    void Reset();
}

/// <summary>
/// Orders unvalidated attribute-station candidates with the same layout tuple used by
/// BattleDecisionEngine.SelectExpansionAttributeGrid.
/// </summary>
public static class DefenseExpansionAttributeGridRanker
{
    private const double MinimumArea = 0.000001d;
    private const double MaximumReasonableLoopLengthRatio = 8d;

    public static IReadOnlyList<AutoPlayerGrid> Rank(
        IEnumerable<AutoPlayerGrid>? candidates,
        JObject? catapultResult)
    {
        JObject state = State(catapultResult);
        List<JObject> allPoints = (state["catapults"] as JArray)?
            .OfType<JObject>()
            .ToList() ?? new List<JObject>();
        List<GridPoint> commonPoints = allPoints
            .Where(IsAvailableExpansionPoint)
            .Where(item => item["isAttribute"]?.Value<bool>() != true)
            .Select(item => TryReadGrid(item["grid"], out GridPoint point) ? point : (GridPoint?)null)
            .Where(point => point.HasValue)
            .Select(point => point!.Value)
            .ToList();
        if (commonPoints.Count < 2)
        {
            return Array.Empty<AutoPlayerGrid>();
        }

        List<GridPoint> occupiedPoints = allPoints
            .Where(item => ReadInt(item["railMembershipCount"], 0) > 0)
            .Select(item => TryReadGrid(item["grid"], out GridPoint point) ? point : (GridPoint?)null)
            .Where(point => point.HasValue)
            .Select(point => point!.Value)
            .ToList();
        bool hasOccupiedCentroid = occupiedPoints.Count > 0;
        double occupiedX = hasOccupiedCentroid ? occupiedPoints.Average(point => point.X) : 0d;
        double occupiedY = hasOccupiedCentroid ? occupiedPoints.Average(point => point.Y) : 0d;

        List<RankedGrid> ranked = new();
        foreach (AutoPlayerGrid candidate in candidates?.Distinct() ?? Enumerable.Empty<AutoPlayerGrid>())
        {
            for (int first = 0; first < commonPoints.Count - 1; first++)
            {
                for (int second = first + 1; second < commonPoints.Count; second++)
                {
                    LayoutScore score = Score(
                        candidate,
                        commonPoints[first],
                        commonPoints[second],
                        hasOccupiedCentroid,
                        occupiedX,
                        occupiedY);
                    if (score.Area <= MinimumArea)
                    {
                        continue;
                    }

                    ranked.Add(new RankedGrid(candidate, score));
                }
            }
        }

        double reasonableLoopLengthLimit = CalculateReasonableLoopLengthLimit(
            ranked.Select(item => item.Score.Layout));
        return ranked
            .Where(item => item.Score.Layout.LoopLength <= reasonableLoopLengthLimit)
            .OrderBy(item => item, RankedGridComparer.Instance)
            .Select(item => item.Grid)
            .Distinct()
            .ToArray();
    }

    internal static double CalculateReasonableLoopLengthLimit(
        IEnumerable<RailLayoutScore>? layouts)
    {
        double shortest = layouts?
            .Where(layout => layout?.IsValid == true &&
                             layout.LoopLength > MinimumArea &&
                             !double.IsNaN(layout.LoopLength) &&
                             !double.IsInfinity(layout.LoopLength))
            .Select(layout => layout.LoopLength)
            .DefaultIfEmpty(double.PositiveInfinity)
            .Min() ?? double.PositiveInfinity;
        return double.IsInfinity(shortest)
            ? double.PositiveInfinity
            : shortest * MaximumReasonableLoopLengthRatio;
    }

    private static LayoutScore Score(
        AutoPlayerGrid attribute,
        GridPoint first,
        GridPoint second,
        bool hasOccupiedCentroid,
        double occupiedX,
        double occupiedY)
    {
        double distance = DistanceSquared(first.X, first.Y, attribute.X, attribute.Y)
                          + DistanceSquared(second.X, second.Y, attribute.X, attribute.Y)
                          + DistanceSquared(first.X, first.Y, second.X, second.Y);
        double area = Math.Abs(
            (first.X - attribute.X) * (second.Y - attribute.Y)
            - (first.Y - attribute.Y) * (second.X - attribute.X));
        RailLayoutScore layout = RailLayoutStrategyPlanner.EvaluateEstimated(new[]
        {
            new RailLayoutPoint(attribute.X, attribute.Y),
            new RailLayoutPoint(first.X, first.Y),
            new RailLayoutPoint(second.X, second.Y)
        });
        if (!hasOccupiedCentroid)
        {
            return new LayoutScore(layout, false, 0, 0d, distance, area);
        }

        double candidateX = (attribute.X + first.X + second.X) / 3d;
        double candidateY = (attribute.Y + first.Y + second.Y) / 3d;
        double occupiedMagnitude = Math.Sqrt(occupiedX * occupiedX + occupiedY * occupiedY);
        double candidateMagnitude = Math.Sqrt(candidateX * candidateX + candidateY * candidateY);
        if (occupiedMagnitude <= MinimumArea || candidateMagnitude <= MinimumArea)
        {
            return new LayoutScore(layout, false, 0, 0d, distance, area);
        }

        double cosine = (occupiedX * candidateX + occupiedY * candidateY)
                        / (occupiedMagnitude * candidateMagnitude);
        return new LayoutScore(layout, true, cosine <= 0d ? 0 : 1, cosine, distance, area);
    }

    private static bool IsAvailableExpansionPoint(JObject point) =>
        point["active"]?.Value<bool>() != false
        && point["canUseForNewRail"]?.Value<bool>() == true
        && point["canPickLine"]?.Value<bool>() != false
        && point["frozen"]?.Value<bool>() != true
        && point["railReachMax"]?.Value<bool>() != true
        && ReadInt(point["railMembershipCount"], 0) == 0
        && ReadInt(point["linePointInstanceId"], 0) != 0;

    private static bool TryReadGrid(JToken? token, out GridPoint point)
    {
        point = default;
        if (token is not JObject value ||
            !TryReadDouble(value["x"], out double x) ||
            !TryReadDouble(value["y"], out double y))
        {
            return false;
        }

        point = new GridPoint(x, y);
        return true;
    }

    private static bool TryReadDouble(JToken? token, out double value)
    {
        value = 0d;
        if (token == null || token.Type == JTokenType.Null)
        {
            return false;
        }

        if (token.Type is JTokenType.Integer or JTokenType.Float)
        {
            value = token.Value<double>();
            return true;
        }

        return double.TryParse(
            token.Value<string>(),
            System.Globalization.NumberStyles.Float,
            System.Globalization.CultureInfo.InvariantCulture,
            out value);
    }

    private static int ReadInt(JToken? token, int fallback)
    {
        if (token == null || token.Type == JTokenType.Null)
        {
            return fallback;
        }

        return token.Type == JTokenType.Integer
            ? token.Value<int>()
            : int.TryParse(token.Value<string>(), out int parsed) ? parsed : fallback;
    }

    private static JObject State(JObject? result) =>
        result?.SelectToken("data.state") as JObject
        ?? result?["state"] as JObject
        ?? result
        ?? new JObject();

    private static double DistanceSquared(double leftX, double leftY, double rightX, double rightY)
    {
        double x = leftX - rightX;
        double y = leftY - rightY;
        return x * x + y * y;
    }

    private readonly struct GridPoint
    {
        public GridPoint(double x, double y)
        {
            X = x;
            Y = y;
        }

        public double X { get; }
        public double Y { get; }
    }

    private readonly struct LayoutScore
    {
        public LayoutScore(
            RailLayoutScore layout,
            bool hasCoverageContext,
            int sideRank,
            double directionCosine,
            double distance,
            double area)
        {
            Layout = layout;
            HasCoverageContext = hasCoverageContext;
            SideRank = sideRank;
            DirectionCosine = directionCosine;
            Distance = distance;
            Area = area;
        }

        public RailLayoutScore Layout { get; }
        public bool HasCoverageContext { get; }
        public int SideRank { get; }
        public double DirectionCosine { get; }
        public double Distance { get; }
        public double Area { get; }
    }

    private readonly struct RankedGrid
    {
        public RankedGrid(AutoPlayerGrid grid, LayoutScore score)
        {
            Grid = grid;
            Score = score;
        }

        public AutoPlayerGrid Grid { get; }
        public LayoutScore Score { get; }
    }

    private sealed class LayoutScoreComparer : IComparer<LayoutScore>
    {
        public static LayoutScoreComparer Instance { get; } = new();

        public int Compare(LayoutScore left, LayoutScore right)
        {
            int comparison = RailLayoutStrategyPlanner.CompareForDefense(left.Layout, right.Layout);
            if (comparison != 0) return comparison;
            comparison = (left.HasCoverageContext ? left.SideRank : 0)
                .CompareTo(right.HasCoverageContext ? right.SideRank : 0);
            if (comparison != 0) return comparison;
            comparison = (left.HasCoverageContext ? left.DirectionCosine : 0d)
                .CompareTo(right.HasCoverageContext ? right.DirectionCosine : 0d);
            if (comparison != 0) return comparison;
            comparison = left.Distance.CompareTo(right.Distance);
            if (comparison != 0) return comparison;
            return right.Area.CompareTo(left.Area);
        }
    }

    private sealed class RankedGridComparer : IComparer<RankedGrid>
    {
        public static RankedGridComparer Instance { get; } = new();

        public int Compare(RankedGrid left, RankedGrid right)
        {
            int comparison = LayoutScoreComparer.Instance.Compare(left.Score, right.Score);
            if (comparison != 0) return comparison;
            comparison = left.Grid.X.CompareTo(right.Grid.X);
            return comparison != 0 ? comparison : left.Grid.Y.CompareTo(right.Grid.Y);
        }
    }
}

/// <summary>
/// Orders live disposable candidates from nearest to farthest from the current threat grid.
/// </summary>
public static class BattleDisposableGridRanker
{
    public static IReadOnlyList<AutoPlayerGrid> Rank(
        IEnumerable<AutoPlayerGrid>? candidates,
        AutoPlayerGrid threatGrid) =>
        (candidates ?? Enumerable.Empty<AutoPlayerGrid>())
        .Distinct()
        .OrderBy(candidate => DistanceSquared(candidate, threatGrid))
        .ThenBy(candidate => candidate.X)
        .ThenBy(candidate => candidate.Y)
        .ToArray();

    private static long DistanceSquared(AutoPlayerGrid candidate, AutoPlayerGrid threat)
    {
        long x = (long)candidate.X - threat.X;
        long y = (long)candidate.Y - threat.Y;
        return x * x + y * y;
    }
}
