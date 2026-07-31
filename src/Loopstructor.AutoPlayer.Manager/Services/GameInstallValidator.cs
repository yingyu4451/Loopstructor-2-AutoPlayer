using System.Diagnostics;
using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;
using System.Security.Cryptography;
using Loopstructor.AutoPlayer.Manager.Models;

namespace Loopstructor.AutoPlayer.Manager.Services;

public sealed class GameInstallValidator
{
    public const string ExpectedSteamAppId = "3841840";

    public async Task<GameInstallValidation> ValidateAsync(string candidateRoot, CancellationToken cancellationToken = default)
    {
        string root;
        try
        {
            root = Path.GetFullPath(candidateRoot ?? string.Empty)
                .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        }
        catch (Exception exception)
        {
            GameInstallValidation invalidPath = new() { GameRoot = candidateRoot ?? string.Empty };
            invalidPath.Errors.Add("所选路径无效。详细信息：" + exception.Message);
            return invalidPath;
        }

        GameInstallValidation result = new() { GameRoot = root };
        if (root.Any(character => character > 0x7f))
        {
            result.Errors.Add(
                "当前 BepInEx 与此 Unity Mono 构建不兼容包含中文或其他非 ASCII 字符的完整游戏路径。" +
                "游戏本体本身不受此限制；请仅将测试包移动到仅含 ASCII 字符的路径后重新选择（可包含英文字母、数字和空格）。" +
                "当前路径：" + root);
            return result;
        }

        if (!Directory.Exists(root))
        {
            result.Errors.Add("所选游戏目录不存在。");
            return result;
        }

        string[] managedAssemblies;
        try
        {
            managedAssemblies = Directory.GetDirectories(root, "*_Data", SearchOption.TopDirectoryOnly)
                .Select(directory => Path.Combine(directory, "Managed", "Assembly-CSharp.dll"))
                .Where(File.Exists)
                .ToArray();
        }
        catch (Exception exception)
        {
            result.Errors.Add("无法检查游戏目录。详细信息：" + exception.Message);
            return result;
        }

        if (managedAssemblies.Length != 1)
        {
            result.Errors.Add($"应当只存在一个 *_Data/Managed/Assembly-CSharp.dll，实际找到 {managedAssemblies.Length} 个。");
            return result;
        }

        result.AssemblyPath = managedAssemblies[0];
        result.DataDirectory = Directory.GetParent(Directory.GetParent(result.AssemblyPath)!.FullName)!.FullName;
        string dataName = Path.GetFileName(result.DataDirectory);
        string executableStem = dataName.EndsWith("_Data", StringComparison.OrdinalIgnoreCase)
            ? dataName[..^5]
            : string.Empty;
        result.ExecutablePath = Path.Combine(root, executableStem + ".exe");

        if (!File.Exists(result.ExecutablePath))
        {
            result.Errors.Add("缺少与 Unity 数据目录对应的游戏可执行文件。");
        }

        if (!File.Exists(Path.Combine(root, "UnityPlayer.dll")))
        {
            result.Errors.Add("游戏构建根目录中缺少 UnityPlayer.dll。");
        }

        if (!File.Exists(Path.Combine(result.DataDirectory, "globalgamemanagers")))
        {
            result.Errors.Add("缺少 Unity globalgamemanagers 文件。");
        }

        string steamAppIdPath = Path.Combine(root, "steam_appid.txt");
        if (File.Exists(steamAppIdPath))
        {
            try
            {
                result.SteamAppId = (await File.ReadAllTextAsync(steamAppIdPath, cancellationToken)).Trim();
                if (!string.Equals(result.SteamAppId, ExpectedSteamAppId, StringComparison.Ordinal))
                {
                    result.Errors.Add($"steam_appid.txt 标识的应用为 {result.SteamAppId}，不是 Skyspine 应用 {ExpectedSteamAppId}。");
                }
            }
            catch (Exception exception)
            {
                result.Errors.Add("无法读取 steam_appid.txt。详细信息：" + exception.Message);
            }
        }
        else
        {
            result.Warnings.Add(
                "未找到 steam_appid.txt；完成可执行文件和程序集校验后，Manager 将仅为本次进程设置 Skyspine AppID。");
        }

        if (File.Exists(result.ExecutablePath))
        {
            try
            {
                FileVersionInfo version = FileVersionInfo.GetVersionInfo(result.ExecutablePath);
                result.ProductName = version.ProductName ?? string.Empty;
                result.ProductVersion = version.ProductVersion ?? version.FileVersion ?? string.Empty;
            }
            catch (Exception exception)
            {
                result.Warnings.Add("无法读取可执行文件的版本元数据。详细信息：" + exception.Message);
            }
        }

        bool skyspineIdentity = executableStem.Contains("Skyspine", StringComparison.OrdinalIgnoreCase)
                                || result.ProductName.Contains("Skyspine", StringComparison.OrdinalIgnoreCase);
        if (!skyspineIdentity)
        {
            result.Errors.Add("该构建未标识为 Loopstructor 2: Skyspine；不接受旧版 Loopstructor 构建。");
        }

        if (result.Errors.Count > 0)
        {
            return result;
        }

        try
        {
            (bool contractPresent, string mvid) = ReadAutomationContract(result.AssemblyPath);
            result.AssemblyMvid = mvid;
            if (!contractPresent)
            {
                result.Errors.Add("Assembly-CSharp.dll 不包含 Skyspine GuiGameAutomation 运行时合同。");
                return result;
            }

            result.AssemblySha256 = await ComputeSha256Async(result.AssemblyPath, cancellationToken);
        }
        catch (Exception exception)
        {
            result.Errors.Add("无法验证 Assembly-CSharp.dll。详细信息：" + exception.Message);
        }

        return result;
    }

    public static async Task<string> ComputeSha256Async(string path, CancellationToken cancellationToken = default)
    {
        await using FileStream stream = new(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        using SHA256 sha256 = SHA256.Create();
        byte[] digest = await sha256.ComputeHashAsync(stream, cancellationToken);
        return Convert.ToHexString(digest).ToLowerInvariant();
    }

    private static (bool ContractPresent, string Mvid) ReadAutomationContract(string assemblyPath)
    {
        using FileStream stream = File.Open(assemblyPath, FileMode.Open, FileAccess.Read, FileShare.Read);
        using PEReader peReader = new(stream, PEStreamOptions.LeaveOpen);
        if (!peReader.HasMetadata)
        {
            return (false, string.Empty);
        }

        MetadataReader metadata = peReader.GetMetadataReader();
        bool hasStateRuntime = false;
        bool hasStartRuntime = false;
        bool hasResult = false;
        foreach (TypeDefinitionHandle handle in metadata.TypeDefinitions)
        {
            TypeDefinition definition = metadata.GetTypeDefinition(handle);
            string typeNamespace = metadata.GetString(definition.Namespace);
            if (!string.Equals(typeNamespace, "GuiGameAutomation.Runtime", StringComparison.Ordinal))
            {
                continue;
            }

            string name = metadata.GetString(definition.Name);
            hasStateRuntime |= string.Equals(name, "GuiGameMcpStateRuntime", StringComparison.Ordinal);
            hasStartRuntime |= string.Equals(name, "GuiGameMcpStartFlowRuntime", StringComparison.Ordinal);
            hasResult |= string.Equals(name, "GuiGameMcpResult", StringComparison.Ordinal);
        }

        ModuleDefinition module = metadata.GetModuleDefinition();
        string mvid = module.Mvid.IsNil ? string.Empty : metadata.GetGuid(module.Mvid).ToString("D");
        return (hasStateRuntime && hasStartRuntime && hasResult, mvid);
    }
}
