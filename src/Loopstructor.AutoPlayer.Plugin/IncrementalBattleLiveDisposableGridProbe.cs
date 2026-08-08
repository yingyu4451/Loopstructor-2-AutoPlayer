using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Reflection;
using Loopstructor.AutoPlayer.Core;
using UnityEngine;

namespace Loopstructor.AutoPlayer.Plugin;

/// <summary>
/// Validates the active GridChooseInteraction near the strongest threat in bounded slices.
/// It calls the native restoring single-grid evaluator and never leaves pointer or interaction state changed.
/// </summary>
internal sealed class IncrementalBattleLiveDisposableGridProbe : IBattleLiveDisposableGridProbe
{
    private const int MaximumValidationCount = 240;
    private const int MaximumValidationsPerSlice = 8;
    private const double SliceBudgetMilliseconds = 3.0d;

    private readonly RuntimeGridCandidatePoolReader _candidateReader = new();
    private readonly List<AutoPlayerGrid> _rankedCandidates = new();
    private PropertyInfo? _gameControllerInstance;
    private PropertyInfo? _gameControllerGrid;
    private MethodInfo? _resolveLiveGridInteraction;
    private MethodInfo? _evaluateLiveGrid;
    private object? _liveInteraction;
    private int _nextCandidateIndex;
    private int _totalProbed;
    private bool _initialized;
    private string _contractError = string.Empty;

    public bool TryInitialize(
        double threatWorldX,
        double threatWorldY,
        double threatWorldZ,
        out string error)
    {
        ResetProbeState();
        if (!IsFinite(threatWorldX) || !IsFinite(threatWorldY) || !IsFinite(threatWorldZ))
        {
            error = "威胁世界坐标不是有限数值。";
            _contractError = error;
            return false;
        }

        try
        {
            if (!TryResolveContract(out error) ||
                !_candidateReader.TryRead(out IReadOnlyList<AutoPlayerGrid> candidates, out error))
            {
                _contractError = error;
                return false;
            }

            object? gameController = _gameControllerInstance!.GetValue(null, null);
            if (gameController == null || _gameControllerGrid!.GetValue(gameController, null) is not Grid gameGrid)
            {
                error = "GameController.Grid 尚未初始化。";
                _contractError = error;
                return false;
            }

            Vector3 threatWorld = new((float)threatWorldX, (float)threatWorldY, (float)threatWorldZ);
            Vector3Int threatCell = gameGrid.WorldToCell(threatWorld);
            AutoPlayerGrid threatGrid = new(threatCell.x, threatCell.y);
            object? interaction = _resolveLiveGridInteraction!.Invoke(null, null);
            if (interaction == null)
            {
                error = "当前没有可供增量探测的 GridChooseInteraction 预览。";
                _contractError = error;
                return false;
            }

            _rankedCandidates.AddRange(BattleDisposableGridRanker.Rank(candidates, threatGrid));
            if (_rankedCandidates.Count == 0)
            {
                error = "当前地图没有可供实时道具检查的候选格。";
                _contractError = error;
                return false;
            }

            _liveInteraction = interaction;
            _initialized = true;
            _contractError = string.Empty;
            error = string.Empty;
            return true;
        }
        catch (Exception ex)
        {
            error = "初始化战斗实时格子探测失败：" + Unwrap(ex).Message;
            _contractError = error;
            ResetProbeState();
            return false;
        }
    }

    public IncrementalGridProbeResult ProbeNext()
    {
        if (!_initialized ||
            _liveInteraction == null ||
            _resolveLiveGridInteraction == null ||
            _evaluateLiveGrid == null)
        {
            return Unavailable();
        }

        Stopwatch slice = Stopwatch.StartNew();
        int probedThisSlice = 0;
        try
        {
            object? currentInteraction = _resolveLiveGridInteraction.Invoke(null, null);
            if (!ReferenceEquals(currentInteraction, _liveInteraction))
            {
                _initialized = false;
                _contractError = "道具预览已退出或已被另一项交互替换。";
                return Unavailable();
            }

            while (_nextCandidateIndex < _rankedCandidates.Count &&
                   _totalProbed < MaximumValidationCount &&
                   probedThisSlice < MaximumValidationsPerSlice &&
                   (probedThisSlice == 0 || slice.Elapsed.TotalMilliseconds < SliceBudgetMilliseconds))
            {
                AutoPlayerGrid candidate = _rankedCandidates[_nextCandidateIndex++];
                object? returnValue = _evaluateLiveGrid.Invoke(
                    null,
                    new object?[]
                    {
                        _liveInteraction,
                        new Vector2Int(candidate.X, candidate.Y)
                    });
                _totalProbed++;
                probedThisSlice++;
                if (returnValue is not bool pass)
                {
                    _initialized = false;
                    _contractError = "实时单格校验返回了未知结果。";
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
            _contractError = "战斗候选格实时校验失败：" + Unwrap(ex).Message;
            return Unavailable();
        }

        if (_nextCandidateIndex >= _rankedCandidates.Count || _totalProbed >= MaximumValidationCount)
        {
            return new IncrementalGridProbeResult(
                IncrementalGridProbeStatus.Exhausted,
                totalProbed: _totalProbed,
                detail: $"已达到战斗候选格检查边界（候选 {_rankedCandidates.Count}，已检查 {_totalProbed}）。");
        }

        return new IncrementalGridProbeResult(
            IncrementalGridProbeStatus.Probing,
            totalProbed: _totalProbed,
            detail: $"本帧检查 {probedThisSlice} 个战斗候选格，用时 {slice.Elapsed.TotalMilliseconds:0.###} ms。");
    }

    public void Reset() => ResetProbeState();

    private bool TryResolveContract(out string error)
    {
        if (_gameControllerInstance != null &&
            _gameControllerGrid != null &&
            _resolveLiveGridInteraction != null &&
            _evaluateLiveGrid != null)
        {
            error = string.Empty;
            return true;
        }

        Type? gameController = FindType("MetroTD.GameController");
        Type? gridInteractionUtil = FindType("GuiGameAutomation.Runtime.GuiGameMcpGridInteractionUtil");
        PropertyInfo? gameInstance = gameController != null
            ? FindStaticProperty(gameController, "Instance")
            : null;
        PropertyInfo? gameGrid = gameController?.GetProperty(
            "Grid",
            BindingFlags.Public | BindingFlags.Instance);
        MethodInfo? resolveInteraction = gridInteractionUtil?.GetMethod(
            "ResolveLiveGridInteraction",
            BindingFlags.NonPublic | BindingFlags.Static,
            null,
            Type.EmptyTypes,
            null);
        MethodInfo? evaluateGrid = gridInteractionUtil?
            .GetMethods(BindingFlags.NonPublic | BindingFlags.Static)
            .FirstOrDefault(method =>
            {
                if (!string.Equals(method.Name, "EvaluateLiveGrid", StringComparison.Ordinal) ||
                    method.ReturnType != typeof(bool))
                {
                    return false;
                }

                ParameterInfo[] parameters = method.GetParameters();
                return parameters.Length == 2 &&
                       string.Equals(parameters[0].ParameterType.Name, "GridChooseInteraction", StringComparison.Ordinal) &&
                       parameters[1].ParameterType == typeof(Vector2Int);
            });
        if (gameController == null ||
            gameInstance == null ||
            gameGrid == null ||
            resolveInteraction == null ||
            evaluateGrid == null)
        {
            List<string> missing = new();
            if (gameController == null) missing.Add("MetroTD.GameController");
            if (gameInstance == null) missing.Add("GameController.Instance");
            if (gameGrid == null) missing.Add("GameController.Grid");
            if (resolveInteraction == null) missing.Add("GuiGameMcpGridInteractionUtil.ResolveLiveGridInteraction");
            if (evaluateGrid == null) missing.Add("GuiGameMcpGridInteractionUtil.EvaluateLiveGrid");
            error = "缺少战斗实时格子运行时成员：" + string.Join("、", missing);
            return false;
        }

        _gameControllerInstance = gameInstance;
        _gameControllerGrid = gameGrid;
        _resolveLiveGridInteraction = resolveInteraction;
        _evaluateLiveGrid = evaluateGrid;
        error = string.Empty;
        return true;
    }

    private IncrementalGridProbeResult Unavailable() =>
        new(
            IncrementalGridProbeStatus.Unavailable,
            totalProbed: _totalProbed,
            detail: string.IsNullOrWhiteSpace(_contractError) ? "战斗实时格子探测尚未初始化。" : _contractError);

    private void ResetProbeState()
    {
        _rankedCandidates.Clear();
        _liveInteraction = null;
        _nextCandidateIndex = 0;
        _totalProbed = 0;
        _initialized = false;
    }

    private static PropertyInfo? FindStaticProperty(Type type, string name)
    {
        for (Type? current = type; current != null; current = current.BaseType)
        {
            PropertyInfo? property = current.GetProperty(
                name,
                BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static | BindingFlags.DeclaredOnly);
            if (property != null)
            {
                return property;
            }
        }

        return null;
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

    private static bool IsFinite(double value) => !double.IsNaN(value) && !double.IsInfinity(value);

    private static Exception Unwrap(Exception exception) =>
        exception is TargetInvocationException { InnerException: not null } invocation
            ? invocation.InnerException
            : exception;
}
