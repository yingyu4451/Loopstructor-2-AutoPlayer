using Loopstructor.AutoPlayer.Manager.Models;
using Newtonsoft.Json.Linq;

namespace Loopstructor.AutoPlayer.Manager.Services;

public sealed class UnityProjectBridgeInstaller
{
    public const string PackageName = "com.loopstructor.qa-editor-bridge";

    private readonly DistributionLayout _layout;

    public UnityProjectBridgeInstaller(DistributionLayout layout)
    {
        _layout = layout ?? throw new ArgumentNullException(nameof(layout));
    }

    public UnityProjectInspection Inspect(string candidateRoot)
    {
        if (!TryNormalizeProject(candidateRoot, out string root, out string error))
            return Invalid(candidateRoot, error);

        string versionFile = Path.Combine(root, "ProjectSettings", "ProjectVersion.txt");
        if (!Directory.Exists(Path.Combine(root, "Assets"))
            || !Directory.Exists(Path.Combine(root, "Packages"))
            || !File.Exists(versionFile))
        {
            return Invalid(root, "所选目录不是完整的 Unity 工程，应包含 Assets、Packages 和 ProjectSettings/ProjectVersion.txt。");
        }

        string version = ReadUnityVersion(versionFile);
        string packageRoot = GetPackageRoot(root);
        bool installed = Directory.Exists(packageRoot) && IsOwnedPackage(packageRoot);
        string message = installed
            ? "Unity 工程可用，Editor 连接组件已安装。"
            : Directory.Exists(packageRoot)
                ? "Unity 工程可用，但目标包目录属于其他包。"
                : "Unity 工程可用，Editor 连接组件未安装。";
        return new UnityProjectInspection
        {
            Path = root,
            Valid = true,
            UnityVersion = version,
            BridgeInstalled = installed,
            Message = message
        };
    }

    public EditorBridgeOperationResult Install(string candidateRoot)
    {
        UnityProjectInspection inspection = Inspect(candidateRoot);
        if (!inspection.Valid) return EditorBridgeOperationResult.Fail(inspection.Message);

        string sourceRoot = ResolvePackageSource();
        string packageRoot = GetPackageRoot(inspection.Path);
        if (Directory.Exists(packageRoot) && !IsOwnedPackage(packageRoot))
            return EditorBridgeOperationResult.Fail("目标 Packages 目录已存在，但不属于 Loopstructor Editor Bridge，已拒绝覆盖。");

        EnsureRequiredPayload();
        bool updating = Directory.Exists(packageRoot);
        string packagesRoot = Path.GetDirectoryName(packageRoot)!;
        string stagingRoot = Path.Combine(packagesRoot, "." + PackageName + ".stage-" + Guid.NewGuid().ToString("N"));
        string backupRoot = Path.Combine(packagesRoot, "." + PackageName + ".backup-" + Guid.NewGuid().ToString("N"));
        try
        {
            CopyDirectory(sourceRoot, stagingRoot);
            NormalizeUnityTextEncoding(stagingRoot);
            CopyManagedDependencies(stagingRoot);
            if (!IsOwnedPackage(stagingRoot)) throw new InvalidDataException("Editor Bridge 源包清单无效。");

            if (updating) Directory.Move(packageRoot, backupRoot);
            Directory.Move(stagingRoot, packageRoot);
            TryDeleteDirectory(backupRoot);
            UnityProjectInspection installed = Inspect(inspection.Path);
            return new EditorBridgeOperationResult
            {
                Success = true,
                Message = updating
                    ? "Editor 连接组件已更新。重新聚焦 Unity，等待脚本编译完成。"
                    : "Editor 连接组件已安装。重新聚焦 Unity，等待脚本编译完成。",
                Inspection = installed
            };
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidDataException)
        {
            if (Directory.Exists(backupRoot))
            {
                TryDeleteDirectory(packageRoot);
                if (!Directory.Exists(packageRoot)) Directory.Move(backupRoot, packageRoot);
            }
            return EditorBridgeOperationResult.Fail("无法安装 Editor Bridge。详细信息：" + exception.Message);
        }
        finally
        {
            TryDeleteDirectory(stagingRoot);
            TryDeleteDirectory(backupRoot);
        }
    }

    public EditorBridgeOperationResult Uninstall(string candidateRoot)
    {
        UnityProjectInspection inspection = Inspect(candidateRoot);
        if (!inspection.Valid) return EditorBridgeOperationResult.Fail(inspection.Message);
        string packageRoot = GetPackageRoot(inspection.Path);
        if (!Directory.Exists(packageRoot))
            return new EditorBridgeOperationResult { Success = true, Message = "Editor 连接组件未安装。", Inspection = inspection };
        if (!IsOwnedPackage(packageRoot))
            return EditorBridgeOperationResult.Fail("目标 Packages 目录不属于 Loopstructor Editor Bridge，已拒绝删除。");
        try
        {
            Directory.Delete(packageRoot, recursive: true);
            return new EditorBridgeOperationResult
            {
                Success = true,
                Message = "Editor 连接组件已卸载。",
                Inspection = Inspect(inspection.Path)
            };
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return EditorBridgeOperationResult.Fail("无法卸载 Editor Bridge。详细信息：" + exception.Message);
        }
    }

    private string ResolvePackageSource()
    {
        string[] candidates =
        {
            Path.Combine(_layout.Root, "resources", "unity-package", PackageName),
            Path.Combine(_layout.Root, "manager", "resources", "unity-package", PackageName)
        };
        return candidates.FirstOrDefault(Directory.Exists)
               ?? throw new InvalidDataException("发布目录中缺少 Unity Editor Bridge 源包。");
    }

    private void EnsureRequiredPayload()
    {
        string runtimeRoot = ResolveEditorRuntimePayloadRoot();
        foreach (string fileName in new[] { "Loopstructor.AutoPlayer.EditorBridge.Runtime.dll", "Loopstructor.AutoPlayer.Core.dll" })
        {
            if (!File.Exists(Path.Combine(runtimeRoot, fileName)))
                throw new InvalidDataException("Editor Bridge 缺少运行依赖：" + fileName);
        }
    }

    private void CopyManagedDependencies(string packageRoot)
    {
        string managedRoot = Path.Combine(packageRoot, "Editor", "Managed");
        Directory.CreateDirectory(managedRoot);
        string runtimeRoot = ResolveEditorRuntimePayloadRoot();
        File.Copy(Path.Combine(runtimeRoot, "Loopstructor.AutoPlayer.EditorBridge.Runtime.dll"), Path.Combine(managedRoot, "Loopstructor.AutoPlayer.EditorBridge.Runtime.dll"), true);
        File.Copy(Path.Combine(runtimeRoot, "Loopstructor.AutoPlayer.Core.dll"), Path.Combine(managedRoot, "Loopstructor.AutoPlayer.Core.dll"), true);
    }

    private string ResolveEditorRuntimePayloadRoot()
    {
        string[] candidates =
        {
            Path.Combine(_layout.PayloadRoot, "editor"),
            Path.Combine(_layout.Root, "src", "Loopstructor.AutoPlayer.EditorBridge.Runtime", "bin", "Release", "netstandard2.1"),
            Path.Combine(_layout.Root, "artifacts", "package", "Loopstructor-2-QA-Tool", "payload", "editor")
        };
        return candidates.FirstOrDefault(path => File.Exists(Path.Combine(path, "Loopstructor.AutoPlayer.EditorBridge.Runtime.dll")))
               ?? candidates[0];
    }

    private static string ReadUnityVersion(string path)
    {
        foreach (string line in File.ReadLines(path))
        {
            const string prefix = "m_EditorVersion:";
            if (line.TrimStart().StartsWith(prefix, StringComparison.Ordinal)) return line[(line.IndexOf(prefix, StringComparison.Ordinal) + prefix.Length)..].Trim();
        }
        return string.Empty;
    }

    private static bool IsOwnedPackage(string root)
    {
        try
        {
            if (new DirectoryInfo(root).Attributes.HasFlag(FileAttributes.ReparsePoint)) return false;
            string manifest = Path.Combine(root, "package.json");
            return File.Exists(manifest)
                   && string.Equals(JObject.Parse(File.ReadAllText(manifest)).Value<string>("name"), PackageName, StringComparison.Ordinal);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or Newtonsoft.Json.JsonException)
        {
            return false;
        }
    }

    private static string GetPackageRoot(string projectRoot) => Path.Combine(projectRoot, "Packages", PackageName);

    private static bool TryNormalizeProject(string candidate, out string root, out string error)
    {
        try
        {
            root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(candidate ?? string.Empty));
            error = Directory.Exists(root) ? string.Empty : "所选 Unity 工程目录不存在。";
            return error.Length == 0;
        }
        catch (Exception exception)
        {
            root = candidate ?? string.Empty;
            error = "Unity 工程路径无效。详细信息：" + exception.Message;
            return false;
        }
    }

    private static UnityProjectInspection Invalid(string path, string message) => new() { Path = path ?? string.Empty, Message = message };

    private static void CopyDirectory(string sourceRoot, string destinationRoot)
    {
        Directory.CreateDirectory(destinationRoot);
        foreach (string source in Directory.EnumerateFiles(sourceRoot, "*", SearchOption.AllDirectories))
        {
            FileInfo file = new(source);
            if (file.Attributes.HasFlag(FileAttributes.ReparsePoint)) throw new InvalidDataException("Editor Bridge 源包不能包含重解析点。");
            string relative = Path.GetRelativePath(sourceRoot, source);
            string destination = Path.Combine(destinationRoot, relative);
            Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
            file.CopyTo(destination, true);
        }
    }

    private static void NormalizeUnityTextEncoding(string root)
    {
        HashSet<string> textExtensions = new(StringComparer.OrdinalIgnoreCase)
        {
            ".cs", ".json", ".asmdef", ".asmref", ".md"
        };
        foreach (string path in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories))
        {
            if (!textExtensions.Contains(Path.GetExtension(path))) continue;
            string content = File.ReadAllText(path);
            File.WriteAllText(path, content, new System.Text.UTF8Encoding(encoderShouldEmitUTF8Identifier: true));
        }
    }

    private static void TryDeleteDirectory(string path)
    {
        try { if (Directory.Exists(path)) Directory.Delete(path, recursive: true); } catch { }
    }
}
