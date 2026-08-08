using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Reflection;
using Loopstructor.AutoPlayer.Core;
using Newtonsoft.Json.Linq;
using UnityEngine;

namespace Loopstructor.AutoPlayer.Plugin;

/// <summary>Incrementally validates placement or move grids without scanning the complete MCP option payload.</summary>
internal sealed class IncrementalDefenseStationGridProbe
{
    private const int MaximumValidationCount = 240;
    private const int MaximumValidationsPerSlice = 8;
    private const double SliceBudgetMilliseconds = 3.0d;

    private readonly RuntimeGridCandidatePoolReader _candidateReader = new();
    private readonly List<AutoPlayerGrid> _rankedCandidates = new();
    private MethodInfo? _tryValidateGrid;
    private string _disposableEnum = string.Empty;
    private int _nextCandidateIndex;
    private int _totalProbed;
    private bool _initialized;
    private bool _validateCandidates = true;
    private string _contractError = string.Empty;

    public bool TryInitializePlacement(
        string disposableEnum,
        JObject? catapultResult,
        out string error) =>
        TryInitialize(
            disposableEnum,
            candidates => DefenseStationGridRanker.RankPlacement(
                disposableEnum,
                candidates,
                catapultResult),
            validateCandidates: true,
            out error);

    public bool TryInitializeMove(
        string disposableEnum,
        JObject? railResult,
        int lineInstanceId,
        AutoPlayerGrid currentGrid,
        out string error) =>
        TryInitialize(
            disposableEnum,
            candidates => DefenseStationGridRanker.RankMove(
                candidates,
                railResult,
                lineInstanceId,
                currentGrid),
            validateCandidates: false,
            out error);

    public bool TryInitializeMove(
        RailStationMoveCandidate candidate,
        out string error) =>
        TryInitialize(
            candidate.StationDisposableEnum,
            candidates => DefenseStationGridRanker.RankExistingStationMove(candidates, candidate),
            validateCandidates: false,
            out error);

    public IncrementalGridProbeResult ProbeNext()
    {
        if (!_initialized || _tryValidateGrid == null)
        {
            return Unavailable();
        }

        Stopwatch slice = Stopwatch.StartNew();
        int probedThisSlice = 0;
        try
        {
            while (_nextCandidateIndex < _rankedCandidates.Count &&
                   _totalProbed < MaximumValidationCount &&
                   probedThisSlice < MaximumValidationsPerSlice &&
                   (probedThisSlice == 0 || slice.Elapsed.TotalMilliseconds < SliceBudgetMilliseconds))
            {
                AutoPlayerGrid candidate = _rankedCandidates[_nextCandidateIndex++];
                if (!_validateCandidates)
                {
                    _totalProbed++;
                    return new IncrementalGridProbeResult(
                        IncrementalGridProbeStatus.Found,
                        candidate,
                        _totalProbed,
                        "已按相邻线段长度选出候选格；启动移动后仍会用实时交互条件复核。");
                }

                object?[] arguments =
                {
                    _disposableEnum,
                    new Vector2Int(candidate.X, candidate.Y),
                    null
                };
                object? returnValue = _tryValidateGrid.Invoke(null, arguments);
                _totalProbed++;
                probedThisSlice++;
                if (returnValue is not bool pass)
                {
                    _initialized = false;
                    _contractError = "单格模板校验返回了未知结果。";
                    return Unavailable();
                }

                if (pass)
                {
                    return new IncrementalGridProbeResult(
                        IncrementalGridProbeStatus.Found,
                        candidate,
                        _totalProbed,
                        $"本次用时 {slice.Elapsed.TotalMilliseconds:0.###} ms。");
                }
            }
        }
        catch (Exception ex)
        {
            _initialized = false;
            _contractError = "站点候选格只读校验失败：" + Unwrap(ex).Message;
            return Unavailable();
        }

        if (_nextCandidateIndex >= _rankedCandidates.Count || _totalProbed >= MaximumValidationCount)
        {
            return new IncrementalGridProbeResult(
                IncrementalGridProbeStatus.Exhausted,
                totalProbed: _totalProbed,
                detail: $"已检查 {_totalProbed} 个站点候选格，没有合法目标。");
        }

        return new IncrementalGridProbeResult(
            IncrementalGridProbeStatus.Probing,
            totalProbed: _totalProbed,
            detail: $"本帧检查 {probedThisSlice} 个站点候选格，用时 {slice.Elapsed.TotalMilliseconds:0.###} ms。");
    }

    public void Reset()
    {
        _rankedCandidates.Clear();
        _disposableEnum = string.Empty;
        _nextCandidateIndex = 0;
        _totalProbed = 0;
        _initialized = false;
        _validateCandidates = true;
        _contractError = string.Empty;
    }

    private bool TryInitialize(
        string disposableEnum,
        Func<IReadOnlyList<AutoPlayerGrid>, IReadOnlyList<AutoPlayerGrid>> rank,
        bool validateCandidates,
        out string error)
    {
        Reset();
        if (string.IsNullOrWhiteSpace(disposableEnum))
        {
            error = "缺少站点道具枚举。";
            _contractError = error;
            return false;
        }

        if (!TryResolveContract(out error) ||
            !_candidateReader.TryRead(out IReadOnlyList<AutoPlayerGrid> candidates, out error))
        {
            _contractError = error;
            return false;
        }

        _disposableEnum = disposableEnum.Trim();
        _validateCandidates = validateCandidates;
        _rankedCandidates.AddRange(rank(candidates));
        if (_rankedCandidates.Count == 0)
        {
            error = "没有比当前站点位置更合适的候选格。";
            _contractError = error;
            return false;
        }

        _initialized = true;
        error = string.Empty;
        return true;
    }

    private bool TryResolveContract(out string error)
    {
        if (_tryValidateGrid != null)
        {
            error = string.Empty;
            return true;
        }

        Type? util = FindType("GuiGameAutomation.Runtime.GuiGameMcpGridInteractionUtil");
        _tryValidateGrid = util?
            .GetMethods(BindingFlags.Public | BindingFlags.Static)
            .FirstOrDefault(method =>
            {
                ParameterInfo[] parameters = method.GetParameters();
                return string.Equals(method.Name, "TryValidateDisposableGridOption", StringComparison.Ordinal) &&
                       parameters.Length == 3 &&
                       parameters[0].ParameterType == typeof(string) &&
                       parameters[1].ParameterType == typeof(Vector2Int) &&
                       parameters[2].ParameterType == typeof(object).MakeByRefType();
            });
        error = _tryValidateGrid == null
            ? "缺少 GuiGameMcpGridInteractionUtil.TryValidateDisposableGridOption。"
            : string.Empty;
        return _tryValidateGrid != null;
    }

    private IncrementalGridProbeResult Unavailable() => new(
        IncrementalGridProbeStatus.Unavailable,
        totalProbed: _totalProbed,
        detail: string.IsNullOrWhiteSpace(_contractError) ? "站点候选格探测尚未初始化。" : _contractError);

    private static Type? FindType(string fullName)
    {
        foreach (Assembly assembly in AppDomain.CurrentDomain.GetAssemblies())
        {
            Type? type = assembly.GetType(fullName, false);
            if (type != null) return type;
        }

        return null;
    }

    private static Exception Unwrap(Exception exception) =>
        exception is TargetInvocationException { InnerException: not null } invocation
            ? invocation.InnerException
            : exception;
}
