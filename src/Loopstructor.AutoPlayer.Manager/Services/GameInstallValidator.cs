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
            invalidPath.Errors.Add("The selected path is invalid: " + exception.Message);
            return invalidPath;
        }

        GameInstallValidation result = new() { GameRoot = root };
        if (!Directory.Exists(root))
        {
            result.Errors.Add("The selected game directory does not exist.");
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
            result.Errors.Add("The game directory could not be inspected: " + exception.Message);
            return result;
        }

        if (managedAssemblies.Length != 1)
        {
            result.Errors.Add($"Expected exactly one *_Data/Managed/Assembly-CSharp.dll, found {managedAssemblies.Length}.");
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
            result.Errors.Add("The executable matching the Unity data directory is missing.");
        }

        if (!File.Exists(Path.Combine(root, "UnityPlayer.dll")))
        {
            result.Errors.Add("UnityPlayer.dll is missing from the build root.");
        }

        if (!File.Exists(Path.Combine(result.DataDirectory, "globalgamemanagers")))
        {
            result.Errors.Add("The Unity globalgamemanagers file is missing.");
        }

        string steamAppIdPath = Path.Combine(root, "steam_appid.txt");
        if (File.Exists(steamAppIdPath))
        {
            try
            {
                result.SteamAppId = (await File.ReadAllTextAsync(steamAppIdPath, cancellationToken)).Trim();
                if (!string.Equals(result.SteamAppId, ExpectedSteamAppId, StringComparison.Ordinal))
                {
                    result.Errors.Add($"steam_appid.txt identifies {result.SteamAppId}, not Skyspine app {ExpectedSteamAppId}.");
                }
            }
            catch (Exception exception)
            {
                result.Errors.Add("steam_appid.txt could not be read: " + exception.Message);
            }
        }
        else
        {
            result.Warnings.Add(
                "steam_appid.txt is absent; Manager will use a process-scoped Skyspine AppID after executable and assembly validation.");
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
                result.Warnings.Add("Executable version metadata could not be read: " + exception.Message);
            }
        }

        bool skyspineIdentity = executableStem.Contains("Skyspine", StringComparison.OrdinalIgnoreCase)
                                || result.ProductName.Contains("Skyspine", StringComparison.OrdinalIgnoreCase);
        if (!skyspineIdentity)
        {
            result.Errors.Add("The build does not identify itself as Loopstructor 2: Skyspine; legacy Loopstructor builds are rejected.");
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
                result.Errors.Add("Assembly-CSharp.dll does not contain the Skyspine GuiGameAutomation runtime contract.");
                return result;
            }

            result.AssemblySha256 = await ComputeSha256Async(result.AssemblyPath, cancellationToken);
        }
        catch (Exception exception)
        {
            result.Errors.Add("Assembly-CSharp.dll could not be verified: " + exception.Message);
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
