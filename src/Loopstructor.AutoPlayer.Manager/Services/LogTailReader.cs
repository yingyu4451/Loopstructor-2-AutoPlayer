using System.Text;

namespace Loopstructor.AutoPlayer.Manager.Services;

public sealed class LogTailReader
{
    private string _path = string.Empty;
    private long _position;

    public void Reset(string path, bool startAtEnd = false)
    {
        _path = Path.GetFullPath(path);
        _position = startAtEnd && File.Exists(_path)
            ? new FileInfo(_path).Length
            : 0;
    }

    public IReadOnlyList<string> ReadAvailable(int maximumLines = 250)
    {
        if (string.IsNullOrWhiteSpace(_path) || !File.Exists(_path))
        {
            return Array.Empty<string>();
        }

        using FileStream stream = new(_path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        if (stream.Length < _position)
        {
            _position = 0;
        }

        stream.Position = _position;
        using StreamReader reader = new(stream, Encoding.UTF8, detectEncodingFromByteOrderMarks: true, leaveOpen: true);
        List<string> lines = new();
        while (lines.Count < Math.Max(1, maximumLines) && reader.ReadLine() is { } line)
        {
            lines.Add(line);
        }

        _position = stream.Position;
        return lines;
    }
}
