using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Loopstructor.AutoPlayer.Core;
using UnityEngine;

namespace Loopstructor.AutoPlayer.Plugin;

/// <summary>
/// Caches the MapPosManager candidate-pool contract and reads the current scene's grid coordinates.
/// It never searches Unity resources or evaluates placement conditions.
/// </summary>
internal sealed class RuntimeGridCandidatePoolReader
{
    private PropertyInfo? _mapPosManagerInstance;
    private PropertyInfo? _catapultRingPosition;
    private PropertyInfo? _energyCatapultRingPosition;
    private PropertyInfo? _ordinaryMinimumSpacing;
    private PropertyInfo? _energyMinimumSpacing;

    public bool TryRead(out IReadOnlyList<AutoPlayerGrid> candidates, out string error)
    {
        candidates = Array.Empty<AutoPlayerGrid>();
        if (!TryReadTyped(out IReadOnlyList<AutoPlayerGrid> ordinary, out IReadOnlyList<AutoPlayerGrid> energy, out error))
        {
            return false;
        }

        candidates = new HashSet<AutoPlayerGrid>(ordinary.Concat(energy)).ToArray();
        return true;
    }

    public bool TryReadTyped(
        out IReadOnlyList<AutoPlayerGrid> ordinaryCandidates,
        out IReadOnlyList<AutoPlayerGrid> energyCandidates,
        out string error)
    {
        ordinaryCandidates = Array.Empty<AutoPlayerGrid>();
        energyCandidates = Array.Empty<AutoPlayerGrid>();
        try
        {
            if (!TryResolveContract(out error))
            {
                return false;
            }

            object? manager = _mapPosManagerInstance!.GetValue(null, null);
            if (manager == null)
            {
                error = "MapPosManager.Instance 尚未初始化。";
                return false;
            }

            HashSet<AutoPlayerGrid> ordinary = new();
            HashSet<AutoPlayerGrid> energy = new();
            AddCandidateDictionary(ordinary, _catapultRingPosition!.GetValue(manager, null));
            AddCandidateDictionary(energy, _energyCatapultRingPosition!.GetValue(manager, null));
            if (ordinary.Count == 0 || energy.Count == 0)
            {
                error = "MapPosManager 没有同时返回普通站点和能量站点候选格。";
                return false;
            }

            ordinaryCandidates = ordinary.OrderBy(grid => grid.X).ThenBy(grid => grid.Y).ToArray();
            energyCandidates = energy.OrderBy(grid => grid.X).ThenBy(grid => grid.Y).ToArray();
            error = string.Empty;
            return true;
        }
        catch (Exception ex)
        {
            error = "读取弹射点候选池失败：" + Unwrap(ex).Message;
            return false;
        }
    }

    public bool TryReadSpacingRules(out StationSpacingRules rules, out string error)
    {
        rules = default;
        try
        {
            if (!TryResolveContract(out error)) return false;
            object? manager = _mapPosManagerInstance!.GetValue(null, null);
            if (manager == null)
            {
                error = "MapPosManager.Instance 尚未初始化。";
                return false;
            }

            double ordinary = Convert.ToDouble(_ordinaryMinimumSpacing!.GetValue(manager, null));
            double energy = Convert.ToDouble(_energyMinimumSpacing!.GetValue(manager, null));
            rules = new StationSpacingRules(ordinary, energy);
            if (!rules.IsKnown)
            {
                error = "MapPosManager 返回了无效的弹射点最小间距。";
                return false;
            }

            error = string.Empty;
            return true;
        }
        catch (Exception ex)
        {
            error = "读取弹射点最小间距失败：" + Unwrap(ex).Message;
            return false;
        }
    }

    private bool TryResolveContract(out string error)
    {
        if (_mapPosManagerInstance != null &&
            _catapultRingPosition != null &&
            _energyCatapultRingPosition != null &&
            _ordinaryMinimumSpacing != null &&
            _energyMinimumSpacing != null)
        {
            error = string.Empty;
            return true;
        }

        Type? mapPosManager = FindType("MapPosManager");
        PropertyInfo? instance = mapPosManager != null
            ? FindStaticProperty(mapPosManager, "Instance")
            : null;
        PropertyInfo? catapultRings = mapPosManager?.GetProperty(
            "CatapultRingPosition",
            BindingFlags.Public | BindingFlags.Instance);
        PropertyInfo? energyRings = mapPosManager?.GetProperty(
            "EnergyCatapultRingPosition",
            BindingFlags.Public | BindingFlags.Instance);
        PropertyInfo? ordinaryMinimum = mapPosManager?.GetProperty(
            "minDisAwayStation",
            BindingFlags.Public | BindingFlags.Instance);
        PropertyInfo? energyMinimum = mapPosManager?.GetProperty(
            "minEnergyDisAwayStation",
            BindingFlags.Public | BindingFlags.Instance);
        if (mapPosManager == null || instance == null || catapultRings == null || energyRings == null ||
            ordinaryMinimum == null || energyMinimum == null)
        {
            List<string> missing = new();
            if (mapPosManager == null) missing.Add("MapPosManager");
            if (instance == null) missing.Add("MapPosManager.Instance");
            if (catapultRings == null) missing.Add("MapPosManager.CatapultRingPosition");
            if (energyRings == null) missing.Add("MapPosManager.EnergyCatapultRingPosition");
            if (ordinaryMinimum == null) missing.Add("MapPosManager.minDisAwayStation");
            if (energyMinimum == null) missing.Add("MapPosManager.minEnergyDisAwayStation");
            error = "缺少候选格运行时成员：" + string.Join("、", missing);
            return false;
        }

        _mapPosManagerInstance = instance;
        _catapultRingPosition = catapultRings;
        _energyCatapultRingPosition = energyRings;
        _ordinaryMinimumSpacing = ordinaryMinimum;
        _energyMinimumSpacing = energyMinimum;
        error = string.Empty;
        return true;
    }

    private static void AddCandidateDictionary(HashSet<AutoPlayerGrid> destination, object? dictionary)
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

    private static void AddCandidateList(HashSet<AutoPlayerGrid> destination, object? values)
    {
        if (values is not IEnumerable grids)
        {
            return;
        }

        foreach (object? value in grids)
        {
            if (value is Vector2Int grid)
            {
                destination.Add(new AutoPlayerGrid(grid.x, grid.y));
            }
        }
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
