using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using BepInEx.Logging;
using Loopstructor.AutoPlayer.Core;
using Newtonsoft.Json.Linq;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace Loopstructor.AutoPlayer.Plugin;

internal sealed class AutoPlayController
{
    private enum BattleTacticStep
    {
        QueryThreats,
        QueryDisposable,
        UseDisposable,
        QueryDisposablePreview,
        QueryDisposableGridOptions,
        ConfirmDisposable,
        CancelDisposable,
        QueryRail,
        QueryTrain,
        MoveTrain,
        Complete
    }

    private enum DefenseMaintenanceStep
    {
        QueryTrain,
        QueryVehicle,
        MoveVehicle
    }

    private const int MaxTimelineEvents = 100;
    private const float SaveVerificationTimeoutSeconds = 30f;
    private const float OutcomeVerificationTimeoutSeconds = 10f;
    private const float MinimumBattlePollIntervalSeconds = 2f;
    private const float BattleTacticFrameDelaySeconds = 0.05f;
    private const float BattleTacticCycleIntervalSeconds = 8f;
    private const float MapSelectionTransitionTimeoutSeconds = 12f;
    private const float EventOptionGenerationDelaySeconds = 3.25f;
    private static readonly TimeSpan FrontEndTransitionTimeout = TimeSpan.FromSeconds(20);

    private readonly object _sync = new();
    private readonly RuntimeBridge _bridge;
    private readonly PluginSettings _settings;
    private readonly BuildFingerprint _fingerprint;
    private readonly ActivationContext _activation;
    private readonly EvidenceRecorder _evidence;
    private readonly ManualLogSource _log;
    private readonly DecisionEngine _decisionEngine = new();
    private readonly BattleDecisionEngine _battleDecisionEngine = new();
    private readonly List<TimelineEvent> _timeline = new();
    private readonly SceneTransitionGate _frontEndTransitionGate = new();

    private AutomationRunOptions _options = new();
    private AutoPlayerRunState _runState;
    private AutomationStage _stage = AutomationStage.WaitingForGame;
    private string _stageDetail = string.Empty;
    private string _lastCommand = string.Empty;
    private string _lastMessage = string.Empty;
    private string _scene = string.Empty;
    private string _evidenceDirectory = string.Empty;
    private string _compatibilityError = string.Empty;
    private AutomationOutcome _outcome;
    private int _consecutiveFailures;
    private int _wavesStarted;
    private int _wavesCompleted;
    private float _nextTickAt;
    private float _nextSaveProbeAt;
    private float _lastProgressAt;
    private float _gameOverDetectedAt = -1f;
    private bool _defensePrepared;
    private bool _speedConfigured;
    private bool _pendingSublevel;
    private bool _mapSelectionPending;
    private float _mapSelectionPendingAt;
    private float _eventOptionsReadyAt = -1f;
    private string _pendingEventPanel = string.Empty;
    private AutomationAction? _pendingMapAction;
    private bool _wasInWave;
    private bool _wishReturnClicked;
    private bool _needsProcessRestart;
    private bool _cheatAvailable;
    private bool _cheatModeEnabled;
    private bool _cheatUsed;
    private bool _enemyIdsVisible;
    private bool _baseGodModeEnabled;
    private int _cheatActionCount;
    private string _cheatAvailabilityReason = string.Empty;
    private IReadOnlyList<string> _cheatCapabilities = Array.Empty<string>();
    private bool _frontEndReadinessObserved;
    private bool _gameModeVerified;
    private bool _runtimeInitialized;
    private BattleTacticStep _battleTacticStep;
    private bool _battleTacticPending;
    private float _nextBattleWaveProbeAt;
    private float _nextBattleTacticCycleAt;
    private bool _battleDisposableUsedThisWave;
    private string _ownedDisposableEnum = string.Empty;
    private int _ownedDisposableInteractionInstanceId;
    private JObject? _battleWaveSnapshot;
    private AutomationAction? _battlePendingAction;
    private JObject? _battleThreats;
    private JObject? _battleDisposable;
    private JObject? _battleRail;
    private JObject? _battleTrain;
    private JObject? _battleConfirmationArguments;
    private bool _defenseMaintenanceRequested;
    private bool _defenseMaintenanceReady;
    private DefenseMaintenanceStep _defenseMaintenanceStep;
    private JObject? _defenseTrain;
    private AutomationAction? _defensePendingAction;
    private string _pendingActionKey = string.Empty;
    private DateTime _startedAtUtc;
    private DateTime _lastActionAtUtc;
    private DateTime? _pausedAtUtc;
    private TimeSpan _pausedDuration;

    public AutoPlayController(
        RuntimeBridge bridge,
        PluginSettings settings,
        BuildFingerprint fingerprint,
        ActivationContext activation,
        EvidenceRecorder evidence,
        ManualLogSource log)
    {
        _bridge = bridge;
        _settings = settings;
        _fingerprint = fingerprint;
        _activation = activation;
        _evidence = evidence;
        _log = log;
        _cheatUsed = activation.CheatProfileTainted;
        _compatibilityError = BuildCompatibilityError();
        _runState = string.IsNullOrEmpty(_compatibilityError)
            ? AutoPlayerRunState.Standby
            : AutoPlayerRunState.Incompatible;
        _stageDetail = string.IsNullOrEmpty(_compatibilityError)
            ? _activation.IsPlayerMode
                ? "玩家模式已在后台待命，可随时从 Manager 开始自动游玩。"
                : "隔离 QA 模式已激活，正在等待开始命令。"
            : _compatibilityError;
        if (_cheatUsed)
        {
            AddTimeline("cheat", "检测到当前控制配置的作弊记录；后续自动游玩会继续标记为 cheat-modified。");
        }
        if (!string.IsNullOrEmpty(_compatibilityError)) AddTimeline("error", _compatibilityError);
    }

    public bool Start(AutomationRunOptions? options, out string message)
    {
        lock (_sync)
        {
            if (_cheatModeEnabled)
            {
                message = "作弊模式当前已启用，不能开始自动游玩。请先关闭作弊模式。";
                return false;
            }

            if (_needsProcessRestart)
            {
                _runState = AutoPlayerRunState.Faulted;
                _stage = AutomationStage.Recovery;
                _lastMessage = "上一次自动游玩发生故障，必须重新启动游戏进程。";
                _stageDetail = _lastMessage;
                message = _lastMessage;
                return false;
            }

            if (!string.IsNullOrEmpty(_compatibilityError))
            {
                _runState = AutoPlayerRunState.Incompatible;
                _lastMessage = _compatibilityError;
                message = _compatibilityError;
                return false;
            }

            if (_runState == AutoPlayerRunState.Running)
            {
                message = "自动游玩已在运行。";
                return false;
            }

            _options = Normalize(options ?? new AutomationRunOptions());
            _runState = AutoPlayerRunState.Running;
            _outcome = AutomationOutcome.InProgress;
            _stage = AutomationStage.WaitingForGame;
            _stageDetail = _activation.IsPlayerMode
                ? "正在等待受支持的游戏场景。"
                : "正在等待存档隔离验证和受支持的场景。";
            _consecutiveFailures = 0;
            _wavesStarted = 0;
            _wavesCompleted = 0;
            _defensePrepared = false;
            _speedConfigured = !_options.OverrideGameSpeed;
            _pendingSublevel = false;
            _mapSelectionPending = false;
            _mapSelectionPendingAt = -1f;
            _eventOptionsReadyAt = -1f;
            _pendingEventPanel = string.Empty;
            _pendingMapAction = null;
            _wasInWave = false;
            _wishReturnClicked = false;
            _frontEndReadinessObserved = false;
            _gameModeVerified = false;
            _runtimeInitialized = false;
            ResetBattleTactics();
            RequestDefenseMaintenance();
            _pendingActionKey = string.Empty;
            _frontEndTransitionGate.Reset();
            GameOutcomeObserver.Reset();
            _gameOverDetectedAt = -1f;
            _startedAtUtc = DateTime.UtcNow;
            _lastActionAtUtc = _startedAtUtc;
            _pausedAtUtc = null;
            _pausedDuration = TimeSpan.Zero;
            _lastProgressAt = Time.realtimeSinceStartup;
            _nextTickAt = 0f;
            _evidenceDirectory = _evidence.CreateRunDirectory();
            _timeline.Clear();
            AddTimeline("start", $"已使用{ModeDisplayName(_options.Mode)}模式开始自动游玩。");
            message = "自动游玩已开始。";
            return true;
        }
    }

    public void ConfigureCheat(bool available, string reason, IReadOnlyList<string> capabilities)
    {
        lock (_sync)
        {
            _cheatAvailable = available;
            _cheatAvailabilityReason = reason ?? string.Empty;
            _cheatCapabilities = capabilities ?? Array.Empty<string>();
        }
    }

    public bool TrySetCheatMode(bool enabled, out string message)
    {
        lock (_sync)
        {
            if (enabled)
            {
                if (!_cheatAvailable)
                {
                    message = string.IsNullOrWhiteSpace(_cheatAvailabilityReason)
                        ? "当前游戏构建不支持作弊工具。"
                        : _cheatAvailabilityReason;
                    return false;
                }

                if (_runState is AutoPlayerRunState.Running or AutoPlayerRunState.Paused)
                {
                    message = "自动游玩正在运行或暂停。请先停止自动游玩，再启用作弊模式。";
                    return false;
                }

                if (_needsProcessRestart)
                {
                    message = "当前游戏进程已要求重启，不能再进入作弊模式。";
                    return false;
                }
            }

            _cheatModeEnabled = enabled;
            message = enabled ? "作弊模式已启用。" : "作弊模式已关闭。";
            return true;
        }
    }

    public void RecordCheatAction(string command, string message)
    {
        lock (_sync)
        {
            _cheatUsed = true;
            _cheatActionCount++;
            _lastCommand = command;
            _lastMessage = message;
            _lastActionAtUtc = DateTime.UtcNow;
            AddTimeline("cheat", message + " 当前控制配置已记录作弊修改；后续自动游玩证据会标记为 cheat-modified。");
        }
    }

    public void SetEnemyIdsVisible(bool visible)
    {
        lock (_sync)
        {
            _enemyIdsVisible = visible;
        }
    }

    public void SetBaseGodModeEnabled(bool enabled)
    {
        lock (_sync)
        {
            _baseGodModeEnabled = enabled;
        }
    }

    public bool Pause(out string message)
    {
        lock (_sync)
        {
            if (_runState != AutoPlayerRunState.Running)
            {
                message = "自动游玩当前未运行。";
                return false;
            }

            ReleaseOwnedDisposablePreview();
            _runState = AutoPlayerRunState.Paused;
            _pausedAtUtc = DateTime.UtcNow;
            _stageDetail = "自动游玩命令已暂停；游戏本身并未暂停。";
            AddTimeline("pause", _stageDetail);
            message = "自动游玩已暂停。";
            return true;
        }
    }

    public bool Resume(out string message)
    {
        lock (_sync)
        {
            if (_runState != AutoPlayerRunState.Paused)
            {
                message = "自动游玩当前未暂停。";
                return false;
            }

            _runState = AutoPlayerRunState.Running;
            if (_pausedAtUtc.HasValue)
            {
                _pausedDuration += DateTime.UtcNow - _pausedAtUtc.Value;
                _pausedAtUtc = null;
            }
            _stageDetail = "自动游玩已继续。";
            _lastProgressAt = Time.realtimeSinceStartup;
            _nextTickAt = 0f;
            AddTimeline("resume", _stageDetail);
            message = _stageDetail;
            return true;
        }
    }

    public bool Stop(out string message)
    {
        lock (_sync)
        {
            if (_runState == AutoPlayerRunState.Standby)
            {
                message = "自动游玩已经停止。";
                return false;
            }

            ReleaseOwnedDisposablePreview();
            _runState = AutoPlayerRunState.Standby;
            if (_outcome is AutomationOutcome.Unknown or AutomationOutcome.InProgress)
            {
                _outcome = AutomationOutcome.Stopped;
            }
            _stage = AutomationStage.WaitingForGame;
            _stageDetail = "已停止，不会再向游戏发送命令。";
            AddTimeline("stop", _stageDetail);
            _evidence.WriteStatus(EnsureEvidenceDirectory(), Snapshot());
            message = "自动游玩已停止。";
            return true;
        }
    }

    public void Tick()
    {
        // Isolated QA sessions verify the redirected save root before Start.
        // Resident player mode intentionally leaves the player's save path untouched.
        if (!_activation.IsPlayerMode
            && !SaveIsolationPatch.Verified
            && !SaveIsolationPatch.VerificationFailed
            && Time.realtimeSinceStartup >= _nextSaveProbeAt)
        {
            _nextSaveProbeAt = Time.realtimeSinceStartup + 0.5f;
            SaveIsolationPatch.ProbeRuntimeSaveFolder();
        }
        if (_runState != AutoPlayerRunState.Running || Time.realtimeSinceStartup < _nextTickAt) return;
        float configuredInterval = Math.Max(0.2f, _settings.TickIntervalSeconds.Value);
        float tickInterval = _wasInWave
            ? Math.Max(MinimumBattlePollIntervalSeconds, configuredInterval)
            : configuredInterval;
        _nextTickAt = Time.realtimeSinceStartup + tickInterval;

        AutomationOutcome observedOutcome = GameOutcomeObserver.Outcome;
        if (observedOutcome is AutomationOutcome.Victory or AutomationOutcome.Defeat)
        {
            TickSettlement();
            return;
        }

        if (DateTime.UtcNow - _startedAtUtc - _pausedDuration >= TimeSpan.FromMinutes(_options.MaxRunMinutes))
        {
            _outcome = AutomationOutcome.Timeout;
            Fault("已达到配置的运行时间上限，但尚未观察到游戏胜利。");
            return;
        }

        string activeScene = SceneManager.GetActiveScene().name;
        if (!string.Equals(activeScene, _scene, StringComparison.Ordinal))
        {
            bool completedTransition = _frontEndTransitionGate.ObserveScene(activeScene);
            _scene = activeScene;
            _defensePrepared = false;
            _speedConfigured = !_options.OverrideGameSpeed;
            _pendingSublevel = false;
            _mapSelectionPending = false;
            _mapSelectionPendingAt = -1f;
            _eventOptionsReadyAt = -1f;
            _pendingEventPanel = string.Empty;
            _pendingMapAction = null;
            _wasInWave = false;
            _wishReturnClicked = false;
            _frontEndReadinessObserved = false;
            _gameModeVerified = false;
            _runtimeInitialized = false;
            ResetBattleTactics();
            RequestDefenseMaintenance();
            _pendingActionKey = string.Empty;
            _gameOverDetectedAt = -1f;
            AddTimeline("scene", "已进入场景 " + activeScene + "。");
            if (completedTransition)
            {
                AddTimeline("transition", "已观察到前端命令触发场景切换。");
            }
            MarkProgress();
        }

        if (!_activation.IsPlayerMode && SaveIsolationPatch.VerificationFailed)
        {
            Fault(SaveIsolationPatch.VerificationError);
            return;
        }

        if (!_activation.IsPlayerMode && !SaveIsolationPatch.Verified)
        {
            SetStage(AutomationStage.WaitingForGame, "正在等待 SaveManager 确认隔离的测试存档。");
            if (Time.realtimeSinceStartup - _lastProgressAt >= SaveVerificationTimeoutSeconds)
            {
                Fault("存档隔离未通过验证，因此尚未向游戏发送命令。");
            }

            return;
        }

        try
        {
            if (string.Equals(activeScene, "StartGameScene", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(activeScene, "RandomChooseScene", StringComparison.OrdinalIgnoreCase))
            {
                TickFrontEnd(activeScene);
            }
            else if (string.Equals(activeScene, "NewGameScene", StringComparison.OrdinalIgnoreCase))
            {
                TickInGame();
            }
            else
            {
                SetStage(AutomationStage.WaitingForGame, "正在等待受支持的场景；当前场景为 " + activeScene + "。");
            }

            CheckForStall();
        }
        catch (Exception exception)
        {
            RegisterFailure("自动游玩控制器发生异常：" + exception.Message);
            _log.LogError("自动游玩控制器发生未处理异常：" + exception);
        }
    }

    public AutoPlayerStatus Snapshot()
    {
        lock (_sync)
        {
            return new AutoPlayerStatus
            {
                ActivationMode = _activation.ActivationMode,
                PluginVersion = PluginInfo.Version,
                RunState = _runState,
                Outcome = _outcome,
                Stage = _stage,
                StageDetail = _stageDetail,
                Scene = _scene,
                ProductName = _fingerprint.ProductName,
                CompanyName = _fingerprint.CompanyName,
                GameVersion = _fingerprint.ProductVersion,
                UnityVersion = _fingerprint.UnityVersion,
                BuildGuid = _fingerprint.BuildGuid,
                SteamBuildId = _fingerprint.SteamBuildId,
                AssemblySha256 = _fingerprint.AssemblySha256,
                AssemblyMvid = _fingerprint.AssemblyMvid,
                ManagedAssemblySha256 = _fingerprint.ManagedAssemblySha256,
                ProductIdentityValid = _fingerprint.ProductIdentityValid,
                FingerprintAccepted = _fingerprint.MatchesExpectedAssembly(_activation.ExpectedAssemblySha256),
                CompatibilityError = _compatibilityError,
                RuntimeContractAvailable = _bridge.IsAvailable,
                MissingRuntimeMembers = _bridge.MissingMembers,
                SaveIsolationApplied = SaveIsolationPatch.Applied,
                SaveIsolationVerified = SaveIsolationPatch.Verified,
                SaveIsolationError = SaveIsolationPatch.VerificationError,
                PlatformWritesBlocked = PlatformWriteIsolationPatch.Applied,
                GameArtifactsRedirected = GameArtifactIsolationPatch.Applied,
                IsolatedSaveRoot = SaveIsolationPatch.IsolatedRoot,
                ArtifactDirectory = _activation.ArtifactRoot,
                NeedsProcessRestart = _needsProcessRestart,
                ConsecutiveFailures = _consecutiveFailures,
                WavesStarted = _wavesStarted,
                WavesCompleted = _wavesCompleted,
                StartedAtUtc = _startedAtUtc,
                LastActionAtUtc = _lastActionAtUtc,
                LastCommand = _lastCommand,
                LastMessage = _lastMessage,
                EvidenceDirectory = _evidenceDirectory,
                CheatSessionAuthorized = _activation.CheatModeAllowed,
                CheatAvailable = _cheatAvailable,
                CheatModeEnabled = _cheatModeEnabled,
                CheatUsed = _cheatUsed,
                CheatActionCount = _cheatActionCount,
                EnemyIdsVisible = _enemyIdsVisible,
                BaseGodModeEnabled = _baseGodModeEnabled,
                MapSkipEnabled = MapSkipPatch.Enabled,
                RunIntegrity = _cheatUsed ? "cheat-modified" : "clean",
                CheatAvailabilityReason = _cheatAvailabilityReason,
                Timeline = _timeline.ToArray()
            };
        }
    }

    public BridgeHello Hello()
    {
        lock (_sync)
        {
            return new BridgeHello
            {
                ActivationMode = _activation.ActivationMode,
                ProtocolVersion = Protocol.CurrentVersion,
                GameProcessId = GetCurrentProcessId(),
                ProcessInstanceId = _activation.ProcessInstanceId,
                PluginVersion = PluginInfo.Version,
                GameVersion = _fingerprint.ProductVersion,
                UnityVersion = _fingerprint.UnityVersion,
                BuildGuid = _fingerprint.BuildGuid,
                AssemblySha256 = _fingerprint.AssemblySha256,
                AssemblyMvid = _fingerprint.AssemblyMvid,
                ProductIdentityValid = _fingerprint.ProductIdentityValid,
                FingerprintAccepted = _fingerprint.MatchesExpectedAssembly(_activation.ExpectedAssemblySha256),
                CompatibilityError = _compatibilityError,
                RuntimeContractAvailable = _bridge.IsAvailable,
                MissingMembers = _bridge.MissingMembers,
                Commands = _bridge.AvailableCommands,
                SaveIsolationApplied = SaveIsolationPatch.Applied,
                SaveIsolationVerified = SaveIsolationPatch.Verified,
                PlatformWritesBlocked = PlatformWriteIsolationPatch.Applied,
                GameArtifactsRedirected = GameArtifactIsolationPatch.Applied,
                ProfileRoot = _activation.ProfileRoot,
                ArtifactRoot = _activation.ArtifactRoot,
                CheatProtocolVersion = Protocol.CheatCurrentVersion,
                CheatSessionAuthorized = _activation.CheatModeAllowed,
                CheatAvailable = _cheatAvailable,
                CheatModeEnabled = _cheatModeEnabled,
                CheatUsed = _cheatUsed,
                MapSkipEnabled = MapSkipPatch.Enabled,
                CheatAvailabilityReason = _cheatAvailabilityReason,
                CheatCapabilities = _cheatCapabilities
            };
        }
    }

    private void TickFrontEnd(string activeScene)
    {
        if (_frontEndTransitionGate.IsWaiting)
        {
            if (_frontEndTransitionGate.HasTimedOut(DateTime.UtcNow, FrontEndTransitionTimeout))
            {
                Fault("前端命令 " + _frontEndTransitionGate.Command +
                      " 已成功返回，但场景未在安全时限内切换；为避免重复提交，当前进程必须重启。");
                return;
            }

            SetStage(
                AutomationStage.FrontEnd,
                "已发送 " + _frontEndTransitionGate.Command + "，正在等待游戏完成场景切换。");
            return;
        }

        string query = string.Equals(activeScene, "RandomChooseScene", StringComparison.OrdinalIgnoreCase)
            ? "queryRandomMode"
            : "queryFrontend";
        JObject result = _bridge.Invoke(query);
        switch (RuntimeResultInspector.Classify(result))
        {
            case RuntimeResultDisposition.Unsafe:
                Fault("命令 " + query + " 报告状态已被污染，需要启动新的游戏进程：" + RuntimeResultInspector.Message(result));
                return;
            case RuntimeResultDisposition.Pending:
                SetStage(AutomationStage.FrontEnd, Message(result));
                return;
            case RuntimeResultDisposition.Failure:
                RegisterFailure(Message(result));
                return;
        }

        AutomationAction action = _decisionEngine.DecideFrontEnd(result, _options);
        if (IsFrontEndMutation(action.Command))
        {
            if (!_bridge.IsFrontEndInitializationComplete(out string readinessMessage))
            {
                _frontEndReadinessObserved = false;
                SetStage(action.Stage, readinessMessage + " 尚未发送前端命令。");
                return;
            }

            if (!_frontEndReadinessObserved)
            {
                _frontEndReadinessObserved = true;
                SetStage(action.Stage, "已确认前端就绪，正在等待一个稳定的轮询周期。");
                return;
            }
        }

        if (string.Equals(action.Command, "submitCommonMode", StringComparison.OrdinalIgnoreCase))
        {
            if (!_bridge.TryDisableCommonModeTutorial(out string tutorialMessage))
            {
                RegisterFailure(tutorialMessage);
                return;
            }

            AddTimeline("guard", tutorialMessage);
        }

        bool executed = Execute(action);
        if (executed && IsSceneTransitionCommand(action.Command))
        {
            _frontEndTransitionGate.Begin(action.Command, activeScene, DateTime.UtcNow);
            SetStage(action.Stage, "已发送 " + action.Command + "，正在等待场景切换。");
        }
    }

    private void TickInGame()
    {
        if (!EnsureInGameRuntimeReady()) return;

        if (!_gameModeVerified)
        {
            if (!_bridge.TryGetGameMode(out string gameMode, out string modeMessage))
            {
                SetStage(AutomationStage.InitializingRun, modeMessage);
                return;
            }

            string expectedMode = _options.Mode == AutomationGameMode.Common ? "commonMode" : "randomMode";
            if (!string.Equals(gameMode, expectedMode, StringComparison.OrdinalIgnoreCase))
            {
                Fault("游戏进入了意外模式 " + gameMode + "，预期为 " + expectedMode +
                      "；已停止自动游玩以避免测试错误模式。");
                return;
            }

            _gameModeVerified = true;
            AddTimeline("guard", "已验证当前游戏模式为 " + gameMode + "。");
            MarkProgress();
        }

        if (TryHandleObservedWave()) return;
        if (TryHandlePendingMapSelection()) return;
        if (_defensePrepared &&
            _defenseMaintenanceRequested &&
            _defenseMaintenanceReady &&
            TryMaintainDefense()) return;

        JObject affordances = _bridge.Invoke("queryAffordances");
        switch (RuntimeResultInspector.Classify(affordances))
        {
            case RuntimeResultDisposition.Unsafe:
                Fault("命令 queryAffordances 报告状态已被污染，需要启动新的游戏进程：" + Message(affordances));
                return;
            case RuntimeResultDisposition.Pending:
                SetStage(AutomationStage.InitializingRun, Message(affordances));
                return;
            case RuntimeResultDisposition.Failure:
                RegisterFailure("命令 queryAffordances 失败：" + Message(affordances));
                return;
        }

        JObject state = affordances.SelectToken("data.state") as JObject ?? new JObject();
        bool gameOver = state["gameOver"]?.Value<bool>() == true;
        bool inWave = state.SelectToken("wave.isInWaving")?.Value<bool>() == true;
        ObserveWaveTransition(inWave);

        if (gameOver)
        {
            TickSettlement();
            return;
        }

        if (!_defensePrepared && HasPlacedCombatVehicle(state))
        {
            _defensePrepared = true;
            RequestDefenseMaintenance();
            AddTimeline("defense", "已检测到场上现有战车，将从当前防线继续自动游玩。");
            SetStage(AutomationStage.PreparingDefense, "已识别现有防线，下一轮将检查背包战车与车列容量。");
            return;
        }

        if (_options.MaxWaves > 0 && _wavesCompleted >= _options.MaxWaves)
        {
            _outcome = AutomationOutcome.WaveLimit;
            Fault("已达到配置的波次上限，但尚未观察到游戏胜利。");
            return;
        }

        JArray blockers = state["blockers"] as JArray ?? new JArray();
        bool blocked = blockers.Count > 0;
        if (!inWave && !blocked && _defensePrepared && _defenseMaintenanceRequested)
        {
            _defenseMaintenanceReady = true;
            _nextTickAt = Time.realtimeSinceStartup + BattleTacticFrameDelaySeconds;
            SetStage(AutomationStage.PreparingDefense, "防线当前可编辑，准备检查背包战车与车列容量。");
            return;
        }

        JObject? reward = HasBlocker(blockers, "reward") ? _bridge.Invoke("queryReward") : null;
        JObject? events = HasBlocker(blockers, "EventUI") || HasBlocker(blockers, "RepairUI")
            ? _bridge.Invoke("queryEventOptions")
            : null;

        if (!inWave && !blocked && !_defensePrepared && !_pendingSublevel)
        {
            bool prepared = Execute(new AutomationAction(
                "prepareDefaultDefense",
                JObject.FromObject(new { includeDebug = false }),
                AutomationStage.PreparingDefense,
                "正在通过等同玩家操作的接口准备默认闭合轨道和初始载具。"));
            if (prepared && _runState == AutoPlayerRunState.Running)
            {
                _defensePrepared = true;
                RequestDefenseMaintenance();
            }
            return;
        }

        if (!inWave && !blocked && _defensePrepared && !_speedConfigured && _options.OverrideGameSpeed)
        {
            bool configured = Execute(new AutomationAction(
                "setTimeSpeed",
                JObject.FromObject(new { speedState = _options.SpeedState }),
                AutomationStage.InitializingRun,
                "设置配置的游戏内速度。"));
            if (configured && _runState == AutoPlayerRunState.Running) _speedConfigured = true;
            return;
        }

        if (_pendingSublevel && !blocked && !inWave)
        {
            bool selected = Execute(new AutomationAction(
                "selectSublevel",
                JObject.FromObject(new { index = 0 }),
                AutomationStage.SelectingRoute,
                "选择第一个可用的子关卡。"));
            if (selected && _runState == AutoPlayerRunState.Running) _pendingSublevel = false;
            return;
        }

        AutomationAction action = _decisionEngine.DecideInGame(affordances, reward, events);
        if (action.Stage == AutomationStage.Completed)
        {
            Fault("运行时报告本局结束，但插件没有观察到可验证的胜利事件：" + action.Reason);
            return;
        }

        Execute(action);
    }

    private bool EnsureInGameRuntimeReady()
    {
        if (_runtimeInitialized) return true;

        JObject initialization = _bridge.Invoke("queryState");
        switch (RuntimeResultInspector.Classify(initialization))
        {
            case RuntimeResultDisposition.Unsafe:
                Fault("命令 queryState 报告状态已被污染，需要启动新的游戏进程：" + Message(initialization));
                return false;
            case RuntimeResultDisposition.Pending:
                SetStage(AutomationStage.InitializingRun, Message(initialization));
                return false;
            case RuntimeResultDisposition.Failure:
                RegisterFailure("命令 queryState 失败：" + Message(initialization));
                return false;
        }

        _runtimeInitialized = true;
        return true;
    }

    private bool TryHandleObservedWave()
    {
        if (!_wasInWave) return false;

        if (_battleTacticPending && _battleWaveSnapshot != null)
        {
            _battleTacticPending = false;
            RunBattleTacticStep(_battleWaveSnapshot);
            if (_battleTacticStep == BattleTacticStep.Complete &&
                _nextBattleTacticCycleAt <= Time.realtimeSinceStartup)
            {
                _nextBattleTacticCycleAt = Time.realtimeSinceStartup + BattleTacticCycleIntervalSeconds;
            }
            if (_runState == AutoPlayerRunState.Running &&
                _wasInWave &&
                _battleTacticStep != BattleTacticStep.Complete)
            {
                _battleTacticPending = true;
                _nextTickAt = Time.realtimeSinceStartup + BattleTacticFrameDelaySeconds;
            }
            else if (_runState == AutoPlayerRunState.Running && _wasInWave)
            {
                _nextTickAt = Math.Max(
                    Time.realtimeSinceStartup + BattleTacticFrameDelaySeconds,
                    _nextBattleWaveProbeAt);
            }
            return true;
        }

        JObject waveResult = _bridge.Invoke("queryWave");
        switch (RuntimeResultInspector.Classify(waveResult))
        {
            case RuntimeResultDisposition.Unsafe:
                Fault("命令 queryWave 报告状态已被污染，需要启动新的游戏进程：" + Message(waveResult));
                return true;
            case RuntimeResultDisposition.Pending:
                SetStage(AutomationStage.Battle, Message(waveResult));
                return true;
            case RuntimeResultDisposition.Failure:
                RegisterFailure("命令 queryWave 失败；为避免完整状态扫描拖慢战斗，将在退避后重试：" + Message(waveResult));
                _nextTickAt = Time.realtimeSinceStartup + 5f;
                return true;
        }

        _consecutiveFailures = 0;
        JObject waveState = waveResult.SelectToken("data.state") as JObject ?? new JObject();
        JArray blockers = waveState["blockers"] as JArray ?? new JArray();
        bool inWave = waveState["isInWaving"]?.Value<bool>() == true;
        bool gameOver = HasBlocker(blockers, "gameOver")
                        || GameOutcomeObserver.Outcome is AutomationOutcome.Victory or AutomationOutcome.Defeat;
        ObserveWaveTransition(inWave);

        if (gameOver)
        {
            ScheduleNormalPoll();
            TickSettlement();
            return true;
        }

        if (!inWave)
        {
            ScheduleNormalPoll();
            return true;
        }

        SetStage(AutomationStage.Battle, BuildWaveStageDetail(waveState));
        if (_battleTacticStep == BattleTacticStep.Complete &&
            Time.realtimeSinceStartup >= _nextBattleTacticCycleAt)
        {
            BeginBattleTacticCycle();
        }
        _nextBattleWaveProbeAt = Time.realtimeSinceStartup + Math.Max(
            MinimumBattlePollIntervalSeconds,
            _settings.TickIntervalSeconds.Value);
        _battleWaveSnapshot = waveResult;
        _battleTacticPending = _battleTacticStep != BattleTacticStep.Complete;
        if (_battleTacticPending)
        {
            _nextTickAt = Time.realtimeSinceStartup + BattleTacticFrameDelaySeconds;
        }
        return true;
    }

    private bool TryHandlePendingMapSelection()
    {
        if (_pendingMapAction != null)
        {
            AutomationAction action = _pendingMapAction;
            _pendingMapAction = null;
            Execute(action);
            if (string.Equals(action.Command, "chooseWaveFunctionOption", StringComparison.OrdinalIgnoreCase))
            {
                _pendingEventPanel = string.Empty;
                _eventOptionsReadyAt = -1f;
            }
            return true;
        }

        if (!_mapSelectionPending) return false;

        if (!string.IsNullOrWhiteSpace(_pendingEventPanel))
        {
            if (_mapSelectionPendingAt >= 0f &&
                Time.realtimeSinceStartup - _mapSelectionPendingAt >= MapSelectionTransitionTimeoutSeconds)
            {
                _mapSelectionPending = false;
                _mapSelectionPendingAt = -1f;
                _eventOptionsReadyAt = -1f;
                _pendingEventPanel = string.Empty;
                AddWarning("地图节点点击后的事件处理超过安全时限，将在下一轮重新读取完整状态。");
                return true;
            }

            if (Time.realtimeSinceStartup < _eventOptionsReadyAt)
            {
                _nextTickAt = Math.Max(_nextTickAt, _eventOptionsReadyAt);
                SetStage(AutomationStage.ManagingEvent, "轨神事件正在播放入场动画，等待游戏生成可选项。");
                return true;
            }

            JObject eventResult = _bridge.Invoke("queryEventOptions");
            switch (RuntimeResultInspector.Classify(eventResult))
            {
                case RuntimeResultDisposition.Unsafe:
                    Fault("命令 queryEventOptions 报告状态已被污染，需要启动新的游戏进程：" + Message(eventResult));
                    return true;
                case RuntimeResultDisposition.Pending:
                    SetStage(AutomationStage.ManagingEvent, Message(eventResult));
                    return true;
                case RuntimeResultDisposition.Failure:
                    RegisterFailure("命令 queryEventOptions 失败：" + Message(eventResult));
                    return true;
            }

            AutomationAction eventAction = _decisionEngine.DecideEvent(eventResult, _pendingEventPanel);
            if (string.Equals(eventAction.Command, "wait", StringComparison.OrdinalIgnoreCase))
            {
                SetStage(eventAction.Stage, eventAction.Reason);
                return true;
            }

            _pendingMapAction = eventAction;
            _nextTickAt = Time.realtimeSinceStartup + BattleTacticFrameDelaySeconds;
            return true;
        }

        JObject transitionResult = _bridge.Invoke("queryWave");
        switch (RuntimeResultInspector.Classify(transitionResult))
        {
            case RuntimeResultDisposition.Unsafe:
                Fault("命令 queryWave 报告状态已被污染，需要启动新的游戏进程：" + Message(transitionResult));
                return true;
            case RuntimeResultDisposition.Pending:
                SetStage(AutomationStage.SelectingRoute, Message(transitionResult));
                return true;
            case RuntimeResultDisposition.Failure:
                RegisterFailure("地图节点点击后的轻量状态查询失败，将在退避后重试：" + Message(transitionResult));
                _nextTickAt = Time.realtimeSinceStartup + 2f;
                return true;
        }

        _consecutiveFailures = 0;
        JObject state = transitionResult.SelectToken("data.state") as JObject ?? new JObject();
        JArray blockers = state["blockers"] as JArray ?? new JArray();
        bool inWave = state["isInWaving"]?.Value<bool>() == true;
        ObserveWaveTransition(inWave);

        if (HasBlocker(blockers, "gameOver") ||
            GameOutcomeObserver.Outcome is AutomationOutcome.Victory or AutomationOutcome.Defeat)
        {
            _mapSelectionPending = false;
            _mapSelectionPendingAt = -1f;
            _eventOptionsReadyAt = -1f;
            _pendingEventPanel = string.Empty;
            TickSettlement();
            return true;
        }

        if (inWave)
        {
            _mapSelectionPending = false;
            _mapSelectionPendingAt = -1f;
            _eventOptionsReadyAt = -1f;
            _pendingEventPanel = string.Empty;
            SetStage(AutomationStage.Battle, BuildWaveStageDetail(state));
            return true;
        }

        string? panel = HasBlocker(blockers, "EventUI")
            ? "EventUI"
            : HasBlocker(blockers, "RepairUI")
                ? "RepairUI"
                : null;
        if (panel != null)
        {
            _pendingEventPanel = panel;
            if (string.Equals(panel, "EventUI", StringComparison.OrdinalIgnoreCase))
            {
                if (_eventOptionsReadyAt < 0f)
                {
                    float transitionStartedAt = _mapSelectionPendingAt >= 0f
                        ? _mapSelectionPendingAt
                        : Time.realtimeSinceStartup;
                    _eventOptionsReadyAt = transitionStartedAt + EventOptionGenerationDelaySeconds;
                }

                if (Time.realtimeSinceStartup < _eventOptionsReadyAt)
                {
                    _nextTickAt = Math.Max(_nextTickAt, _eventOptionsReadyAt);
                    SetStage(AutomationStage.ManagingEvent, "轨神事件正在播放入场动画，等待游戏生成可选项。");
                    return true;
                }
            }
            else
            {
                _eventOptionsReadyAt = Time.realtimeSinceStartup;
            }

            _nextTickAt = Time.realtimeSinceStartup + BattleTacticFrameDelaySeconds;
            SetStage(AutomationStage.ManagingEvent, "事件界面已打开，准备读取可用选项。");
            return true;
        }

        if (state["canStartWave"]?.Value<bool>() == true)
        {
            _mapSelectionPending = false;
            _mapSelectionPendingAt = -1f;
            _eventOptionsReadyAt = -1f;
            _pendingEventPanel = string.Empty;
            _pendingMapAction = new AutomationAction(
                "startWave",
                null,
                AutomationStage.StartingWave,
                "地图节点已稳定提交，开始选定的波次。");
            _nextTickAt = Time.realtimeSinceStartup + BattleTacticFrameDelaySeconds;
            return true;
        }

        if (blockers.Count > 0 || state["canSelectNextNode"]?.Value<bool>() == true)
        {
            _mapSelectionPending = false;
            _mapSelectionPendingAt = -1f;
            _eventOptionsReadyAt = -1f;
            _pendingEventPanel = string.Empty;
            return true;
        }

        if (_mapSelectionPendingAt >= 0f &&
            Time.realtimeSinceStartup - _mapSelectionPendingAt >= MapSelectionTransitionTimeoutSeconds)
        {
            _mapSelectionPending = false;
            _mapSelectionPendingAt = -1f;
            _eventOptionsReadyAt = -1f;
            _pendingEventPanel = string.Empty;
            AddWarning("地图节点点击后的轨神事件过渡超过安全时限，将在下一轮重新读取完整状态。");
            return true;
        }

        SetStage(AutomationStage.SelectingRoute, "地图节点已点击，正在等待轨神事件或关卡状态提交。");
        return true;
    }

    private void RunBattleTacticStep(JObject waveResult)
    {
        switch (_battleTacticStep)
        {
            case BattleTacticStep.QueryThreats:
                if (TryInvokeOptional("queryWaveThreats", null, out JObject threats))
                {
                    _battleThreats = threats;
                    _battleConfirmationArguments = BuildThreatWorldArguments(threats);
                }
                _battleTacticStep = _battleDisposableUsedThisWave
                    ? BattleTacticStep.QueryRail
                    : BattleTacticStep.QueryDisposable;
                return;

            case BattleTacticStep.QueryDisposable:
                if (!TryInvokeOptional("queryDisposable", null, out JObject disposable))
                {
                    _battleTacticStep = BattleTacticStep.QueryRail;
                    return;
                }

                _battleDisposable = disposable;
                if (State(disposable)["isInPreview"]?.Value<bool>() == true)
                {
                    if (IsOwnedDisposablePreview(disposable))
                    {
                        SetStage(AutomationStage.Battle, "检测到 AutoPlayer 上轮未完成的消耗品预览，准备安全清理。");
                        _battleTacticStep = BattleTacticStep.CancelDisposable;
                        return;
                    }

                    SetStage(AutomationStage.Battle, "检测到玩家正在预览消耗品；AutoPlayer 不会接管该交互。");
                    ClearOwnedDisposable();
                    _battleTacticStep = BattleTacticStep.Complete;
                    return;
                }

                AutomationAction? disposableAction = _battleDecisionEngine.Decide(
                    new BattleDecisionContext
                    {
                        DisposablePhase = BattleDisposablePhase.Ready,
                        AllowDisposableUse = true,
                        AllowVehicleReinforcement = false
                    },
                    waveResult,
                    disposable,
                    null,
                    null);
                if (disposableAction == null)
                {
                    _battleTacticStep = BattleTacticStep.QueryRail;
                    return;
                }

                if (!string.Equals(disposableAction.Command, "useDisposable", StringComparison.OrdinalIgnoreCase))
                {
                    _battleTacticStep = BattleTacticStep.QueryRail;
                    return;
                }

                _ownedDisposableEnum = ResolveSelectedDisposableEnum(disposable, disposableAction.Arguments);
                if (string.IsNullOrWhiteSpace(_ownedDisposableEnum))
                {
                    AddWarning("无法确认待使用消耗品的枚举身份，已跳过本次自动使用。");
                    _battleTacticStep = BattleTacticStep.QueryRail;
                    return;
                }
                _battlePendingAction = disposableAction;
                _battleTacticStep = BattleTacticStep.UseDisposable;
                return;

            case BattleTacticStep.UseDisposable:
                AutomationAction? useAction = _battlePendingAction;
                _battlePendingAction = null;
                if (useAction == null ||
                    !string.Equals(useAction.Command, "useDisposable", StringComparison.OrdinalIgnoreCase))
                {
                    _battleTacticStep = BattleTacticStep.QueryRail;
                    return;
                }

                if (!TryInvokeOptional("queryDisposable", null, out JObject useCheck) ||
                    State(useCheck)["isInPreview"]?.Value<bool>() == true)
                {
                    SetStage(AutomationStage.Battle, "玩家已开始消耗品预览；已放弃 AutoPlayer 待执行的道具操作。");
                    ClearOwnedDisposable();
                    _battleTacticStep = BattleTacticStep.Complete;
                    return;
                }

                bool used = TryExecuteActiveBattleAction(useAction, out JObject useResult);
                if (used && _runState == AutoPlayerRunState.Running)
                {
                    _battleDisposableUsedThisWave = true;
                    JObject usedState = State(useResult);
                    if (usedState["isInPreview"]?.Value<bool>() == true)
                    {
                        _ownedDisposableInteractionInstanceId =
                            usedState["interactionInstanceId"]?.Value<int?>() ?? 0;
                        if (_ownedDisposableInteractionInstanceId == 0)
                        {
                            AddWarning("自动使用消耗品后无法取得预览实例身份；已立即尝试取消该预览。");
                            Execute(new AutomationAction(
                                "cancelDisposable",
                                null,
                                AutomationStage.Battle,
                                "取消缺少实例身份的自动消耗品预览，恢复游戏输入。"), optional: true);
                            ClearOwnedDisposable();
                            _battleTacticStep = BattleTacticStep.Complete;
                            return;
                        }

                        _battleTacticStep = BattleTacticStep.QueryDisposablePreview;
                    }
                    else
                    {
                        ClearOwnedDisposable();
                        _battleTacticStep = BattleTacticStep.QueryRail;
                    }
                }
                else
                {
                    ClearOwnedDisposable();
                    _battleTacticStep = BattleTacticStep.QueryRail;
                }
                return;

            case BattleTacticStep.QueryDisposablePreview:
                if (!TryInvokeOptional("queryDisposable", null, out JObject preview))
                {
                    _battleTacticStep = BattleTacticStep.CancelDisposable;
                    return;
                }

                _battleDisposable = preview;
                JObject previewState = State(preview);
                if (previewState["isInPreview"]?.Value<bool>() != true)
                {
                    ClearOwnedDisposable();
                    _battleTacticStep = BattleTacticStep.QueryRail;
                    return;
                }

                if (!IsOwnedDisposablePreview(preview))
                {
                    SetStage(AutomationStage.Battle, "当前消耗品预览不属于 AutoPlayer；已停止本轮战术以保留玩家操作。");
                    ClearOwnedDisposable();
                    _battleTacticStep = BattleTacticStep.Complete;
                    return;
                }

                string confirmKind = previewState.SelectToken("confirmContract.confirmKind")?.Value<string>() ?? string.Empty;
                if (string.Equals(confirmKind, "grid", StringComparison.OrdinalIgnoreCase))
                {
                    _battleTacticStep = BattleTacticStep.QueryDisposableGridOptions;
                    return;
                }

                JObject? confirmationArguments = confirmKind is "world" or "positionRaycast"
                    ? _battleConfirmationArguments
                    : null;
                AutomationAction? confirmAction = _battleDecisionEngine.Decide(
                    new BattleDecisionContext
                    {
                        DisposablePhase = BattleDisposablePhase.Confirming,
                        AllowDisposableUse = false,
                        AllowVehicleReinforcement = false,
                        DisposableConfirmationArguments = confirmationArguments
                    },
                    waveResult,
                    preview,
                    null,
                    null);
                if (confirmAction == null ||
                    string.Equals(confirmAction.Command, "cancelDisposable", StringComparison.OrdinalIgnoreCase))
                {
                    _battleTacticStep = BattleTacticStep.CancelDisposable;
                    return;
                }

                _battlePendingAction = confirmAction;
                _battleTacticStep = BattleTacticStep.ConfirmDisposable;
                return;

            case BattleTacticStep.QueryDisposableGridOptions:
                if (!TryInvokeOptional(
                        "queryDisposableGridOptions",
                        JObject.FromObject(new { maxResults = 12 }),
                        out JObject gridOptions))
                {
                    _battleTacticStep = BattleTacticStep.CancelDisposable;
                    return;
                }

                AutomationAction? gridAction = _battleDecisionEngine.Decide(
                    new BattleDecisionContext
                    {
                        DisposablePhase = BattleDisposablePhase.Confirming,
                        AllowDisposableUse = false,
                        AllowVehicleReinforcement = false,
                        DisposableGridOptionsResult = gridOptions
                    },
                    waveResult,
                    _battleDisposable,
                    null,
                    null);
                if (gridAction == null ||
                    string.Equals(gridAction.Command, "cancelDisposable", StringComparison.OrdinalIgnoreCase))
                {
                    _battleTacticStep = BattleTacticStep.CancelDisposable;
                    return;
                }

                _battlePendingAction = gridAction;
                _battleTacticStep = BattleTacticStep.ConfirmDisposable;
                return;

            case BattleTacticStep.ConfirmDisposable:
                AutomationAction? confirm = _battlePendingAction;
                _battlePendingAction = null;
                if (!TryInvokeOptional("queryDisposable", null, out JObject confirmCheck) ||
                    !IsOwnedDisposablePreview(confirmCheck))
                {
                    SetStage(AutomationStage.Battle, "消耗品预览实例已变化；AutoPlayer 不会确认玩家的操作。");
                    ClearOwnedDisposable();
                    _battleTacticStep = BattleTacticStep.Complete;
                    return;
                }

                if (confirm != null && TryExecuteActiveBattleAction(confirm, out _))
                {
                    ClearOwnedDisposable();
                    _battleTacticStep = BattleTacticStep.QueryRail;
                }
                else
                {
                    _battleTacticStep = BattleTacticStep.CancelDisposable;
                }
                return;

            case BattleTacticStep.CancelDisposable:
                if (!TryInvokeOptional("queryDisposable", null, out JObject cancelCheck))
                {
                    _battleTacticStep = BattleTacticStep.Complete;
                    return;
                }

                if (!IsOwnedDisposablePreview(cancelCheck))
                {
                    bool playerPreviewActive = State(cancelCheck)["isInPreview"]?.Value<bool>() == true;
                    ClearOwnedDisposable();
                    _battleTacticStep = playerPreviewActive
                        ? BattleTacticStep.Complete
                        : BattleTacticStep.QueryRail;
                    return;
                }

                if (_bridge.HasCommand("cancelDisposable"))
                {
                    Execute(new AutomationAction(
                        "cancelDisposable",
                        null,
                        AutomationStage.Battle,
                        "取消无法安全确认的消耗品预览，恢复游戏输入。"), optional: true);
                }
                ClearOwnedDisposable();
                _battleTacticStep = BattleTacticStep.QueryRail;
                return;

            case BattleTacticStep.QueryRail:
                if (_battleThreats == null || !TryInvokeOptional("queryRail", null, out JObject rail))
                {
                    _battleTacticStep = BattleTacticStep.Complete;
                    return;
                }
                _battleRail = rail;
                _battleTacticStep = BattleTacticStep.QueryTrain;
                return;

            case BattleTacticStep.QueryTrain:
                if (!TryInvokeOptional("queryTrain", null, out JObject train))
                {
                    _battleTacticStep = BattleTacticStep.Complete;
                    return;
                }
                _battleTrain = train;
                AutomationAction movement = _battleDecisionEngine.DecideTrainMovement(
                    _battleThreats,
                    _battleRail,
                    _battleTrain);
                if (!string.Equals(movement.Command, "wait", StringComparison.OrdinalIgnoreCase))
                {
                    _battlePendingAction = movement;
                    _battleTacticStep = BattleTacticStep.MoveTrain;
                    return;
                }
                SetStage(AutomationStage.Battle, movement.Reason);
                _battleTacticStep = BattleTacticStep.Complete;
                return;

            case BattleTacticStep.MoveTrain:
                AutomationAction? move = _battlePendingAction;
                _battlePendingAction = null;
                if (move != null &&
                    TryInvokeOptional("queryRail", null, out JObject latestRail) &&
                    TryInvokeOptional("queryTrain", null, out JObject latestTrain))
                {
                    AutomationAction latestMovement = _battleDecisionEngine.DecideTrainMovement(
                        _battleThreats,
                        latestRail,
                        latestTrain);
                    if (!string.Equals(latestMovement.Command, "wait", StringComparison.OrdinalIgnoreCase))
                    {
                        TryExecuteActiveBattleAction(latestMovement, out _);
                    }
                }
                _battleTacticStep = BattleTacticStep.Complete;
                return;

            case BattleTacticStep.Complete:
            default:
                return;
        }
    }

    private bool TryMaintainDefense()
    {
        if (!_defenseMaintenanceRequested || !_defenseMaintenanceReady) return false;
        if (!_bridge.HasCommand("queryTrain") || !_bridge.HasCommand("moveVehicleInTrain"))
        {
            _defenseMaintenanceRequested = false;
            _defenseMaintenanceReady = false;
            AddWarning("当前游戏构建缺少战车自动编列接口，已保留现有防线继续游玩。");
            return true;
        }

        if (_defenseMaintenanceStep == DefenseMaintenanceStep.QueryTrain)
        {
            if (!TryInvokeOptional("queryTrain", null, out JObject train))
            {
                _defenseMaintenanceRequested = false;
                _defenseMaintenanceReady = false;
                return true;
            }

            _defenseTrain = train;
            _defenseMaintenanceStep = DefenseMaintenanceStep.QueryVehicle;
            _nextTickAt = Time.realtimeSinceStartup + BattleTacticFrameDelaySeconds;
            SetStage(AutomationStage.PreparingDefense, "正在检查现有车列容量。");
            return true;
        }

        if (_defenseMaintenanceStep == DefenseMaintenanceStep.MoveVehicle)
        {
            AutomationAction? pendingAction = _defensePendingAction;
            _defensePendingAction = null;
            bool executed = pendingAction != null && Execute(pendingAction, optional: true);
            _defenseMaintenanceStep = DefenseMaintenanceStep.QueryTrain;
            _defenseMaintenanceReady = false;
            _defenseTrain = null;
            if (!executed) _defenseMaintenanceRequested = false;
            return true;
        }

        if (!TryInvokeOptional("queryVehicle", null, out JObject vehicles))
        {
            _defenseMaintenanceRequested = false;
            _defenseMaintenanceReady = false;
            return true;
        }

        AutomationAction? action = _battleDecisionEngine.Decide(
            new BattleDecisionContext
            {
                AllowDisposableUse = false,
                AllowVehicleReinforcement = true
            },
            null,
            null,
            _defenseTrain,
            vehicles);
        if (action == null)
        {
            _defenseMaintenanceRequested = false;
            _defenseMaintenanceReady = false;
            _defenseMaintenanceStep = DefenseMaintenanceStep.QueryTrain;
            _defenseTrain = null;
            SetStage(AutomationStage.PreparingDefense, "防线维护完成，没有可继续编入的背包战车或车列容量。");
            return true;
        }

        _defensePendingAction = action;
        _defenseMaintenanceStep = DefenseMaintenanceStep.MoveVehicle;
        _nextTickAt = Time.realtimeSinceStartup + BattleTacticFrameDelaySeconds;
        return true;
    }

    private bool TryInvokeOptional(string command, JObject? arguments, out JObject result)
    {
        result = new JObject();
        if (!_bridge.HasCommand(command))
        {
            AddWarning("当前游戏构建不支持可选自动战术命令 " + command + "，已跳过该战术。");
            return false;
        }

        JObject invocation = _bridge.Invoke(command, arguments);
        switch (RuntimeResultInspector.Classify(invocation))
        {
            case RuntimeResultDisposition.Unsafe:
                Fault("命令 " + command + " 报告状态已被污染，需要启动新的游戏进程：" + Message(invocation));
                return false;
            case RuntimeResultDisposition.Pending:
            case RuntimeResultDisposition.Failure:
                AddWarning("可选自动战术命令 " + command + " 未执行：" + Message(invocation));
                return false;
            default:
                result = invocation;
                return true;
        }
    }

    private void RequestDefenseMaintenance()
    {
        _defenseMaintenanceRequested = true;
        _defenseMaintenanceReady = false;
        _defenseMaintenanceStep = DefenseMaintenanceStep.QueryTrain;
        _defenseTrain = null;
        _defensePendingAction = null;
    }

    private void ResetBattleTactics()
    {
        _nextBattleWaveProbeAt = 0f;
        _nextBattleTacticCycleAt = 0f;
        _battleDisposableUsedThisWave = false;
        _ownedDisposableEnum = string.Empty;
        _ownedDisposableInteractionInstanceId = 0;
        BeginBattleTacticCycle();
    }

    private void BeginBattleTacticCycle()
    {
        _battleTacticStep = BattleTacticStep.QueryThreats;
        _battleTacticPending = false;
        _battleWaveSnapshot = null;
        _battlePendingAction = null;
        _battleThreats = null;
        _battleDisposable = null;
        _battleRail = null;
        _battleTrain = null;
        _battleConfirmationArguments = null;
    }

    private void ReleaseOwnedDisposablePreview()
    {
        if (!string.IsNullOrWhiteSpace(_ownedDisposableEnum) &&
            _bridge.HasCommand("queryDisposable") &&
            _bridge.HasCommand("cancelDisposable"))
        {
            JObject current = _bridge.Invoke("queryDisposable");
            if (RuntimeResultInspector.IsSuccess(current) && IsOwnedDisposablePreview(current))
            {
                JObject result = _bridge.Invoke("cancelDisposable");
                if (RuntimeResultInspector.IsSuccess(result))
                {
                    AddTimeline("battle-action", "已取消自动游玩发起的消耗品预览，恢复玩家输入。");
                }
                else
                {
                    AddWarning("暂停自动游玩时未能取消消耗品预览：" + Message(result));
                }
            }
        }

        ResetBattleTactics();
    }

    private bool TryExecuteActiveBattleAction(AutomationAction action, out JObject result)
    {
        result = new JObject();
        JObject validation = _bridge.Invoke("queryWave");
        switch (RuntimeResultInspector.Classify(validation))
        {
            case RuntimeResultDisposition.Unsafe:
                Fault("战术动作执行前的波次校验报告状态已被污染：" + Message(validation));
                return false;
            case RuntimeResultDisposition.Pending:
            case RuntimeResultDisposition.Failure:
                AddWarning("战术动作执行前无法确认波次仍在进行，已跳过该动作：" + Message(validation));
                return false;
        }

        JObject state = State(validation);
        if (state["isInWaving"]?.Value<bool>() != true)
        {
            SetStage(AutomationStage.Battle, "波次已经结束，已取消尚未执行的战术动作。");
            return false;
        }

        _battleWaveSnapshot = validation;
        return ExecuteWithResult(action, optional: true, out result);
    }

    private bool IsOwnedDisposablePreview(JObject result)
    {
        JObject state = State(result);
        return !string.IsNullOrWhiteSpace(_ownedDisposableEnum) &&
               _ownedDisposableInteractionInstanceId != 0 &&
               state["isInPreview"]?.Value<bool>() == true &&
               state["interactionInstanceId"]?.Value<int?>() == _ownedDisposableInteractionInstanceId &&
               string.Equals(
                   state["disposableEnum"]?.Value<string>(),
                   _ownedDisposableEnum,
                   StringComparison.OrdinalIgnoreCase);
    }

    private void ClearOwnedDisposable()
    {
        _ownedDisposableEnum = string.Empty;
        _ownedDisposableInteractionInstanceId = 0;
        _battlePendingAction = null;
    }

    private static string ResolveSelectedDisposableEnum(JObject result, JObject arguments)
    {
        JArray items = State(result)["items"] as JArray ?? new JArray();
        int itemInstanceId = arguments["itemInstanceId"]?.Value<int?>() ?? 0;
        int index = arguments["index"]?.Value<int?>() ?? -1;
        string? path = arguments["path"]?.Value<string>();
        JObject? item = items.OfType<JObject>().FirstOrDefault(candidate =>
            (itemInstanceId != 0 && candidate["itemInstanceId"]?.Value<int?>() == itemInstanceId) ||
            (index >= 0 && candidate["index"]?.Value<int?>() == index) ||
            (!string.IsNullOrWhiteSpace(path) && string.Equals(
                 candidate["path"]?.Value<string>() ?? candidate["itemPath"]?.Value<string>(),
                 path,
                 StringComparison.Ordinal)));
        return item?["disposableEnum"]?.Value<string>() ?? string.Empty;
    }

    private static JObject? BuildThreatWorldArguments(JObject threatsResult)
    {
        JObject threats = State(threatsResult);
        JObject? nest = (threats["nests"] as JArray)?.OfType<JObject>()
            .Where(item => item["active"]?.Value<bool>() != false && item["world"] is JObject)
            .OrderByDescending(item =>
                (item.SelectToken("spawn.level")?.Value<int?>() ?? 1) *
                (item.SelectToken("spawn.amount")?.Value<int?>() ?? 1))
            .ThenBy(item => item["index"]?.Value<int?>() ?? int.MaxValue)
            .FirstOrDefault();
        return nest?["world"] is JObject world
            ? new JObject { ["world"] = world.DeepClone() }
            : null;
    }

    private static JObject State(JObject? result) =>
        result?.SelectToken("data.state") as JObject
        ?? result?["state"] as JObject
        ?? result
        ?? new JObject();

    private void ScheduleNormalPoll()
    {
        _nextTickAt = Time.realtimeSinceStartup + Math.Max(0.2f, _settings.TickIntervalSeconds.Value);
    }

    private static string BuildWaveStageDetail(JObject waveState)
    {
        int? remaining = waveState.SelectToken("enemy.remaining")?.Value<int?>();
        string node = waveState["nodeType"]?.Value<string>() ?? string.Empty;
        string nodeName = node switch
        {
            "common" => "普通节点",
            "ferocityCommon" => "狂暴节点",
            "elite" => "精英节点",
            "boss" => "首领节点",
            _ => "当前节点"
        };
        return remaining.HasValue
            ? $"战斗中：{nodeName}，剩余 {remaining.Value} 个敌人。"
            : $"战斗中：{nodeName}。";
    }

    private void TickSettlement()
    {
        if (_gameOverDetectedAt < 0f)
        {
            _gameOverDetectedAt = Time.realtimeSinceStartup;
            SetStage(AutomationStage.Completed, "已检测到本局结束，正在验证结算界面。");
            AddTimeline("settlement", _stageDetail);
            MarkProgress();
        }

        AutomationOutcome observedOutcome = GameOutcomeObserver.Outcome;
        if (observedOutcome == AutomationOutcome.Defeat)
        {
            _outcome = AutomationOutcome.Defeat;
            Fault("已观察到游戏失败事件；本轮自动游玩没有获胜。");
            return;
        }

        if (observedOutcome != AutomationOutcome.Victory)
        {
            if (Time.realtimeSinceStartup - _gameOverDetectedAt >= OutcomeVerificationTimeoutSeconds)
            {
                Fault("游戏已经结束，但未能验证独立的胜利或失败事件。");
                return;
            }

            SetStage(AutomationStage.Completed, "游戏已经结束，正在等待独立胜负事件验证。");
            return;
        }

        _outcome = AutomationOutcome.Victory;

        JObject interactables = _bridge.Invoke("queryUiInteractables");
        switch (RuntimeResultInspector.Classify(interactables))
        {
            case RuntimeResultDisposition.Unsafe:
                Fault("命令 queryUiInteractables 报告状态已被污染，需要启动新的游戏进程：" + Message(interactables));
                return;
            case RuntimeResultDisposition.Pending:
                SetStage(AutomationStage.Completed, Message(interactables));
                return;
            case RuntimeResultDisposition.Failure:
                RegisterFailure("命令 queryUiInteractables 失败：" + Message(interactables));
                return;
        }

        _consecutiveFailures = 0;
        if (RuntimeResultInspector.HasActiveSettlementInteractable(interactables))
        {
            Complete("已通过可交互的结算界面确认本局结束。");
            return;
        }

        if (!_wishReturnClicked &&
            RuntimeResultInspector.TryGetWishPanelReturnInstanceId(interactables, out int returnButtonInstanceId))
        {
            bool clicked = Execute(new AutomationAction(
                "uiClick",
                JObject.FromObject(new { instanceId = returnButtonInstanceId }),
                AutomationStage.Completed,
                "通过返回按钮关闭愿望清单提示。"));
            if (clicked && _runState == AutoPlayerRunState.Running)
            {
                _wishReturnClicked = true;
                SetStage(AutomationStage.Completed, "愿望清单提示已关闭，正在等待结算界面。");
            }

            return;
        }

        SetStage(
            AutomationStage.Completed,
            _wishReturnClicked
                ? "正在等待可交互的结算界面。"
                : "正在等待愿望清单提示或结算界面。");
    }

    private bool Execute(AutomationAction action, bool optional = false) =>
        ExecuteWithResult(action, optional, out _);

    private bool ExecuteWithResult(AutomationAction action, bool optional, out JObject result)
    {
        result = new JObject();
        SetStage(action.Stage, action.Reason);
        if (string.Equals(action.Command, "wait", StringComparison.Ordinal))
        {
            _pendingActionKey = string.Empty;
            return false;
        }

        string actionKey = action.Command + ":" + action.Arguments.ToString(Newtonsoft.Json.Formatting.None);
        if (string.Equals(actionKey, _pendingActionKey, StringComparison.Ordinal))
        {
            SetStage(action.Stage, "正在等待先前发送的 " + action.Command + " 操作改变游戏状态。");
            return false;
        }

        _pendingActionKey = string.Empty;

        _lastCommand = action.Command;
        _lastActionAtUtc = DateTime.UtcNow;
        result = _bridge.Invoke(action.Command, action.Arguments);
        _lastMessage = Message(result);
        RuntimeResultDisposition disposition = RuntimeResultInspector.Classify(result);
        if (disposition == RuntimeResultDisposition.Unsafe)
        {
            Fault("命令 " + action.Command + " 报告状态已被污染，需要启动新的游戏进程：" + _lastMessage);
            return false;
        }

        if (disposition == RuntimeResultDisposition.Pending)
        {
            if (RuntimeResultInspector.IsSuccess(result))
            {
                _pendingActionKey = actionKey;
                if (IsRewardAcquisitionCommand(action.Command)) RequestDefenseMaintenance();
                MarkProgress();
                AddTimeline(optional ? "battle-pending" : "pending", action.Reason + " " + _lastMessage);
            }

            SetStage(action.Stage, _lastMessage);
            return false;
        }

        if (disposition == RuntimeResultDisposition.Failure)
        {
            if (optional)
            {
                AddWarning("可选自动战术命令 " + action.Command + " 失败：" + _lastMessage);
                return false;
            }

            if (string.Equals(action.Command, "prepareDefaultDefense", StringComparison.OrdinalIgnoreCase) &&
                RuntimeResultInspector.IsRetryableDefaultDefenseFailure(result))
            {
                SetStage(action.Stage, _lastMessage + " 将再次检查干净的开局状态。");
                return false;
            }

            RegisterFailure("命令 " + action.Command + " 失败：" + _lastMessage);
            return false;
        }

        if (string.Equals(action.Command, "selectMapNode", StringComparison.OrdinalIgnoreCase) &&
            !RuntimeResultInspector.HasCommittedMapNode(result))
        {
            _consecutiveFailures = 0;
            _mapSelectionPending = true;
            _mapSelectionPendingAt = Time.realtimeSinceStartup;
            _eventOptionsReadyAt = -1f;
            _pendingEventPanel = string.Empty;
            _pendingMapAction = null;
            MarkProgress();
            AddTimeline("pending", "地图节点已收到点击，正在等待轨神事件或关卡状态提交。");
            SetStage(AutomationStage.SelectingRoute, "地图节点已点击，正在等待轨神事件或关卡状态提交。");
            return true;
        }

        _consecutiveFailures = 0;
        if (IsRewardAcquisitionCommand(action.Command)) RequestDefenseMaintenance();
        MarkProgress();
        AddTimeline(optional ? "battle-action" : "action", action.Reason + " " + _lastMessage);
        if (string.Equals(action.Command, "selectMapNode", StringComparison.OrdinalIgnoreCase) &&
            result.SelectToken("data.state.pendingSubLevelNode") is JToken pendingNode &&
            pendingNode.Type != JTokenType.Null)
        {
            _pendingSublevel = true;
        }

        if (string.Equals(action.Command, "selectMapNode", StringComparison.OrdinalIgnoreCase))
        {
            _mapSelectionPending = !_pendingSublevel;
            _mapSelectionPendingAt = _mapSelectionPending ? Time.realtimeSinceStartup : -1f;
            _eventOptionsReadyAt = -1f;
            _pendingEventPanel = string.Empty;
            _pendingMapAction = null;
        }

        return true;
    }

    private static bool IsFrontEndMutation(string command) =>
        !string.Equals(command, "wait", StringComparison.OrdinalIgnoreCase);

    private static bool IsRewardAcquisitionCommand(string command) =>
        string.Equals(command, "collectRewardObject", StringComparison.OrdinalIgnoreCase) ||
        string.Equals(command, "chooseRewardOption", StringComparison.OrdinalIgnoreCase) ||
        string.Equals(command, "chooseWaveFunctionOption", StringComparison.OrdinalIgnoreCase);

    private static bool IsSceneTransitionCommand(string command) =>
        string.Equals(command, "submitCommonMode", StringComparison.OrdinalIgnoreCase) ||
        string.Equals(command, "continueGame", StringComparison.OrdinalIgnoreCase) ||
        string.Equals(command, "enterRandomMode", StringComparison.OrdinalIgnoreCase) ||
        string.Equals(command, "submitRandomMode", StringComparison.OrdinalIgnoreCase);

    private void ObserveWaveTransition(bool inWave)
    {
        if (inWave && !_wasInWave)
        {
            _wasInWave = true;
            _wavesStarted++;
            ResetBattleTactics();
            AddTimeline("wave-start", "已观察到第 " + _wavesStarted + " 个波次开始。");
            MarkProgress();
        }
        else if (!inWave && _wasInWave)
        {
            _wasInWave = false;
            _wavesCompleted++;
            ResetBattleTactics();
            RequestDefenseMaintenance();
            AddTimeline("wave-complete", "已观察到第 " + _wavesCompleted + " 个波次完成。");
            MarkProgress();
        }
    }

    private void RegisterFailure(string message)
    {
        _consecutiveFailures++;
        _lastMessage = message;
        AddTimeline("error", message);
        if (_consecutiveFailures >= Math.Max(1, _settings.MaxConsecutiveFailures.Value))
        {
            Fault("命令连续失败次数已达到配置上限：" + message);
        }
    }

    private void CheckForStall()
    {
        if (_runState != AutoPlayerRunState.Running) return;
        float configured = Math.Max(15f, _settings.StallTimeoutSeconds.Value);
        float timeout = _stage == AutomationStage.Battle ? Math.Max(300f, configured) : configured;
        if (Time.realtimeSinceStartup - _lastProgressAt >= timeout)
        {
            Fault("未观察到可验证的自动游玩进展：" + _stageDetail);
        }
    }

    private void Fault(string reason)
    {
        lock (_sync)
        {
            if (_runState == AutoPlayerRunState.Faulted) return;
            _runState = AutoPlayerRunState.Faulted;
            _stage = AutomationStage.Recovery;
            _stageDetail = reason;
            _lastMessage = reason;
            _needsProcessRestart = true;
            if (_outcome is AutomationOutcome.Unknown or AutomationOutcome.InProgress)
            {
                _outcome = AutomationOutcome.Error;
            }
            AddTimeline("fault", reason);
            _evidence.CaptureFailure(EnsureEvidenceDirectory(), reason, Snapshot());
        }
    }

    private void Complete(string reason)
    {
        if (_outcome != AutomationOutcome.Victory)
        {
            Fault("拒绝把未验证为胜利的运行标记为完成：" + reason);
            return;
        }

        lock (_sync)
        {
            if (_runState == AutoPlayerRunState.Completed) return;
            _runState = AutoPlayerRunState.Completed;
            _stage = AutomationStage.Completed;
            _stageDetail = reason;
            _lastMessage = reason;
            AddTimeline("complete", reason);
            _evidence.CaptureCompletion(EnsureEvidenceDirectory(), Snapshot());
        }
    }

    private void SetStage(AutomationStage stage, string detail)
    {
        lock (_sync)
        {
            bool stageChanged = _stage != stage;
            _stage = stage;
            _stageDetail = detail;
            if (stageChanged) _log.LogDebug(StageDisplayName(stage) + "：" + detail);
        }
    }

    private void AddWarning(string message)
    {
        _lastMessage = message;
        _log.LogWarning(message);
        AddTimeline("warning", message);
    }

    private static bool HasPlacedCombatVehicle(JObject state) =>
        (state.SelectToken("vehicle.vehicles") as JArray)?.OfType<JObject>().Any(vehicle =>
            vehicle["active"]?.Value<bool>() == true &&
            vehicle["inBag"]?.Value<bool>() != true &&
            vehicle["isFixedHead"]?.Value<bool>() != true) == true;

    private void MarkProgress() => _lastProgressAt = Time.realtimeSinceStartup;

    private string EnsureEvidenceDirectory()
    {
        if (string.IsNullOrWhiteSpace(_evidenceDirectory)) _evidenceDirectory = _evidence.CreateRunDirectory();
        return _evidenceDirectory;
    }

    private void AddTimeline(string kind, string message)
    {
        lock (_sync)
        {
            _timeline.Add(new TimelineEvent
            {
                TimestampUtc = DateTime.UtcNow,
                Stage = _stage,
                Kind = kind,
                Message = message
            });
            while (_timeline.Count > MaxTimelineEvents) _timeline.RemoveAt(0);
        }
    }

    private string BuildCompatibilityError()
    {
        if (!_fingerprint.ProductIdentityValid)
            return "所选进程不是 PoneGames 的 Loopstructor 2: Skyspine。";
        if (!_fingerprint.MatchesExpectedAssembly(_activation.ExpectedAssemblySha256))
            return "Assembly-CSharp.dll 在验证后发生变化；请更新或重新安装自动游玩适配器。";
        if (!_bridge.IsAvailable)
            return "当前游戏版本缺少必需的自动游玩运行时成员：" + string.Join(", ", _bridge.MissingMembers);
        if (!_activation.IsPlayerMode)
        {
            if (!SaveIsolationPatch.Installed)
                return "无法安装存档隔离挂钩。";
            if (!PlatformWriteIsolationPatch.Applied)
                return "外部平台写入隔离不完整。";
            if (!GameArtifactIsolationPatch.Applied)
                return "游戏诊断产物隔离不完整。";
        }
        if (!GameOutcomeObserver.Installed)
            return "无法安装只读胜负结果观察器。";
        return string.Empty;
    }

    private static string StageDisplayName(AutomationStage stage) => stage switch
    {
        AutomationStage.WaitingForGame => "等待游戏",
        AutomationStage.FrontEnd => "游戏前端",
        AutomationStage.RandomSelection => "随机模式选择",
        AutomationStage.InitializingRun => "初始化本局",
        AutomationStage.PreparingDefense => "准备防线",
        AutomationStage.ManagingRewards => "处理奖励",
        AutomationStage.ManagingEvent => "处理事件",
        AutomationStage.ManagingShop => "处理商店",
        AutomationStage.SelectingRoute => "选择路线",
        AutomationStage.StartingWave => "开始波次",
        AutomationStage.Battle => "战斗",
        AutomationStage.Completed => "已完成",
        AutomationStage.Recovery => "故障恢复",
        _ => stage.ToString()
    };

    private static string ModeDisplayName(AutomationGameMode mode) =>
        mode == AutomationGameMode.Random ? "随机" : "普通";

    private static AutomationRunOptions Normalize(AutomationRunOptions options)
    {
        options.CharacterIndex = Math.Max(0, options.CharacterIndex);
        options.DifficultyIndex = Math.Max(0, options.DifficultyIndex);
        options.SuperModuleIndex = Math.Max(0, options.SuperModuleIndex);
        options.RandomVehicleIndex = Math.Max(0, options.RandomVehicleIndex);
        options.RandomFetterIndex = Math.Max(0, options.RandomFetterIndex);
        AutoPlayerGameSpeed.Normalize(options);
        options.MaxRunMinutes = Math.Max(1, Math.Min(1440, options.MaxRunMinutes));
        options.MaxWaves = Math.Max(0, options.MaxWaves);
        return options;
    }

    private static int GetCurrentProcessId()
    {
#if NET5_0_OR_GREATER
        return Environment.ProcessId;
#else
        using Process process = Process.GetCurrentProcess();
        return process.Id;
#endif
    }

    private static string Message(JObject result) => RuntimeResultInspector.Message(result);
    private static bool HasBlocker(JArray blockers, string key) => blockers.OfType<JObject>()
        .Any(item => string.Equals(item["key"]?.Value<string>(), key, StringComparison.OrdinalIgnoreCase));
}
