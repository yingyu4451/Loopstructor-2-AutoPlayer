using System;
using System.IO;
using System.Text;
using Newtonsoft.Json;

namespace Loopstructor.AutoPlayer.Core;

/// <summary>
/// Persists a fail-closed marker inside an isolated QA profile before a cheat
/// write is allowed to reach the game. Marker presence alone is authoritative:
/// even an incomplete file left by a process crash permanently taints that
/// profile for normal automation.
/// </summary>
public static class CheatProfileTaintMarker
{
    public const string FileName = ".loopstructor-autoplayer-cheat-tainted.json";

    public static string GetPath(string profileRoot) =>
        Path.Combine(Path.GetFullPath(profileRoot), FileName);

    public static bool IsTainted(string profileRoot)
    {
        try
        {
            string fullProfileRoot = Path.GetFullPath(profileRoot);
            FileAttributes rootAttributes;
            try
            {
                rootAttributes = File.GetAttributes(fullProfileRoot);
            }
            catch (FileNotFoundException)
            {
                return false;
            }
            catch (DirectoryNotFoundException)
            {
                return false;
            }

            if ((rootAttributes & FileAttributes.Directory) == 0)
            {
                return true;
            }

            return Probe(Path.Combine(fullProfileRoot, FileName), out _) != MarkerPresence.Absent;
        }
        catch
        {
            // Callers only pass an already validated profile root. If that root
            // later becomes unreadable, it must not be treated as a clean save.
            return true;
        }
    }

    public static bool TryMark(
        string profileRoot,
        string requestId,
        string command,
        out string error)
    {
        error = string.Empty;
        string? markerPath = null;
        try
        {
            string fullProfileRoot = Path.GetFullPath(profileRoot);
            markerPath = Path.Combine(fullProfileRoot, FileName);
            MarkerPresence initialPresence = Probe(markerPath, out string probeError);
            if (initialPresence == MarkerPresence.Present)
            {
                return true;
            }

            if (initialPresence == MarkerPresence.Unknown)
            {
                error = probeError;
                return false;
            }

            Directory.CreateDirectory(fullProfileRoot);
            byte[] payload = new UTF8Encoding(false).GetBytes(JsonConvert.SerializeObject(new
            {
                schemaVersion = 1,
                taintedUtc = DateTime.UtcNow,
                requestId = Limit(requestId, 256),
                command = Limit(command, 256)
            }));

            // CreateNew is the atomic boundary. A crash after this point may
            // leave partial JSON, but marker presence still means "tainted".
            using (FileStream stream = new(
                       markerPath,
                       FileMode.CreateNew,
                       FileAccess.Write,
                       FileShare.Read,
                       4096,
                       FileOptions.WriteThrough))
            {
                stream.Write(payload, 0, payload.Length);
                stream.Flush(true);
            }

            return true;
        }
        catch (Exception exception)
        {
            // Another process or a late I/O failure may have created the
            // marker. Only confirmed presence permits the game write; an
            // unreadable/unknown state remains blocked.
            if (markerPath != null && Probe(markerPath, out _) == MarkerPresence.Present)
            {
                return true;
            }

            error = exception.Message;
            return false;
        }
    }

    private static MarkerPresence Probe(string markerPath, out string error)
    {
        try
        {
            _ = File.GetAttributes(markerPath);
            error = string.Empty;
            return MarkerPresence.Present;
        }
        catch (FileNotFoundException)
        {
            error = string.Empty;
            return MarkerPresence.Absent;
        }
        catch (DirectoryNotFoundException)
        {
            error = string.Empty;
            return MarkerPresence.Absent;
        }
        catch (Exception exception)
        {
            error = exception.Message;
            return MarkerPresence.Unknown;
        }
    }

    private static string Limit(string? value, int maximumLength)
    {
        string result = value ?? string.Empty;
        return result.Length <= maximumLength ? result : result.Substring(0, maximumLength);
    }

    private enum MarkerPresence
    {
        Absent,
        Present,
        Unknown
    }
}
