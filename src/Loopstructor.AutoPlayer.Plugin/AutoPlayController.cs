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
            ? "Activated and waiting for a start command."
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
                _lastMessage = "The game process must be restarted after the previous automation fault.";
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
                message = "Automation is already running.";
                return false;
            }

            _options = Normalize(options ?? new AutomationRunOptions());
            _runState = AutoPlayerRunState.Running;
            _stage = AutomationStage.WaitingForGame;
            _stageDetail = "Waiting for verified save isolation and a supported scene.";
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
            AddTimeline("start", $"Automation started in {_options.Mode} mode.");
            message = "Automation started.";
            return true;
        }
    }

    public bool Pause(out string message)
    {
        lock (_sync)
        {
            if (_runState != AutoPlayerRunState.Running)
            {
                message = "Automation is not running.";
                return false;
            }

            _runState = AutoPlayerRunState.Paused;
            _stageDetail = "Automation commands are paused; the game itself is not paused.";
            AddTimeline("pause", _stageDetail);
            message = "Automation paused.";
            return true;
        }
    }

    public bool Resume(out string message)
    {
        lock (_sync)
        {
            if (_runState != AutoPlayerRunState.Paused)
            {
                message = "Automation is not paused.";
                return false;
            }

            _runState = AutoPlayerRunState.Running;
            _stageDetail = "Automation resumed.";
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
                message = "Automation is already stopped.";
                return false;
            }

            _runState = AutoPlayerRunState.Standby;
            _stage = AutomationStage.WaitingForGame;
            _stageDetail = "Stopped; no further game commands will be issued.";
            AddTimeline("stop", _stageDetail);
            _evidence.WriteStatus(EnsureEvidenceDirectory(), Snapshot());
            message = "Automation stopped.";
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
            Complete("The configured run time limit was reached.");
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
            AddTimeline("scene", "Entered scene " + activeScene + ".");
            MarkProgress();
        }

        if (SaveIsolationPatch.VerificationFailed)
        {
            Fault(SaveIsolationPatch.VerificationError);
            return;
        }

        if (!SaveIsolationPatch.Verified)
        {
            SetStage(AutomationStage.WaitingForGame, "Waiting for SaveManager to confirm the isolated QA profile.");
            if (Time.realtimeSinceStartup - _lastProgressAt >= SaveVerificationTimeoutSeconds)
            {
                Fault("Save isolation was not verified; no game command was issued.");
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
                SetStage(AutomationStage.WaitingForGame, "Waiting for a supported scene; current scene is " + activeScene + ".");
            }

            CheckForStall();
        }
        catch (Exception exception)
        {
            RegisterFailure("Controller exception: " + exception.Message);
            _log.LogError(exception);
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
                Fault(query + " reported a polluted state that requires a fresh process: " + RuntimeResultInspector.Message(result));
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
                SetStage(action.Stage, readinessMessage + " No front-end command has been issued.");
                return;
            }

            if (!_frontEndReadinessObserved)
            {
                _frontEndReadinessObserved = true;
                SetStage(action.Stage, "Front-end readiness was observed; waiting for one stable polling interval.");
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
                Fault("queryState reported a polluted state that requires a fresh process: " + Message(initialization));
                return;
            case RuntimeResultDisposition.Pending:
                SetStage(AutomationStage.InitializingRun, Message(initialization));
                return;
            case RuntimeResultDisposition.Failure:
                RegisterFailure("queryState: " + Message(initialization));
                return;
        }

        JObject affordances = _bridge.Invoke("queryAffordances");
        switch (RuntimeResultInspector.Classify(affordances))
        {
            case RuntimeResultDisposition.Unsafe:
                Fault("queryAffordances reported a polluted state that requires a fresh process: " + Message(affordances));
                return;
            case RuntimeResultDisposition.Pending:
                SetStage(AutomationStage.InitializingRun, Message(affordances));
                return;
            case RuntimeResultDisposition.Failure:
                RegisterFailure("queryAffordances: " + Message(affordances));
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
            Complete("The configured wave limit was reached.");
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
                "Prepare the default closed rail and initial vehicle through player-equivalent APIs."));
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
                "Set the configured in-game speed."));
            if (configured && _runState == AutoPlayerRunState.Running) _speedConfigured = true;
            return;
        }

        if (_pendingSublevel && !blocked && !inWave)
        {
            bool selected = Execute(new AutomationAction(
                "selectSublevel",
                JObject.FromObject(new { index = 0 }),
                AutomationStage.SelectingRoute,
                "Select the first available sublevel."));
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
            SetStage(AutomationStage.Completed, "Game-over observed; verifying the settlement UI.");
            AddTimeline("settlement", _stageDetail);
            MarkProgress();
        }

        JObject interactables = _bridge.Invoke("queryUiInteractables");
        switch (RuntimeResultInspector.Classify(interactables))
        {
            case RuntimeResultDisposition.Unsafe:
                Fault("queryUiInteractables reported a polluted state that requires a fresh process: " + Message(interactables));
                return;
            case RuntimeResultDisposition.Pending:
                SetStage(AutomationStage.Completed, Message(interactables));
                return;
            case RuntimeResultDisposition.Failure:
                RegisterFailure("queryUiInteractables: " + Message(interactables));
                return;
        }

        _consecutiveFailures = 0;
        if (RuntimeResultInspector.HasActiveSettlementInteractable(interactables))
        {
            Complete("Game-over confirmed by an active settlement-panel interaction.");
            return;
        }

        if (!_wishReturnClicked &&
            RuntimeResultInspector.TryGetWishPanelReturnInstanceId(interactables, out int returnButtonInstanceId))
        {
            bool clicked = Execute(new AutomationAction(
                "uiClick",
                JObject.FromObject(new { instanceId = returnButtonInstanceId }),
                AutomationStage.Completed,
                "Dismiss the wish-list prompt through its Return button."));
            if (clicked && _runState == AutoPlayerRunState.Running)
            {
                _wishReturnClicked = true;
                SetStage(AutomationStage.Completed, "Wish-list prompt dismissed; waiting for the settlement panel.");
            }

            return;
        }

        SetStage(
            AutomationStage.Completed,
            _wishReturnClicked
                ? "Waiting for an active settlement-panel interaction."
                : "Waiting for the wish-list prompt or settlement panel.");
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
            SetStage(action.Stage, "Waiting for the previously issued " + action.Command + " action to change game state.");
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
            Fault(action.Command + " reported a polluted state that requires a fresh process: " + _lastMessage);
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
                SetStage(action.Stage, _lastMessage + " The clean opening state will be checked again.");
                return false;
            }

            RegisterFailure(action.Command + ": " + _lastMessage);
            return false;
        }

        if (string.Equals(action.Command, "selectMapNode", StringComparison.OrdinalIgnoreCase) &&
            !RuntimeResultInspector.HasCommittedMapNode(result))
        {
            RegisterFailure("selectMapNode returned success without committing a selected or pending sublevel node.");
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
            AddTimeline("wave-start", "Observed wave start " + _wavesStarted + ".");
            MarkProgress();
        }
        else if (!inWave && _wasInWave)
        {
            _wasInWave = false;
            _wavesCompleted++;
            AddTimeline("wave-complete", "Observed wave completion " + _wavesCompleted + ".");
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
            Fault("Consecutive command failures reached the configured limit: " + message);
        }
    }

    private void CheckForStall()
    {
        if (_runState != AutoPlayerRunState.Running) return;
        float configured = Math.Max(15f, _settings.StallTimeoutSeconds.Value);
        float timeout = _stage == AutomationStage.Battle ? Math.Max(300f, configured) : configured;
        if (Time.realtimeSinceStartup - _lastProgressAt >= timeout)
        {
            Fault("No verifiable automation progress was observed: " + _stageDetail);
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
            if (changed) _log.LogDebug(stage + ": " + detail);
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
            return "The selected process is not Loopstructor 2: Skyspine by PoneGames.";
        if (!_fingerprint.MatchesExpectedAssembly(_activation.ExpectedAssemblySha256))
            return "Assembly-CSharp.dll changed after validation; update or reinstall the automation adapter.";
        if (!_bridge.IsAvailable)
            return "The game build is missing required automation runtime members: " + string.Join(", ", _bridge.MissingMembers);
        if (!SaveIsolationPatch.Installed)
            return "Save isolation hooks could not be installed.";
        if (!PlatformWriteIsolationPatch.Applied)
            return "External platform write isolation is incomplete.";
        if (!GameArtifactIsolationPatch.Applied)
            return "Game diagnostic artifact isolation is incomplete.";
        return string.Empty;
    }

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
