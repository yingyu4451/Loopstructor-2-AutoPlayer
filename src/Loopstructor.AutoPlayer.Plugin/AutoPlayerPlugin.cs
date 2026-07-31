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
            Logger.LogInfo("本次启动未激活 AutoPlayer：" + activationReason);
            return;
        }

        ProtectActivatedManagerObject();

        PluginSettings settings = new(Config);
        RuntimeBridge bridge = new();
        bridge.Initialize();
        if (!bridge.IsAvailable)
        {
            Logger.LogWarning("自动游玩运行时契约不完整：" + string.Join(", ", bridge.MissingMembers));
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
            Logger.LogWarning("兼容性检查未通过，补丁尚未安装；游戏进程保持未修改状态。");
        }

        _evidence = new EvidenceRecorder(activation.ArtifactRoot);
        _controller = new AutoPlayController(bridge, settings, fingerprint, activation, _evidence, Logger);
        _controlServer = new PipeControlServer(_controller, activation);
        _controlServer.Start();
        _statusPath = Path.Combine(activation.ArtifactRoot, "status.json");
        WriteStatus();
        Logger.LogInfo($"{PluginInfo.Name} {PluginInfo.Version} 已通过{ActivationSourceLabel(activation.Source)}激活，当前处于待命模式。");
    }

    private void ProtectActivatedManagerObject()
    {
        UnityEngine.Object.DontDestroyOnLoad(gameObject);
        gameObject.hideFlags |= UnityEngine.HideFlags.HideAndDontSave;
        Logger.LogInfo("已保护激活的 BepInEx 管理器对象，避免其被场景清理。");
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
            Logger.LogWarning("无法写入自动游玩状态：" + exception.Message);
        }
    }

    private static string ActivationSourceLabel(string source) =>
        string.Equals(source, "environment", StringComparison.OrdinalIgnoreCase)
            ? "环境变量"
            : string.Equals(source, "ticket", StringComparison.OrdinalIgnoreCase)
                ? "启动票据"
                : source;
}
