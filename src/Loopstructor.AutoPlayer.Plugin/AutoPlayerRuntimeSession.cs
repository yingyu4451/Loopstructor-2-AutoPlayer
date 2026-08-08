using System;
using System.IO;
using BepInEx.Configuration;
using BepInEx.Logging;
using HarmonyLib;
using Newtonsoft.Json;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace Loopstructor.AutoPlayer.Plugin;

/// <summary>
/// Owns the runtime independently from BepInEx's manager GameObject, which this game destroys
/// while replacing its bootstrap scene.
/// </summary>
internal sealed class AutoPlayerRuntimeSession : IDisposable
{
    private const string HostObjectName = "Loopstructor.AutoPlayer.Runtime";
    private static readonly object SessionSync = new();
    private static AutoPlayerRuntimeSession? _current;
    private static string _startupFailure = string.Empty;

    private readonly ActivationContext _activation;
    private readonly ConfigFile _config;
    private readonly ManualLogSource _logger;
    private Harmony? _harmony;
    private AutoPlayController? _controller;
    private CheatController? _cheatController;
    private PipeControlServer? _controlServer;
    private AutoPlayerRuntimeHost? _host;
    private string _statusPath = string.Empty;
    private string _lastStatusPayload = string.Empty;
    private float _nextStatusWriteAt;
    private bool _eventsAttached;
    private bool _quitting;
    private bool _disposed;
    private DateTime _nextHostRecoveryAttemptUtc;

    private AutoPlayerRuntimeSession(
        ActivationContext activation,
        ConfigFile config,
        ManualLogSource logger)
    {
        _activation = activation;
        _config = config;
        _logger = logger;
    }

    internal static bool IsRunning
    {
        get
        {
            lock (SessionSync)
            {
                return _current is { _disposed: false };
            }
        }
    }

    internal static bool TryStart(
        ActivationContext activation,
        ConfigFile config,
        ManualLogSource logger)
    {
        AutoPlayerRuntimeSession session;
        lock (SessionSync)
        {
            if (!string.IsNullOrWhiteSpace(_startupFailure))
            {
                logger.LogError(
                    "AutoPlayer 在本进程中曾启动失败，为避免复用不完整的补丁状态将不再重试；请重启游戏。首次错误："
                    + _startupFailure);
                return false;
            }

            if (_current is { _disposed: false })
            {
                if (_current.MatchesActivation(activation))
                {
                    logger.LogWarning("AutoPlayer 独立运行时已经存在；忽略重复的 BepInEx 启动组件。");
                    return true;
                }

                logger.LogError("检测到与当前 AutoPlayer 运行时不一致的重复激活信息；已拒绝复用现有会话。");
                return false;
            }

            session = new AutoPlayerRuntimeSession(activation, config, logger);
            _current = session;
        }

        try
        {
            session.Initialize();
            return true;
        }
        catch (Exception exception)
        {
            lock (SessionSync)
            {
                _startupFailure = exception.GetType().Name + "：" + exception.Message;
            }
            try
            {
                logger.LogError("AutoPlayer 独立运行时启动失败：" + exception);
            }
            catch
            {
                // Logging must not prevent fail-closed cleanup.
            }
            session.Dispose();
            return false;
        }
    }

    private void Initialize()
    {
        PluginSettings settings = new(_config);
        RuntimeBridge bridge = new();
        bridge.Initialize();
        if (!bridge.IsAvailable)
        {
            _logger.LogWarning("自动游玩运行时契约不完整：" + string.Join(", ", bridge.MissingMembers));
        }

        BuildFingerprint fingerprint = BuildFingerprint.Capture();
        _harmony = new Harmony(PluginInfo.Guid);
        bool baseContractAccepted = fingerprint.ProductIdentityValid
                                    && fingerprint.MatchesExpectedAssembly(_activation.ExpectedAssemblySha256)
                                    && bridge.IsAvailable;
        if (baseContractAccepted)
        {
            if (!_activation.IsPlayerMode)
            {
                SaveIsolationPatch.Install(_harmony, _activation.ProfileRoot, _logger.LogInfo);
                PlatformWriteIsolationPatch.Install(_harmony, _logger.LogInfo);
                GameArtifactIsolationPatch.Install(_harmony, _activation.ArtifactRoot, _logger.LogInfo);
            }
            else
            {
                _logger.LogInfo("玩家模式已进入本机鉴权待命；不会重定向存档、平台写入或游戏诊断产物。");
            }

            GameOutcomeObserver.Install(_harmony, _logger.LogInfo);
            if (!MapSkipPatch.Install(_harmony, _logger.LogInfo))
            {
                _logger.LogWarning("地图跳关未能接入游戏地图输入流程；该功能将保持不可用。");
            }
        }
        else
        {
            _logger.LogWarning("兼容性检查未通过，补丁尚未安装；游戏进程保持未修改状态。");
        }

        EvidenceRecorder evidence = new(_activation.ArtifactRoot);
        _controller = new AutoPlayController(bridge, settings, fingerprint, _activation, evidence, _logger);
        _cheatController = new CheatController(_controller, _activation, _logger, baseContractAccepted);
        if (baseContractAccepted && !SpawnPointCaptureInputPatch.Install(_harmony, _logger.LogInfo))
        {
            _logger.LogWarning("怪物生成位置捕获未能接入游戏输入流水线；该功能将保持不可用。");
        }

        _controlServer = new PipeControlServer(_controller, _cheatController, _activation);
        _statusPath = Path.Combine(_activation.ArtifactRoot, "status.json");
        AttachLifecycleEvents();
        EnsureHost();
        _controlServer.Start();
        if (!_activation.IsPlayerMode)
        {
            WriteStatus(force: true);
            _nextStatusWriteAt = Time.realtimeSinceStartup + 5f;
        }
        _logger.LogInfo(
            $"{PluginInfo.Name} {PluginInfo.Version} 已通过{ActivationSourceLabel(_activation.Source)}激活，当前处于待命模式。");
    }

    private void AttachLifecycleEvents()
    {
        if (_eventsAttached) return;
        SceneManager.sceneLoaded += OnSceneLoaded;
        SceneManager.activeSceneChanged += OnActiveSceneChanged;
        Application.onBeforeRender += OnBeforeRender;
        Application.quitting += OnApplicationQuitting;
        _eventsAttached = true;
    }

    private void DetachLifecycleEvents()
    {
        if (!_eventsAttached) return;
        SceneManager.sceneLoaded -= OnSceneLoaded;
        SceneManager.activeSceneChanged -= OnActiveSceneChanged;
        Application.onBeforeRender -= OnBeforeRender;
        Application.quitting -= OnApplicationQuitting;
        _eventsAttached = false;
    }

    private void EnsureHost()
    {
        if (_disposed || _quitting || _host != null) return;

        GameObject? hostObject = null;
        AutoPlayerRuntimeHost? host = null;
        try
        {
            hostObject = new GameObject(HostObjectName)
            {
                hideFlags = HideFlags.HideAndDontSave
            };
            UnityEngine.Object.DontDestroyOnLoad(hostObject);
            host = hostObject.AddComponent<AutoPlayerRuntimeHost>();
            host.Attach(this);
            _host = host;
            _nextHostRecoveryAttemptUtc = DateTime.MinValue;
            LogInfoBestEffort(
                $"独立 AutoPlayer 运行时宿主已就绪；当前场景：{SceneManager.GetActiveScene().name}。");
        }
        catch
        {
            _host = null;
            try { host?.Detach(); } catch { }
            try
            {
                if (hostObject != null) UnityEngine.Object.Destroy(hostObject);
            }
            catch
            {
                // The host is already unreachable; Unity will reclaim it with the process.
            }
            throw;
        }
    }

    private void OnSceneLoaded(Scene scene, LoadSceneMode mode)
    {
        RecoverHostIfNeeded();
    }

    private void OnActiveSceneChanged(Scene previous, Scene current)
    {
        RecoverHostIfNeeded();
    }

    private void OnBeforeRender()
    {
        RecoverHostIfNeeded();
    }

    private void OnApplicationQuitting()
    {
        BeginQuit();
    }

    internal void BeginQuit()
    {
        if (_quitting || _disposed) return;
        _quitting = true;
        Dispose();
    }

    internal void PumpFrame()
    {
        if (_disposed) return;
        _controller?.RecordFrame(Time.unscaledDeltaTime);
        _controlServer?.Pump();
        _cheatController?.Tick();
        _controller?.Tick();
        if (!_activation.IsPlayerMode
            && _controller != null
            && Time.realtimeSinceStartup >= _nextStatusWriteAt)
        {
            _nextStatusWriteAt = Time.realtimeSinceStartup + 5f;
            WriteStatus();
        }
    }

    internal void DrawOverlay()
    {
        if (!_disposed) _cheatController?.DrawEnemyOverlays();
    }

    internal void HostDestroyed(AutoPlayerRuntimeHost host)
    {
        if (!ReferenceEquals(_host, host)) return;
        _host = null;
        if (!_disposed && !_quitting)
        {
            try
            {
                _cheatController?.OnRuntimeHostLost();
            }
            catch (Exception exception)
            {
                LogErrorBestEffort("运行时宿主中断后的作弊状态安全重置失败：" + exception);
            }
            _nextHostRecoveryAttemptUtc = DateTime.MinValue;
            LogWarningBestEffort("独立 AutoPlayer 运行时宿主被场景清理；将在主线程自动重建。");
        }
    }

    private void RecoverHostIfNeeded()
    {
        if (_disposed || _quitting || _host != null || DateTime.UtcNow < _nextHostRecoveryAttemptUtc) return;
        try
        {
            EnsureHost();
        }
        catch (Exception exception)
        {
            _nextHostRecoveryAttemptUtc = DateTime.UtcNow.AddSeconds(5);
            LogErrorBestEffort("重建独立 AutoPlayer 运行时宿主失败；5 秒后重试：" + exception);
        }
    }

    private bool MatchesActivation(ActivationContext other) =>
        _activation.ActivationMode == other.ActivationMode
        && _activation.CheatModeAllowed == other.CheatModeAllowed
        && string.Equals(_activation.PipeName, other.PipeName, StringComparison.Ordinal)
        && TokensEqual(_activation.Token, other.Token)
        && string.Equals(_activation.ProfileRoot, other.ProfileRoot, StringComparison.OrdinalIgnoreCase)
        && string.Equals(_activation.ArtifactRoot, other.ArtifactRoot, StringComparison.OrdinalIgnoreCase)
        && string.Equals(
            _activation.ExpectedAssemblySha256,
            other.ExpectedAssemblySha256,
            StringComparison.OrdinalIgnoreCase);

    private static bool TokensEqual(string first, string second)
    {
        if (first.Length != second.Length) return false;
        int difference = 0;
        for (int index = 0; index < first.Length; index++) difference |= first[index] ^ second[index];
        return difference == 0;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        AutoPlayerRuntimeHost? host = _host;
        try
        {
            CleanupStep("解绑 Unity 生命周期事件", DetachLifecycleEvents);
            _host = null;
            CleanupStep("分离独立运行时宿主", () => host?.Detach());

            CleanupStep("记录退出日志", () => _logger.LogInfo("AutoPlayer 运行时正在退出；本机控制通道已关闭。"));
            CleanupStep("关闭本机控制通道", () => _controlServer?.Dispose());
            _controlServer = null;
            CleanupStep("释放作弊控制器", () => _cheatController?.Dispose());
            _cheatController = null;
            CleanupStep("移除怪物生成点输入补丁", SpawnPointCaptureInputPatch.Detach);
            CleanupStep("重置地图跳关补丁", MapSkipPatch.Reset);
            if (!_activation.IsPlayerMode) CleanupStep("写入最终自动游玩状态", () => WriteStatus(force: true));
            _controller = null;
            CleanupStep("移除 Harmony 补丁", () => _harmony?.UnpatchSelf());
            _harmony = null;

            if (!_quitting && host != null)
            {
                CleanupStep("销毁独立运行时宿主", () => UnityEngine.Object.Destroy(host.gameObject));
            }
        }
        finally
        {
            lock (SessionSync)
            {
                if (ReferenceEquals(_current, this)) _current = null;
            }
        }
    }

    private void CleanupStep(string operation, Action action)
    {
        try
        {
            action();
        }
        catch (Exception exception)
        {
            try
            {
                _logger.LogError($"AutoPlayer 清理失败（{operation}）：{exception}");
            }
            catch
            {
                // Remaining cleanup steps are more important than logging this failure.
            }
        }
    }

    private void LogInfoBestEffort(string message)
    {
        try { _logger.LogInfo(message); } catch { }
    }

    private void LogWarningBestEffort(string message)
    {
        try { _logger.LogWarning(message); } catch { }
    }

    private void LogErrorBestEffort(string message)
    {
        try { _logger.LogError(message); } catch { }
    }

    private void WriteStatus(bool force = false)
    {
        if (_controller == null || string.IsNullOrWhiteSpace(_statusPath)) return;
        try
        {
            string payload = JsonConvert.SerializeObject(_controller.Snapshot(), Formatting.Indented);
            if (!force && string.Equals(payload, _lastStatusPayload, StringComparison.Ordinal)) return;
            EvidenceRecorder.AtomicWrite(_statusPath, payload);
            _lastStatusPayload = payload;
        }
        catch (Exception exception)
        {
            _logger.LogWarning("无法写入自动游玩状态：" + exception.Message);
        }
    }

    private static string ActivationSourceLabel(string source) =>
        string.Equals(source, "environment", StringComparison.OrdinalIgnoreCase)
            ? "环境变量"
            : string.Equals(source, "ticket", StringComparison.OrdinalIgnoreCase)
                ? "启动票据"
                : source;
}

internal sealed class AutoPlayerRuntimeHost : MonoBehaviour
{
    private AutoPlayerRuntimeSession? _session;

    internal void Attach(AutoPlayerRuntimeSession session)
    {
        _session = session;
    }

    internal void Detach()
    {
        _session = null;
    }

    private void Update()
    {
        _session?.PumpFrame();
    }

    private void OnGUI()
    {
        _session?.DrawOverlay();
    }

    private void OnApplicationQuit()
    {
        _session?.BeginQuit();
    }

    private void OnDestroy()
    {
        AutoPlayerRuntimeSession? session = _session;
        _session = null;
        session?.HostDestroyed(this);
    }
}
