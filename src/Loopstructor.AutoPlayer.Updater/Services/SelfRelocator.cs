using System.Diagnostics;
using System.Reflection;

namespace Loopstructor.AutoPlayer.Updater.Services;

public sealed class SelfRelocator
{
    private const string TemporaryFolderName = "LoopstructorAutoPlayerUpdater";

    public bool RelaunchFromTemporaryCopy(IReadOnlyList<string> originalArguments)
    {
        string sourceRoot = Path.GetFullPath(AppContext.BaseDirectory)
            .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        string temporaryBase = Path.Combine(Path.GetTempPath(), TemporaryFolderName);
        Directory.CreateDirectory(temporaryBase);
        CleanupOldCopies(temporaryBase, sourceRoot);
        string destinationRoot = Path.Combine(temporaryBase, "updater-" + Guid.NewGuid().ToString("N"));
        CopyDirectory(sourceRoot, destinationRoot);

        string entryAssembly = Assembly.GetEntryAssembly()?.Location
                               ?? throw new InvalidOperationException("Updater entry assembly could not be located.");
        string relativeAssembly = Path.GetRelativePath(sourceRoot, entryAssembly);
        string copiedAssembly = Path.Combine(destinationRoot, relativeAssembly);
        string copiedExecutable = Path.ChangeExtension(copiedAssembly, ".exe");
        ProcessStartInfo startInfo;
        if (File.Exists(copiedExecutable))
        {
            startInfo = new ProcessStartInfo(copiedExecutable);
        }
        else
        {
            string processPath = Environment.ProcessPath
                                 ?? throw new InvalidOperationException(".NET host path could not be determined.");
            startInfo = new ProcessStartInfo(processPath);
            startInfo.ArgumentList.Add(copiedAssembly);
        }

        startInfo.WorkingDirectory = destinationRoot;
        startInfo.UseShellExecute = false;
        foreach (string argument in originalArguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        startInfo.ArgumentList.Add("--staged-run");
        Process? process = Process.Start(startInfo);
        if (process == null)
        {
            throw new InvalidOperationException("Windows did not start the temporary updater copy.");
        }

        return true;
    }

    private static void CopyDirectory(string sourceRoot, string destinationRoot)
    {
        DirectoryInfo source = new(sourceRoot);
        if (source.Attributes.HasFlag(FileAttributes.ReparsePoint))
        {
            throw new InvalidOperationException("Updater source directory cannot be a reparse point.");
        }

        Directory.CreateDirectory(destinationRoot);
        foreach (string directory in Directory.GetDirectories(sourceRoot, "*", SearchOption.AllDirectories))
        {
            DirectoryInfo info = new(directory);
            if (info.Attributes.HasFlag(FileAttributes.ReparsePoint))
            {
                throw new InvalidOperationException("Updater source contains a reparse point: " + directory);
            }

            Directory.CreateDirectory(Path.Combine(destinationRoot, Path.GetRelativePath(sourceRoot, directory)));
        }

        foreach (string file in Directory.GetFiles(sourceRoot, "*", SearchOption.AllDirectories))
        {
            string destination = Path.Combine(destinationRoot, Path.GetRelativePath(sourceRoot, file));
            Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
            File.Copy(file, destination, overwrite: false);
        }
    }

    private static void CleanupOldCopies(string temporaryBase, string currentSource)
    {
        string safePrefix = Path.GetFullPath(temporaryBase)
            .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;
        foreach (string directory in Directory.GetDirectories(temporaryBase, "updater-*", SearchOption.TopDirectoryOnly))
        {
            try
            {
                string full = Path.GetFullPath(directory);
                if (!full.StartsWith(safePrefix, StringComparison.OrdinalIgnoreCase)
                    || string.Equals(full, currentSource, StringComparison.OrdinalIgnoreCase)
                    || Directory.GetCreationTimeUtc(full) > DateTime.UtcNow.AddDays(-7))
                {
                    continue;
                }

                Directory.Delete(full, recursive: true);
            }
            catch
            {
                // Old temporary copies are non-authoritative and may still be in use.
            }
        }
    }
}
