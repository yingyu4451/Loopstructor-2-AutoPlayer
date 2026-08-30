using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Reflection;
using Loopstructor.AutoPlayer.Core;
using UnityEngine;

namespace Loopstructor.AutoPlayer.Plugin;

/// <summary>
/// Reads MapPosManager's already-built candidate pools, then validates a small time-bounded slice on
/// each controller tick. It intentionally does not call QueryDisposableGridOptions because that game
/// command scans and serializes every candidate even when maxResults is small.
/// </summary>
internal sealed class IncrementalAttributePlacementGridProbe : IOpeningDefenseGridProbe
{
    private const string AttributeDisposableEnum = "FreePoint_Attribute";
    private const int MaximumValidationCount = 240;
    private const int MaximumValidationsPerSlice = 8;
    private const double SliceBudgetMilliseconds = 3.0d;

    private readonly List<OpeningDefenseGrid> _rankedCandidates = new();
    private string _disposableEnum = string.Empty;
    private PropertyInfo? _mapPosManagerInstance;
    private PropertyInfo? _catapultRingPosition;
    private PropertyInfo? _energyCatapultRingPosition;
    private PropertyInfo? _ordinaryMinimumSpacing;
    private PropertyInfo? _energyMinimumSpacing;
    private MethodInfo? _tryValidateGrid;
    private int _nextCandidateIndex;
    private int _totalProbed;
    private bool _initialized;
    private string _contractError = string.Empty;

    public bool TryInitialize(
        string disposableEnum,
        Newtonsoft.Json.Linq.JObject? catapultResult,
        bool placementIsAttribute,
        out string error)
    {
        ResetProbeState();
        bool placeAttribute = placementIsAttribute || string.Equals(
            disposableEnum,
            AttributeDisposableEnum,
            StringComparison.Ordinal);
        _disposableEnum = disposableEnum?.Trim() ?? string.Empty;
        if (string.IsNullOrWhiteSpace(_disposableEnum))
        {
            error = "缺少开局弹射点道具枚举。";
            return false;
        }
        if (!TryResolveContract(out error))
        {
            return false;
        }

        try
        {
            object? manager = _mapPosManagerInstance!.GetValue(null, null);
            if (manager == null)
            {
                error = "MapPosManager.Instance 尚未初始化。";
                return false;
            }

            HashSet<OpeningDefenseGrid> ordinaryCandidates = new();
            HashSet<OpeningDefenseGrid> energyCandidates = new();
            AddCandidateDictionary(ordinaryCandidates, _catapultRingPosition!.GetValue(manager, null));
            AddCandidateDictionary(energyCandidates, _energyCatapultRingPosition!.GetValue(manager, null));
            HashSet<OpeningDefenseGrid> candidates = placeAttribute
                ? energyCandidates
                : ordinaryCandidates;
            StationSpacingRules spacingRules = new(
                Convert.ToDouble(_ordinaryMinimumSpacing!.GetValue(manager, null)),
                Convert.ToDouble(_energyMinimumSpacing!.GetValue(manager, null)));
            if (!spacingRules.IsKnown)
            {
                error = "MapPosManager 返回了无效的弹射点最小间距。";
                return false;
            }
            _rankedCandidates.AddRange(
                DefenseStationGridRanker
                    .RankPlacement(
                        _disposableEnum,
                        candidates.Select(grid => new AutoPlayerGrid(grid.X, grid.Y)),
                        catapultResult,
                        spacingRules,
                        placementIsAttribute: placeAttribute)
                    .Select(grid => new OpeningDefenseGrid(grid.X, grid.Y)));
            if (_rankedCandidates.Count == 0)
            {
                error = "MapPosManager 没有返回可用的弹射点候选格。";
                return false;
            }

            _initialized = true;
            error = string.Empty;
            return true;
        }
        catch (Exception ex)
        {
            error = "读取弹射点候选格失败：" + Unwrap(ex).Message;
            ResetProbeState();
            return false;
        }
    }

    public OpeningDefenseGridProbeResult ProbeNext()
    {
        if (!_initialized || _tryValidateGrid == null)
        {
            return new OpeningDefenseGridProbeResult(
                OpeningDefenseGridProbeStatus.Unavailable,
                totalProbed: _totalProbed,
                detail: string.IsNullOrWhiteSpace(_contractError) ? "增量候选格探测尚未初始化。" : _contractError);
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
                OpeningDefenseGrid candidate = _rankedCandidates[_nextCandidateIndex++];
                object?[] arguments =
                {
                    _disposableEnum,
                    new Vector2Int(candidate.X, candidate.Y),
                    null
                };
                object? returnValue = _tryValidateGrid.Invoke(null, arguments);
                _totalProbed++;
                probedThisSlice++;
                if (returnValue is bool pass && pass)
                {
                    return new OpeningDefenseGridProbeResult(
                        OpeningDefenseGridProbeStatus.Found,
                        candidate,
                        _totalProbed,
                        $"本次用时 {slice.Elapsed.TotalMilliseconds:0.###} ms。");
                }
            }
        }
        catch (Exception ex)
        {
            _initialized = false;
            return new OpeningDefenseGridProbeResult(
                OpeningDefenseGridProbeStatus.Unavailable,
                totalProbed: _totalProbed,
                detail: "候选格只读校验失败：" + Unwrap(ex).Message);
        }

        if (_nextCandidateIndex >= _rankedCandidates.Count || _totalProbed >= MaximumValidationCount)
        {
            return new OpeningDefenseGridProbeResult(
                OpeningDefenseGridProbeStatus.Exhausted,
                totalProbed: _totalProbed,
                detail: $"已达到候选格检查边界（候选 {_rankedCandidates.Count}，已检查 {_totalProbed}）。");
        }

        return new OpeningDefenseGridProbeResult(
            OpeningDefenseGridProbeStatus.Probing,
            totalProbed: _totalProbed,
            detail: $"本帧检查 {probedThisSlice} 个候选格，用时 {slice.Elapsed.TotalMilliseconds:0.###} ms。");
    }

    public void Reset() => ResetProbeState();

    private bool TryResolveContract(out string error)
    {
        if (_mapPosManagerInstance != null &&
            _catapultRingPosition != null &&
            _energyCatapultRingPosition != null &&
            _ordinaryMinimumSpacing != null &&
            _energyMinimumSpacing != null &&
            _tryValidateGrid != null)
        {
            error = string.Empty;
            return true;
        }

        Type? mapPosManager = FindType("MapPosManager");
        Type? gridInteractionUtil = FindType("GuiGameAutomation.Runtime.GuiGameMcpGridInteractionUtil");
        _mapPosManagerInstance = mapPosManager != null ? FindStaticProperty(mapPosManager, "Instance") : null;
        _catapultRingPosition = mapPosManager?.GetProperty(
            "CatapultRingPosition",
            BindingFlags.Public | BindingFlags.Instance);
        _energyCatapultRingPosition = mapPosManager?.GetProperty(
            "EnergyCatapultRingPosition",
            BindingFlags.Public | BindingFlags.Instance);
        _ordinaryMinimumSpacing = mapPosManager?.GetProperty(
            "minDisAwayStation",
            BindingFlags.Public | BindingFlags.Instance);
        _energyMinimumSpacing = mapPosManager?.GetProperty(
            "minEnergyDisAwayStation",
            BindingFlags.Public | BindingFlags.Instance);
        _tryValidateGrid = gridInteractionUtil?
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

        List<string> missing = new();
        if (mapPosManager == null) missing.Add("MapPosManager");
        if (_mapPosManagerInstance == null) missing.Add("MapPosManager.Instance");
        if (_catapultRingPosition == null) missing.Add("MapPosManager.CatapultRingPosition");
        if (_energyCatapultRingPosition == null) missing.Add("MapPosManager.EnergyCatapultRingPosition");
        if (_ordinaryMinimumSpacing == null) missing.Add("MapPosManager.minDisAwayStation");
        if (_energyMinimumSpacing == null) missing.Add("MapPosManager.minEnergyDisAwayStation");
        if (_tryValidateGrid == null) missing.Add("GuiGameMcpGridInteractionUtil.TryValidateDisposableGridOption");

        _contractError = missing.Count == 0
            ? string.Empty
            : "缺少增量候选格运行时成员：" + string.Join("、", missing);
        error = _contractError;
        return missing.Count == 0;
    }

    private static void AddCandidateDictionary(HashSet<OpeningDefenseGrid> destination, object? dictionary)
    {
        if (dictionary is IDictionary nonGenericDictionary)
        {
            foreach (DictionaryEntry entry in nonGenericDictionary)
            {
                AddCandidateList(destination, entry.Value);
            }

            return;
        }

        if (dictionary is not IEnumerable entries)
        {
            return;
        }

        foreach (object? entry in entries)
        {
            object? value = entry?.GetType()
                .GetProperty("Value", BindingFlags.Public | BindingFlags.Instance)?
                .GetValue(entry, null);
            AddCandidateList(destination, value);
        }
    }

    private static void AddCandidateList(HashSet<OpeningDefenseGrid> destination, object? values)
    {
        if (values is not IEnumerable grids)
        {
            return;
        }

        foreach (object? value in grids)
        {
            if (value is Vector2Int grid)
            {
                destination.Add(new OpeningDefenseGrid(grid.x, grid.y));
            }
        }
    }

    private void ResetProbeState()
    {
        _rankedCandidates.Clear();
        _disposableEnum = string.Empty;
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

    private static Exception Unwrap(Exception exception) =>
        exception is TargetInvocationException { InnerException: not null } invocation
            ? invocation.InnerException
            : exception;
}
