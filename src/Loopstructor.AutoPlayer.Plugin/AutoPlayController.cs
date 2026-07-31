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
    private const int MaxTimelineEvents = 100;
    private const float SaveVerificationTimeoutSeconds = 30f;

    private readonly object _sync = new();
    private readonly RuntimeBridge _bridge;
    private readonly PluginSettings _settings;
    private readonly BuildFingerprint _fingerprint;
    private readonly ActivationContext _activation;
    private readonly EvidenceRecorder _evidence;
    private readonly ManualLogSource _log;
    private readonly DecisionEngine _decisionEngine = new();
    private readonly List<TimelineEvent> _timeline = new();

    private AutomationRunOptions _options = new();
    private AutoPlayerRunState _runState;
    private AutomationStage _stage = AutomationStage.WaitingForGame;
    private string _stageDetail = string.Empty;
    private string _lastCommand = string.Empty;
    private string _lastMessage = string.Empty;
    private string _scene = string.Empty;
    private string _evidenceDirectory = string.Empty;
    private string _compatibilityError = string.Empty;
    private int _consecutiveFailures;
    private int _wavesStarted;
    private int _wavesCompleted;
    private float _nextTickAt;
    private float _lastProgressAt;
    private float _gameOverDetectedAt = -1f;
    private bool _defensePrepared;
    private bool _openingDefenseRequired;
    private bool _speedConfigured;
    private bool _pendingSublevel;
    private bool _wasInWave;
    private bool _wishReturnClicked;
    private bool _needsProcessRestart;
    private bool _frontEndReadinessObserved;
    private string _pendingActionKey = string.Empty;
    private DateTime _startedAtUtc;
    private DateTime _lastActionAtUtc;

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
        _compatibilityError = BuildCompatibilityError();
        _runState = string.IsNullOrEmpty(_compatibilityError)
            ? AutoPlayerRunState.Standby
            : AutoPlayerRunState.Incompatible;
        _stageDetail = string.IsNullOrEmpty(_compatibilityError)
            ? "已激活，正在等待开始命令。"
            : _compatibilityError;
        if (!string.IsNullOrEmpty(_compatibilityError)) AddTimeline("error", _compatibilityError);
    }

    public bool Start(AutomationRunOptions? options, out string message)
    {
        lock (_sync)
        {
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
            _stage = AutomationStage.WaitingForGame;
            _stageDetail = "正在等待存档隔离验证和受支持的场景。";
            _consecutiveFailures = 0;
            _wavesStarted = 0;
            _wavesCompleted = 0;
            _openingDefenseRequired = false;
            _defensePrepared = string.Equals(
                SceneManager.GetActiveScene().name,
                "NewGameScene",
                StringComparison.OrdinalIgnoreCase);
            _speedConfigured = false;
            _pendingSublevel = false;
            _wasInWave = false;
            _wishReturnClicked = false;
            _frontEndReadinessObserved = false;
            _pendingActionKey = string.Empty;
            _gameOverDetectedAt = -1f;
            _startedAtUtc = DateTime.UtcNow;
            _lastActionAtUtc = _startedAtUtc;
            _lastProgressAt = Time.realtimeSinceStartup;
            _nextTickAt = 0f;
            _evidenceDirectory = _evidence.CreateRunDirectory();
            AddTimeline("start", $"已使用{ModeDisplayName(_options.Mode)}模式开始自动游玩。");
            message = "自动游玩已开始。";
            return true;
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

            _runState = AutoPlayerRunState.Paused;
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

            _runState = AutoPlayerRunState.Standby;
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
        // The manager requires save isolation to be verified before it enables Start.
        // Probe while in standby so the safety handshake cannot deadlock.
        SaveIsolationPatch.ProbeRuntimeSaveFolder();
        if (_runState != AutoPlayerRunState.Running || Time.realtimeSinceStartup < _nextTickAt) return;
        _nextTickAt = Time.realtimeSinceStartup + Math.Max(0.2f, _settings.TickIntervalSeconds.Value);

        if (DateTime.UtcNow - _startedAtUtc >= TimeSpan.FromMinutes(_options.MaxRunMinutes))
        {
            Complete("已达到配置的运行时间上限。");
            return;
        }

        string activeScene = SceneManager.GetActiveScene().name;
        if (!string.Equals(activeScene, _scene, StringComparison.Ordinal))
        {
            _scene = activeScene;
            _defensePrepared = string.Equals(activeScene, "NewGameScene", StringComparison.OrdinalIgnoreCase) &&
                               !_openingDefenseRequired;
            _speedConfigured = false;
            _pendingSublevel = false;
            _wasInWave = false;
            _wishReturnClicked = false;
            _frontEndReadinessObserved = false;
            _pendingActionKey = string.Empty;
            _gameOverDetectedAt = -1f;
            AddTimeline("scene", "已进入场景 " + activeScene + "。");
            MarkProgress();
        }

        if (SaveIsolationPatch.VerificationFailed)
        {
            Fault(SaveIsolationPatch.VerificationError);
            return;
        }

        if (!SaveIsolationPatch.Verified)
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
                PluginVersion = PluginInfo.Version,
                RunState = _runState,
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
                Timeline = _timeline.ToArray()
            };
        }
    }

    public BridgeHello Hello()
    {
        return new BridgeHello
        {
            ProtocolVersion = Protocol.CurrentVersion,
            GameProcessId = GetCurrentProcessId(),
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
            ArtifactRoot = _activation.ArtifactRoot
        };
    }

    private void TickFrontEnd(string activeScene)
    {
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

        Execute(action);
    }

    private void TickInGame()
    {
        JObject initialization = _bridge.Invoke("queryState");
        switch (RuntimeResultInspector.Classify(initialization))
        {
            case RuntimeResultDisposition.Unsafe:
                Fault("命令 queryState 报告状态已被污染，需要启动新的游戏进程：" + Message(initialization));
                return;
            case RuntimeResultDisposition.Pending:
                SetStage(AutomationStage.InitializingRun, Message(initialization));
                return;
            case RuntimeResultDisposition.Failure:
                RegisterFailure("命令 queryState 失败：" + Message(initialization));
                return;
        }

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

        if (_options.MaxWaves > 0 && _wavesCompleted >= _options.MaxWaves)
        {
            Complete("已达到配置的波次上限。");
            return;
        }

        JArray blockers = state["blockers"] as JArray ?? new JArray();
        bool blocked = blockers.Count > 0;
        bool canSelectNextNode = state.SelectToken("map.canSelectNextNode")?.Value<bool>() == true;
        JObject? reward = HasBlocker(blockers, "reward") ? _bridge.Invoke("queryReward") : null;
        JObject? events = HasBlocker(blockers, "EventUI") || HasBlocker(blockers, "RepairUI")
            ? _bridge.Invoke("queryEventOptions")
            : null;

        if (!inWave && !blocked && !_defensePrepared && !_pendingSublevel && !canSelectNextNode)
        {
            bool prepared = Execute(new AutomationAction(
                "prepareDefaultDefense",
                JObject.FromObject(new { includeDebug = false }),
                AutomationStage.PreparingDefense,
                "正在通过等同玩家操作的接口准备默认闭合轨道和初始载具。"));
            if (prepared && _runState == AutoPlayerRunState.Running)
            {
                _defensePrepared = true;
                _openingDefenseRequired = false;
            }
            return;
        }

        if (!inWave && !blocked && _defensePrepared && !_speedConfigured)
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
            Complete(action.Reason);
            return;
        }

        Execute(action);
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

    private bool Execute(AutomationAction action)
    {
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
        JObject result = _bridge.Invoke(action.Command, action.Arguments);
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
                MarkProgress();
                AddTimeline("pending", action.Reason + " " + _lastMessage);
            }

            SetStage(action.Stage, _lastMessage);
            return false;
        }

        if (disposition == RuntimeResultDisposition.Failure)
        {
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
            RegisterFailure("命令 selectMapNode 返回成功，但未提交已选择或待处理的子关卡节点。");
            return false;
        }

        _consecutiveFailures = 0;
        MarkProgress();
        AddTimeline("action", action.Reason + " " + _lastMessage);
        if (string.Equals(action.Command, "submitCommonMode", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(action.Command, "submitRandomMode", StringComparison.OrdinalIgnoreCase))
        {
            _openingDefenseRequired = true;
        }
        else if (string.Equals(action.Command, "continueGame", StringComparison.OrdinalIgnoreCase))
        {
            _openingDefenseRequired = false;
        }

        if (string.Equals(action.Command, "selectMapNode", StringComparison.OrdinalIgnoreCase) &&
            result.SelectToken("data.state.pendingSubLevelNode") is JToken pendingNode &&
            pendingNode.Type != JTokenType.Null)
        {
            _pendingSublevel = true;
        }

        return true;
    }

    private static bool IsFrontEndMutation(string command) =>
        !string.Equals(command, "wait", StringComparison.OrdinalIgnoreCase);

    private void ObserveWaveTransition(bool inWave)
    {
        if (inWave && !_wasInWave)
        {
            _wasInWave = true;
            _wavesStarted++;
            AddTimeline("wave-start", "已观察到第 " + _wavesStarted + " 个波次开始。");
            MarkProgress();
        }
        else if (!inWave && _wasInWave)
        {
            _wasInWave = false;
            _wavesCompleted++;
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
            AddTimeline("fault", reason);
            _evidence.CaptureFailure(EnsureEvidenceDirectory(), reason, Snapshot());
        }
    }

    private void Complete(string reason)
    {
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
            bool changed = _stage != stage || !string.Equals(_stageDetail, detail, StringComparison.Ordinal);
            _stage = stage;
            _stageDetail = detail;
            if (changed) _log.LogDebug(StageDisplayName(stage) + "：" + detail);
        }
    }

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
        if (!SaveIsolationPatch.Installed)
            return "无法安装存档隔离挂钩。";
        if (!PlatformWriteIsolationPatch.Applied)
            return "外部平台写入隔离不完整。";
        if (!GameArtifactIsolationPatch.Applied)
            return "游戏诊断产物隔离不完整。";
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
        options.SpeedState = Math.Max(0, Math.Min(2, options.SpeedState));
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
