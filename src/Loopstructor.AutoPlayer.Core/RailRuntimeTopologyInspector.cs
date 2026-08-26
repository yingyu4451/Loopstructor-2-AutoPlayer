using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Newtonsoft.Json.Linq;

namespace Loopstructor.AutoPlayer.Core;

public sealed class RailRuntimeValidation
{
    public int RailInstanceId { get; set; }
    public RailLoopValidationResult Loop { get; set; } = new();
    public string Fingerprint { get; set; } = string.Empty;
}

public sealed class RailRuntimeTopologyInspection
{
    public bool HasRails { get; set; }
    public bool AllValid { get; set; }
    public string Fingerprint { get; set; } = string.Empty;
    public string Detail { get; set; } = string.Empty;
    public IReadOnlyList<RailRuntimeValidation> Rails { get; set; } = Array.Empty<RailRuntimeValidation>();
}

/// <summary>Converts the exact queryRail station and segment facts into the shared cycle validator.</summary>
public static class RailRuntimeTopologyInspector
{
    public static RailRuntimeTopologyInspection Inspect(JObject? result)
    {
        JObject state = result?.SelectToken("data.state") as JObject ??
                        result?["state"] as JObject ?? result ?? new JObject();
        JObject[] rails = (state["rails"] as JArray)?.OfType<JObject>()
            .OrderBy(rail => ReadInt(rail["instanceId"]))
            .ToArray() ?? Array.Empty<JObject>();
        RailRuntimeValidation[] validations = rails.Select(InspectRail).ToArray();
        bool valid = validations.Length > 0 && validations.All(item => item.Loop.IsValid);
        string detail = validations.Length == 0
            ? "当前没有可验证的轨道。"
            : valid
                ? $"已验证 {validations.Length} 条轨道均为包围基地的单一简单闭环。"
                : string.Join(" ", validations.Where(item => !item.Loop.IsValid)
                    .Select(item => $"轨道 {item.RailInstanceId}：{string.Join("；", item.Loop.Errors)}"));
        return new RailRuntimeTopologyInspection
        {
            HasRails = validations.Length > 0,
            AllValid = valid,
            Fingerprint = string.Join("|", validations.Select(item => item.Fingerprint)),
            Detail = detail,
            Rails = validations
        };
    }

    public static RailRuntimeValidation InspectRail(JObject rail)
    {
        JObject[] stations = ((rail["orderedStations"] as JArray) ?? (rail["points"] as JArray))?
            .OfType<JObject>().Where(item => item != null).ToArray() ?? Array.Empty<JObject>();
        List<string> extractionErrors = new();
        List<RailLoopNode> nodes = new();
        Dictionary<string, int> idByGrid = new(StringComparer.Ordinal);
        foreach (JObject station in stations)
        {
            int id = ReadInt(station["pointId"],
                ReadInt(station["linePointInstanceId"], ReadInt(station["instanceId"])));
            if (!TryReadGrid(station["grid"], out RailLayoutPoint point))
            {
                extractionErrors.Add("站点缺少可验证的网格坐标。");
                continue;
            }
            string gridKey = GridKey(point);
            if (idByGrid.ContainsKey(gridKey))
                extractionErrors.Add("多个站点占用了同一网格坐标。");
            else
                idByGrid.Add(gridKey, id);
            nodes.Add(new RailLoopNode
            {
                Id = id,
                IsAttribute = station["isAttribute"]?.Value<bool>() == true,
                Point = point
            });
        }

        List<RailLoopEdge> edges = new();
        foreach (JObject line in (rail["lines"] as JArray)?.OfType<JObject>() ?? Enumerable.Empty<JObject>())
        {
            if (!TryReadGrid(line["from"], out RailLayoutPoint from) ||
                !TryReadGrid(line["to"], out RailLayoutPoint to) ||
                !idByGrid.TryGetValue(GridKey(from), out int fromId) ||
                !idByGrid.TryGetValue(GridKey(to), out int toId))
            {
                extractionErrors.Add("线段端点无法映射到当前轨道站点。");
                continue;
            }
            edges.Add(new RailLoopEdge { FromId = fromId, ToId = toId });
        }

        RailLoopValidationResult validation = RailLoopValidator.Validate(nodes, edges);
        if (extractionErrors.Count > 0)
        {
            validation.IsValid = false;
            validation.Errors = validation.Errors.Concat(extractionErrors)
                .Distinct(StringComparer.Ordinal).ToArray();
        }
        int railId = ReadInt(rail["instanceId"], ReadInt(rail["railInternalId"], ReadInt(rail["id"])));
        string nodeFingerprint = string.Join(",", nodes.OrderBy(node => node.Id)
            .Select(node => $"{node.Id}:{GridKey(node.Point)}:{(node.IsAttribute ? 1 : 0)}"));
        string edgeFingerprint = string.Join(",", edges.Select(edge => edge.FromId < edge.ToId
                ? (Left: edge.FromId, Right: edge.ToId)
                : (Left: edge.ToId, Right: edge.FromId))
            .OrderBy(edge => edge.Left).ThenBy(edge => edge.Right)
            .Select(edge => $"{edge.Left}-{edge.Right}"));
        return new RailRuntimeValidation
        {
            RailInstanceId = railId,
            Loop = validation,
            Fingerprint = $"{railId}[{nodeFingerprint}][{edgeFingerprint}]"
        };
    }

    private static int ReadInt(JToken? token, int fallback = 0) =>
        token?.Type == JTokenType.Integer ? token.Value<int>() : fallback;

    private static bool TryReadGrid(JToken? token, out RailLayoutPoint point)
    {
        point = default;
        double? x = token?["x"]?.Value<double?>();
        double? y = token?["y"]?.Value<double?>();
        if (!x.HasValue || !y.HasValue || double.IsNaN(x.Value) || double.IsNaN(y.Value) ||
            double.IsInfinity(x.Value) || double.IsInfinity(y.Value)) return false;
        point = new RailLayoutPoint(x.Value, y.Value);
        return true;
    }

    private static string GridKey(RailLayoutPoint point) =>
        point.X.ToString("0.######", CultureInfo.InvariantCulture) + "," +
        point.Y.ToString("0.######", CultureInfo.InvariantCulture);
}
