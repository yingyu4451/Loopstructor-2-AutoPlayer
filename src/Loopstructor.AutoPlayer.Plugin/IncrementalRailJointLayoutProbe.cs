using System;
using System.Collections.Generic;
using Loopstructor.AutoPlayer.Core;

namespace Loopstructor.AutoPlayer.Plugin;

internal sealed class IncrementalRailJointLayoutProbe
{
    internal const int BeamWidth = RailJointLayoutSearch.BeamWidth;
    internal const double SliceBudgetMilliseconds = RailJointLayoutSearch.DefaultSliceBudgetMilliseconds;

    private readonly RuntimeGridCandidatePoolReader _candidateReader = new();
    private RailJointLayoutSearch? _search;
    private string _error = string.Empty;

    public bool TryInitialize(
        IReadOnlyList<RailStationMoveCandidate> candidates,
        out string error)
    {
        Reset();
        if (candidates == null || candidates.Count == 0)
        {
            error = "当前闭环没有可移动的能量或特殊站点。";
            return false;
        }
        if (!_candidateReader.TryReadTyped(
                out IReadOnlyList<AutoPlayerGrid> ordinary,
                out IReadOnlyList<AutoPlayerGrid> energy,
                out error))
        {
            _error = error;
            return false;
        }
        _search = new RailJointLayoutSearch(candidates, ordinary, energy);
        error = string.Empty;
        return true;
    }

    public RailJointLayoutSearchResult ProbeNext() =>
        _search?.ProbeNext(SliceBudgetMilliseconds) ?? new RailJointLayoutSearchResult
        {
            Status = RailJointLayoutSearchStatus.Exhausted,
            Detail = string.IsNullOrWhiteSpace(_error) ? "联合布局规划尚未初始化。" : _error
        };

    public void Reset()
    {
        _search = null;
        _error = string.Empty;
    }
}
