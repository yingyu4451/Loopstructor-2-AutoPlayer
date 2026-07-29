using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;

namespace Loopstructor.AutoPlayer.Updater.Services;

public sealed class UpdateTargetLock : IDisposable
{
    private readonly FileStream _stream;

    private UpdateTargetLock(string path, FileStream stream)
    {
        Path = path;
        _stream = stream;
    }

    public string Path { get; }

    public static UpdateTargetLock Acquire(string targetRoot, TimeSpan timeout)
    {
        string target = ReleasePackageValidator.NormalizeRoot(targetRoot);
        string parent = Directory.GetParent(target)?.FullName
                        ?? throw new InvalidOperationException("Update target has no parent directory.");
        string identity = Convert.ToHexString(SHA256.HashData(
            Encoding.UTF8.GetBytes(target.ToUpperInvariant()))).ToLowerInvariant()[..24];
        string lockPath = System.IO.Path.Combine(parent, ".LoopstructorAutoPlayer-update-" + identity + ".lock");
        Stopwatch stopwatch = Stopwatch.StartNew();
        while (true)
        {
            try
            {
                FileStream stream = new(
                    lockPath,
                    FileMode.OpenOrCreate,
                    FileAccess.ReadWrite,
                    FileShare.None,
                    1,
                    FileOptions.WriteThrough);
                stream.SetLength(0);
                using StreamWriter writer = new(stream, Encoding.UTF8, leaveOpen: true);
                writer.Write($"pid={Environment.ProcessId}; acquiredUtc={DateTime.UtcNow:O}");
                writer.Flush();
                stream.Flush(flushToDisk: true);
                return new UpdateTargetLock(lockPath, stream);
            }
            catch (IOException) when (stopwatch.Elapsed < timeout)
            {
                Thread.Sleep(200);
            }
        }
    }

    public void Dispose()
    {
        _stream.Dispose();
        try { File.Delete(Path); } catch { }
    }
}
