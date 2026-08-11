using System;
using System.Collections.Generic;
using System.Linq;

namespace Loopstructor.AutoPlayer.Core;

/// <summary>
/// A grid position used by the rail-layout scorer. The main station is the grid origin in the
/// supported game build, which is also the coordinate space returned by queryCatapults/queryRail.
/// </summary>
public readonly struct RailLayoutPoint : IEquatable<RailLayoutPoint>
{
    public RailLayoutPoint(double x, double y)
    {
        X = x;
        Y = y;
    }

    public double X { get; }
    public double Y { get; }

    public bool Equals(RailLayoutPoint other) =>
        Math.Abs(X - other.X) <= 0.000001d && Math.Abs(Y - other.Y) <= 0.000001d;

    public override bool Equals(object? obj) => obj is RailLayoutPoint other && Equals(other);

    public override int GetHashCode() => unchecked((X.GetHashCode() * 397) ^ Y.GetHashCode());
}

/// <summary>
/// Stable, read-only facts used to compare player-equivalent rail layouts.
/// TriggerRate is N/T: a vehicle fires/receives station effects when it reaches a station, so
/// neither raw station count nor short rail length alone is a sufficient throughput measure.
/// </summary>
public sealed class RailLayoutScore
{
    public bool IsValid { get; set; }
    public bool EncirclesBase { get; set; }
    public int CoveredQuadrants { get; set; }
    public double AngularCoverageDegrees { get; set; }
    public double MaxAngularGapDegrees { get; set; }
    public double AverageRadius { get; set; }
    public double RadiusVariance { get; set; }
    public double LoopLength { get; set; }
    public int StationCount { get; set; }
    public double LoopCycleSeconds { get; set; }
    public double TriggerRate { get; set; }
    public double DefenseUtility { get; set; }

    public bool CoversAllQuadrants => CoveredQuadrants >= 4;
    public bool HasNoLargeBlindArc => IsValid && MaxAngularGapDegrees <= 90.001d;
}

public sealed class RailLoopPointCandidate
{
    public int InstanceId { get; set; }
    public bool IsAttribute { get; set; }
    public RailLayoutPoint Grid { get; set; }
}

public sealed class RailLoopPlan
{
    public IReadOnlyList<int> OrderedPointInstanceIds { get; set; } = Array.Empty<int>();
    public IReadOnlyList<RailLayoutPoint> OrderedPoints { get; set; } = Array.Empty<RailLayoutPoint>();
    public RailLayoutScore Score { get; set; } = new();
}

/// <summary>
/// Scores rail geometry around the main station. Spatial coverage is compared before throughput:
/// a very short loop collapsed onto one side cannot defend enemies arriving from every direction.
/// Once layouts provide the same coverage tier, N/T is the primary tie-breaker.
/// </summary>
public static class RailLayoutStrategyPlanner
{
    private const double Epsilon = 0.000001d;

    public static RailLayoutScore Evaluate(
        IEnumerable<RailLayoutPoint>? points,
        int stationCount,
        double loopCycleSeconds)
    {
        RailLayoutPoint[] source = points?
            .Where(IsFinite)
            .Distinct()
            .ToArray() ?? Array.Empty<RailLayoutPoint>();
        if (source.Length < 3 || stationCount < 1 || !IsPositiveFinite(loopCycleSeconds))
        {
            return new RailLayoutScore();
        }

        RailLayoutPoint[] hull = BuildConvexHull(source);
        double loopLength = CalculateClosedLength(source);
        double maxAngularGap = CalculateMaxAngularGap(source);
        double angularCoverage = Math.Max(0d, 360d - maxAngularGap);
        double[] radii = source.Select(point => Math.Sqrt(RadiusSquared(point))).ToArray();
        double averageRadius = radii.Average();
        double radiusVariance = radii.Average(radius =>
        {
            double delta = radius - averageRadius;
            return delta * delta;
        });
        int coveredQuadrants = source
            .Where(point => RadiusSquared(point) > Epsilon)
            .Select(Quadrant)
            .Distinct()
            .Count();

        return new RailLayoutScore
        {
            IsValid = hull.Length >= 3 && loopLength > Epsilon,
            EncirclesBase = hull.Length >= 3 && ContainsOrigin(hull),
            CoveredQuadrants = coveredQuadrants,
            AngularCoverageDegrees = angularCoverage,
            MaxAngularGapDegrees = maxAngularGap,
            AverageRadius = averageRadius,
            RadiusVariance = radiusVariance,
            LoopLength = loopLength,
            StationCount = stationCount,
            LoopCycleSeconds = loopCycleSeconds,
            TriggerRate = stationCount / loopCycleSeconds,
            DefenseUtility = CalculateDefenseUtility(
                stationCount / loopCycleSeconds,
                coveredQuadrants,
                angularCoverage,
                hull.Length >= 3 && ContainsOrigin(hull))
        };
    }

    /// <summary>
    /// Evaluates geometry before the game provides an exact preview cycle. Perimeter is used only
    /// as a speed-neutral cycle proxy; the command layer must still use previewRailPath before a write.
    /// </summary>
    public static RailLayoutScore EvaluateEstimated(IEnumerable<RailLayoutPoint>? points)
    {
        RailLayoutPoint[] source = points?.Where(IsFinite).Distinct().ToArray()
                                  ?? Array.Empty<RailLayoutPoint>();
        double length = CalculateClosedLength(source);
        return Evaluate(source, source.Length, length);
    }

    /// <summary>
    /// Builds one legal player-loop proposal (exactly one attribute station first, then at least
    /// two common stations). It starts with every available common station in polar order and
    /// removes only a station whose removal improves the hard coverage/N-over-T ordering. This
    /// naturally keeps a compact four-direction ring, while excluding redundant radial outliers.
    /// </summary>
    public static RailLoopPlan? PlanPlayerLoop(IEnumerable<RailLoopPointCandidate>? candidates)
    {
        RailLoopPointCandidate[] source = candidates?
            .Where(candidate => candidate != null && candidate.InstanceId != 0 && IsFinite(candidate.Grid))
            .GroupBy(candidate => candidate.InstanceId)
            .Select(group => group.First())
            .ToArray() ?? Array.Empty<RailLoopPointCandidate>();
        RailLoopPointCandidate[] attributes = source
            .Where(candidate => candidate.IsAttribute)
            .OrderBy(candidate => candidate.InstanceId)
            .ToArray();
        RailLoopPointCandidate[] commons = source
            .Where(candidate => !candidate.IsAttribute)
            .GroupBy(candidate => candidate.Grid)
            .Select(group => group.OrderBy(candidate => candidate.InstanceId).First())
            .OrderBy(candidate => candidate.InstanceId)
            .ToArray();
        if (attributes.Length == 0 || commons.Length < 2)
        {
            return null;
        }

        RailLoopPlan? best = null;
        foreach (RailLoopPointCandidate attribute in attributes)
        {
            List<RailLoopPointCandidate> selected = commons
                .Where(candidate => !candidate.Grid.Equals(attribute.Grid))
                .Concat(new[] { attribute })
                .ToList();
            if (selected.Count < 3)
            {
                continue;
            }

            RailLoopPlan current = BuildLoopPlan(selected, attribute.InstanceId);
            if (!current.Score.IsValid)
            {
                continue;
            }

            while (selected.Count > 3)
            {
                RailLoopPlan? bestRemoval = null;
                int bestRemovedId = 0;
                foreach (RailLoopPointCandidate removable in selected
                             .Where(candidate => !candidate.IsAttribute)
                             .OrderBy(candidate => candidate.InstanceId))
                {
                    RailLoopPlan proposal = BuildLoopPlan(
                        selected.Where(candidate => candidate.InstanceId != removable.InstanceId),
                        attribute.InstanceId);
                    if (!proposal.Score.IsValid || CompareForDefense(proposal.Score, current.Score) >= 0)
                    {
                        continue;
                    }

                    if (bestRemoval == null ||
                        CompareForDefense(proposal.Score, bestRemoval.Score) < 0 ||
                        (CompareForDefense(proposal.Score, bestRemoval.Score) == 0 &&
                         removable.InstanceId < bestRemovedId))
                    {
                        bestRemoval = proposal;
                        bestRemovedId = removable.InstanceId;
                    }
                }

                if (bestRemoval == null)
                {
                    break;
                }

                selected.RemoveAll(candidate => candidate.InstanceId == bestRemovedId);
                current = bestRemoval;
            }

            if (best == null ||
                CompareForDefense(current.Score, best.Score) < 0 ||
                (CompareForDefense(current.Score, best.Score) == 0 &&
                 CompareIdentity(current.OrderedPointInstanceIds, best.OrderedPointInstanceIds) < 0))
            {
                best = current;
            }
        }

        return best;
    }

    /// <summary>
    /// Negative means <paramref name="left"/> is the better all-direction defense layout.
    /// </summary>
    public static int CompareForDefense(RailLayoutScore? left, RailLayoutScore? right)
    {
        if (ReferenceEquals(left, right)) return 0;
        if (left == null) return 1;
        if (right == null) return -1;

        int comparison = CompareCoverage(left, right);
        if (comparison != 0) return comparison;
        comparison = CompareDescending(left.TriggerRate, right.TriggerRate, Epsilon);
        if (comparison != 0) return comparison;
        comparison = CompareAscending(left.LoopCycleSeconds, right.LoopCycleSeconds, Epsilon);
        if (comparison != 0) return comparison;
        comparison = CompareAscending(left.AverageRadius, right.AverageRadius, Epsilon);
        if (comparison != 0) return comparison;
        comparison = CompareAscending(left.RadiusVariance, right.RadiusVariance, 0.001d);
        if (comparison != 0) return comparison;
        comparison = CompareAscending(left.LoopLength, right.LoopLength, Epsilon);
        if (comparison != 0) return comparison;
        comparison = CompareAscending(left.MaxAngularGapDegrees, right.MaxAngularGapDegrees, 0.001d);
        if (comparison != 0) return comparison;
        return CompareDescending(left.DefenseUtility, right.DefenseUtility, Epsilon);
    }

    /// <summary>
    /// Negative means <paramref name="left"/> has the better base-relative spatial coverage.
    /// This comparison deliberately ignores speed so callers can combine it with an exact N/T
    /// value returned by the game instead of estimating speed from geometric length.
    /// </summary>
    public static int CompareCoverage(RailLayoutScore? left, RailLayoutScore? right)
    {
        if (ReferenceEquals(left, right)) return 0;
        if (left == null) return 1;
        if (right == null) return -1;

        int comparison = right.IsValid.CompareTo(left.IsValid);
        if (comparison != 0) return comparison;
        comparison = right.EncirclesBase.CompareTo(left.EncirclesBase);
        if (comparison != 0) return comparison;
        comparison = right.CoversAllQuadrants.CompareTo(left.CoversAllQuadrants);
        if (comparison != 0) return comparison;
        comparison = right.CoveredQuadrants.CompareTo(left.CoveredQuadrants);
        if (comparison != 0) return comparison;
        comparison = right.HasNoLargeBlindArc.CompareTo(left.HasNoLargeBlindArc);
        if (comparison != 0) return comparison;

        // While both layouts still have a blind arc, make every repair step reduce it. Once both
        // layouts pass the 90-degree hard threshold, exact N/T is more important than shaving a
        // few additional degrees from an already defended direction.
        return !left.HasNoLargeBlindArc && !right.HasNoLargeBlindArc
            ? CompareAscending(left.MaxAngularGapDegrees, right.MaxAngularGapDegrees, 0.001d)
            : 0;
    }

    public static bool DoesNotReduceCoverage(RailLayoutScore baseline, RailLayoutScore candidate)
    {
        if (!baseline.IsValid || !candidate.IsValid) return false;
        return CompareCoverage(candidate, baseline) <= 0;
    }

    public static bool IsStrictDefenseImprovement(RailLayoutScore baseline, RailLayoutScore candidate)
    {
        if (!baseline.IsValid || !candidate.IsValid) return false;
        int coverage = CompareCoverage(candidate, baseline);
        if (coverage < 0) return true;
        if (coverage > 0) return false;
        return CompareForDefense(candidate, baseline) < 0;
    }

    private static RailLoopPlan BuildLoopPlan(
        IEnumerable<RailLoopPointCandidate> selected,
        int attributeInstanceId)
    {
        RailLoopPointCandidate[] polar = selected
            .OrderBy(candidate => PolarAngle(candidate.Grid))
            .ThenBy(candidate => RadiusSquared(candidate.Grid))
            .ThenBy(candidate => candidate.InstanceId)
            .ToArray();
        int attributeIndex = Array.FindIndex(polar, candidate => candidate.InstanceId == attributeInstanceId);
        RailLoopPointCandidate[] ordered = attributeIndex <= 0
            ? polar
            : polar.Skip(attributeIndex).Concat(polar.Take(attributeIndex)).ToArray();
        RailLayoutPoint[] grids = ordered.Select(candidate => candidate.Grid).ToArray();
        return new RailLoopPlan
        {
            OrderedPointInstanceIds = ordered.Select(candidate => candidate.InstanceId).ToArray(),
            OrderedPoints = grids,
            Score = EvaluateEstimated(grids)
        };
    }

    private static int CompareIdentity(IReadOnlyList<int> left, IReadOnlyList<int> right)
    {
        int count = Math.Min(left.Count, right.Count);
        for (int index = 0; index < count; index++)
        {
            int comparison = left[index].CompareTo(right[index]);
            if (comparison != 0) return comparison;
        }
        return left.Count.CompareTo(right.Count);
    }

    private static RailLayoutPoint[] BuildConvexHull(IEnumerable<RailLayoutPoint> points)
    {
        RailLayoutPoint[] sorted = points
            .Distinct()
            .OrderBy(point => point.X)
            .ThenBy(point => point.Y)
            .ToArray();
        if (sorted.Length <= 2) return sorted;

        List<RailLayoutPoint> lower = new();
        foreach (RailLayoutPoint point in sorted)
        {
            while (lower.Count >= 2 && Cross(lower[lower.Count - 2], lower[lower.Count - 1], point) <= Epsilon)
            {
                lower.RemoveAt(lower.Count - 1);
            }
            lower.Add(point);
        }

        List<RailLayoutPoint> upper = new();
        for (int index = sorted.Length - 1; index >= 0; index--)
        {
            RailLayoutPoint point = sorted[index];
            while (upper.Count >= 2 && Cross(upper[upper.Count - 2], upper[upper.Count - 1], point) <= Epsilon)
            {
                upper.RemoveAt(upper.Count - 1);
            }
            upper.Add(point);
        }

        lower.RemoveAt(lower.Count - 1);
        upper.RemoveAt(upper.Count - 1);
        lower.AddRange(upper);
        return lower.ToArray();
    }

    private static bool ContainsOrigin(IReadOnlyList<RailLayoutPoint> convexHull)
    {
        bool hasPositive = false;
        bool hasNegative = false;
        for (int index = 0; index < convexHull.Count; index++)
        {
            RailLayoutPoint from = convexHull[index];
            RailLayoutPoint to = convexHull[(index + 1) % convexHull.Count];
            double cross = Cross(from, to, new RailLayoutPoint(0d, 0d));
            // Touching a station or a rail edge is not four-direction coverage. The base must be
            // strictly inside the loop so a loop collapsed onto an axis cannot receive the same
            // score as a loop that genuinely surrounds it.
            if (Math.Abs(cross) <= Epsilon) return false;
            hasPositive |= cross > Epsilon;
            hasNegative |= cross < -Epsilon;
            if (hasPositive && hasNegative) return false;
        }
        return hasPositive || hasNegative;
    }

    private static double CalculateMaxAngularGap(IReadOnlyCollection<RailLayoutPoint> points)
    {
        double[] angles = points
            .Where(point => RadiusSquared(point) > Epsilon)
            .Select(point =>
            {
                double angle = Math.Atan2(point.Y, point.X) * 180d / Math.PI;
                return angle < 0d ? angle + 360d : angle;
            })
            .OrderBy(value => value)
            .ToArray();
        if (angles.Length < 2) return 360d;

        double largestGap = 360d - angles[angles.Length - 1] + angles[0];
        for (int index = 1; index < angles.Length; index++)
        {
            largestGap = Math.Max(largestGap, angles[index] - angles[index - 1]);
        }
        return largestGap;
    }

    private static double CalculateClosedLength(IReadOnlyList<RailLayoutPoint> points)
    {
        if (points.Count < 2) return 0d;
        double length = 0d;
        for (int index = 0; index < points.Count; index++)
        {
            RailLayoutPoint from = points[index];
            RailLayoutPoint to = points[(index + 1) % points.Count];
            double x = from.X - to.X;
            double y = from.Y - to.Y;
            length += Math.Sqrt(x * x + y * y);
        }
        return length;
    }

    private static int Quadrant(RailLayoutPoint point)
    {
        double angle = Math.Atan2(point.Y, point.X);
        if (angle < 0d) angle += Math.PI * 2d;
        return Math.Min(3, (int)Math.Floor(angle / (Math.PI / 2d)));
    }

    private static double RadiusSquared(RailLayoutPoint point) => point.X * point.X + point.Y * point.Y;

    private static double PolarAngle(RailLayoutPoint point)
    {
        double angle = Math.Atan2(point.Y, point.X);
        return angle < 0d ? angle + Math.PI * 2d : angle;
    }

    private static int CompareAscending(double left, double right, double tolerance)
    {
        if (Math.Abs(left - right) <= tolerance) return 0;
        return left < right ? -1 : 1;
    }

    private static int CompareDescending(double left, double right, double tolerance) =>
        CompareAscending(right, left, tolerance);

    private static double CalculateDefenseUtility(
        double triggerRate,
        int coveredQuadrants,
        double angularCoverageDegrees,
        bool encirclesBase)
    {
        // Coverage is deliberately a bounded multiplier over N/T. It is strong enough to reject a
        // compact one-sided cluster when a reasonably short enclosing loop exists, but cannot make
        // an absurdly long, low-frequency loop win merely because it crosses every direction.
        double coverageMultiplier = 1d +
                                    Math.Max(coveredQuadrants - 1, 0) * 0.35d +
                                    angularCoverageDegrees / 360d +
                                    (encirclesBase ? 1.5d : 0d);
        return triggerRate * coverageMultiplier;
    }

    private static double Cross(RailLayoutPoint origin, RailLayoutPoint left, RailLayoutPoint right) =>
        (left.X - origin.X) * (right.Y - origin.Y) -
        (left.Y - origin.Y) * (right.X - origin.X);

    private static bool IsFinite(RailLayoutPoint point) =>
        !double.IsNaN(point.X) && !double.IsInfinity(point.X) &&
        !double.IsNaN(point.Y) && !double.IsInfinity(point.Y);

    private static bool IsPositiveFinite(double value) =>
        value > Epsilon && !double.IsNaN(value) && !double.IsInfinity(value);
}
