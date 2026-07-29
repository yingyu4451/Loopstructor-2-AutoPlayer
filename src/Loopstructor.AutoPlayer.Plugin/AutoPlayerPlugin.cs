using System;
using System.IO;
using BepInEx;
using HarmonyLib;
using Loopstructor.AutoPlayer.Core;
using Newtonsoft.Json;

namespace Loopstructor.AutoPlayer.Plugin;

/// <summary>Hosts the activated, process-local QA automation adapter.</summary>
[BepInPlugin(PluginInfo.Guid, PluginInfo.Name, PluginInfo.Version)]
public sealed class AutoPlayerPlugin : BaseUnityPlugin
{
    private Harmony? _harmony;
    private AutoPlayController? _controller;
    private PipeControlServer? _controlServer;
    private EvidenceRecorder? _evidence;
    private string _statusPath = string.Empty;
    private float _nextStatusWriteAt;

    private void Awake()
    {
        if (!ActivationContext.TryLoad(Paths.GameRootPath, out ActivationContext? activation, out string activationReason) || activation == null)
        {
            Logger.LogInfo("Auto Player is inert for this launch: " + activationReason);
            return;
        }

        ProtectActivatedManagerObject();

        PluginSettings settings = new(Config);
        RuntimeBridge bridge = new();
        bridge.Initialize();
        if (!bridge.IsAvailable)
        {
            Logger.LogWarning("Automation runtime contract is incomplete: " + string.Join(", ", bridge.MissingMembers));
        }

        BuildFingerprint fingerprint = BuildFingerprint.Capture();
        _harmony = new Harmony(PluginInfo.Guid);
        bool baseContractAccepted = fingerprint.ProductIdentityValid
                                    && fingerprint.MatchesExpectedAssembly(activation.ExpectedAssemblySha256)
                                    && bridge.IsAvailable;
        if (baseContractAccepted)
        {
            SaveIsolationPatch.Install(_harmony, activation.ProfileRoot, Logger.LogInfo);
            PlatformWriteIsolationPatch.Install(_harmony, Logger.LogInfo);
            GameArtifactIsolationPatch.Install(_harmony, activation.ArtifactRoot, Logger.LogInfo);
        }
        else
        {
            Logger.LogWarning("Compatibility gate failed before patch installation; the game process remains unmodified.");
        }

        _evidence = new EvidenceRecorder(activation.ArtifactRoot);
        _controller = new AutoPlayController(bridge, settings, fingerprint, activation, _evidence, Logger);
        _controlServer = new PipeControlServer(_controller, activation);
        _controlServer.Start();
        _statusPath = Path.Combine(activation.ArtifactRoot, "status.json");
        WriteStatus();
        Logger.LogInfo($"{PluginInfo.Name} {PluginInfo.Version} activated from {activation.Source} in standby mode.");
    }

    private void ProtectActivatedManagerObject()
    {
        UnityEngine.Object.DontDestroyOnLoad(gameObject);
        gameObject.hideFlags |= UnityEngine.HideFlags.HideAndDontSave;
        Logger.LogInfo("Protected the activated BepInEx manager object from scene cleanup.");
    }

    private void Update()
    {
        _controlServer?.Pump();
        _controller?.Tick();
        if (_controller != null && UnityEngine.Time.realtimeSinceStartup >= _nextStatusWriteAt)
        {
            _nextStatusWriteAt = UnityEngine.Time.realtimeSinceStartup + 1f;
            WriteStatus();
        }
    }

    private void OnDestroy()
    {
        WriteStatus();
        _controlServer?.Dispose();
        _controlServer = null;
        _controller = null;
        _harmony?.UnpatchSelf();
        _harmony = null;
    }

    private void WriteStatus()
    {
        if (_controller == null || string.IsNullOrWhiteSpace(_statusPath)) return;
        try
        {
            EvidenceRecorder.AtomicWrite(_statusPath, JsonConvert.SerializeObject(_controller.Snapshot(), Formatting.Indented));
        }
        catch (Exception exception)
        {
            Logger.LogWarning("Could not write automation status: " + exception.Message);
        }
    }
}
