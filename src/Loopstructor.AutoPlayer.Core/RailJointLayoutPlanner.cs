using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;

namespace Loopstructor.AutoPlayer.Core;

public enum RailJointLayoutSearchStatus
{
    Probing,
    Found,
    Exhausted
}

public sealed class RailJointMoveTarget
{
    public RailStationMoveCandidate Candidate { get; set; } = new();
    public int StablePointId => Candidate.StationPointId;
    public AutoPlayerGrid OriginalGrid { get; set; }
    public AutoPlayerGrid TargetGrid { get; set; }
}

public sealed class RailJointLayoutPlan
{
    public int RailInstanceId { get; set; }
    public int RailInternalId { get; set; }
    public double OriginalLoopCycleSeconds { get; set; }
    public double PredictedLoopCycleSeconds { get; set; }
    public double PredictedTriggerRate { get; set; }
    public RailLayoutScore BaselineScore { get; set; } = new();
    public RailLayoutScore PredictedScore { get; set; } = new();
    public IReadOnlyList<RailJointMoveTarget> Targets { get; set; } = Array.Empty<RailJointMoveTarget>();
    public IReadOnlyList<AutoPlayerGrid> OrderedTargetGrids { get; set; } = Array.Empty<AutoPlayerGrid>();
    public IReadOnlyList<int> OrderedStablePointIds { get; set; } = Array.Empty<int>();
    public bool RequiresReconnect { get; set; }
    public int EvaluatedLayoutCount { get; set; }
}

public sealed class RailJointLayoutSearchResult
{
    public RailJointLayoutSearchStatus Status { get; set; }
    public RailJointLayoutPlan? Plan { get; set; }
    public int EvaluatedLayoutCount { get; set; }
    public double SliceMilliseconds { get; set; }
    public string Detail { get; set; } = string.Empty;
}

/// <summary>
/// Incremental whole-loop station planner. One or two movable stations are exhaustively combined;
/// larger sets use a deterministic width-512 beam. Every probe slice is bounded by wall-clock time.
/// </summary>
public sealed class RailJointLayoutSearch
{
    public const int BeamWidth = 512;
    public const double DefaultSliceBudgetMilliseconds = 3d;

    private readonly List<RailSearchState> _rails = new();
    private int _railIndex;
    private int _evaluated;
    private RailJointLayoutPlan? _best;
    private bool _complete;

    public RailJointLayoutSearch(
        IEnumerable<RailStationMoveCandidate>? movableStations,
        IEnumerable<AutoPlayerGrid>? ordinaryCandidates,
        IEnumerable<AutoPlayerGrid>? energyCandidates)
    {
        AutoPlayerGrid[] ordinary = Normalize(ordinaryCandidates);
        AutoPlayerGrid[] energy = Normalize(energyCandidates);
        foreach (IGrouping<int, RailStationMoveCandidate> group in (movableStations ?? Enumerable.Empty<RailStationMoveCandidate>())
                     .Where(IsUsable)
                     .GroupBy(candidate => candidate.RailInstanceId)
                     .OrderBy(group => group.Key))
        {
            RailSearchState? state = RailSearchState.TryCreate(group.ToArray(), ordinary, energy);
            if (state != null) _rails.Add(state);
        }
        _complete = _rails.Count == 0;
    }

    public RailJointLayoutSearchResult ProbeNext(double budgetMilliseconds = DefaultSliceBudgetMilliseconds)
    {
        if (_complete)
        {
            return CompletedResult(0d);
        }

        Stopwatch stopwatch = Stopwatch.StartNew();
        double budget = IsPositiveFinite(budgetMilliseconds)
            ? Math.Min(budgetMilliseconds, DefaultSliceBudgetMilliseconds)
            : DefaultSliceBudgetMilliseconds;
        int unitsThisSlice = 0;
        do
        {
            RailSearchState state = _rails[_railIndex];
            if (state.AdvanceOne(out RailJointLayoutPlan? candidate, out bool evaluatedLayout))
            {
                unitsThisSlice++;
                if (evaluatedLayout) _evaluated++;
                if (candidate != null)
                {
                    candidate.EvaluatedLayoutCount = _evaluated;
                    if (_best == null || ComparePlans(candidate, _best) < 0) _best = candidate;
                }
            }
            if (state.IsComplete)
            {
                _railIndex++;
                if (_railIndex >= _rails.Count)
                {
                    _complete = true;
                    break;
                }
            }
        } while (unitsThisSlice == 0 || stopwatch.Elapsed.TotalMilliseconds < budget);

        if (_complete) return CompletedResult(stopwatch.Elapsed.TotalMilliseconds);
        return new RailJointLayoutSearchResult
        {
            Status = RailJointLayoutSearchStatus.Probing,
            EvaluatedLayoutCount = _evaluated,
            SliceMilliseconds = stopwatch.Elapsed.TotalMilliseconds,
            Detail = $"本帧联合评估后累计检查 {_evaluated} 个完整/束搜索布局，用时 {stopwatch.Elapsed.TotalMilliseconds:0.###} ms。"
        };
    }

    public static RailJointLayoutPlan? FindBest(
        IEnumerable<RailStationMoveCandidate>? movableStations,
        IEnumerable<AutoPlayerGrid>? ordinaryCandidates,
        IEnumerable<AutoPlayerGrid>? energyCandidates)
    {
        RailJointLayoutSearch search = new(movableStations, ordinaryCandidates, energyCandidates);
        RailJointLayoutSearchResult result;
        do result = search.ProbeNext(DefaultSliceBudgetMilliseconds);
        while (result.Status == RailJointLayoutSearchStatus.Probing);
        return result.Plan;
    }

    private RailJointLayoutSearchResult CompletedResult(double elapsed)
    {
        return new RailJointLayoutSearchResult
        {
            Status = _best == null ? RailJointLayoutSearchStatus.Exhausted : RailJointLayoutSearchStatus.Found,
            Plan = _best,
            EvaluatedLayoutCount = _evaluated,
            SliceMilliseconds = elapsed,
            Detail = _best == null
                ? $"已检查 {_evaluated} 个联合布局，没有满足全向防御且严格改善的最终方案。"
                : $"已检查 {_evaluated} 个联合布局，一次确定 {_best.Targets.Count} 个站点的最终位置。"
        };
    }

    private static int ComparePlans(RailJointLayoutPlan left, RailJointLayoutPlan right)
    {
        int comparison = RailLayoutStrategyPlanner.CompareForDefense(left.PredictedScore, right.PredictedScore);
        if (comparison != 0) return comparison;
        comparison = left.Targets.Count.CompareTo(right.Targets.Count);
        if (comparison != 0) return comparison;
        string leftKey = string.Join(";", left.Targets.Select(target =>
            target.StablePointId + ":" + target.TargetGrid.X + "," + target.TargetGrid.Y));
        string rightKey = string.Join(";", right.Targets.Select(target =>
            target.StablePointId + ":" + target.TargetGrid.X + "," + target.TargetGrid.Y));
        return string.CompareOrdinal(leftKey, rightKey);
    }

    private static bool IsUsable(RailStationMoveCandidate candidate) =>
        candidate != null && candidate.RailInstanceId != 0 &&
        candidate.OrderedStationGrids.Count >= 3 &&
        candidate.OrderedStationGrids.Count == candidate.OrderedStationKinds.Count &&
        candidate.OrderedStationGrids.Count == candidate.OrderedStationPointIds.Count &&
        candidate.OrderedStationPointIds.Contains(candidate.StationPointId) &&
        candidate.SpacingRules.IsKnown && IsPositiveFinite(candidate.CurrentLoopCycleSeconds);

    private static AutoPlayerGrid[] Normalize(IEnumerable<AutoPlayerGrid>? grids) =>
        (grids ?? Enumerable.Empty<AutoPlayerGrid>()).Distinct().OrderBy(grid => grid.X).ThenBy(grid => grid.Y).ToArray();

    private static bool IsPositiveFinite(double value) =>
        value > 0d && !double.IsNaN(value) && !double.IsInfinity(value);

    private sealed class RailSearchState
    {
        private readonly RailStationMoveCandidate[] _stations;
        private readonly IReadOnlyList<AutoPlayerGrid>[] _options;
        private readonly RailLayoutScore _baseline;
        private readonly int[] _indices;
        private readonly bool _useBeam;
        private List<PartialAssignment> _frontier = new() { new PartialAssignment(Array.Empty<AutoPlayerGrid>()) };
        private readonly SortedSet<PartialAssignment> _nextFrontier =
            new(PartialAssignmentComparer.Instance);
        private int _depth;
        private int _frontierIndex;
        private int _optionIndex;
        private List<PartialAssignment>? _completedAssignments;
        private int _completedAssignmentIndex;
        private bool _odometerStarted;

        private RailSearchState(
            RailStationMoveCandidate[] stations,
            IReadOnlyList<AutoPlayerGrid>[] options,
            RailLayoutScore baseline)
        {
            _stations = stations;
            _options = options;
            _baseline = baseline;
            _indices = new int[stations.Length];
            _useBeam = stations.Length > 2;
        }

        public bool IsComplete { get; private set; }

        public static RailSearchState? TryCreate(
            RailStationMoveCandidate[] source,
            IReadOnlyList<AutoPlayerGrid> ordinary,
            IReadOnlyList<AutoPlayerGrid> energy)
        {
            RailStationMoveCandidate canonical = source[0];
            RailStationMoveCandidate[] stations = source
                .GroupBy(candidate => candidate.StationPointId)
                .Select(group => group.First())
                .OrderBy(candidate => candidate.StationPointId)
                .ToArray();
            if (stations.Length == 0 || stations.Any(candidate =>
                    candidate.OrderedStationPointIds.Count != canonical.OrderedStationPointIds.Count ||
                    !candidate.OrderedStationPointIds.SequenceEqual(canonical.OrderedStationPointIds))) return null;

            IReadOnlyList<AutoPlayerGrid>[] options = stations.Select(candidate =>
                (candidate.StationIsAttribute ? energy : ordinary)
                .Concat(new[] { candidate.CurrentGrid })
                .Distinct()
                .OrderBy(grid => grid.Equals(candidate.CurrentGrid) ? 0 : 1)
                .ThenBy(grid => grid.X)
                .ThenBy(grid => grid.Y)
                .ToArray() as IReadOnlyList<AutoPlayerGrid>).ToArray();
            if (options.Any(list => list.Count == 0)) return null;
            RailLayoutScore baseline = Score(canonical, canonical.OrderedStationGrids);
            return new RailSearchState(stations, options, baseline);
        }

        public bool AdvanceOne(out RailJointLayoutPlan? plan, out bool evaluatedLayout)
        {
            plan = null;
            evaluatedLayout = false;
            if (IsComplete) return false;
            if (!_useBeam)
            {
                AutoPlayerGrid[] assignment = new AutoPlayerGrid[_stations.Length];
                for (int index = 0; index < assignment.Length; index++) assignment[index] = _options[index][_indices[index]];
                plan = Evaluate(assignment);
                evaluatedLayout = true;
                AdvanceOdometer();
                return true;
            }

            if (_completedAssignments != null)
            {
                plan = Evaluate(_completedAssignments[_completedAssignmentIndex++].Grids);
                evaluatedLayout = true;
                if (_completedAssignmentIndex >= _completedAssignments.Count) IsComplete = true;
                return true;
            }

            PartialAssignment partial = _frontier[_frontierIndex];
            AutoPlayerGrid option = _options[_depth][_optionIndex++];
            AutoPlayerGrid[] assigned = partial.Grids.Concat(new[] { option }).ToArray();
            if (IsPartialLegal(assigned))
            {
                _nextFrontier.Add(new PartialAssignment(assigned, PartialScore(assigned)));
                if (_nextFrontier.Count > BeamWidth) _nextFrontier.Remove(_nextFrontier.Max!);
            }
            if (_optionIndex >= _options[_depth].Count)
            {
                _optionIndex = 0;
                _frontierIndex++;
                if (_frontierIndex >= _frontier.Count)
                {
                    _depth++;
                    if (_depth >= _stations.Length)
                    {
                        _completedAssignments = _nextFrontier.ToList();
                        _nextFrontier.Clear();
                        _completedAssignmentIndex = 0;
                        if (_completedAssignments.Count == 0) IsComplete = true;
                    }
                    else
                    {
                        _frontier = _nextFrontier.ToList();
                        _nextFrontier.Clear();
                        _frontierIndex = 0;
                        if (_frontier.Count == 0) IsComplete = true;
                    }
                }
            }
            return true;
        }

        private void AdvanceOdometer()
        {
            _odometerStarted = true;
            for (int index = _indices.Length - 1; index >= 0; index--)
            {
                _indices[index]++;
                if (_indices[index] < _options[index].Count) return;
                _indices[index] = 0;
            }
            IsComplete = _odometerStarted;
        }

        private RailJointLayoutPlan? Evaluate(IReadOnlyList<AutoPlayerGrid> assignment)
        {
            RailStationMoveCandidate canonical = _stations[0];
            Dictionary<int, AutoPlayerGrid> targetByPointId = new();
            for (int index = 0; index < _stations.Length; index++) targetByPointId[_stations[index].StationPointId] = assignment[index];
            Dictionary<int, AutoPlayerGrid> gridByPointId = canonical.OrderedStationPointIds.Select((pointId, index) =>
                new KeyValuePair<int, AutoPlayerGrid>(pointId,
                targetByPointId.TryGetValue(pointId, out AutoPlayerGrid target)
                    ? target
                    : canonical.OrderedStationGrids[index])).ToDictionary(pair => pair.Key, pair => pair.Value);
            Dictionary<int, bool> kindByPointId = canonical.OrderedStationPointIds.Select((pointId, index) =>
                new KeyValuePair<int, bool>(pointId, canonical.OrderedStationKinds[index]))
                .ToDictionary(pair => pair.Key, pair => pair.Value);
            int attributePointId = kindByPointId.Count(pair => pair.Value) == 1
                ? kindByPointId.Single(pair => pair.Value).Key
                : 0;
            IReadOnlyList<int> orderedPointIds = RailLayoutStrategyPlanner.OrderSimplePlayerLoop(
                gridByPointId.Select(pair => new RailLoopPointCandidate
                {
                    InstanceId = pair.Key,
                    IsAttribute = kindByPointId[pair.Key],
                    Grid = new RailLayoutPoint(pair.Value.X, pair.Value.Y)
                }),
                attributePointId);
            if (orderedPointIds.Count != canonical.OrderedStationPointIds.Count) return null;
            AutoPlayerGrid[] ordered = orderedPointIds.Select(pointId => gridByPointId[pointId]).ToArray();
            bool[] orderedKinds = orderedPointIds.Select(pointId => kindByPointId[pointId]).ToArray();
            if (!IsFullyLegal(ordered, orderedKinds, canonical.SpacingRules)) return null;
            RailLayoutScore score = Score(canonical, ordered, orderedKinds);
            if (!score.EncirclesBase || !score.CoversAllQuadrants || !score.HasNoLargeBlindArc ||
                !RailLayoutStrategyPlanner.IsStrictDefenseImprovement(_baseline, score)) return null;
            RailJointMoveTarget[] targets = _stations.Select((candidate, index) => new RailJointMoveTarget
                {
                    Candidate = candidate,
                    OriginalGrid = candidate.CurrentGrid,
                    TargetGrid = assignment[index]
                })
                .Where(target => !target.OriginalGrid.Equals(target.TargetGrid))
                .OrderBy(target => target.StablePointId)
                .ToArray();
            bool requiresReconnect = !_baseline.IsValid ||
                                     !IsEquivalentCycle(canonical.OrderedStationPointIds, orderedPointIds);
            if (targets.Length == 0 && !requiresReconnect) return null;
            return new RailJointLayoutPlan
            {
                RailInstanceId = canonical.RailInstanceId,
                RailInternalId = canonical.RailInternalId,
                OriginalLoopCycleSeconds = canonical.CurrentLoopCycleSeconds,
                PredictedLoopCycleSeconds = score.LoopCycleSeconds,
                PredictedTriggerRate = score.TriggerRate,
                BaselineScore = _baseline,
                PredictedScore = score,
                Targets = targets,
                OrderedTargetGrids = ordered,
                OrderedStablePointIds = orderedPointIds.ToArray(),
                RequiresReconnect = requiresReconnect
            };
        }

        private static bool IsEquivalentCycle(IReadOnlyList<int> current, IReadOnlyList<int> candidate)
        {
            if (current.Count != candidate.Count || current.Count == 0 || current[0] != candidate[0]) return false;
            if (current.SequenceEqual(candidate)) return true;
            return current.Skip(1).Reverse().SequenceEqual(candidate.Skip(1));
        }

        private RailLayoutScore PartialScore(IReadOnlyList<AutoPlayerGrid> assigned)
        {
            RailStationMoveCandidate canonical = _stations[0];
            Dictionary<int, AutoPlayerGrid> positions = new();
            for (int index = 0; index < assigned.Count; index++) positions[_stations[index].StationPointId] = assigned[index];
            Dictionary<int, AutoPlayerGrid> gridByPointId = canonical.OrderedStationPointIds.Select((pointId, index) =>
                new KeyValuePair<int, AutoPlayerGrid>(pointId,
                    positions.TryGetValue(pointId, out AutoPlayerGrid grid) ? grid : canonical.OrderedStationGrids[index]))
                .ToDictionary(pair => pair.Key, pair => pair.Value);
            Dictionary<int, bool> kindByPointId = canonical.OrderedStationPointIds.Select((pointId, index) =>
                    new KeyValuePair<int, bool>(pointId, canonical.OrderedStationKinds[index]))
                .ToDictionary(pair => pair.Key, pair => pair.Value);
            int attributePointId = kindByPointId.Count(pair => pair.Value) == 1
                ? kindByPointId.Single(pair => pair.Value).Key
                : 0;
            IReadOnlyList<int> ids = RailLayoutStrategyPlanner.OrderSimplePlayerLoop(
                gridByPointId.Select(pair => new RailLoopPointCandidate
                {
                    InstanceId = pair.Key,
                    IsAttribute = kindByPointId[pair.Key],
                    Grid = new RailLayoutPoint(pair.Value.X, pair.Value.Y)
                }),
                attributePointId);
            return ids.Count == gridByPointId.Count
                ? Score(canonical, ids.Select(id => gridByPointId[id]).ToArray(), ids.Select(id => kindByPointId[id]).ToArray())
                : new RailLayoutScore();
        }

        private bool IsPartialLegal(IReadOnlyList<AutoPlayerGrid> assigned)
        {
            HashSet<AutoPlayerGrid> occupied = new();
            HashSet<int> movableIds = new(_stations.Select(station => station.StationPointId));
            RailStationMoveCandidate canonical = _stations[0];
            for (int index = 0; index < canonical.OrderedStationGrids.Count; index++)
            {
                if (!movableIds.Contains(canonical.OrderedStationPointIds[index])) occupied.Add(canonical.OrderedStationGrids[index]);
            }
            for (int index = 0; index < assigned.Count; index++)
            {
                if (!occupied.Add(assigned[index])) return false;
                bool attribute = _stations[index].StationIsAttribute;
                foreach (int fixedIndex in Enumerable.Range(0, canonical.OrderedStationGrids.Count)
                             .Where(i => !movableIds.Contains(canonical.OrderedStationPointIds[i])))
                {
                    if (!SpacingLegal(assigned[index], attribute, canonical.OrderedStationGrids[fixedIndex],
                            canonical.OrderedStationKinds[fixedIndex], canonical.SpacingRules)) return false;
                }
                for (int other = 0; other < index; other++)
                {
                    if (!SpacingLegal(assigned[index], attribute, assigned[other], _stations[other].StationIsAttribute,
                            canonical.SpacingRules)) return false;
                }
            }
            return true;
        }

        private static RailLayoutScore Score(
            RailStationMoveCandidate candidate,
            IReadOnlyList<AutoPlayerGrid> ordered,
            IReadOnlyList<bool>? orderedKinds = null)
        {
            double baselineLength = ClosedLength(candidate.OrderedStationGrids);
            double targetLength = ClosedLength(ordered);
            double cycle = baselineLength > 0d && targetLength > 0d
                ? candidate.CurrentLoopCycleSeconds * targetLength / baselineLength
                : double.PositiveInfinity;
            return RailLayoutStrategyPlanner.EvaluateWithSpacing(
                ordered.Select(grid => new RailLayoutPoint(grid.X, grid.Y)),
                orderedKinds ?? candidate.OrderedStationKinds,
                candidate.StationCount,
                cycle,
                candidate.SpacingRules);
        }

        private static bool IsFullyLegal(
            IReadOnlyList<AutoPlayerGrid> grids,
            IReadOnlyList<bool> kinds,
            StationSpacingRules rules)
        {
            if (grids.Count < 3 || grids.Count != kinds.Count || grids.Distinct().Count() != grids.Count) return false;
            for (int left = 0; left < grids.Count; left++)
            for (int right = left + 1; right < grids.Count; right++)
                if (!SpacingLegal(grids[left], kinds[left], grids[right], kinds[right], rules)) return false;
            RailLoopValidationResult geometry = RailLoopValidator.ValidateOrdered(
                grids.Select((grid, index) => new RailLoopNode
                {
                    Id = index + 1,
                    IsAttribute = kinds[index],
                    Point = new RailLayoutPoint(grid.X, grid.Y)
                }));
            return geometry.IsSingleCycle && geometry.IsSimpleGeometry;
        }

        private static bool SpacingLegal(
            AutoPlayerGrid left,
            bool leftAttribute,
            AutoPlayerGrid right,
            bool rightAttribute,
            StationSpacingRules rules)
        {
            double x = left.X - right.X;
            double y = left.Y - right.Y;
            return Math.Sqrt(x * x + y * y) + 0.000001d >= rules.MinimumFor(leftAttribute, rightAttribute);
        }

        private static double ClosedLength(IReadOnlyList<AutoPlayerGrid> grids)
        {
            double length = 0d;
            for (int index = 0; index < grids.Count; index++)
            {
                AutoPlayerGrid left = grids[index];
                AutoPlayerGrid right = grids[(index + 1) % grids.Count];
                double x = left.X - right.X;
                double y = left.Y - right.Y;
                length += Math.Sqrt(x * x + y * y);
            }
            return length;
        }

        private sealed class PartialAssignment
        {
            public PartialAssignment(IReadOnlyList<AutoPlayerGrid> grids, RailLayoutScore? score = null)
            {
                Grids = grids;
                Score = score ?? new RailLayoutScore();
            }
            public IReadOnlyList<AutoPlayerGrid> Grids { get; }
            public RailLayoutScore Score { get; }
        }

        private sealed class PartialAssignmentComparer : IComparer<PartialAssignment>
        {
            public static readonly PartialAssignmentComparer Instance = new();
            public int Compare(PartialAssignment? left, PartialAssignment? right)
            {
                if (ReferenceEquals(left, right)) return 0;
                if (left == null) return 1;
                if (right == null) return -1;
                int comparison = RailLayoutStrategyPlanner.CompareForDefense(left.Score, right.Score);
                if (comparison != 0) return comparison;
                string leftKey = string.Join(";", left.Grids.Select(grid => grid.X + "," + grid.Y));
                string rightKey = string.Join(";", right.Grids.Select(grid => grid.X + "," + grid.Y));
                return string.CompareOrdinal(leftKey, rightKey);
            }
        }
    }
}
