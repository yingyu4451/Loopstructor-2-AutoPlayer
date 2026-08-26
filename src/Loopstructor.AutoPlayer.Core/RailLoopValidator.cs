using System;
using System.Collections.Generic;
using System.Linq;

namespace Loopstructor.AutoPlayer.Core;

public sealed class RailLoopNode
{
    public int Id { get; set; }
    public bool IsAttribute { get; set; }
    public RailLayoutPoint Point { get; set; }
}

public sealed class RailLoopEdge
{
    public int FromId { get; set; }
    public int ToId { get; set; }
}

public sealed class RailLoopValidationResult
{
    public bool IsValid { get; set; }
    public bool IsSingleCycle { get; set; }
    public bool IsSimpleGeometry { get; set; }
    public bool EncirclesBase { get; set; }
    public bool CoversAllQuadrants { get; set; }
    public bool HasNoLargeBlindArc { get; set; }
    public int SelfIntersectionCount { get; set; }
    public IReadOnlyList<int> OrderedNodeIds { get; set; } = Array.Empty<int>();
    public IReadOnlyList<string> Errors { get; set; } = Array.Empty<string>();
}

/// <summary>
/// Validates the actual player rail graph and its base-relative geometry. A rail is accepted only
/// when every node belongs to one degree-two component and the resulting polygon is a simple ring.
/// </summary>
public static class RailLoopValidator
{
    private const double Epsilon = 0.000001d;

    public static RailLoopValidationResult ValidateOrdered(
        IEnumerable<RailLoopNode>? nodes,
        RailLayoutPoint? basePoint = null)
    {
        RailLoopNode[] source = nodes?.ToArray() ?? Array.Empty<RailLoopNode>();
        RailLoopEdge[] edges = source.Length < 2
            ? Array.Empty<RailLoopEdge>()
            : source.Select((node, index) => new RailLoopEdge
            {
                FromId = node.Id,
                ToId = source[(index + 1) % source.Length].Id
            }).ToArray();
        return Validate(source, edges, basePoint);
    }

    public static RailLoopValidationResult Validate(
        IEnumerable<RailLoopNode>? nodes,
        IEnumerable<RailLoopEdge>? edges,
        RailLayoutPoint? basePoint = null)
    {
        RailLoopNode[] sourceNodes = nodes?.ToArray() ?? Array.Empty<RailLoopNode>();
        RailLoopEdge[] sourceEdges = edges?.ToArray() ?? Array.Empty<RailLoopEdge>();
        RailLayoutPoint origin = basePoint ?? new RailLayoutPoint(0d, 0d);
        List<string> errors = new();

        if (sourceNodes.Length < 3) errors.Add("闭环至少需要三个站点。");
        if (sourceNodes.Any(node => node == null || node.Id == 0 || !IsFinite(node.Point)))
            errors.Add("站点身份或坐标无效。");
        if (sourceNodes.Select(node => node.Id).Distinct().Count() != sourceNodes.Length)
            errors.Add("轨道包含重复站点身份。");
        if (sourceNodes.Select(node => node.Point).Distinct().Count() != sourceNodes.Length)
            errors.Add("多个站点占用了同一坐标。");
        if (sourceNodes.Count(node => node.IsAttribute) != 1)
            errors.Add("闭环必须恰好包含一个始发站。");
        if (sourceNodes.Count(node => !node.IsAttribute) < 2)
            errors.Add("闭环至少需要两个中继站。");

        Dictionary<int, RailLoopNode> nodeById = sourceNodes
            .Where(node => node != null && node.Id != 0)
            .GroupBy(node => node.Id)
            .ToDictionary(group => group.Key, group => group.First());
        Dictionary<int, List<int>> adjacency = nodeById.Keys.ToDictionary(id => id, _ => new List<int>());
        HashSet<(int Left, int Right)> uniqueEdges = new();
        foreach (RailLoopEdge edge in sourceEdges)
        {
            if (edge == null || edge.FromId == edge.ToId)
            {
                errors.Add("轨道包含折返到自身的线段。");
                continue;
            }
            if (!nodeById.ContainsKey(edge.FromId) || !nodeById.ContainsKey(edge.ToId))
            {
                errors.Add("线段端点不属于当前轨道站点集合。");
                continue;
            }

            (int Left, int Right) key = edge.FromId < edge.ToId
                ? (edge.FromId, edge.ToId)
                : (edge.ToId, edge.FromId);
            if (!uniqueEdges.Add(key))
            {
                errors.Add("轨道包含重复线段。");
                continue;
            }
            adjacency[edge.FromId].Add(edge.ToId);
            adjacency[edge.ToId].Add(edge.FromId);
        }

        if (sourceEdges.Length != sourceNodes.Length || uniqueEdges.Count != sourceNodes.Length)
            errors.Add("闭环的线段数必须与站点数一致。");
        if (adjacency.Any(pair => pair.Value.Count != 2))
            errors.Add("闭环中每个站点必须恰好连接两条线段。");

        int startId = sourceNodes.FirstOrDefault(node => node.IsAttribute)?.Id
                      ?? sourceNodes.FirstOrDefault()?.Id
                      ?? 0;
        List<int> ordered = BuildOrderedCycle(startId, adjacency, sourceNodes.Length);
        bool connected = startId != 0 && CountReachable(startId, adjacency) == nodeById.Count;
        bool singleCycle = sourceNodes.Length >= 3 && connected && ordered.Count == sourceNodes.Length &&
                           uniqueEdges.Count == sourceNodes.Length &&
                           adjacency.All(pair => pair.Value.Count == 2);
        if (!connected && sourceNodes.Length > 0) errors.Add("轨道站点没有组成单一连通分量。");
        if (!singleCycle) errors.Add("站点和线段没有形成唯一闭环。");

        RailLayoutPoint[] polygon = ordered
            .Where(nodeById.ContainsKey)
            .Select(id => nodeById[id].Point)
            .ToArray();
        int intersections = singleCycle ? CountSelfIntersections(polygon) : 0;
        bool simple = singleCycle && intersections == 0;
        if (intersections > 0) errors.Add($"轨道存在 {intersections} 处非相邻线段交叉。");
        bool containsBase = simple && ContainsPointStrict(polygon, origin);
        if (simple && !containsBase) errors.Add("实际闭环没有包围基地。");

        int quadrants = polygon
            .Where(point => DistanceSquared(point, origin) > Epsilon)
            .Select(point => Quadrant(point, origin))
            .Distinct()
            .Count();
        bool coversAllQuadrants = quadrants >= 4;
        if (simple && !coversAllQuadrants) errors.Add("闭环没有覆盖基地四个方向。");
        double maxAngularGap = CalculateMaxAngularGap(polygon, origin);
        bool noLargeBlindArc = simple && maxAngularGap <= 90.001d;
        if (simple && !noLargeBlindArc) errors.Add("闭环存在超过 90 度的防御盲区。");

        string[] distinctErrors = errors.Distinct(StringComparer.Ordinal).ToArray();
        return new RailLoopValidationResult
        {
            IsValid = distinctErrors.Length == 0,
            IsSingleCycle = singleCycle,
            IsSimpleGeometry = simple,
            EncirclesBase = containsBase,
            CoversAllQuadrants = coversAllQuadrants,
            HasNoLargeBlindArc = noLargeBlindArc,
            SelfIntersectionCount = intersections,
            OrderedNodeIds = ordered,
            Errors = distinctErrors
        };
    }

    private static List<int> BuildOrderedCycle(
        int startId,
        IReadOnlyDictionary<int, List<int>> adjacency,
        int expectedCount)
    {
        List<int> result = new();
        if (startId == 0 || !adjacency.TryGetValue(startId, out List<int>? startNeighbours) ||
            startNeighbours.Count != 2) return result;

        int previous = 0;
        int current = startId;
        for (int step = 0; step < expectedCount; step++)
        {
            if (result.Contains(current) || !adjacency.TryGetValue(current, out List<int>? neighbours) ||
                neighbours.Count != 2) return new List<int>();
            result.Add(current);
            int next = neighbours[0] == previous ? neighbours[1] : neighbours[0];
            previous = current;
            current = next;
        }
        return current == startId ? result : new List<int>();
    }

    private static int CountReachable(int startId, IReadOnlyDictionary<int, List<int>> adjacency)
    {
        HashSet<int> visited = new();
        Stack<int> pending = new();
        pending.Push(startId);
        while (pending.Count > 0)
        {
            int current = pending.Pop();
            if (!visited.Add(current) || !adjacency.TryGetValue(current, out List<int>? neighbours)) continue;
            foreach (int next in neighbours) pending.Push(next);
        }
        return visited.Count;
    }

    private static int CountSelfIntersections(IReadOnlyList<RailLayoutPoint> polygon)
    {
        int count = 0;
        for (int left = 0; left < polygon.Count; left++)
        {
            int leftNext = (left + 1) % polygon.Count;
            for (int right = left + 1; right < polygon.Count; right++)
            {
                int rightNext = (right + 1) % polygon.Count;
                if (left == right || leftNext == right || rightNext == left) continue;
                if (SegmentsIntersect(polygon[left], polygon[leftNext], polygon[right], polygon[rightNext])) count++;
            }
        }
        return count;
    }

    private static bool SegmentsIntersect(
        RailLayoutPoint a,
        RailLayoutPoint b,
        RailLayoutPoint c,
        RailLayoutPoint d)
    {
        double abC = Cross(a, b, c);
        double abD = Cross(a, b, d);
        double cdA = Cross(c, d, a);
        double cdB = Cross(c, d, b);
        if (((abC > Epsilon && abD < -Epsilon) || (abC < -Epsilon && abD > Epsilon)) &&
            ((cdA > Epsilon && cdB < -Epsilon) || (cdA < -Epsilon && cdB > Epsilon))) return true;
        return (Math.Abs(abC) <= Epsilon && OnSegment(a, b, c)) ||
               (Math.Abs(abD) <= Epsilon && OnSegment(a, b, d)) ||
               (Math.Abs(cdA) <= Epsilon && OnSegment(c, d, a)) ||
               (Math.Abs(cdB) <= Epsilon && OnSegment(c, d, b));
    }

    private static bool ContainsPointStrict(IReadOnlyList<RailLayoutPoint> polygon, RailLayoutPoint point)
    {
        bool inside = false;
        for (int left = 0, right = polygon.Count - 1; left < polygon.Count; right = left++)
        {
            RailLayoutPoint a = polygon[right];
            RailLayoutPoint b = polygon[left];
            if (Math.Abs(Cross(a, b, point)) <= Epsilon && OnSegment(a, b, point)) return false;
            bool crosses = (a.Y > point.Y) != (b.Y > point.Y) &&
                           point.X < (b.X - a.X) * (point.Y - a.Y) / (b.Y - a.Y) + a.X;
            if (crosses) inside = !inside;
        }
        return inside;
    }

    private static bool OnSegment(RailLayoutPoint a, RailLayoutPoint b, RailLayoutPoint point) =>
        point.X >= Math.Min(a.X, b.X) - Epsilon && point.X <= Math.Max(a.X, b.X) + Epsilon &&
        point.Y >= Math.Min(a.Y, b.Y) - Epsilon && point.Y <= Math.Max(a.Y, b.Y) + Epsilon;

    private static double CalculateMaxAngularGap(
        IEnumerable<RailLayoutPoint> points,
        RailLayoutPoint origin)
    {
        double[] angles = points
            .Where(point => DistanceSquared(point, origin) > Epsilon)
            .Select(point =>
            {
                double angle = Math.Atan2(point.Y - origin.Y, point.X - origin.X) * 180d / Math.PI;
                return angle < 0d ? angle + 360d : angle;
            })
            .OrderBy(value => value)
            .ToArray();
        if (angles.Length < 2) return 360d;
        double largest = 360d - angles[angles.Length - 1] + angles[0];
        for (int index = 1; index < angles.Length; index++)
            largest = Math.Max(largest, angles[index] - angles[index - 1]);
        return largest;
    }

    private static int Quadrant(RailLayoutPoint point, RailLayoutPoint origin)
    {
        double angle = Math.Atan2(point.Y - origin.Y, point.X - origin.X);
        if (angle < 0d) angle += Math.PI * 2d;
        return Math.Min(3, (int)Math.Floor(angle / (Math.PI / 2d)));
    }

    private static double DistanceSquared(RailLayoutPoint left, RailLayoutPoint right)
    {
        double x = left.X - right.X;
        double y = left.Y - right.Y;
        return x * x + y * y;
    }

    private static double Cross(RailLayoutPoint origin, RailLayoutPoint left, RailLayoutPoint right) =>
        (left.X - origin.X) * (right.Y - origin.Y) -
        (left.Y - origin.Y) * (right.X - origin.X);

    private static bool IsFinite(RailLayoutPoint point) =>
        !double.IsNaN(point.X) && !double.IsInfinity(point.X) &&
        !double.IsNaN(point.Y) && !double.IsInfinity(point.Y);
}
