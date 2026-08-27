using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using BepInEx.Logging;
using Loopstructor.AutoPlayer.Core;
using Newtonsoft.Json.Linq;
using UnityEngine;

namespace Loopstructor.AutoPlayer.Plugin;

/// <summary>One-shot screen-space evidence for a changed rail. It never supplies input coordinates.</summary>
internal sealed class RailVisualVerifier
{
    private string _lastFingerprint = string.Empty;

    public void Reset() => _lastFingerprint = string.Empty;

    public void CaptureIfChanged(
        JObject railResult,
        RailRuntimeTopologyInspection topology,
        string evidenceDirectory,
        EvidenceRecorder evidence,
        ManualLogSource log)
    {
        if (!topology.HasRails || string.IsNullOrWhiteSpace(topology.Fingerprint) ||
            string.Equals(_lastFingerprint, topology.Fingerprint, StringComparison.Ordinal)) return;
        _lastFingerprint = topology.Fingerprint;

        try
        {
            BuildProjection(railResult, topology, out List<RailVisualNode> nodes,
                out List<RailVisualEdge> edges, out RailLoopValidationResult? screenValidation,
                out string projectionMessage);
            evidence.CaptureRailTopology(
                evidenceDirectory,
                topology.Fingerprint,
                topology,
                nodes,
                edges,
                screenValidation,
                projectionMessage);
            if (screenValidation != null && !screenValidation.IsSimpleGeometry)
                log.LogWarning("轨道视觉复核发现屏幕投影异常；拓扑校验仍是操作门禁：" +
                               string.Join("；", screenValidation.Errors));
        }
        catch (Exception exception)
        {
            log.LogWarning("轨道视觉证据生成失败，拓扑校验结果不受影响：" + exception.Message);
        }
    }

    private static void BuildProjection(
        JObject result,
        RailRuntimeTopologyInspection topology,
        out List<RailVisualNode> visualNodes,
        out List<RailVisualEdge> visualEdges,
        out RailLoopValidationResult? screenValidation,
        out string message)
    {
        visualNodes = new List<RailVisualNode>();
        visualEdges = new List<RailVisualEdge>();
        screenValidation = null;
        Camera camera = Camera.main;
        Type? linePointType = FindType("MetroTD.LineSystem.LinePoint");
        PropertyInfo? idProperty = linePointType?.GetProperty("ID", BindingFlags.Public | BindingFlags.Instance);
        if (camera == null || linePointType == null || idProperty == null ||
            !TryProjectMainBase(camera, out RailLayoutPoint projectedBase))
        {
            message = "当前帧没有可用的世界相机、基地或 LinePoint 运行时对象，仅保存拓扑与原始截图。";
            return;
        }

        Dictionary<int, Component> components = Resources.FindObjectsOfTypeAll(linePointType)
            .OfType<Component>()
            .Where(component => component.gameObject.scene.IsValid())
            .Select(component => new { Component = component, Id = ReadId(idProperty, component) })
            .Where(item => item.Id.HasValue)
            .GroupBy(item => item.Id!.Value)
            .ToDictionary(group => group.Key, group => group.First().Component);
        JObject state = result.SelectToken("data.state") as JObject ?? result["state"] as JObject ?? result;
        JObject[] rails = (state["rails"] as JArray)?.OfType<JObject>().ToArray() ?? Array.Empty<JObject>();
        List<RailLoopValidationResult> projectedValidations = new();
        bool projectionComplete = rails.Length > 0;
        foreach (JObject rail in rails)
        {
            List<RailLoopNode> projectedNodes = new();
            List<RailLoopEdge> projectedEdges = new();
            List<RailVisualEdge> projectedVisualEdges = new();
            Dictionary<string, int> pointByGrid = new(StringComparer.Ordinal);
            Dictionary<int, RailVisualNode> visualById = new();
            foreach (JObject station in ((rail["orderedStations"] as JArray) ?? (rail["points"] as JArray))?
                         .OfType<JObject>() ?? Enumerable.Empty<JObject>())
            {
                if (station["pointId"]?.Type != JTokenType.Integer) continue;
                int pointId = station["pointId"]!.Value<int>();
                if (!components.TryGetValue(pointId, out Component component)) continue;
                Vector3 screen = camera.WorldToScreenPoint(component.transform.position);
                if (screen.z <= 0f) continue;
                RailVisualNode visual = new()
                {
                    PointId = pointId,
                    X = screen.x,
                    Y = Screen.height - screen.y
                };
                visualById[pointId] = visual;
                visualNodes.Add(visual);
                projectedNodes.Add(new RailLoopNode
                {
                    Id = pointId,
                    IsAttribute = station["isAttribute"]?.Value<bool>() == true,
                    Point = new RailLayoutPoint(visual.X, visual.Y)
                });
                pointByGrid[GridKey(station["grid"])] = pointId;
            }
            foreach (JObject line in (rail["lines"] as JArray)?.OfType<JObject>() ?? Enumerable.Empty<JObject>())
            {
                if (!pointByGrid.TryGetValue(GridKey(line["from"]), out int fromId) ||
                    !pointByGrid.TryGetValue(GridKey(line["to"]), out int toId) ||
                    !visualById.TryGetValue(fromId, out RailVisualNode? from) ||
                    !visualById.TryGetValue(toId, out RailVisualNode? to)) continue;
                projectedEdges.Add(new RailLoopEdge { FromId = fromId, ToId = toId });
                projectedVisualEdges.Add(new RailVisualEdge
                {
                    FromPointId = fromId,
                    ToPointId = toId,
                    FromX = from.X,
                    FromY = from.Y,
                    ToX = to.X,
                    ToY = to.Y
                });
            }
            int expectedStations = ((rail["orderedStations"] as JArray) ?? (rail["points"] as JArray))?.Count ?? 0;
            int expectedEdges = (rail["lines"] as JArray)?.Count ?? 0;
            if (projectedNodes.Count != expectedStations || projectedEdges.Count != expectedEdges)
            {
                projectionComplete = false;
                continue;
            }

            RailLoopValidationResult projectedValidation = RailLoopValidator.Validate(
                projectedNodes,
                projectedEdges,
                projectedBase);
            projectedValidations.Add(projectedValidation);
            foreach (RailVisualEdge edge in projectedVisualEdges)
            {
                edge.IsValid = projectedValidation.IsValid;
                visualEdges.Add(edge);
            }
        }
        if (projectionComplete && projectedValidations.Count == rails.Length)
        {
            string[] errors = projectedValidations.SelectMany(validation => validation.Errors)
                .Distinct(StringComparer.Ordinal).ToArray();
            screenValidation = new RailLoopValidationResult
            {
                IsValid = projectedValidations.All(validation => validation.IsValid),
                IsSingleCycle = projectedValidations.All(validation => validation.IsSingleCycle),
                IsSimpleGeometry = projectedValidations.All(validation => validation.IsSimpleGeometry),
                EncirclesBase = projectedValidations.All(validation => validation.EncirclesBase),
                CoversAllQuadrants = projectedValidations.All(validation => validation.CoversAllQuadrants),
                HasNoLargeBlindArc = projectedValidations.All(validation => validation.HasNoLargeBlindArc),
                SelfIntersectionCount = projectedValidations.Sum(validation => validation.SelfIntersectionCount),
                Errors = errors
            };
        }
        message = screenValidation == null
            ? "屏幕投影信息不完整，仅保存拓扑与原始截图。"
            : "已把真实 LinePoint 与主基地世界位置投影到屏幕，并逐轨复核闭合、交叉与基地包围关系。";
    }

    private static bool TryProjectMainBase(Camera camera, out RailLayoutPoint screenPoint)
    {
        screenPoint = default;
        Type? controllerType = FindType("MetroTD.GameController");
        PropertyInfo? instanceProperty = controllerType?.GetProperty(
            "Instance",
            BindingFlags.Public | BindingFlags.Static);
        object? controller = instanceProperty?.GetValue(null, null);
        PropertyInfo? mainBaseProperty = controllerType?.GetProperty(
            "MainBase",
            BindingFlags.Public | BindingFlags.Instance);
        if (controller == null || mainBaseProperty?.GetValue(controller, null) is not Component mainBase)
            return false;
        Vector3 screen = camera.WorldToScreenPoint(mainBase.transform.position);
        if (screen.z <= 0f) return false;
        screenPoint = new RailLayoutPoint(screen.x, Screen.height - screen.y);
        return true;
    }

    private static int? ReadId(PropertyInfo property, object target)
    {
        try { return property.GetValue(target, null) is int id ? id : null; }
        catch { return null; }
    }

    private static string GridKey(JToken? grid) =>
        (grid?["x"]?.Value<int?>() ?? int.MinValue) + "," +
        (grid?["y"]?.Value<int?>() ?? int.MinValue);

    private static Type? FindType(string fullName)
    {
        foreach (Assembly assembly in AppDomain.CurrentDomain.GetAssemblies())
        {
            Type? type = assembly.GetType(fullName, false);
            if (type != null) return type;
        }
        return null;
    }
}
