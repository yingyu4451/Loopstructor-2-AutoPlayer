using System.Diagnostics;
using Loopstructor.AutoPlayer.Manager.Models;
using Newtonsoft.Json;

namespace Loopstructor.AutoPlayer.Manager.Services;

public sealed class GameLauncher
{
    private readonly ActivationSessionFactory _sessionFactory;
    private readonly BepInExConfigWriter _configWriter;

    public GameLauncher(
        ActivationSessionFactory? sessionFactory = null,
        BepInExConfigWriter? configWriter = null)
    {
        _sessionFactory = sessionFactory ?? new ActivationSessionFactory();
        _configWriter = configWriter ?? new BepInExConfigWriter();
    }

    public GameLaunchResult Launch(GameInstallValidation game, string profileName)
    {
        if (!game.IsValid)
        {
            return new GameLaunchResult { Message = "The selected Skyspine build is not valid." };
        }

        ActivationSession? session = null;
        try
        {
            _configWriter.Write(game.GameRoot, game.AssemblySha256);
            session = _sessionFactory.Create(game, profileName);
            ProcessStartInfo startInfo = CreateStartInfo(game, session);

            Process? process = Process.Start(startInfo);
            if (process == null)
            {
                session.DeleteTicket();
                return new GameLaunchResult { Message = "Windows did not create the game process." };
            }

            session.ProcessId = process.Id;
            WriteLaunchMetadata(session, game);
            return new GameLaunchResult
            {
                Success = true,
                Message = $"Skyspine started (PID {process.Id}).",
                Session = session
            };
        }
        catch (Exception exception)
        {
            session?.DeleteTicket();
            return new GameLaunchResult { Message = "The game could not be started: " + exception.Message };
        }
    }

    internal static ProcessStartInfo CreateStartInfo(GameInstallValidation game, ActivationSession session)
    {
        ProcessStartInfo startInfo = new(game.ExecutablePath)
        {
            WorkingDirectory = game.GameRoot,
            UseShellExecute = false
        };
        startInfo.ArgumentList.Add("-logFile");
        startInfo.ArgumentList.Add(session.LogPath);

        // Steamworks accepts these development-session variables without a
        // steam_appid.txt file, so the selected QA build is not replaced by
        // Steam's installed depot when RestartAppIfNecessary runs.
        startInfo.Environment["SteamAppId"] = GameInstallValidator.ExpectedSteamAppId;
        startInfo.Environment["SteamGameId"] = GameInstallValidator.ExpectedSteamAppId;
        foreach ((string key, string value) in session.EnvironmentVariables)
        {
            startInfo.Environment[key] = value;
        }

        return startInfo;
    }

    private static void WriteLaunchMetadata(ActivationSession session, GameInstallValidation game)
    {
        object metadata = new
        {
            launchedAtUtc = DateTime.UtcNow,
            gameRoot = game.GameRoot,
            executable = game.ExecutablePath,
            expectedAssemblySha256 = game.AssemblySha256,
            assemblyMvid = game.AssemblyMvid,
            pipeName = session.Ticket.PipeName,
            profileRoot = session.Ticket.ProfileRoot,
            artifactRoot = session.Ticket.ArtifactRoot,
            steamAppId = GameInstallValidator.ExpectedSteamAppId,
            processId = session.ProcessId
        };
        AtomicFile.WriteAllText(
            Path.Combine(session.Ticket.ArtifactRoot, "launch.json"),
            JsonConvert.SerializeObject(metadata, Formatting.Indented));
    }
}
