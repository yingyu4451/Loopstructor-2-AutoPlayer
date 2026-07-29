using System;
using System.IO;
using Newtonsoft.Json;
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

    public string CaptureFailure(string directory, string reason, object status)
    {
        Directory.CreateDirectory(directory);
        string timestamp = DateTime.UtcNow.ToString("yyyyMMdd-HHmmss-fff");
        AtomicWrite(Path.Combine(directory, timestamp + "-failure.txt"), reason + Environment.NewLine);
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
