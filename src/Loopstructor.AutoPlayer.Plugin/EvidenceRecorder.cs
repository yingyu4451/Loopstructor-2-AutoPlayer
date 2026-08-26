using System;
using System.IO;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using Loopstructor.AutoPlayer.Core;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using UnityEngine;

namespace Loopstructor.AutoPlayer.Plugin;

internal sealed class EvidenceRecorder
{
    private readonly string _root;

    public EvidenceRecorder(string root)
    {
        _root = Path.GetFullPath(root);
        Directory.CreateDirectory(_root);
    }

    public string CreateRunDirectory()
    {
        string path = Path.Combine(_root, "run-" + DateTime.UtcNow.ToString("yyyyMMdd-HHmmss-fff") + "Z");
        Directory.CreateDirectory(path);
        return path;
    }

    public void WriteStatus(string directory, object status)
    {
        if (string.IsNullOrWhiteSpace(directory)) return;
        Directory.CreateDirectory(directory);
        AtomicWrite(Path.Combine(directory, "status.json"), JsonConvert.SerializeObject(status, Formatting.Indented));
    }

    public string CaptureFailure(string directory, string reason, object status, JObject? runtimeResult = null)
    {
        Directory.CreateDirectory(directory);
        string timestamp = DateTime.UtcNow.ToString("yyyyMMdd-HHmmss-fff");
        AtomicWrite(Path.Combine(directory, timestamp + "-failure.txt"), reason + Environment.NewLine);
        if (runtimeResult != null)
        {
            AtomicWrite(
                Path.Combine(directory, timestamp + "-runtime-result.json"),
                runtimeResult.ToString(Formatting.Indented));
        }
        WriteStatus(directory, status);
        string screenshot = Path.Combine(directory, timestamp + "-screen.png");
        try
        {
            ScreenCapture.CaptureScreenshot(screenshot);
        }
        catch (Exception exception)
        {
            AtomicWrite(Path.Combine(directory, timestamp + "-screenshot-error.txt"), exception.ToString());
        }

        return screenshot;
    }

    public string CaptureCompletion(string directory, object status)
    {
        Directory.CreateDirectory(directory);
        string timestamp = DateTime.UtcNow.ToString("yyyyMMdd-HHmmss-fff");
        WriteStatus(directory, status);
        return CaptureScreenshot(directory, timestamp + "-complete");
    }

    public void CaptureRailTopology(
        string directory,
        string fingerprint,
        RailRuntimeTopologyInspection topology,
        IReadOnlyList<RailVisualNode> nodes,
        IReadOnlyList<RailVisualEdge> edges,
        RailLoopValidationResult? screenValidation,
        string projectionMessage)
    {
        if (string.IsNullOrWhiteSpace(directory)) return;
        Directory.CreateDirectory(directory);
        string timestamp = DateTime.UtcNow.ToString("yyyyMMdd-HHmmss-fff");
        string stem = timestamp + "-rail-topology";
        string screenshot = CaptureScreenshot(directory, stem);
        AtomicWrite(Path.Combine(directory, stem + ".json"), JsonConvert.SerializeObject(new
        {
            fingerprint,
            topology,
            projectionMessage,
            screenValidation,
            nodes,
            edges
        }, Formatting.Indented));

        int width = Math.Max(1, Screen.width);
        int height = Math.Max(1, Screen.height);
        StringBuilder svg = new();
        svg.AppendLine($"<svg xmlns=\"http://www.w3.org/2000/svg\" width=\"{width}\" height=\"{height}\" viewBox=\"0 0 {width} {height}\">");
        svg.AppendLine($"<image href=\"{Path.GetFileName(screenshot)}\" width=\"{width}\" height=\"{height}\" />");
        foreach (RailVisualEdge edge in edges)
        {
            string color = edge.IsValid ? "#79D53B" : "#E2473F";
            svg.AppendLine($"<line x1=\"{edge.FromX:0.##}\" y1=\"{edge.FromY:0.##}\" x2=\"{edge.ToX:0.##}\" y2=\"{edge.ToY:0.##}\" stroke=\"{color}\" stroke-width=\"3\" />");
        }
        foreach (RailVisualNode node in nodes)
        {
            svg.AppendLine($"<circle cx=\"{node.X:0.##}\" cy=\"{node.Y:0.##}\" r=\"10\" fill=\"#11110F\" stroke=\"#D7A84B\" stroke-width=\"2\" />");
            svg.AppendLine($"<text x=\"{node.X:0.##}\" y=\"{node.Y + 5:0.##}\" fill=\"#FFFFFF\" text-anchor=\"middle\" font-size=\"12\">{node.PointId}</text>");
        }
        svg.AppendLine("</svg>");
        AtomicWrite(Path.Combine(directory, stem + "-overlay.svg"), svg.ToString());
    }

    public static void AtomicWrite(string path, string content)
    {
        string temp = path + ".tmp";
        if (File.Exists(temp)) File.Delete(temp);
        File.WriteAllText(temp, content);
        if (File.Exists(path)) File.Delete(path);
        File.Move(temp, path);
    }

    private static string CaptureScreenshot(string directory, string fileName)
    {
        string screenshot = Path.Combine(directory, fileName + ".png");
        try
        {
            ScreenCapture.CaptureScreenshot(screenshot);
        }
        catch (Exception exception)
        {
            AtomicWrite(Path.Combine(directory, fileName + "-screenshot-error.txt"), exception.ToString());
        }

        return screenshot;
    }
}

internal sealed class RailVisualNode
{
    public int PointId { get; set; }
    public double X { get; set; }
    public double Y { get; set; }
}

internal sealed class RailVisualEdge
{
    public int FromPointId { get; set; }
    public int ToPointId { get; set; }
    public double FromX { get; set; }
    public double FromY { get; set; }
    public double ToX { get; set; }
    public double ToY { get; set; }
    public bool IsValid { get; set; }
}
