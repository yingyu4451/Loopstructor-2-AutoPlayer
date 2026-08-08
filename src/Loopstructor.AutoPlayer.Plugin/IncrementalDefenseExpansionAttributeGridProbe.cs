using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Reflection;
using Loopstructor.AutoPlayer.Core;
using Newtonsoft.Json.Linq;
using UnityEngine;

namespace Loopstructor.AutoPlayer.Plugin;

/// <summary>
/// Finds one legal expansion attribute-station grid in small, time-bounded slices.
/// Candidate ordering matches the existing defense-expansion layout policy.
/// </summary>
internal sealed class IncrementalDefenseExpansionAttributeGridProbe : IDefenseExpansionAttributeGridProbe
{
    private const string AttributeDisposableEnum = "FreePoint_Attribute";
    private const int MaximumValidationCount = 240;
    private const int MaximumValidationsPerSlice = 8;
    private const double SliceBudgetMilliseconds = 3.0d;

    private readonly RuntimeGridCandidatePoolReader _candidateReader = new();
    private readonly List<AutoPlayerGrid> _rankedCandidates = new();
    private MethodInfo? _tryValidateGrid;
    private int _nextCandidateIndex;
    private int _totalProbed;
    private bool _initialized;
    private string _contractError = string.Empty;

    public bool TryInitialize(JObject? catapultResult, out string error)
    {
        ResetProbeState();
        try
        {
            if (!TryResolveContract(out error) ||
                !_candidateReader.TryRead(out IReadOnlyList<AutoPlayerGrid> candidates, out error))
            {
                _contractError = error;
                return false;
            }

            _rankedCandidates.AddRange(DefenseExpansionAttributeGridRanker.Rank(candidates, catapultResult));
            if (_rankedCandidates.Count == 0)
            {
                error = "现有弹射点无法与候选动力站组成合法的非共线扩建回路。";
                return false;
            }

            _initialized = true;
            _contractError = string.Empty;
            error = string.Empty;
            return true;
        }
        catch (Exception ex)
        {
            error = "扩建候选格排序失败：" + Unwrap(ex).Message;
            _contractError = error;
            ResetProbeState();
            return false;
        }
    }

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
                object?[] arguments =
                {
                    AttributeDisposableEnum,
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
            _contractError = "扩建候选格只读校验失败：" + Unwrap(ex).Message;
            return Unavailable();
        }

        if (_nextCandidateIndex >= _rankedCandidates.Count || _totalProbed >= MaximumValidationCount)
        {
            return new IncrementalGridProbeResult(
                IncrementalGridProbeStatus.Exhausted,
                totalProbed: _totalProbed,
                detail: $"已达到扩建候选格检查边界（候选 {_rankedCandidates.Count}，已检查 {_totalProbed}）。");
        }

        return new IncrementalGridProbeResult(
            IncrementalGridProbeStatus.Probing,
            totalProbed: _totalProbed,
            detail: $"本帧检查 {probedThisSlice} 个扩建候选格，用时 {slice.Elapsed.TotalMilliseconds:0.###} ms。");
    }

    public void Reset() => ResetProbeState();

    private bool TryResolveContract(out string error)
    {
        if (_tryValidateGrid != null)
        {
            error = string.Empty;
            return true;
        }

        Type? gridInteractionUtil = FindType("GuiGameAutomation.Runtime.GuiGameMcpGridInteractionUtil");
        MethodInfo? validator = gridInteractionUtil?
            .GetMethods(BindingFlags.Public | BindingFlags.Static)
            .FirstOrDefault(method =>
            {
                if (!string.Equals(method.Name, "TryValidateDisposableGridOption", StringComparison.Ordinal))
                {
                    return false;
                }

                ParameterInfo[] parameters = method.GetParameters();
                return parameters.Length == 3 &&
                       parameters[0].ParameterType == typeof(string) &&
                       parameters[1].ParameterType == typeof(Vector2Int) &&
                       parameters[2].ParameterType == typeof(object).MakeByRefType();
            });
        if (validator == null)
        {
            error = "缺少 GuiGameMcpGridInteractionUtil.TryValidateDisposableGridOption。";
            return false;
        }

        _tryValidateGrid = validator;
        error = string.Empty;
        return true;
    }

    private IncrementalGridProbeResult Unavailable() =>
        new(
            IncrementalGridProbeStatus.Unavailable,
            totalProbed: _totalProbed,
            detail: string.IsNullOrWhiteSpace(_contractError) ? "扩建候选格探测尚未初始化。" : _contractError);

    private void ResetProbeState()
    {
        _rankedCandidates.Clear();
        _nextCandidateIndex = 0;
        _totalProbed = 0;
        _initialized = false;
    }

    private static Type? FindType(string fullName)
    {
        foreach (Assembly assembly in AppDomain.CurrentDomain.GetAssemblies())
        {
            Type? type = assembly.GetType(fullName, false);
            if (type != null)
            {
                return type;
            }
        }

        return null;
    }

    private static Exception Unwrap(Exception exception) =>
        exception is TargetInvocationException { InnerException: not null } invocation
            ? invocation.InnerException
            : exception;
}
