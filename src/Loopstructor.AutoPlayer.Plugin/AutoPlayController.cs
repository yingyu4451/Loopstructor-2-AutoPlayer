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
        ProbeDisposableGrid,
        ConfirmDisposable,
        WaitForDisposableSettlement,
        CancelDisposable,
        VerifyDisposableCancellation,
        QueryRail,
        RunSpecialStationMaintenance,
        Complete
    }

    private enum DefenseMaintenanceStep
    {
        QueryTrain,
        QueryVehicle,
        RunMerge,
        ObserveMergeSettlement,
        ConfirmMergeSettlement,
        CloseMergePanel,
        ReconcileMerge,
        QueryCatapults,
        QueryRailExpansionCandidates,
        PreviewRailInsertionCandidate,
        SelectRailInsertion,
        PreviewBattleSpecialRebuildCandidate,
        SelectBattleSpecialRebuild,
        ProbeSpecialStationMoveGrid,
        QueryFreshMovableStation,
        QueryFreshMovableStationState,
        StartSpecialStationMove,
        VerifySpecialStationMoveStarted,
        ValidateSpecialStationMoveGrid,
        ConfirmSpecialStationMove,
        VerifySpecialStationMoved,
        VerifySpecialStationMoveResult,
        CancelSpecialStationMove,
        VerifySpecialStationMoveCancelled,
        VerifySpecialStationMoveRollbackRail,
        VerifySpecialStationMoveRollbackResult,
        DisconnectRailForRebuild,
        VerifyRailRebuildDisconnected,
        PreviewRailRebuild,
        DrawRailRebuild,
        VerifyRailRebuild,
        VerifyRailRebuildVehicles,
        RecoverRailRebuild,
        VerifyRailRebuildRecovery,
        InsertRailPoint,
        VerifyRailInsertion,
        QueryExpansionAttributeDisposable,
        ProbeExpansionAttributeGrid,
        UseExpansionAttributeDisposable,
        ConfirmExpansionAttributeDisposable,
        WaitForExpansionAttributeSettlement,
        VerifyExpansionAttribute,
        QueryExpansionAttributeCleanup,
        CancelExpansionAttributeDisposable,
        VerifyExpansionAttributeCleanup,
        PreviewExpansion,
        QueryExpansionRailBaseline,
        DrawExpansion,
        VerifyExpansionRail,
        VerifyExpansion,
        PlaceExpansionVehicle,
        MoveVehicle
    }

    private enum OwnedPreviewReleaseOperation
    {
        None,
        Pause,
        Stop,
        Fault
    }

    private enum OwnedPreviewReleaseStep
    {
        QueryOwnership,
        CancelOwnedPreview,
        VerifyReleased
    }

    private const int MaxTimelineEvents = 500;
    private const float SaveVerificationTimeoutSeconds = 30f;
    private const float OutcomeVerificationTimeoutSeconds = 10f;
    private const float MinimumBattlePollIntervalSeconds = 2f;
    private const float BattleTacticFrameDelaySeconds = 0.25f;
    private const float BattleTacticRetryDelaySeconds = 1f;
    private const float BattleTacticCycleIntervalSeconds = 12f;
    private const float FreshMovableStationRetryDelaySeconds = 1f;
    private const int MaxFreshMovableStationRetryAttempts = 2;
    private const float MoveGridInitializationRetryDelaySeconds = 1f;
    private const int MaxMoveGridInitializationRetryAttempts = 2;
    private const float MinimumFullWaveQueryIntervalSeconds = 1.25f;
    private const float MaximumFullWaveQueryIntervalSeconds = 3f;
    private const double FullWaveQueryTimeBudgetRatio = 0.05;
    private const float MapSelectionTransitionTimeoutSeconds = 12f;
    private const float NormalEventAppearanceGraceSeconds = 1f;
    private const float EventOptionGenerationDelaySeconds = 3.25f;
    private const float RepairPanelAnimationSeconds = 1.5f;
    private const float SelectionPreviewObservationSeconds = 1f;
    private const float RewardCollectionObservationSeconds = 0.75f;
    private const float MapOpenAnimationFallbackSeconds = 1.55f;
    private const float MapOpenAnimationPollSeconds = 0.1f;
    private const float RewardObjectAppearanceGraceSeconds = 1.25f;
    private const float RewardObjectAppearancePollSeconds = 0.1f;
    private const float MergeSettlementObservationSeconds = 0.75f;
    private const float MergeSettlementAppearanceTimeoutSeconds = 10f;
    private const float MergePassTimeoutSeconds = 30f;
    private const float RewardVehicleContextFrameDelaySeconds = 0.02f;
    private const float RewardSelectionSettlementPollSeconds = 0.5f;
    private const float RewardSelectionSettlementTimeoutSeconds = 20f;
    private const float MergeMutationSettlementTimeoutSeconds = 20f;
    private const float WaveFunctionOptionSettlementTimeoutSeconds = 20f;
    private const int MaxOpeningDefenseConfirmGuardFailures = 12;
    private const float MapProgressProbeIntervalSeconds = 1f;
    private const float WaveStartObservationTimeoutSeconds = 5f;
    private const int MaxDefenseExpansionVerificationAttempts = 12;
    private const int MaxDisposableSettlementObservationAttempts = 80;
    private const int MaxOwnedPreviewReleaseVerificationAttempts = 12;
    private const int MaxWaveStartAttempts = 3;
    private const int MaxMergePassesPerMaintenance = 8;
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
    private readonly RailExpansionPlanner _railExpansionPlanner = new();
    private readonly RailRebuildTransactionPlanner _railRebuildPlanner = new();
    private readonly MergeAutomationPlanner _mergeAutomationPlanner = new();
    private readonly MergeMutationSettlementGuard _mergeMutationSettlementGuard = new();
    private readonly RewardObjectSettlementGuard _rewardObjectSettlementGuard = new();
    private readonly HashSet<int> _rewardObjectCollectionLedger = new();
    private readonly RewardSelectionSettlementGuard _rewardSelectionSettlementGuard = new();
    private readonly WaveFunctionOptionSettlementGuard _waveFunctionOptionSettlementGuard = new();
    private readonly FrameTimingSampler _frameTimingSampler = new();
    private readonly NormalEventUiRuntimeReader _normalEventUiReader = new();
    private readonly OpeningDefenseInteractionGuard _openingDefenseInteractionGuard = new();
    private readonly PendingDisposableMutationGuard _openingPendingDisposableMutationGuard = new();
    private readonly PendingDisposableMutationGuard _defensePendingDisposableMutationGuard = new();
    private readonly PendingDefenseMutationGuard _defenseStructuralMutationGuard = new();
    private readonly OpeningDefensePreparationPlanner _openingDefensePreparationPlanner =
        new(new IncrementalAttributePlacementGridProbe());
    private readonly IncrementalBattleLiveDisposableGridProbe _battleLiveDisposableGridProbe = new();
    private readonly IncrementalDefenseExpansionAttributeGridProbe _defenseExpansionAttributeGridProbe = new();
    private readonly IncrementalDefenseStationGridProbe _defenseStationGridProbe = new();
    private readonly List<TimelineEvent> _timeline = new();
    private readonly SceneTransitionGate _frontEndTransitionGate = new();
    private readonly NativeSelectionHighlighter _selectionHighlighter = new();

    private AutomationRunOptions _options = new();
    private AutoPlayerRunState _runState;
    private AutomationStage _stage = AutomationStage.WaitingForGame;
    private string _stageDetail = string.Empty;
    private string _lastCommand = string.Empty;
    private string _lastMessage = string.Empty;
    private JObject? _lastRuntimeResult;
    private string _scene = string.Empty;
    private int _sceneHandle = int.MinValue;
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
    private float _eventOptionSelectionReadyAt = -1f;
    private string _eventOptionsFingerprint = string.Empty;
    private string _pendingEventPanel = string.Empty;
    private bool _normalEventProbeRequired;
    private bool _normalEventObserved;
    private float _normalEventActionReadyAt = -1f;
    private string _normalEventFingerprint = string.Empty;
    private int _normalEventProbeFailures;
    private AutomationAction? _pendingMapAction;
    private string _selectionHighlightOwner = string.Empty;
    private string _selectionHighlightFingerprint = string.Empty;
    private string _selectionPreviewFingerprint = string.Empty;
    private float _selectionPreviewReadyAt = -1f;
    private bool _mapPreviewOpenPending;
    private float _mapPreviewOpenRequestedAt = -1f;
    private bool _mapPreviewOpenAnimationObserved;
    private AutomationAction? _deferredFrontEndAction;
    private AutomationAction? _deferredNormalEventAction;
    private bool _deferredNormalEventChoosingOption;
    private AutomationAction? _deferredRewardAction;
    private AutomationAction? _deferredSettlementAction;
    private float _rewardObjectsReadyAt = -1f;
    private float _rewardObjectsAppearanceReadyAt = -1f;
    private string _rewardObjectsFingerprint = string.Empty;
    private float _rewardOptionsReadyAt = -1f;
    private string _rewardOptionsFingerprint = string.Empty;
    private string _rewardVehicleContextFingerprint = string.Empty;
    private bool _rewardVehicleContextAttempted;
    private bool _rewardVehicleContextFailed;
    private JObject? _rewardVehicleContextResult;
    private bool _wasInWave;
    private bool _wishReturnClicked;
    private bool _needsProcessRestart;
    private bool _cheatAvailable;
    private bool _cheatModeEnabled;
    private bool _cheatUsed;
    private bool _enemyIdsVisible;
    private bool _enemyBuffsVisible;
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
    private float _nextFullWaveQueryAt;
    private float _adaptiveFullWaveQueryInterval = MinimumFullWaveQueryIntervalSeconds;
    private JObject? _cachedFullWaveQueryResult;
    private bool _freshFullWaveQueryIssued;
    private JObject? _pendingMapDecisionState;
    private JObject? _pendingOpeningVehicleState;
    private bool _battleDisposableUsedThisWave;
    private bool _battleDisposableUnavailableThisWave;
    private readonly HashSet<int> _battleTrainIdentitiesMovedThisWave = new();
    private string _ownedDisposableEnum = string.Empty;
    private int _ownedDisposableInteractionInstanceId;
    private JObject? _battleWaveSnapshot;
    private AutomationAction? _battlePendingAction;
    private JObject? _battleThreats;
    private JObject? _battleDisposable;
    private JObject? _battleRail;
    private JObject? _battleTrain;
    private JObject? _battleConfirmationArguments;
    private int _battleDisposableSettlementObservationAttempts;
    private bool _battleWaveEndPendingPreviewRelease;
    private bool _defenseMaintenanceRequested;
    private bool _defenseMaintenanceReady;
    private DefenseMaintenanceStep _defenseMaintenanceStep;
    private JObject? _defenseTrain;
    private JObject? _defenseVehicle;
    private MergeAutomationState _mergeAutomationState = MergeAutomationState.Initial;
    private JObject? _mergeAutomationQueryResult;
    private bool _mergeExhausted;
    private int _mergePassCount;
    private float _mergeSettlementWaitStartedAt = -1f;
    private float _mergeSettlementObservedAt = -1f;
    private int _mergeSettlementQueryFailures;
    private float _mergePassStartedAt = -1f;
    private string _mergeRecoveryReason = string.Empty;
    private int _mergeRecoveryAttempts;
    private AutomationAction? _defensePendingAction;
    private AutomationAction? _defenseExpansionAction;
    private JObject? _defenseExpansionDrawResult;
    private JObject? _defenseRailBaselineResult;
    private JObject? _defenseVerifiedRailResult;
    private int _defenseExpectedRailInstanceId;
    private int _defenseRailVerificationAttempts;
    private int _defenseTrainCountBeforeExpansion;
    private int _defenseExpansionVerificationAttempts;
    private readonly HashSet<string> _rejectedDefenseExpansionPaths = new(StringComparer.Ordinal);
    private bool _defenseExpansionSuspended;
    private int _openingDefenseInteractionInstanceId;
    private bool _openingDefenseWaitingForForeignPreview;
    private int _openingDefenseConfirmGuardFailures;
    private bool _openingPendingDisposableQueryCatapults;
    private JObject? _openingPendingDisposableObservation;
    private JObject? _defenseCatapults;
    private AutomationAction? _defenseAttributeUseAction;
    private AutomationAction? _defenseAttributeConfirmAction;
    private JObject? _defenseAttributeGrid;
    private int _defenseAttributeInteractionInstanceId;
    private int _defenseAttributeCountBeforePlacement;
    private int _defenseAttributeVerificationAttempts;
    private int _defenseAttributeSettlementObservationAttempts;
    private int _defenseAttributeCleanupVerificationAttempts;
    private string _defenseAttributeFailureDetail = string.Empty;
    private bool _defensePendingDisposableQueryCatapults;
    private JObject? _defensePendingDisposableObservation;
    private bool _defenseNeedsNewLoopExpansion;
    private string _defensePlacementDisposableEnum = "FreePoint_Attribute";
    private int _defensePlacementCountBefore;
    private JObject? _defenseRailExpansionBaseline;
    private IReadOnlyList<RailInsertionCandidate> _defenseRailInsertionCandidates =
        Array.Empty<RailInsertionCandidate>();
    private readonly List<RailInsertionPreviewScore> _defenseRailInsertionScores = new();
    private int _defenseRailInsertionPreviewIndex;
    private RailInsertionPreviewScore? _defenseSelectedRailInsertion;
    private RailStationMoveCandidate? _defenseSpecialMoveCandidate;
    private JObject? _defenseSpecialMoveGrid;
    private int _defenseSpecialMoveInteractionInstanceId;
    private double _defenseSpecialMovePredictedCycleSeconds;
    private bool _defenseSpecialMoveCancelRequested;
    private bool _defenseSpecialMoveConfirmationAccepted;
    private bool _defenseBattleSpecialMoveOnly;
    private RailRebuildSnapshot? _defenseRailRebuildSnapshot;
    private bool _defenseRailRebuildRecoveryAttempted;
    private bool _defenseRailRebuildExplicitPollution;
    private double _defenseRailRebuildPreviewCycleSeconds;
    private IReadOnlyList<RailRebuildSnapshot> _defenseRailRebuildCandidates = Array.Empty<RailRebuildSnapshot>();
    private readonly List<(RailRebuildSnapshot Snapshot, double Cycle)> _defenseRailRebuildScores = new();
    private int _defenseRailRebuildCandidateIndex;
    private readonly HashSet<string> _defenseRailMaintenanceActionFingerprints =
        new(StringComparer.Ordinal);
    private string _defenseRailMaintenanceLayoutFingerprint = string.Empty;
    private string _defenseRailMaintenanceStableLayoutFingerprint = string.Empty;
    private int _defenseFreshMovableStationRetryAttempts;
    private int _defenseMoveGridInitializationRetryAttempts;
    private int _defenseStructuralVerificationAttempts;
    private OwnedPreviewReleaseOperation _ownedPreviewReleaseOperation;
    private OwnedPreviewReleaseStep _ownedPreviewReleaseStep;
    private AutomationAction? _ownedPreviewReleaseCancelAction;
    private string _ownedPreviewReleaseFaultReason = string.Empty;
    private string _ownedPreviewReleaseCancelFailure = string.Empty;
    private int _ownedPreviewReleaseQueryFailureAttempts;
    private int _ownedPreviewReleaseVerificationAttempts;
    private bool _ownedPreviewCancellationAlreadyIssued;
    private bool _ownedPreviewReleaseCancellationOutcomeUncertain;
    private bool _ownedPreviewConfirmationOutcomeUncertain;
    private string _pendingActionKey = string.Empty;
    private bool _waveStartPending;
    private float _waveStartPendingAt = -1f;
    private int _waveStartAttemptCount;
    private string _pendingWaveFunctionFlowDescription = string.Empty;
    private int _currentMapStage = -1;
    private int _currentMapLayer = -1;
    private float _nextMapProgressProbeAt;
    private bool _openingDefensePreparationActive;
    private bool _deferOpeningDefenseCommandOnce;
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
        _normalEventUiReader.Initialize();
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
            if (_ownedPreviewReleaseOperation != OwnedPreviewReleaseOperation.None)
            {
                message = "正在确认并清理由自动游玩创建的道具预览；清理完成前不能开始新的自动游玩。";
                return false;
            }

            if (_defensePendingDisposableMutationGuard.IsArmed)
            {
                message =
                    "上一条动力弹射点确认仍在只读对账中；结果确定前不能开始新的自动游玩，且不会重发该写命令。";
                return false;
            }

            if (_defenseStructuralMutationGuard.IsArmed)
            {
                message =
                    "上一条轨道或弹射点结构写命令仍在只读对账中；结果确定前不能开始新的自动游玩。";
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

            if (_baseGodModeEnabled || MapSkipPatch.Enabled)
            {
                message = "开始自动游玩前必须先关闭会持续改变战局的作弊功能：基地无敌和地图节点自由跳转均需处于关闭状态。敌人 ID 与 Buff 监视可以继续保留。";
                return false;
            }

            if (HasOwnedAutomationPreviewIdentity())
            {
                BeginOwnedPreviewRelease(
                    OwnedPreviewReleaseOperation.Fault,
                    "上一轮仍保留自动游玩道具预览身份；开始新一轮前必须先重新确认并清理。",
                    out message);
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
            ClearDeferredReadDecisions();
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
            ResumeOrResetOpeningDefensePreparation();
            _speedConfigured = !_options.OverrideGameSpeed;
            _pendingSublevel = false;
            _mapSelectionPending = false;
            _mapSelectionPendingAt = -1f;
            ResetEventOptionObservation();
            ResetNormalEventObservation();
            _normalEventProbeRequired = true;
            ResetRewardOptionObservation();
            _pendingMapAction = null;
            _wasInWave = false;
            _battleTrainIdentitiesMovedThisWave.Clear();
            _wishReturnClicked = false;
            _frontEndReadinessObserved = false;
            _gameModeVerified = false;
            _runtimeInitialized = false;
            ResetBattleTactics();
            ResetFullWaveQueryPolling();
            RequestDefenseMaintenance();
            ResetOwnedPreviewReleaseState();
            _pendingActionKey = string.Empty;
            ResetWaveStartObservation();
            _currentMapStage = -1;
            _currentMapLayer = -1;
            _nextMapProgressProbeAt = 0f;
            _bridge.ResetMetrics();
            _lastRuntimeResult = null;
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
            _rejectedDefenseExpansionPaths.Clear();
            _defenseExpansionSuspended = false;
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
                if (_ownedPreviewReleaseOperation != OwnedPreviewReleaseOperation.None)
                {
                    message = "正在确认并清理由自动游玩创建的道具预览；清理完成前不能启用作弊模式。";
                    return false;
                }

                if (!_cheatAvailable)
                {
                    message = string.IsNullOrWhiteSpace(_cheatAvailabilityReason)
                        ? "当前游戏构建不支持作弊工具。"
                        : _cheatAvailabilityReason;
                    return false;
                }

                if (_needsProcessRestart)
                {
                    message = "当前游戏进程已要求重启，不能再进入作弊模式。";
                    return false;
                }

                if (HasOwnedAutomationPreviewIdentity())
                {
                    if (_runState is AutoPlayerRunState.Running or AutoPlayerRunState.Paused)
                    {
                        message =
                            "自动游玩正在结算道具预览；为避免取消本轮操作，请等待当前预览完成后再次启用怪物监视。";
                        return false;
                    }

                    BeginOwnedPreviewRelease(
                        OwnedPreviewReleaseOperation.Fault,
                        "上一轮仍保留自动游玩道具预览身份；启用作弊模式前必须先重新确认并清理。",
                        out message);
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

    public bool IsAutoPlayActive
    {
        get
        {
            lock (_sync)
            {
                return _runState is AutoPlayerRunState.Running or AutoPlayerRunState.Paused;
            }
        }
    }

    public void RecordFrame(double frameSeconds) => _frameTimingSampler.Record(frameSeconds);

    public void SetEnemyBuffsVisible(bool visible)
    {
        lock (_sync)
        {
            _enemyBuffsVisible = visible;
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

            if (_defensePendingDisposableMutationGuard.IsArmed)
            {
                message =
                    "动力弹射点确认结果仍在只读对账中；为避免丢失写入账本，暂时不能暂停，也不会重发确认命令。";
                return false;
            }


            if (_defenseStructuralMutationGuard.IsArmed)
            {
                message = "轨道或弹射点结构写入仍在只读对账中；暂时不能暂停，且不会重发写命令。";
                return false;
            }

            if (BeginOwnedPreviewRelease(
                    OwnedPreviewReleaseOperation.Pause,
                    string.Empty,
                    out message))
            {
                return false;
            }

            ApplyPause();
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
            _normalEventProbeRequired = true;
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

            if (_defensePendingDisposableMutationGuard.IsArmed)
            {
                message =
                    "动力弹射点确认结果仍在只读对账中；为避免丢失写入账本，暂时不能停止，也不会重发确认命令。";
                return false;
            }


            if (_defenseStructuralMutationGuard.IsArmed)
            {
                message = "轨道或弹射点结构写入仍在只读对账中；暂时不能停止，且不会重发写命令。";
                return false;
            }

            if (BeginOwnedPreviewRelease(
                    OwnedPreviewReleaseOperation.Stop,
                    string.Empty,
                    out message))
            {
                return false;
            }

            ApplyStop();
            message = "自动游玩已停止。";
            return true;
        }
    }

    public void Tick()
    {
        _freshFullWaveQueryIssued = false;
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

        Scene activeSceneInfo = SceneManager.GetActiveScene();
        ObserveActiveScene(activeSceneInfo);
        if (_ownedPreviewReleaseOperation != OwnedPreviewReleaseOperation.None)
        {
            if (Time.realtimeSinceStartup >= _nextTickAt) ProcessOwnedPreviewRelease();
            return;
        }
        if (_runState != AutoPlayerRunState.Running || Time.realtimeSinceStartup < _nextTickAt) return;
        float configuredInterval = Math.Max(0.2f, _settings.TickIntervalSeconds.Value);
        float tickInterval = _wasInWave
            ? Math.Max(MinimumBattlePollIntervalSeconds, configuredInterval)
            : configuredInterval;
        _nextTickAt = Time.realtimeSinceStartup + tickInterval;

        string activeScene = activeSceneInfo.name;
        if (_defensePendingDisposableMutationGuard.IsArmed)
        {
            _defenseMaintenanceRequested = true;
            _defenseMaintenanceReady = true;
            _defenseMaintenanceStep = DefenseMaintenanceStep.WaitForExpansionAttributeSettlement;
            if (!string.Equals(activeScene, "NewGameScene", StringComparison.OrdinalIgnoreCase))
            {
                SetStage(
                    AutomationStage.Recovery,
                    "动力弹射点确认结果仍未知；正在等待游戏内场景恢复后继续只读对账，不会重发写命令。");
                return;
            }

            try
            {
                HandlePendingDefenseDisposableMutation();
                CheckForStall();
            }
            catch (Exception exception)
            {
                RegisterFailure("动力弹射点写入只读对账发生异常：" + exception.Message);
                _log.LogError("动力弹射点写入只读对账发生未处理异常：" + exception);
            }

            return;
        }

        if (_defenseStructuralMutationGuard.IsArmed)
        {
            _defenseMaintenanceRequested = true;
            _defenseMaintenanceReady = true;
            if (!string.Equals(activeScene, "NewGameScene", StringComparison.OrdinalIgnoreCase))
            {
                SetStage(
                    AutomationStage.Recovery,
                    "轨道或弹射点结构写入仍在对账；正在等待游戏内场景恢复，不会重发写命令。");
                return;
            }

            try
            {
                TryMaintainDefense();
                CheckForStall();
            }
            catch (Exception exception)
            {
                RegisterFailure("结构写入只读对账发生异常：" + exception.Message);
                _log.LogError("结构写入只读对账发生未处理异常：" + exception);
            }

            return;
        }

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

        if (!_activation.IsPlayerMode && SaveIsolationPatch.VerificationFailed)
        {
            FaultRequiringProcessRestart(SaveIsolationPatch.VerificationError);
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
        FrameTimingSnapshot frameTiming = _frameTimingSampler.Snapshot();
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
                CurrentMapStage = _currentMapStage,
                CurrentMapLayer = _currentMapLayer,
                LastRuntimeCommand = _bridge.LastCommand,
                LastRuntimeCommandDurationMs = _bridge.LastCommandDurationMs,
                MaxRuntimeCommand = _bridge.MaxCommand,
                MaxRuntimeCommandDurationMs = _bridge.MaxCommandDurationMs,
                SlowRuntimeCommandCount = _bridge.SlowCommandCount,
                CurrentFps = frameTiming.CurrentFps,
                OnePercentLowFps = frameTiming.OnePercentLowFps,
                FrameTimeP99Ms = frameTiming.FrameTimeP99Ms,
                FrameSampleCount = frameTiming.SampleCount,
                FrameTelemetryWindowSeconds = frameTiming.WindowSeconds,
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
                EnemyBuffsVisible = _enemyBuffsVisible,
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
                FaultRequiringProcessRestart("前端命令 " + _frontEndTransitionGate.Command +
                      " 已成功返回，但场景未在安全时限内切换；为避免重复提交，当前进程必须重启。");
                return;
            }

            SetStage(
                AutomationStage.FrontEnd,
                "已发送 " + _frontEndTransitionGate.Command + "，正在等待游戏完成场景切换。");
            return;
        }

        if (_deferredFrontEndAction != null)
        {
            AutomationAction deferred = _deferredFrontEndAction;
            _deferredFrontEndAction = null;
            ClearSelectionHighlight("front-end");
            bool deferredExecuted = Execute(deferred);
            if (deferredExecuted && IsSceneTransitionCommand(deferred.Command))
            {
                _frontEndTransitionGate.Begin(deferred.Command, activeScene, DateTime.UtcNow);
                SetStage(deferred.Stage, "已发送 " + deferred.Command + "，正在等待场景切换。");
            }

            return;
        }

        string query = string.Equals(activeScene, "RandomChooseScene", StringComparison.OrdinalIgnoreCase)
            ? "queryRandomMode"
            : "queryFrontend";
        JObject result = _bridge.Invoke(query);
        switch (RuntimeResultInspector.ClassifyReadOnly(result))
        {
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

        if (!IsFrontEndMutation(action.Command))
        {
            Execute(action);
            return;
        }

        if (TryWaitForFrontEndSelectionPreview(action))
        {
            return;
        }

        _deferredFrontEndAction = action;
        ScheduleContinuationFrame();
        SetStage(action.Stage, "已读取前端状态；下一帧再发送 " + action.Command + "，避免同帧叠加运行时命令。");
    }

    private void TickInGame()
    {
        if (!EnsureInGameRuntimeReady()) return;
        ObserveMapProgress();

        // Once opening-defense preparation starts, it owns the frame. This prevents the normal
        // queryWave/queryMap polling path from stacking another runtime command beside a planner
        // command in the same frame.
        if (_openingDefensePreparationActive && !_defensePrepared)
        {
            ContinueOpeningDefensePreparation();
            return;
        }

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

        if (_deferredRewardAction != null)
        {
            AutomationAction deferredReward = _deferredRewardAction;
            _deferredRewardAction = null;
            ClearSelectionHighlight("reward");
            Execute(deferredReward);
            return;
        }

        if (TryHandleNormalEventUi()) return;
        if (TryHandleObservedWave()) return;
        if (TryHandlePendingMapSelection()) return;
        if (_defensePrepared &&
            _defenseMaintenanceRequested &&
            _defenseMaintenanceReady &&
            TryMaintainDefense()) return;

        if (_bridge.TryGetWavePulse(out bool pulseInWave, out bool pulseGameOver, out int pulseRemaining) &&
            (pulseInWave || pulseGameOver))
        {
            CompleteWaveFunctionOptionSettlementFromWavePulse(pulseInWave, pulseGameOver);
            HandleWaveObservation(pulseInWave, pulseGameOver, pulseRemaining, null);
            return;
        }

        if (!TryQueryAdaptiveWaveState(
                "queryWave",
                AutomationStage.InitializingRun,
                out JObject waveResult,
                out JObject state))
        {
            return;
        }

        if (_freshFullWaveQueryIssued)
        {
            ScheduleContinuationFrame();
            SetStage(
                AutomationStage.InitializingRun,
                "已读取完整波次状态；下一帧再处理阻塞界面或发送操作，避免同帧叠加命令。");
            return;
        }

        JArray blockers = state["blockers"] as JArray ?? new JArray();
        if (HandleWaveFunctionOptionSettlementFromWaveState(state, blockers))
        {
            return;
        }
        bool gameOver = HasBlocker(blockers, "gameOver") ||
                        GameOutcomeObserver.Outcome is AutomationOutcome.Victory or AutomationOutcome.Defeat;
        bool inWave = state["isInWaving"]?.Value<bool>() == true;
        ObserveWaveTransition(inWave);

        if (gameOver)
        {
            TickSettlement();
            return;
        }

        if (inWave)
        {
            HandleWaveObservation(
                true,
                false,
                state.SelectToken("enemy.remaining")?.Value<int?>() ?? -1,
                waveResult);
            return;
        }

        if (_options.MaxWaves > 0 && _wavesCompleted >= _options.MaxWaves)
        {
            _outcome = AutomationOutcome.WaveLimit;
            Fault("已达到配置的波次上限，但尚未观察到游戏胜利。");
            return;
        }

        bool blocked = blockers.Count > 0;
        if (!blocked && _defensePrepared && _defenseMaintenanceRequested)
        {
            _defenseMaintenanceReady = true;
            _nextTickAt = Time.realtimeSinceStartup + BattleTacticFrameDelaySeconds;
            SetStage(AutomationStage.PreparingDefense, "防线当前可编辑，准备检查背包战车与车列容量。");
            return;
        }

        if (HasBlocker(blockers, "reward"))
        {
            if (!TryQueryState(
                    "queryReward",
                    AutomationStage.ManagingRewards,
                    out JObject rewardResult,
                    out _))
            {
                if (_rewardObjectSettlementGuard.IsArmed &&
                    RuntimeResultInspector.ClassifyReadOnly(rewardResult) == RuntimeResultDisposition.Pending)
                {
                    HandleRewardObjectSettlement(rewardResult);
                }
                if (_rewardSelectionSettlementGuard.IsArmed &&
                    RuntimeResultInspector.ClassifyReadOnly(rewardResult) == RuntimeResultDisposition.Pending)
                {
                    HandleRewardSelectionSettlement(rewardResult);
                }
                return;
            }

            if (HandleRewardObjectSettlement(rewardResult)) return;
            if (HandleRewardSelectionSettlement(rewardResult)) return;
            if (TryWaitForRewardOptions(rewardResult)) return;
            AutomationAction rewardAction = DecideObservedReward(rewardResult);
            if (string.Equals(rewardAction.Command, "wait", StringComparison.OrdinalIgnoreCase))
            {
                Execute(rewardAction);
                return;
            }

            _deferredRewardAction = rewardAction;
            ScheduleContinuationFrame();
            SetStage(
                AutomationStage.ManagingRewards,
                "已读取并锁定当前奖励身份；下一帧再执行领取或选择，避免同帧叠加运行时命令。");
            return;
        }

        CompleteRewardObjectSettlement("奖励阶段已经退出。");
        _rewardObjectCollectionLedger.Clear();
        CompleteRewardSelectionSettlement("\u5956\u52b1\u754c\u9762\u5df2\u7ecf\u9000\u51fa\u3002");
        ResetRewardOptionObservation();

        string? eventPanel = HasBlocker(blockers, "EventUI")
            ? "EventUI"
            : HasBlocker(blockers, "RepairUI")
                ? "RepairUI"
                : null;
        if (eventPanel != null)
        {
            BeginEventPanelObservation(eventPanel);
            return;
        }

        if (HasBlocker(blockers, "shop"))
        {
            Execute(new AutomationAction(
                "closeShop",
                null,
                AutomationStage.ManagingShop,
                "关闭商店，不消耗测试资源。"));
            return;
        }

        if (HasBlocker(blockers, "UI_PopPanel_Option"))
        {
            Execute(new AutomationAction(
                "submitPopOption",
                JObject.FromObject(new { action = "submit" }),
                AutomationStage.Recovery,
                "确认阻塞操作的选项对话框。"));
            return;
        }

        if (HasBlocker(blockers, "disposablePreview"))
        {
            if (TryHandleOpeningDefensePreviewBlocker())
            {
                return;
            }

            if (HasOwnedAutomationPreviewIdentity())
            {
                Fault(
                    "检测到自动游玩创建但未由当前阶段接管的道具预览；" +
                    "正在按枚举和交互身份重新确认后清理，本轮会以可恢复故障停止。");
                return;
            }

            SetStage(
                AutomationStage.Recovery,
                "检测到玩家正在预览一次性物品；自动游玩不会取消或接管该交互，等待玩家完成操作。");
            return;
        }

        if (blocked)
        {
            SetStage(AutomationStage.Recovery, BuildBlockerDetail(blockers));
            return;
        }

        bool canStartWave = state["canStartWave"]?.Value<bool>() == true;
        bool canSelectNextNode = state["canSelectNextNode"]?.Value<bool>() == true;
        JObject mapState;
        if (_pendingMapDecisionState != null)
        {
            mapState = _pendingMapDecisionState;
            _pendingMapDecisionState = null;
        }
        else if (canStartWave || canSelectNextNode || _pendingSublevel)
        {
            if (!TryQueryState(
                    "queryMap",
                    AutomationStage.SelectingRoute,
                    out _,
                    out mapState))
            {
                return;
            }

            _pendingMapDecisionState = mapState;
            ScheduleContinuationFrame();
            SetStage(
                AutomationStage.SelectingRoute,
                "已读取地图状态；下一帧再读取车辆或执行地图操作，避免同帧叠加命令。");
            return;
        }
        else
        {
            mapState = new JObject();
        }

        bool mapOpen = mapState["mapOpen"]?.Value<bool>() == true;
        bool routeSelectionOutstanding = MapRouteSelectionPolicy.IsSelectionOutstanding(
            canSelectNextNode,
            canStartWave,
            HasMapNode(mapState, "chooseNode"),
            HasMapNode(mapState, "pendingSubLevelNode"));
        if (TryEnsureMapOpenForSelectionPreview(routeSelectionOutstanding, mapOpen))
        {
            return;
        }

        if (!_defensePrepared && canStartWave)
        {
            JObject vehicleState;
            if (_pendingOpeningVehicleState != null)
            {
                vehicleState = _pendingOpeningVehicleState;
                _pendingOpeningVehicleState = null;
            }
            else
            {
                if (!TryQueryState(
                        "queryVehicle",
                        AutomationStage.PreparingDefense,
                        out _,
                        out vehicleState))
                {
                    return;
                }

                _pendingOpeningVehicleState = vehicleState;
                ScheduleContinuationFrame();
                SetStage(
                    AutomationStage.PreparingDefense,
                    "已读取现有战车状态；下一帧再决定是否创建默认防线。");
                return;
            }

            if (HasPlacedCombatVehicle(vehicleState))
            {
                _defensePrepared = true;
                RequestDefenseMaintenance();
                AddTimeline("defense", "已检测到场上现有战车，将从当前防线继续自动游玩。");
                SetStage(AutomationStage.PreparingDefense, "已识别现有防线，下一轮将检查背包战车与车列容量。");
                return;
            }
        }

        if (OpeningDefensePolicy.ShouldPrepare(
                inWave,
                blocked,
                _defensePrepared,
                _pendingSublevel,
                mapOpen,
                canStartWave))
        {
            _openingDefensePreparationActive = true;
            _deferOpeningDefenseCommandOnce = true;
            PrepareOpeningDefenseIncrementally();
            return;
        }

        if (!inWave &&
            !blocked &&
            !_defensePrepared &&
            !_pendingSublevel &&
            canStartWave)
        {
            SetStage(AutomationStage.SelectingRoute, "地图节点已提交，正在等待地图界面关闭后准备默认防线。");
            return;
        }

        if (_defensePrepared && !_speedConfigured && _options.OverrideGameSpeed)
        {
            bool configured = Execute(new AutomationAction(
                "setTimeSpeed",
                JObject.FromObject(new { speedState = _options.SpeedState }),
                AutomationStage.InitializingRun,
                "设置配置的游戏内速度。"));
            if (configured && _runState == AutoPlayerRunState.Running) _speedConfigured = true;
            return;
        }

        if (_pendingSublevel)
        {
            bool selected = Execute(new AutomationAction(
                "selectSublevel",
                JObject.FromObject(new { index = 0 }),
                AutomationStage.SelectingRoute,
                "选择第一个可用的子关卡。"));
            if (selected && _runState == AutoPlayerRunState.Running) _pendingSublevel = false;
            return;
        }

        JObject lightweightAffordances = BuildLightweightAffordanceResult(
            blockers,
            mapState,
            canStartWave,
            routeSelectionOutstanding);
        ExecuteInGameDecision(_decisionEngine.DecideInGame(
            lightweightAffordances,
            null,
            null,
            _options.DecisionPriority));
    }

    private bool TryEnsureMapOpenForSelectionPreview(bool routeSelectionOutstanding, bool mapOpen)
    {
        if (!routeSelectionOutstanding)
        {
            ResetMapPreviewOpenWait();
            return false;
        }

        float now = Time.realtimeSinceStartup;
        if (_mapPreviewOpenPending)
        {
            if (!mapOpen)
            {
                ResetMapPreviewOpenWait();
                InvalidateFullWaveQueryCache();
                ScheduleNormalPoll();
                SetStage(AutomationStage.SelectingRoute, "地图在预览完成前关闭；已取消本次预览，稍后重新读取路线状态。");
                return true;
            }

            float elapsed = Math.Max(0f, now - _mapPreviewOpenRequestedAt);
            bool animationReadable = _bridge.TryGetMapOpenAnimationProgress(
                out bool animationObserved,
                out bool animationCompleted,
                out float normalizedTime);
            bool animationObservedBefore = _mapPreviewOpenAnimationObserved;
            bool animationReady = MapOpenAnimationPolicy.IsReady(
                animationReadable,
                animationObserved,
                animationObservedBefore,
                animationCompleted,
                elapsed,
                MapOpenAnimationFallbackSeconds);
            _mapPreviewOpenAnimationObserved |= animationObserved;
            if (!animationReady)
            {
                InvalidateFullWaveQueryCache();
                ScheduleMapOpenAnimationPoll();
                string progress = animationReadable && _mapPreviewOpenAnimationObserved
                    ? $"（{Math.Min(100f, Math.Max(0f, normalizedTime * 100f)):0}%）"
                    : string.Empty;
                SetStage(AutomationStage.SelectingRoute, "游戏原生地图正在播放打开动画" + progress + "；动画结束后再显示目标节点。");
                return true;
            }

            ResetMapPreviewOpenWait();
            InvalidateFullWaveQueryCache();
            ScheduleContinuationFrame();
            SetStage(AutomationStage.SelectingRoute, "游戏原生地图打开动画已结束；正在重新读取节点并准备 1 秒绿色边框预览。");
            return true;
        }

        if (mapOpen)
        {
            bool animationReadable = _bridge.TryGetMapOpenAnimationProgress(
                out bool animationObserved,
                out bool animationCompleted,
                out _);
            if (animationReadable && animationCompleted)
            {
                return false;
            }

            _mapPreviewOpenPending = true;
            _mapPreviewOpenRequestedAt = now;
            _mapPreviewOpenAnimationObserved = animationObserved;
            ScheduleMapOpenAnimationPoll();
            SetStage(
                AutomationStage.SelectingRoute,
                animationReadable && animationObserved
                    ? "检测到地图仍在播放打开动画；动画结束后再显示目标节点。"
                    : "地图已开始打开，正在等待 Animator 进入 Open 状态；不会提前选择节点。");
            return true;
        }

        AutomationAction openMap = new(
            "uiClickMapButton",
            null,
            AutomationStage.SelectingRoute,
            "打开游戏原生地图，以显示自动游玩即将选择的节点。");
        if (!ExecuteWithResult(openMap, optional: true, out JObject result))
        {
            ScheduleNormalPoll();
            SetStage(AutomationStage.SelectingRoute, "无法打开游戏原生地图；本轮不会盲选节点，稍后重新查询并重试。");
            return true;
        }

        if (State(result)["mapOpen"]?.Value<bool>() != true)
        {
            ScheduleNormalPoll();
            SetStage(AutomationStage.SelectingRoute, "地图按钮已执行，但游戏没有确认地图已打开；本轮不会盲选节点，稍后重试。");
            return true;
        }

        _mapPreviewOpenPending = true;
        _mapPreviewOpenRequestedAt = now;
        _mapPreviewOpenAnimationObserved = false;
        InvalidateFullWaveQueryCache();
        ScheduleMapOpenAnimationPoll();
        SetStage(AutomationStage.SelectingRoute, "游戏原生地图已开始打开；等待原生过渡动画结束后再显示绿色边框。");
        return true;
    }

    private void ResetMapPreviewOpenWait()
    {
        _mapPreviewOpenPending = false;
        _mapPreviewOpenRequestedAt = -1f;
        _mapPreviewOpenAnimationObserved = false;
    }

    private void ScheduleMapOpenAnimationPoll()
    {
        float pollAt = Time.realtimeSinceStartup + MapOpenAnimationPollSeconds;
        if (_nextTickAt <= Time.realtimeSinceStartup || _nextTickAt > pollAt)
        {
            _nextTickAt = pollAt;
        }
    }

    private static bool HasMapNode(JObject mapState, string propertyName) =>
        mapState[propertyName] is JToken node &&
        node.Type != JTokenType.Null;

    private void PrepareOpeningDefenseIncrementally()
    {
        if (_deferOpeningDefenseCommandOnce)
        {
            _deferOpeningDefenseCommandOnce = false;
            _nextTickAt = Math.Max(
                _nextTickAt,
                Time.realtimeSinceStartup + BattleTacticFrameDelaySeconds);
            SetStage(
                AutomationStage.PreparingDefense,
                "已进入逐帧开局防线流程；从下一帧开始每帧最多提交一个运行时命令。");
            return;
        }

        bool incrementalContractAvailable = _bridge.HasCommand("queryCatapults") &&
                                             _bridge.HasCommand("queryDisposable") &&
                                             _bridge.HasCommand("confirmDisposableGrid") &&
                                             _bridge.HasCommand("cancelDisposable") &&
                                             _bridge.HasCommand("queryVehicle") &&
                                             _bridge.HasCommand("previewRailPath") &&
                                             _bridge.HasCommand("queryRail") &&
                                             _bridge.HasCommand("drawRailPath") &&
                                             _bridge.HasCommand("queryTrain") &&
                                             _bridge.HasCommand("moveVehicleInTrain") &&
                                             _bridge.HasCommand("placeVehicleOnLine");
        if (!incrementalContractAvailable)
        {
            Fault(
                "当前游戏版本缺少逐帧开局防线所需的查询、预览、画轨或放车接口；" +
                "已安全停止，不会回退到整图扫描宏。");
            return;
        }

        if (_openingDefensePreparationPlanner.Phase ==
                OpeningDefensePreparationPhase.WaitForPlacementSettlement &&
            _openingDefenseInteractionInstanceId == 0 &&
            !_openingDefenseWaitingForForeignPreview &&
            !_openingPendingDisposableMutationGuard.IsArmed)
        {
            _openingDefensePreparationPlanner.MarkPlacementPreviewReleased();
        }

        OpeningDefensePreparationDecision decision = _openingDefensePreparationPlanner.Decide();
        if (decision.Phase == OpeningDefensePreparationPhase.PlacementVerificationFailed)
        {
            Fault(decision.Detail);
            return;
        }

        if (decision.IsComplete || decision.Action == null)
        {
            _openingDefensePreparationActive = false;
            _defensePrepared = true;
            RequestDefenseMaintenance();
            SetStage(AutomationStage.PreparingDefense, decision.Detail);
            return;
        }

        AutomationAction action = decision.Action;
        if (string.Equals(action.Command, "wait", StringComparison.OrdinalIgnoreCase))
        {
            _nextTickAt = Time.realtimeSinceStartup + BattleTacticFrameDelaySeconds;
            SetStage(AutomationStage.PreparingDefense, decision.Detail);
            return;
        }

        bool confirmingAttribute = string.Equals(
            action.Command,
            "confirmDisposableGrid",
            StringComparison.OrdinalIgnoreCase);
        if (confirmingAttribute && !CanConfirmOpeningDefenseAttributeNow())
        {
            return;
        }

        if (confirmingAttribute)
        {
            ResetOwnedPreviewCancellationTracking();
        }

        bool readOnly = action.Command.StartsWith("query", StringComparison.OrdinalIgnoreCase) ||
                        action.Command.StartsWith("preview", StringComparison.OrdinalIgnoreCase);
        JObject result;
        bool accepted;
        RuntimeResultDisposition plannerDisposition;
        if (readOnly)
        {
            plannerDisposition = ExecuteOpeningDefenseReadOnly(action, out result);
            if (_runState != AutoPlayerRunState.Running)
            {
                return;
            }

            if (plannerDisposition == RuntimeResultDisposition.Pending)
            {
                _nextTickAt = Math.Max(
                    _nextTickAt,
                    Time.realtimeSinceStartup + BattleTacticFrameDelaySeconds);
                return;
            }

            accepted = plannerDisposition == RuntimeResultDisposition.Success;
        }
        else
        {
            // Later read-only phases reconcile every write. Optional execution prevents the generic
            // failure policy from ever retrying a clean draw/placement failure on its own.
            accepted = ExecuteWithResult(action, optional: true, out result);
            if (_runState != AutoPlayerRunState.Running)
            {
                return;
            }

            plannerDisposition = RuntimeResultInspector.Classify(result);
        }

        if (confirmingAttribute && !CaptureOpeningDefensePreviewIdentity(result))
        {
            return;
        }

        if (_runState == AutoPlayerRunState.Running &&
            plannerDisposition == RuntimeResultDisposition.Pending &&
            confirmingAttribute &&
            _openingDefenseInteractionInstanceId != 0)
        {
            _ownedPreviewConfirmationOutcomeUncertain = true;
            _openingDefensePreparationPlanner.Observe(action, result, accepted: true);
            _nextTickAt = Math.Max(
                _nextTickAt,
                Time.realtimeSinceStartup + BattleTacticFrameDelaySeconds);
            return;
        }

        if (confirmingAttribute &&
            plannerDisposition == RuntimeResultDisposition.Pending &&
            _openingDefenseInteractionInstanceId == 0)
        {
            string disposableEnum = action.Arguments["disposableEnum"]?.Value<string>() ?? string.Empty;
            if (!_openingPendingDisposableMutationGuard.TryArm(
                    action,
                    disposableEnum,
                    Time.realtimeSinceStartup))
            {
                FaultRequiringProcessRestart(
                    "开局属性弹射点确认返回 pending，但既没有交互实例身份，" +
                    "也无法建立禁止重发的动作账本；请彻底重启游戏进程。");
                return;
            }

            _ownedPreviewConfirmationOutcomeUncertain = true;
            _openingPendingDisposableQueryCatapults = false;
            _openingPendingDisposableObservation = null;
            _openingDefensePreparationPlanner.Observe(action, result, accepted: true);
            _nextTickAt = Math.Max(
                _nextTickAt,
                Time.realtimeSinceStartup + BattleTacticFrameDelaySeconds);
            SetStage(
                AutomationStage.PreparingDefense,
                "属性弹射点确认正在建立交互对象；已锁定本次网格写入，" +
                "下一帧开始只读对账且不会重复确认。");
            return;
        }

        OpeningDefensePreparationPhase observedPhase = decision.Phase;
        _openingDefensePreparationPlanner.Observe(action, result, accepted);
        if (observedPhase == OpeningDefensePreparationPhase.VerifyAttributePlacement &&
            accepted &&
            _openingDefensePreparationPlanner.Phase == OpeningDefensePreparationPhase.QueryVehicle)
        {
            _ownedPreviewConfirmationOutcomeUncertain = false;
            ResetOwnedPreviewCancellationTrackingIfNoIdentity();
        }
        if (confirmingAttribute &&
            !accepted &&
            _openingDefenseInteractionInstanceId != 0 &&
            _runState == AutoPlayerRunState.Running)
        {
            Fault("开局属性弹射点确认失败并留下了本次道具预览；正在按交互身份安全清理后停止本轮自动游玩。");
            return;
        }

        if (_openingDefensePreparationPlanner.Phase == OpeningDefensePreparationPhase.Completed &&
            _runState == AutoPlayerRunState.Running)
        {
            _openingDefensePreparationActive = false;
            _defensePrepared = true;
            RequestDefenseMaintenance();
        }
        else if (_openingDefensePreparationPlanner.Phase ==
                     OpeningDefensePreparationPhase.PlacementVerificationFailed &&
                 _runState == AutoPlayerRunState.Running)
        {
            Fault(_openingDefensePreparationPlanner.Decide().Detail);
        }
        else if (_runState == AutoPlayerRunState.Running)
        {
            _nextTickAt = Math.Max(
                _nextTickAt,
                Time.realtimeSinceStartup + BattleTacticFrameDelaySeconds);
        }
    }

    private void ObserveActiveScene(Scene activeScene)
    {
        string sceneName = activeScene.name ?? string.Empty;
        int sceneHandle = activeScene.handle;
        if (string.Equals(sceneName, _scene, StringComparison.Ordinal) && sceneHandle == _sceneHandle)
        {
            return;
        }

        bool completedTransition = _frontEndTransitionGate.ObserveScene(sceneName);
        _scene = sceneName;
        _sceneHandle = sceneHandle;
        _defensePrepared = false;
        ResetOpeningDefensePreparation();
        _speedConfigured = !_options.OverrideGameSpeed;
        _pendingSublevel = false;
        _mapSelectionPending = false;
        _mapSelectionPendingAt = -1f;
        ResetEventOptionObservation();
        ResetNormalEventObservation();
        _rewardSelectionSettlementGuard.Reset();
        _rewardObjectSettlementGuard.Reset();
        _waveFunctionOptionSettlementGuard.Reset();
        _rewardObjectCollectionLedger.Clear();
        ResetRewardOptionObservation();
        _pendingMapAction = null;
        ClearSelectionHighlight();
        _deferredFrontEndAction = null;
        _deferredNormalEventAction = null;
        _deferredNormalEventChoosingOption = false;
        _deferredRewardAction = null;
        _deferredSettlementAction = null;
        _pendingMapDecisionState = null;
        _pendingOpeningVehicleState = null;
        _wasInWave = false;
        _wishReturnClicked = false;
        _frontEndReadinessObserved = false;
        _gameModeVerified = false;
        _runtimeInitialized = false;
        ResetBattleTactics();
        ResetFullWaveQueryPolling();
        _mergeMutationSettlementGuard.Reset();
        RequestDefenseMaintenance();
        _pendingActionKey = string.Empty;
        ResetWaveStartObservation();
        _gameOverDetectedAt = -1f;
        AddTimeline("scene", "已进入场景 " + sceneName + "（实例 " + sceneHandle + "）。");
        if (completedTransition)
        {
            AddTimeline("transition", "已观察到前端命令触发场景切换。");
        }
        MarkProgress();
    }

    private void ContinueOpeningDefensePreparation()
    {
        if (_openingDefensePreparationPlanner.Phase ==
                OpeningDefensePreparationPhase.WaitForPlacementSettlement &&
            _openingPendingDisposableMutationGuard.IsArmed)
        {
            HandlePendingOpeningDisposableMutation();
            return;
        }

        if (_openingDefensePreparationPlanner.Phase ==
                OpeningDefensePreparationPhase.WaitForPlacementSettlement &&
            (_openingDefenseInteractionInstanceId != 0 ||
             _openingDefenseWaitingForForeignPreview) &&
            TryHandleOpeningDefensePreviewBlocker())
        {
            return;
        }

        PrepareOpeningDefenseIncrementally();
    }

    private void HandlePendingOpeningDisposableMutation()
    {
        JObject? disposableResult = _openingPendingDisposableObservation;
        JObject? catapultResult = null;
        if (_openingPendingDisposableQueryCatapults)
        {
            if (TryInvokeOptionalReadOnly("queryCatapults", null, out JObject observedCatapults))
            {
                catapultResult = observedCatapults;
            }
        }
        else if (TryInvokeOptionalReadOnly("queryDisposable", null, out JObject observedDisposable))
        {
            disposableResult = observedDisposable;
            _openingPendingDisposableObservation = observedDisposable;
        }

        PendingDisposableMutationResolution resolution =
            _openingPendingDisposableMutationGuard.Observe(
                disposableResult,
                catapultResult,
                Time.realtimeSinceStartup,
                RewardSelectionSettlementTimeoutSeconds);
        switch (resolution)
        {
            case PendingDisposableMutationResolution.InteractionObserved:
                _openingDefenseInteractionInstanceId =
                    _openingPendingDisposableMutationGuard.ResolvedInteractionInstanceId;
                _openingPendingDisposableMutationGuard.Reset();
                ResetPendingOpeningDisposableObservation();
                _nextTickAt = Math.Max(
                    _nextTickAt,
                    Time.realtimeSinceStartup + BattleTacticFrameDelaySeconds);
                SetStage(
                    AutomationStage.PreparingDefense,
                    "已通过只读查询取得延迟创建的属性弹射点交互身份；继续等待生成动画退出。");
                return;

            case PendingDisposableMutationResolution.TargetAttributeCatapultObserved:
                _openingPendingDisposableMutationGuard.Reset();
                ResetPendingOpeningDisposableObservation();
                _openingDefensePreparationPlanner.MarkPlacementPreviewReleased();
                _ownedPreviewConfirmationOutcomeUncertain = false;
                ResetOwnedPreviewCancellationTrackingIfNoIdentity();
                _nextTickAt = Math.Max(
                    _nextTickAt,
                    Time.realtimeSinceStartup + BattleTacticFrameDelaySeconds);
                SetStage(
                    AutomationStage.PreparingDefense,
                    "已通过目标网格的属性弹射点证明延迟确认成功；下一帧继续验证完整站点状态。");
                return;

            case PendingDisposableMutationResolution.Unknown:
                FaultRequiringProcessRestart(
                    "属性弹射点确认写入已锁定且未重发，但在 " +
                    RewardSelectionSettlementTimeoutSeconds.ToString("0") +
                    " 秒内既未出现可验证交互，也未出现目标属性弹射点；最终结果仍未知，请彻底重启游戏进程。");
                return;

            default:
                _openingPendingDisposableQueryCatapults =
                    !_openingPendingDisposableQueryCatapults;
                _nextTickAt = Math.Max(
                    _nextTickAt,
                    Time.realtimeSinceStartup + BattleTacticFrameDelaySeconds);
                SetStage(
                    AutomationStage.PreparingDefense,
                    _openingPendingDisposableQueryCatapults
                        ? "属性弹射点确认结果仍未知；下一帧只读检查目标弹射点，不会重发写命令。"
                        : "属性弹射点确认结果仍未知；下一帧只读检查道具交互，不会重发写命令。");
                return;
        }
    }

    private void ResetPendingOpeningDisposableObservation()
    {
        _openingPendingDisposableQueryCatapults = false;
        _openingPendingDisposableObservation = null;
    }

    private void HandlePendingDefenseDisposableMutation()
    {
        JObject? disposableResult = _defensePendingDisposableObservation;
        JObject? catapultResult = null;
        if (_defensePendingDisposableQueryCatapults)
        {
            if (TryInvokeOptionalReadOnly("queryCatapults", null, out JObject observedCatapults))
            {
                catapultResult = observedCatapults;
            }
        }
        else if (TryInvokeOptionalReadOnly("queryDisposable", null, out JObject observedDisposable))
        {
            disposableResult = observedDisposable;
            _defensePendingDisposableObservation = observedDisposable;
        }

        PendingDisposableMutationResolution resolution =
            _defensePendingDisposableMutationGuard.Observe(
                disposableResult,
                catapultResult,
                Time.realtimeSinceStartup,
                RewardSelectionSettlementTimeoutSeconds);
        switch (resolution)
        {
            case PendingDisposableMutationResolution.TargetAttributeCatapultObserved:
            case PendingDisposableMutationResolution.TargetCatapultObserved:
                _defensePendingDisposableMutationGuard.Reset();
                ResetPendingDefenseDisposableObservation();
                _pendingActionKey = string.Empty;
                _ownedPreviewConfirmationOutcomeUncertain = false;
                _defenseAttributeInteractionInstanceId = 0;
                _defenseAttributeVerificationAttempts = 0;
                _defenseMaintenanceStep = DefenseMaintenanceStep.VerifyExpansionAttribute;
                ScheduleDefenseMaintenanceStep(
                    "已通过目标格动力站证明 pending 确认完成；下一帧继续验证完整站点状态。");
                return;

            case PendingDisposableMutationResolution.Unknown:
                FaultRequiringProcessRestart(
                    "动力弹射点确认写入已锁定且未重发，但 20 秒内未出现目标格动力站；" +
                    "最终写入结果仍未知，请彻底重启游戏进程。");
                return;

            default:
                _defensePendingDisposableQueryCatapults =
                    !_defensePendingDisposableQueryCatapults;
                ScheduleDefenseMaintenanceStep(
                    _defensePendingDisposableQueryCatapults
                        ? "动力弹射点确认结果仍未知；下一帧只读检查目标格站点，不会重发写命令。"
                        : "动力弹射点确认结果仍未知；下一帧只读检查道具交互，不会重发写命令。");
                return;
        }
    }

    private void ResetPendingDefenseDisposableObservation()
    {
        _defensePendingDisposableQueryCatapults = false;
        _defensePendingDisposableObservation = null;
    }

    private RuntimeResultDisposition ExecuteOpeningDefenseReadOnly(
        AutomationAction action,
        out JObject result)
    {
        SetStage(action.Stage, action.Reason);
        _pendingActionKey = string.Empty;
        _lastCommand = action.Command;
        _lastActionAtUtc = DateTime.UtcNow;
        result = string.Equals(
                action.Command,
                "queryOpeningDefenseInteractionGuard",
                StringComparison.OrdinalIgnoreCase)
            ? _openingDefenseInteractionGuard.Query()
            : _bridge.Invoke(action.Command, action.Arguments);
        _lastRuntimeResult = result;
        _lastMessage = Message(result);
        if (string.Equals(action.Command, "previewRailPath", StringComparison.OrdinalIgnoreCase) &&
            IsUnsafeOpeningDefenseRailPreview(result))
        {
            FaultRequiringProcessRestart(
                "开局轨道只读预览改变了轨道数量或报告 statePolluted/needsReset；" +
                "无法证明游戏状态仍与预览前一致，请彻底重启游戏进程。");
            return RuntimeResultDisposition.Unsafe;
        }

        RuntimeResultDisposition disposition = RuntimeResultInspector.ClassifyReadOnly(result);
        switch (disposition)
        {
            case RuntimeResultDisposition.Pending:
                SetStage(action.Stage, _lastMessage);
                break;

            case RuntimeResultDisposition.Failure:
                AddWarning("开局防线只读命令 " + action.Command + " 失败：" + _lastMessage);
                break;

            default:
                _consecutiveFailures = 0;
                MarkProgress();
                AddTimeline("defense-read", action.Reason + " " + _lastMessage);
                break;
        }

        return disposition;
    }

    private bool TryHandleOpeningDefensePreviewBlocker()
    {
        if (_openingDefensePreparationPlanner.Phase !=
                OpeningDefensePreparationPhase.WaitForPlacementSettlement ||
            (_openingDefenseInteractionInstanceId == 0 &&
             !_openingDefenseWaitingForForeignPreview))
        {
            return false;
        }

        if (!TryQueryState(
                "queryDisposable",
                AutomationStage.PreparingDefense,
                out JObject disposableResult,
                out JObject disposableState))
        {
            return true;
        }

        if (_openingDefenseInteractionInstanceId != 0 &&
            _battleDecisionEngine.IsOwnedExpansionAttributePreview(
                disposableResult,
                _openingDefenseInteractionInstanceId,
                requireGridInteraction: false))
        {
            _nextTickAt = Math.Max(
                _nextTickAt,
                Time.realtimeSinceStartup + BattleTacticFrameDelaySeconds);
            SetStage(
                AutomationStage.PreparingDefense,
                "开局属性弹射点仍在播放生成动画；保持本次预览，不发送取消命令。");
            return true;
        }

        if (disposableState["isInPreview"]?.Value<bool>() == true)
        {
            _openingDefenseInteractionInstanceId = 0;
            _openingDefenseWaitingForForeignPreview = true;
            _nextTickAt = Math.Max(
                _nextTickAt,
                Time.realtimeSinceStartup + BattleTacticFrameDelaySeconds);
            SetStage(
                AutomationStage.PreparingDefense,
                "自动游玩的属性弹射点预览已经退出，但检测到另一个道具预览；" +
                "等待该交互结束后再验证站点，不会确认或取消它。");
            return true;
        }

        _openingDefenseInteractionInstanceId = 0;
        _openingDefenseWaitingForForeignPreview = false;
        _openingDefensePreparationPlanner.MarkPlacementPreviewReleased();
        _nextTickAt = Math.Max(
            _nextTickAt,
            Time.realtimeSinceStartup + BattleTacticFrameDelaySeconds);
        SetStage(
            AutomationStage.PreparingDefense,
            "开局属性弹射点预览已经退出；下一帧验证站点是否实际生成。");
        return true;
    }

    private bool CanConfirmOpeningDefenseAttributeNow()
    {
        JObject guardResult = _openingDefenseInteractionGuard.Query();
        _lastCommand = "queryOpeningDefenseInteractionGuard";
        _lastActionAtUtc = DateTime.UtcNow;
        _lastRuntimeResult = guardResult;
        _lastMessage = Message(guardResult);
        JObject guardState = State(guardResult);
        bool guardSucceeded = RuntimeResultInspector.ClassifyReadOnly(guardResult) ==
                              RuntimeResultDisposition.Success;
        bool observationConsistent = guardState["observationConsistent"]?.Value<bool>() == true;
        bool noActiveInteraction = guardState["noActiveInteraction"]?.Value<bool>() == true;
        if (guardSucceeded && observationConsistent && noActiveInteraction)
        {
            _openingDefenseConfirmGuardFailures = 0;
            return true;
        }

        _nextTickAt = Math.Max(
            _nextTickAt,
            Time.realtimeSinceStartup + BattleTacticFrameDelaySeconds);
        if (guardSucceeded && observationConsistent)
        {
            _openingDefenseConfirmGuardFailures = 0;
            SetStage(
                AutomationStage.PreparingDefense,
                "确认写入前检测到正在进行的道具交互；保持等待，不会接管玩家或其他工具的预览。");
            return false;
        }

        _openingDefenseConfirmGuardFailures++;
        if (_openingDefenseConfirmGuardFailures >= MaxOpeningDefenseConfirmGuardFailures)
        {
            Fault(
                "开局属性弹射点确认前连续无法证明道具交互为空闲；尚未提交确认写命令。" +
                "最后结果：" + _lastMessage);
            return false;
        }

        SetStage(
            AutomationStage.PreparingDefense,
            $"确认写入前的道具交互守卫尚未稳定（{_openingDefenseConfirmGuardFailures}/" +
            $"{MaxOpeningDefenseConfirmGuardFailures}）；下一帧继续只读检查。");
        return false;
    }

    private static bool IsUnsafeOpeningDefenseRailPreview(JObject result)
    {
        if (RuntimeResultInspector.IsUnsafe(result))
        {
            return true;
        }

        JObject state = State(result);
        int? beforeRailCount = state["beforeRailCount"]?.Value<int?>();
        int? afterRailCount = state["afterRailCount"]?.Value<int?>();
        return beforeRailCount.HasValue &&
               afterRailCount.HasValue &&
               beforeRailCount.Value != afterRailCount.Value;
    }

    private bool CaptureOpeningDefensePreviewIdentity(JObject confirmResult)
    {
        JObject state = State(confirmResult);
        if (state["isInPreview"]?.Value<bool>() != true)
        {
            _openingDefenseInteractionInstanceId = 0;
            return true;
        }

        int interactionInstanceId =
            _battleDecisionEngine.ReadExpansionAttributeInteractionId(confirmResult);
        if (interactionInstanceId == 0)
        {
            FaultRequiringProcessRestart(
                "开局属性弹射点确认后仍有道具预览，但运行时没有返回可验证的交互身份。");
            return false;
        }

        _openingDefenseInteractionInstanceId = interactionInstanceId;
        return true;
    }

    private void ExecuteInGameDecision(AutomationAction action)
    {
        if (!string.Equals(action.Command, "startWave", StringComparison.OrdinalIgnoreCase))
        {
            if (string.Equals(action.Command, "selectMapNode", StringComparison.OrdinalIgnoreCase))
            {
                ResetWaveStartObservation();
                NativeSelectionTarget? target = BuildInstanceSelectionTarget(
                    action,
                    "MetroTD.RoomSystem.MapNodeUI",
                    "instanceId",
                    "path");
                if (target != null && TryWaitForSelectionPreview(
                        "map",
                        action,
                        target,
                        "已用绿色边框标出下一步地图节点；观察时间结束后再选择。"))
                {
                    return;
                }
            }

            Execute(action);
            return;
        }

        if (_bridge.TryGetWaveFunctionOptionFlow(out bool flowPending, out string flowDescription) && flowPending)
        {
            _waveStartPending = false;
            _waveStartPendingAt = -1f;
            _waveStartAttemptCount = 0;
            string normalizedDescription = string.IsNullOrWhiteSpace(flowDescription)
                ? "未命名事件后续流程"
                : flowDescription.Trim();
            if (!string.Equals(
                    normalizedDescription,
                    _pendingWaveFunctionFlowDescription,
                    StringComparison.Ordinal))
            {
                _pendingWaveFunctionFlowDescription = normalizedDescription;
                AddWarning("检测到尚未完成的事件后续流程：" + normalizedDescription + "；已停止重复发送开波命令。");
            }

            SetStage(
                AutomationStage.ManagingEvent,
                "正在等待游戏完成事件后续流程：" + normalizedDescription + "。不会在此期间重复发送开波命令。");
            return;
        }

        _pendingWaveFunctionFlowDescription = string.Empty;
        float now = Time.realtimeSinceStartup;
        if (_waveStartPending)
        {
            float elapsed = Math.Max(0f, now - _waveStartPendingAt);
            if (elapsed < WaveStartObservationTimeoutSeconds)
            {
                SetStage(
                    AutomationStage.StartingWave,
                    "开波命令已发送，正在等待游戏确认波次开始。不会重复发送命令。");
                return;
            }

            _waveStartPending = false;
            _waveStartPendingAt = -1f;
            if (_waveStartAttemptCount >= MaxWaveStartAttempts)
            {
                PauseForWaveStartRecovery(
                    "开波命令已发送 " + _waveStartAttemptCount +
                    " 次，但始终没有观察到波次开始。自动游玩已暂停，游戏进程无需重启。");
                return;
            }

            AddWarning("开波命令尚未得到游戏确认，将进行第 " + (_waveStartAttemptCount + 1) + " 次有限重试。");
        }

        bool executed = Execute(action);
        if (!executed || _runState != AutoPlayerRunState.Running)
        {
            return;
        }

        _waveStartAttemptCount++;
        _waveStartPending = true;
        _waveStartPendingAt = now;
        _nextTickAt = Math.Max(_nextTickAt, now + Math.Min(WaveStartObservationTimeoutSeconds, 1f));
    }

    private void PauseForWaveStartRecovery(string reason)
    {
        PauseForRecoverableRuntimeState(reason);
    }

    private void PauseForRecoverableRuntimeState(string reason)
    {
        ClearSelectionHighlight();
        _runState = AutoPlayerRunState.Paused;
        _pausedAtUtc = DateTime.UtcNow;
        _stage = AutomationStage.Recovery;
        _stageDetail = reason;
        _lastMessage = reason;
        AddTimeline("pause", reason);
        _evidence.WriteStatus(EnsureEvidenceDirectory(), Snapshot());
    }

    private void ResetWaveStartObservation()
    {
        _waveStartPending = false;
        _waveStartPendingAt = -1f;
        _waveStartAttemptCount = 0;
        _pendingWaveFunctionFlowDescription = string.Empty;
    }

    private bool EnsureInGameRuntimeReady()
    {
        if (_runtimeInitialized) return true;

        JObject initialization = _bridge.Invoke("queryState");
        switch (RuntimeResultInspector.ClassifyReadOnly(initialization))
        {
            case RuntimeResultDisposition.Pending:
                SetStage(AutomationStage.InitializingRun, Message(initialization));
                return false;
            case RuntimeResultDisposition.Failure:
                RegisterFailure("命令 queryState 失败：" + Message(initialization));
                return false;
        }

        _runtimeInitialized = true;
        ScheduleContinuationFrame();
        SetStage(
            AutomationStage.InitializingRun,
            "运行时已完成初始化；下一帧再读取游戏状态，避免同帧叠加命令。");
        return false;
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

        if (_bridge.TryGetWavePulse(out bool pulseInWave, out bool pulseGameOver, out int pulseRemaining))
        {
            CompleteWaveFunctionOptionSettlementFromWavePulse(pulseInWave, pulseGameOver);
            return HandleWaveObservation(pulseInWave, pulseGameOver, pulseRemaining, null);
        }

        if (!TryQueryAdaptiveWaveState(
                "queryWave",
                AutomationStage.Battle,
                out JObject waveResult,
                out JObject waveState))
        {
            return true;
        }

        JArray blockers = waveState["blockers"] as JArray ?? new JArray();
        bool inWave = waveState["isInWaving"]?.Value<bool>() == true;
        bool gameOver = HasBlocker(blockers, "gameOver")
                        || GameOutcomeObserver.Outcome is AutomationOutcome.Victory or AutomationOutcome.Defeat;
        int remaining = waveState.SelectToken("enemy.remaining")?.Value<int?>() ?? -1;
        return HandleWaveObservation(inWave, gameOver, remaining, waveResult);
    }

    private bool TryHandleNormalEventUi()
    {
        if (!_normalEventProbeRequired && !_normalEventObserved && !_mapSelectionPending && !_waveStartPending)
        {
            return false;
        }

        if (_deferredNormalEventAction != null)
        {
            AutomationAction deferred = _deferredNormalEventAction;
            bool choosingDeferredOption = _deferredNormalEventChoosingOption;
            _deferredNormalEventAction = null;
            _deferredNormalEventChoosingOption = false;
            ClearSelectionHighlight("normal-event");
            bool clicked = Execute(deferred);
            if (clicked && _runState == AutoPlayerRunState.Running)
            {
                if (choosingDeferredOption)
                {
                    RequestDefenseMaintenance();
                }

                ResetNormalEventActionObservation();
                _nextTickAt = Time.realtimeSinceStartup + BattleTacticFrameDelaySeconds;
            }

            return true;
        }

        _normalEventProbeRequired = false;
        if (!_normalEventUiReader.TryRead(out NormalEventUiRuntimeState runtimeState))
        {
            if (!_normalEventObserved)
            {
                return false;
            }

            _normalEventProbeFailures++;
            if (_normalEventProbeFailures >= 3)
            {
                PauseForRecoverableRuntimeState(
                    "连续三次无法只读确认普通事件剧情状态。自动游玩已暂停，游戏进程无需重启。");
            }
            else
            {
                _nextTickAt = Time.realtimeSinceStartup + BattleTacticFrameDelaySeconds;
                SetStage(
                    AutomationStage.ManagingEvent,
                    $"普通事件剧情状态暂时不可读（{_normalEventProbeFailures}/3），下一帧继续确认。");
            }

            return true;
        }

        _normalEventProbeFailures = 0;
        if (!runtimeState.IsOpen)
        {
            if (!_normalEventObserved)
            {
                float pendingElapsed = _mapSelectionPendingAt < 0f
                    ? float.MaxValue
                    : Math.Max(0f, Time.realtimeSinceStartup - _mapSelectionPendingAt);
                if (_mapSelectionPending && pendingElapsed < NormalEventAppearanceGraceSeconds)
                {
                    _nextTickAt = Math.Max(
                        _nextTickAt,
                        _mapSelectionPendingAt + NormalEventAppearanceGraceSeconds);
                    SetStage(
                        AutomationStage.SelectingRoute,
                        "地图节点已提交，正在等待新版普通事件界面的短暂生成窗口。");
                    return true;
                }

                return false;
            }

            ResetNormalEventObservation();
            ResetEventOptionObservation();
            ResetWaveStartObservation();
            _mapSelectionPending = false;
            _mapSelectionPendingAt = -1f;
            AddTimeline("event", "普通事件剧情面板已关闭，正在重新读取地图与波次状态。");
            MarkProgress();
            return false;
        }

        float now = Time.realtimeSinceStartup;
        bool firstObservation = !_normalEventObserved;
        _normalEventObserved = true;
        _mapSelectionPending = true;
        _mapSelectionPendingAt = now;
        ResetWaveStartObservation();
        ResetEventOptionObservation();
        if (firstObservation)
        {
            AddTimeline(
                "observation",
                _options.SkipStory
                    ? "已识别新版普通事件剧情面板；跳过剧情已开启，将在真实跳过按钮可用后立即操作。"
                    : "已识别新版普通事件剧情面板；开波重试已停止，等待每段文字动画结束后保留 1 秒观察时间。");
            MarkProgress();
        }

        string storyProgress = runtimeState.CurrentStoryCount > 0
            ? $"第 {runtimeState.CurrentStoryIndex + 1}/{runtimeState.CurrentStoryCount} 段"
            : "当前阶段";
        if (runtimeState.IsTypingStory && !_options.SkipStory)
        {
            ResetNormalEventActionObservation();
            SetStage(AutomationStage.ManagingEvent, "普通事件" + storyProgress + "文字动画正在播放，等待动画自然结束。");
            return true;
        }

        JObject interactables = _bridge.Invoke("queryUiInteractables");
        switch (RuntimeResultInspector.ClassifyReadOnly(interactables))
        {
            case RuntimeResultDisposition.Pending:
                SetStage(AutomationStage.ManagingEvent, Message(interactables));
                return true;
            case RuntimeResultDisposition.Failure:
                RegisterFailure("命令 queryUiInteractables 查询普通事件失败：" + Message(interactables));
                return true;
        }

        _consecutiveFailures = 0;
        NormalEventUiSnapshot snapshot = NormalEventUiInspector.Inspect(interactables);
        if (!snapshot.IsOpen)
        {
            ResetNormalEventActionObservation();
            _nextTickAt = now + BattleTacticFrameDelaySeconds;
            SetStage(AutomationStage.ManagingEvent, "普通事件面板已打开，正在等待当前阶段的按钮生成完成。");
            return true;
        }

        NormalEventUiButton? immediateStoryTarget =
            NormalEventUiDecision.SelectTarget(snapshot, _options.SkipStory);
        if (immediateStoryTarget?.Role == NormalEventUiButtonRole.SkipStory)
        {
            ClearSelectionHighlight("normal-event");
            _deferredNormalEventAction = new AutomationAction(
                "uiClick",
                JObject.FromObject(new { instanceId = immediateStoryTarget.InstanceId }),
                AutomationStage.ManagingEvent,
                "跳过新版普通事件的剩余剧情。");
            _deferredNormalEventChoosingOption = false;
            ScheduleContinuationFrame();
            SetStage(
                AutomationStage.ManagingEvent,
                "已确认普通事件跳过按钮；下一帧点击，保持读取与写操作分帧执行。");
            return true;
        }

        if (runtimeState.IsTypingStory)
        {
            ResetNormalEventActionObservation();
            _nextTickAt = now + BattleTacticFrameDelaySeconds;
            SetStage(
                AutomationStage.ManagingEvent,
                "普通事件剧情正在播放，跳过按钮尚不可用；下一帧继续确认。");
            return true;
        }

        string fingerprint = string.Join(
            "|",
            runtimeState.OptionChosen ? "post-choice" : "pre-choice",
            runtimeState.TypingStage,
            runtimeState.CurrentStoryIndex,
            runtimeState.CurrentStoryCount,
            snapshot.Fingerprint);
        NormalEventUiButton? observedTarget = NormalEventUiDecision.SelectTarget(snapshot, _options.SkipStory);
        if (observedTarget?.Role == NormalEventUiButtonRole.ChooseOption)
        {
            ShowSelectionHighlight(
                "normal-event",
                fingerprint + "|" + observedTarget.InstanceId,
                NativeSelectionTarget.ByInstance(
                    "ActFramework_ByHZR.UI.UIButton",
                    observedTarget.InstanceId,
                    observedTarget.Path));
        }
        else
        {
            ClearSelectionHighlight("normal-event");
        }

        if (!string.Equals(fingerprint, _normalEventFingerprint, StringComparison.Ordinal))
        {
            _normalEventFingerprint = fingerprint;
            _normalEventActionReadyAt = now + SelectionPreviewObservationSeconds;
            _nextTickAt = Math.Max(_nextTickAt, _normalEventActionReadyAt);
            SetStage(
                AutomationStage.ManagingEvent,
                "普通事件" + storyProgress + "已完整显示，保留 1 秒观察时间后再继续。");
            AddTimeline(
                "observation",
                "普通事件" + storyProgress + "动画已结束；将在 1 秒观察时间结束后操作。");
            MarkProgress();
            return true;
        }

        if (now < _normalEventActionReadyAt)
        {
            _nextTickAt = Math.Max(_nextTickAt, _normalEventActionReadyAt);
            SetStage(
                AutomationStage.ManagingEvent,
                "普通事件" + storyProgress + "保持显示，正在等待 1 秒观察时间结束。");
            return true;
        }

        NormalEventUiButton? target = observedTarget;
        bool choosingOption = target?.Role == NormalEventUiButtonRole.ChooseOption;
        bool skippingStory = target?.Role == NormalEventUiButtonRole.SkipStory;

        if (target == null)
        {
            _nextTickAt = now + BattleTacticFrameDelaySeconds;
            SetStage(
                AutomationStage.ManagingEvent,
                "普通事件当前阶段没有可安全点击的按钮，正在等待界面状态变化。");
            return true;
        }

        string actionReason = choosingOption
            ? $"选择新版普通事件中第 {target.OptionIndex + 1} 个可用选项。"
            : skippingStory
                ? "跳过新版普通事件的剩余剧情。"
                : "继续播放新版普通事件剧情。";
        _deferredNormalEventAction = new AutomationAction(
            "uiClick",
            JObject.FromObject(new { instanceId = target.InstanceId }),
            AutomationStage.ManagingEvent,
            actionReason);
        _deferredNormalEventChoosingOption = choosingOption;
        ScheduleContinuationFrame();
        SetStage(
            AutomationStage.ManagingEvent,
            "已确认普通事件按钮；下一帧再点击，避免读取界面和写操作叠加在同一帧。");

        return true;
    }

    private bool TryHandlePendingMapSelection()
    {
        if (_pendingMapAction != null)
        {
            AutomationAction action = _pendingMapAction;
            _pendingMapAction = null;
            ClearSelectionHighlight("event");
            Execute(action);
            if (string.Equals(action.Command, "chooseWaveFunctionOption", StringComparison.OrdinalIgnoreCase))
            {
                ResetEventOptionObservation();
            }
            return true;
        }

        if (!_mapSelectionPending) return false;

        if (!string.IsNullOrWhiteSpace(_pendingEventPanel))
        {
            if (!_waveFunctionOptionSettlementGuard.IsArmed &&
                _mapSelectionPendingAt >= 0f &&
                Time.realtimeSinceStartup - _mapSelectionPendingAt >= MapSelectionTransitionTimeoutSeconds)
            {
                _mapSelectionPending = false;
                _mapSelectionPendingAt = -1f;
                ResetEventOptionObservation();
                AddWarning("地图节点点击后的事件处理超过安全时限，将在下一轮重新读取完整状态。");
                return true;
            }

            if (Time.realtimeSinceStartup < _eventOptionsReadyAt)
            {
                _nextTickAt = Math.Max(_nextTickAt, _eventOptionsReadyAt);
                SetStage(
                    AutomationStage.ManagingEvent,
                    string.Equals(_pendingEventPanel, "RepairUI", StringComparison.OrdinalIgnoreCase)
                        ? "修整界面正在播放入场动画，等待界面稳定。"
                        : "轨神事件正在播放入场动画，等待游戏生成可选项。");
                return true;
            }

            JObject eventResult = _bridge.Invoke(
                "queryEventOptions",
                JObject.FromObject(new { panel = _pendingEventPanel }));
            switch (RuntimeResultInspector.ClassifyReadOnly(eventResult))
            {
                case RuntimeResultDisposition.Pending:
                    SetStage(AutomationStage.ManagingEvent, Message(eventResult));
                    return true;
                case RuntimeResultDisposition.Failure:
                    RegisterFailure("命令 queryEventOptions 失败：" + Message(eventResult));
                    return true;
            }

            if (HandleWaveFunctionOptionSettlementFromOptions(eventResult))
            {
                return true;
            }

            if (TryWaitForEventOptions(eventResult, _pendingEventPanel))
            {
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

        if (_bridge.TryGetWavePulse(out bool pulseInWave, out bool pulseGameOver, out int pulseRemaining) &&
            (pulseInWave || pulseGameOver))
        {
            CompleteWaveFunctionOptionSettlementFromWavePulse(pulseInWave, pulseGameOver);
            _mapSelectionPending = false;
            _mapSelectionPendingAt = -1f;
            ResetEventOptionObservation();
            return HandleWaveObservation(pulseInWave, pulseGameOver, pulseRemaining, null);
        }

        if (!TryQueryAdaptiveWaveState(
                "queryWave",
                AutomationStage.SelectingRoute,
                out JObject transitionResult,
                out JObject state))
        {
            return true;
        }

        if (_freshFullWaveQueryIssued)
        {
            ScheduleContinuationFrame();
            SetStage(
                AutomationStage.SelectingRoute,
                "已读取地图节点提交后的波次状态；下一帧再处理该状态。");
            return true;
        }

        JArray blockers = state["blockers"] as JArray ?? new JArray();
        if (HandleWaveFunctionOptionSettlementFromWaveState(state, blockers))
        {
            return true;
        }
        bool inWave = state["isInWaving"]?.Value<bool>() == true;
        ObserveWaveTransition(inWave);

        if (HasBlocker(blockers, "gameOver") ||
            GameOutcomeObserver.Outcome is AutomationOutcome.Victory or AutomationOutcome.Defeat)
        {
            _mapSelectionPending = false;
            _mapSelectionPendingAt = -1f;
            ResetEventOptionObservation();
            TickSettlement();
            return true;
        }

        if (inWave)
        {
            _mapSelectionPending = false;
            _mapSelectionPendingAt = -1f;
            ResetEventOptionObservation();
            return HandleWaveObservation(
                true,
                false,
                state.SelectToken("enemy.remaining")?.Value<int?>() ?? -1,
                transitionResult);
        }

        string? panel = HasBlocker(blockers, "EventUI")
            ? "EventUI"
            : HasBlocker(blockers, "RepairUI")
                ? "RepairUI"
                : null;
        if (panel != null)
        {
            bool firstObservation = !string.Equals(
                _pendingEventPanel,
                panel,
                StringComparison.OrdinalIgnoreCase);
            _pendingEventPanel = panel;
            if (firstObservation)
            {
                float now = Time.realtimeSinceStartup;
                _mapSelectionPendingAt = now;
                _eventOptionsReadyAt = now + (string.Equals(panel, "EventUI", StringComparison.OrdinalIgnoreCase)
                    ? EventOptionGenerationDelaySeconds
                    : RepairPanelAnimationSeconds);
                _eventOptionSelectionReadyAt = -1f;
                _eventOptionsFingerprint = string.Empty;
                AddTimeline(
                    "observation",
                    string.Equals(panel, "EventUI", StringComparison.OrdinalIgnoreCase)
                        ? "已观察到轨神事件面板；等待入场动画和选项生成完成。"
                        : "已观察到修整面板；等待入场动画完成。");
            }

            if (Time.realtimeSinceStartup < _eventOptionsReadyAt)
            {
                _nextTickAt = Math.Max(_nextTickAt, _eventOptionsReadyAt);
                SetStage(
                    AutomationStage.ManagingEvent,
                    string.Equals(panel, "EventUI", StringComparison.OrdinalIgnoreCase)
                        ? "轨神事件正在播放入场动画，等待游戏生成可选项。"
                        : "修整界面正在播放入场动画，等待界面稳定。");
                return true;
            }

            _nextTickAt = Time.realtimeSinceStartup + BattleTacticFrameDelaySeconds;
            SetStage(AutomationStage.ManagingEvent, "事件界面已打开，准备读取可用选项。");
            return true;
        }

        if (state["canStartWave"]?.Value<bool>() == true)
        {
            _mapSelectionPending = false;
            _mapSelectionPendingAt = -1f;
            ResetEventOptionObservation();
            AddTimeline("route", "地图节点已稳定提交，返回完整准备流程。");
            return false;
        }

        if (blockers.Count > 0 || state["canSelectNextNode"]?.Value<bool>() == true)
        {
            _mapSelectionPending = false;
            _mapSelectionPendingAt = -1f;
            ResetEventOptionObservation();
            return true;
        }

        if (_mapSelectionPendingAt >= 0f &&
            Time.realtimeSinceStartup - _mapSelectionPendingAt >= MapSelectionTransitionTimeoutSeconds)
        {
            _mapSelectionPending = false;
            _mapSelectionPendingAt = -1f;
            ResetEventOptionObservation();
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
                if (!TryInvokeOptionalReadOnly("queryWaveThreats", null, out JObject threats))
                {
                    _nextBattleTacticCycleAt = Time.realtimeSinceStartup + BattleTacticRetryDelaySeconds;
                    _battleTacticStep = BattleTacticStep.Complete;
                    return;
                }

                _battleThreats = threats;
                _battleConfirmationArguments = BuildThreatWorldArguments(threats);
                _battleTacticStep = _battleDisposableUsedThisWave || _battleDisposableUnavailableThisWave
                    ? BattleTacticStep.QueryRail
                    : BattleTacticStep.QueryDisposable;
                return;

            case BattleTacticStep.QueryDisposable:
                if (!TryInvokeOptionalReadOnly("queryDisposable", null, out JObject disposable))
                {
                    _battleDisposableUnavailableThisWave = true;
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
                    _battleDisposableUnavailableThisWave = true;
                    _battleTacticStep = BattleTacticStep.QueryRail;
                    return;
                }

                if (!string.Equals(disposableAction.Command, "useDisposable", StringComparison.OrdinalIgnoreCase))
                {
                    _battleDisposableUnavailableThisWave = true;
                    _battleTacticStep = BattleTacticStep.QueryRail;
                    return;
                }

                _ownedDisposableEnum = ResolveSelectedDisposableEnum(disposable, disposableAction.Arguments);
                if (string.IsNullOrWhiteSpace(_ownedDisposableEnum))
                {
                    _battleDisposableUnavailableThisWave = true;
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

                if (!TryInvokeOptionalReadOnly("queryDisposable", null, out JObject useCheck) ||
                    State(useCheck)["isInPreview"]?.Value<bool>() == true)
                {
                    SetStage(AutomationStage.Battle, "玩家已开始消耗品预览；已放弃 AutoPlayer 待执行的道具操作。");
                    ClearOwnedDisposable();
                    _battleTacticStep = BattleTacticStep.Complete;
                    return;
                }

                bool used = TryExecuteActiveBattleAction(useAction, out JObject useResult);
                if (_runState != AutoPlayerRunState.Running)
                {
                    return;
                }

                RuntimeResultDisposition useDisposition = RuntimeResultInspector.Classify(useResult);
                if (used || useDisposition == RuntimeResultDisposition.Pending)
                {
                    _battleDisposableUsedThisWave = true;
                    JObject usedState = State(useResult);
                    if (usedState["isInPreview"]?.Value<bool>() == true)
                    {
                        ResetOwnedPreviewCancellationTracking();
                        _ownedDisposableInteractionInstanceId =
                            usedState["interactionInstanceId"]?.Value<int?>() ?? 0;
                        if (_ownedDisposableInteractionInstanceId == 0)
                        {
                            FaultRequiringProcessRestart(
                                "自动使用消耗品后仍存在预览，但运行时没有返回可验证的交互身份；" +
                                "为避免取消玩家刚切换出的预览，已停止写操作且必须重启游戏进程。");
                            return;
                        }

                        _battleTacticStep = BattleTacticStep.QueryDisposablePreview;
                    }
                    else if (useDisposition == RuntimeResultDisposition.Pending)
                    {
                        FaultRequiringProcessRestart(
                            "自动使用消耗品的命令仍在处理中，但响应没有返回可验证的预览身份；" +
                            "无法安全判断稍后是否会创建交互，必须重启游戏进程。");
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
                if (!TryInvokeOptionalReadOnly("queryDisposable", null, out JObject preview))
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
                    if (!TryReadWorldPosition(
                            _battleConfirmationArguments,
                            out double threatWorldX,
                            out double threatWorldY,
                            out double threatWorldZ))
                    {
                        AddWarning("当前威胁缺少可验证的世界坐标，已取消本次格子型消耗品预览。");
                        _battleTacticStep = BattleTacticStep.CancelDisposable;
                        return;
                    }

                    if (!_battleLiveDisposableGridProbe.TryInitialize(
                            threatWorldX,
                            threatWorldY,
                            threatWorldZ,
                            out string probeError))
                    {
                        AddWarning("无法启动战斗格子增量探测，已取消本次消耗品预览：" + probeError);
                        _battleTacticStep = BattleTacticStep.CancelDisposable;
                        return;
                    }

                    _battleTacticStep = BattleTacticStep.ProbeDisposableGrid;
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

            case BattleTacticStep.ProbeDisposableGrid:
                IncrementalGridProbeResult battleGridProbe = _battleLiveDisposableGridProbe.ProbeNext();
                if (battleGridProbe.Status == IncrementalGridProbeStatus.Probing)
                {
                    SetStage(AutomationStage.Battle, battleGridProbe.Detail);
                    return;
                }

                if (battleGridProbe.Status != IncrementalGridProbeStatus.Found ||
                    !battleGridProbe.Grid.HasValue)
                {
                    AddWarning("战斗格子增量探测未找到安全落点，已取消本次消耗品预览：" +
                               battleGridProbe.Detail);
                    _battleTacticStep = BattleTacticStep.CancelDisposable;
                    return;
                }

                AutoPlayerGrid battleGrid = battleGridProbe.Grid.Value;
                AutomationAction? gridAction = _battleDecisionEngine.Decide(
                    new BattleDecisionContext
                    {
                        DisposablePhase = BattleDisposablePhase.Confirming,
                        AllowDisposableUse = false,
                        AllowVehicleReinforcement = false,
                        DisposableConfirmationArguments = new JObject
                        {
                            ["grid"] = JObject.FromObject(new { x = battleGrid.X, y = battleGrid.Y })
                        }
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
                JObject confirmCheck = _openingDefenseInteractionGuard.Query();
                if (RuntimeResultInspector.ClassifyReadOnly(confirmCheck) != RuntimeResultDisposition.Success)
                {
                    _battlePendingAction = confirm;
                    _battleDisposableSettlementObservationAttempts++;
                    if (_battleDisposableSettlementObservationAttempts >=
                        MaxOwnedPreviewReleaseVerificationAttempts)
                    {
                        Fault(
                            "确认战斗消耗品前连续无法只读复核预览所有权；" +
                            "正在保留身份并进入安全清理故障流程。");
                    }
                    else
                    {
                        SetStage(
                            AutomationStage.Battle,
                            $"确认战斗消耗品前的所有权查询暂时失败" +
                            $"（{_battleDisposableSettlementObservationAttempts}/" +
                            $"{MaxOwnedPreviewReleaseVerificationAttempts}），不会清除身份或发送写命令。");
                    }
                    return;
                }

                _battleDisposableSettlementObservationAttempts = 0;
                if (!IsOwnedDisposablePreview(confirmCheck))
                {
                    SetStage(AutomationStage.Battle, "消耗品预览实例已变化；AutoPlayer 不会确认玩家的操作。");
                    ClearOwnedDisposable();
                    _battleTacticStep = BattleTacticStep.Complete;
                    return;
                }

                JObject confirmationResult = new();
                bool confirmationAccepted = confirm != null &&
                                            TryExecuteActiveBattleAction(
                                                confirm,
                                                out confirmationResult);
                if (_runState != AutoPlayerRunState.Running)
                {
                    return;
                }

                RuntimeResultDisposition confirmationDisposition =
                    RuntimeResultInspector.Classify(confirmationResult);
                _ownedPreviewConfirmationOutcomeUncertain =
                    confirmationDisposition == RuntimeResultDisposition.Pending;
                if (confirm != null &&
                    (confirmationAccepted ||
                     confirmationDisposition == RuntimeResultDisposition.Pending))
                {
                    _battleDisposableSettlementObservationAttempts = 0;
                    _battleTacticStep = BattleTacticStep.WaitForDisposableSettlement;
                    SetStage(
                        AutomationStage.Battle,
                        "消耗品确认命令已提交；正在等待预览和生成动画完全退出后再执行其他战术。");
                }
                else
                {
                    _battleTacticStep = BattleTacticStep.CancelDisposable;
                }
                return;

            case BattleTacticStep.WaitForDisposableSettlement:
                if (!TryInvokeOptionalReadOnly("queryDisposable", null, out JObject settledDisposable))
                {
                    _battleDisposableSettlementObservationAttempts++;
                    if (_battleDisposableSettlementObservationAttempts >=
                        MaxOwnedPreviewReleaseVerificationAttempts)
                    {
                        Fault(
                            "消耗品确认后连续无法只读确认预览是否退出；" +
                            "正在按已记录身份清理，本轮会以可恢复故障停止。");
                        return;
                    }

                    SetStage(
                        AutomationStage.Battle,
                        $"消耗品确认后的预览状态暂时不可读" +
                        $"（{_battleDisposableSettlementObservationAttempts}/" +
                        $"{MaxOwnedPreviewReleaseVerificationAttempts}），下一帧继续确认。");
                    return;
                }

                JObject settledDisposableState = State(settledDisposable);
                if (IsOwnedDisposablePreview(settledDisposable))
                {
                    _battleDisposableSettlementObservationAttempts++;
                    if (_battleDisposableSettlementObservationAttempts >=
                        MaxDisposableSettlementObservationAttempts)
                    {
                        Fault(
                            "消耗品确认后预览和生成动画长时间未退出；" +
                            "正在按已记录身份清理，本轮会以可恢复故障停止。");
                        return;
                    }

                    SetStage(
                        AutomationStage.Battle,
                        "消耗品生成动画仍在播放；保留当前预览身份，不发送新的战术写命令。");
                    return;
                }

                bool settlementPlayerPreviewActive =
                    settledDisposableState["isInPreview"]?.Value<bool>() == true;
                _ownedPreviewConfirmationOutcomeUncertain = false;
                ClearOwnedDisposable();
                _battleTacticStep = settlementPlayerPreviewActive ||
                                    _battleWaveEndPendingPreviewRelease
                    ? BattleTacticStep.Complete
                    : BattleTacticStep.QueryRail;
                SetStage(
                    AutomationStage.Battle,
                    settlementPlayerPreviewActive
                        ? "自动消耗品预览已经退出；检测到玩家的新预览，本轮战术不会接管该交互。"
                        : "自动消耗品预览和生成动画已经完全退出，继续执行本轮战术。");
                return;

            case BattleTacticStep.CancelDisposable:
                if (!TryInvokeOptionalReadOnly("queryDisposable", null, out JObject cancelCheck))
                {
                    _battleDisposableSettlementObservationAttempts++;
                    if (_battleDisposableSettlementObservationAttempts >=
                        MaxOwnedPreviewReleaseVerificationAttempts)
                    {
                        Fault(
                            "取消战斗消耗品前连续无法只读复核预览所有权；" +
                            "正在保留身份并进入安全清理故障流程。");
                    }
                    else
                    {
                        SetStage(
                            AutomationStage.Battle,
                            $"取消战斗消耗品前的所有权查询暂时失败" +
                            $"（{_battleDisposableSettlementObservationAttempts}/" +
                            $"{MaxOwnedPreviewReleaseVerificationAttempts}），不会继续其他战术或发送取消命令。");
                    }
                    return;
                }

                _battleDisposableSettlementObservationAttempts = 0;
                if (!IsOwnedDisposablePreview(cancelCheck))
                {
                    bool playerPreviewActive = State(cancelCheck)["isInPreview"]?.Value<bool>() == true;
                    ClearOwnedDisposable();
                    _battleTacticStep = playerPreviewActive ||
                                        _battleWaveEndPendingPreviewRelease
                        ? BattleTacticStep.Complete
                        : BattleTacticStep.QueryRail;
                    return;
                }

                if (_bridge.HasCommand("cancelDisposable"))
                {
                    MarkOwnedPreviewCancellationIssued();
                    ExecuteWithResult(
                        new AutomationAction(
                            "cancelDisposable",
                            JObject.FromObject(new
                            {
                                disposableEnum = _ownedDisposableEnum,
                                interactionInstanceId = _ownedDisposableInteractionInstanceId
                            }),
                        AutomationStage.Battle,
                            "取消无法安全确认的消耗品预览，恢复游戏输入。"),
                        optional: true,
                        out JObject battleCancellationResult);
                    ObserveOwnedPreviewCancellationResult(battleCancellationResult);
                    if (_runState != AutoPlayerRunState.Running)
                    {
                        return;
                    }

                    _battleDisposableSettlementObservationAttempts = 0;
                    _battleTacticStep = BattleTacticStep.VerifyDisposableCancellation;
                    SetStage(
                        AutomationStage.Battle,
                        "战斗道具取消命令只发送一次；下一帧只读验证预览是否退出。");
                    return;
                }

                Fault("当前游戏构建缺少取消道具预览命令，无法安全释放本次战斗交互。");
                return;

            case BattleTacticStep.VerifyDisposableCancellation:
                if (!TryInvokeOptionalReadOnly(
                        "queryDisposable",
                        null,
                        out JObject cancellationVerification))
                {
                    _battleDisposableSettlementObservationAttempts++;
                    if (_battleDisposableSettlementObservationAttempts >=
                        MaxOwnedPreviewReleaseVerificationAttempts)
                    {
                        Fault(
                            "取消战斗道具后连续无法只读确认预览是否退出；" +
                            "正在保留身份并进入安全清理故障流程。");
                    }
                    return;
                }

                if (IsOwnedDisposablePreview(cancellationVerification))
                {
                    _battleDisposableSettlementObservationAttempts++;
                    if (_battleDisposableSettlementObservationAttempts >=
                        MaxOwnedPreviewReleaseVerificationAttempts)
                    {
                        Fault(
                            "战斗道具取消命令只发送一次，但相同预览仍未退出；" +
                            "正在进入安全清理故障流程。");
                    }
                    else
                    {
                        SetStage(
                            AutomationStage.Battle,
                            $"正在等待战斗道具预览退出" +
                            $"（{_battleDisposableSettlementObservationAttempts}/" +
                            $"{MaxOwnedPreviewReleaseVerificationAttempts}），不会重复发送取消命令。");
                    }
                    return;
                }

                bool playerPreviewAfterBattleCancellation =
                    State(cancellationVerification)["isInPreview"]?.Value<bool>() == true;
                ClearOwnedDisposable();
                _battleTacticStep = playerPreviewAfterBattleCancellation ||
                                    _battleWaveEndPendingPreviewRelease
                    ? BattleTacticStep.Complete
                    : BattleTacticStep.QueryRail;
                return;

            case BattleTacticStep.QueryRail:
                if (_battleThreats == null || !TryInvokeOptionalReadOnly("queryRail", null, out JObject rail))
                {
                    _battleTacticStep = BattleTacticStep.Complete;
                    return;
                }
                _battleRail = rail;
                TryInvokeOptionalReadOnly("queryTrain", null, out _battleTrain);
                _battleTacticStep = TryBeginBattleSpecialStationMaintenance()
                    ? BattleTacticStep.RunSpecialStationMaintenance
                    : BattleTacticStep.Complete;
                return;

            case BattleTacticStep.RunSpecialStationMaintenance:
                if (!_defenseBattleSpecialMoveOnly)
                {
                    _battleTacticStep = BattleTacticStep.Complete;
                    return;
                }

                TryMaintainDefense();
                return;

            case BattleTacticStep.Complete:
            default:
                return;
        }
    }

    private bool TryMaintainDefense()
    {
        if (_defensePendingDisposableMutationGuard.IsArmed)
        {
            _defenseMaintenanceRequested = true;
            _defenseMaintenanceReady = true;
            _defenseMaintenanceStep = DefenseMaintenanceStep.WaitForExpansionAttributeSettlement;
            HandlePendingDefenseDisposableMutation();
            return true;
        }

        if (!_defenseMaintenanceRequested || !_defenseMaintenanceReady) return false;
        if (!_defenseStructuralMutationGuard.IsArmed &&
            _bridge.TryGetWavePulse(out bool defenseWaveActive, out bool defenseGameOver, out _))
        {
            if (_defenseBattleSpecialMoveOnly && (!defenseWaveActive || defenseGameOver))
            {
                FinishDefenseMaintenance(
                    "波次已经结束；本波能量/特殊弹射点移动计划尚未写入，已安全放弃。",
                    warning: true);
                return true;
            }

            if (!_defenseBattleSpecialMoveOnly && (defenseWaveActive || defenseGameOver))
            {
                if (_defenseAttributeInteractionInstanceId != 0)
                {
                    if (!TryInvokeOptionalReadOnly(
                            "queryDisposable",
                            null,
                            out JObject interruptedAttributePreview) ||
                        _battleDecisionEngine.IsOwnedExpansionPreview(
                            interruptedAttributePreview,
                            _defenseAttributeInteractionInstanceId,
                            _defensePlacementDisposableEnum,
                            requireGridInteraction: false))
                    {
                        Fault(
                            "波次或结算开始时仍无法证明本次扩建道具预览已经退出；" +
                            "正在按已记录身份清理，本轮会以可恢复故障停止。");
                        return true;
                    }
                }

                FinishDefenseMaintenance(
                    "波次或结算已开始，已停止防线维护，不再发送轨道或道具命令。",
                    warning: true);
                return true;
            }
        }

        if (!_defenseBattleSpecialMoveOnly &&
            (!_bridge.HasCommand("queryTrain") ||
             !_bridge.HasCommand("queryVehicle") ||
             !_bridge.HasCommand("moveVehicleInTrain")))
        {
            FinishDefenseMaintenance("当前游戏构建缺少战车自动编列接口，已保留现有防线继续游玩。", warning: true);
            return true;
        }

        switch (_defenseMaintenanceStep)
        {
            case DefenseMaintenanceStep.QueryTrain:
                if (!TryInvokeOptionalReadOnly("queryTrain", null, out JObject train))
                {
                    FinishDefenseMaintenance("无法读取现有车列，已跳过本轮防线维护。", warning: true);
                    return true;
                }

                _defenseTrain = train;
                _defenseMaintenanceStep = DefenseMaintenanceStep.QueryVehicle;
                ScheduleDefenseMaintenanceStep("正在检查现有车列容量。");
                return true;

            case DefenseMaintenanceStep.QueryVehicle:
                if (!TryInvokeOptionalReadOnly("queryVehicle", null, out JObject vehicles))
                {
                    FinishDefenseMaintenance("无法读取背包战车，已跳过本轮防线维护。", warning: true);
                    return true;
                }

                _defenseVehicle = vehicles;
                bool hasPotentialMerge = _mergeAutomationPlanner.HasPotentialMergeCandidate(vehicles);
                if (!_mergeExhausted && hasPotentialMerge)
                {
                    if (_mergePassCount >= MaxMergePassesPerMaintenance)
                    {
                        _mergeExhausted = true;
                        AddWarning("本轮防线维护已达到自动合成次数上限，剩余组合留到下一轮维护处理。");
                    }
                    else if (!HasMergeAutomationContract())
                    {
                        _mergeExhausted = true;
                        AddWarning("检测到可合成战车，但当前游戏构建缺少完整的玩家等价合成接口；已跳过自动合成。");
                    }
                    else if (!TryInvokeOptionalReadOnly("queryMergeUiState", null, out JObject mergeUiState))
                    {
                        _mergeExhausted = true;
                        AddWarning("无法确认游戏原生合成面板当前是否关闭；为避免接管未知界面，已跳过自动合成。");
                    }
                    else if (State(mergeUiState)["mergeOpen"]?.Value<bool>() == true)
                    {
                        PauseForRecoverableRuntimeState(
                            "开始自动合成前发现游戏原生合成面板已经打开。自动游玩已暂停且不要求重启；请关闭该面板后继续。" );
                        return true;
                    }
                    else
                    {
                        _mergeAutomationState = MergeAutomationState.Initial;
                        _mergeAutomationQueryResult = null;
                        _mergeSettlementWaitStartedAt = -1f;
                        _mergeSettlementObservedAt = -1f;
                        _mergeSettlementQueryFailures = 0;
                        _mergePassStartedAt = Time.realtimeSinceStartup;
                        _mergeRecoveryReason = string.Empty;
                        _mergeRecoveryAttempts = 0;
                        _defenseMaintenanceStep = DefenseMaintenanceStep.RunMerge;
                        ScheduleDefenseMaintenanceStep("发现符合玩家公式的同型战车，准备打开游戏原生合成面板。");
                        return true;
                    }
                }
                else if (!hasPotentialMerge)
                {
                    _mergeExhausted = true;
                }

                AutomationAction? reinforcement = _battleDecisionEngine.Decide(
                    new BattleDecisionContext
                    {
                        AllowDisposableUse = false,
                        AllowVehicleReinforcement = true
                    },
                    null,
                    null,
                    _defenseTrain,
                    vehicles);
                if (reinforcement != null)
                {
                    _defensePendingAction = reinforcement;
                    _defenseMaintenanceStep = DefenseMaintenanceStep.MoveVehicle;
                    ScheduleDefenseMaintenanceStep(reinforcement.Reason);
                    return true;
                }

                _defenseNeedsNewLoopExpansion =
                    _battleDecisionEngine.NeedsDefenseExpansion(_defenseTrain, vehicles);

                if (_defenseNeedsNewLoopExpansion && _defenseExpansionSuspended)
                {
                    FinishDefenseMaintenance(
                        "本局此前的扩建已提交但未完成车列验证；为避免重复创建轨道，本局不再自动扩建。",
                        warning: true);
                    return true;
                }

                if (!_bridge.HasCommand("queryCatapults") ||
                    !_bridge.HasCommand("queryRail") ||
                    !_bridge.HasCommand("previewRailPath") ||
                    (!_bridge.HasCommand("insertPointFromLine") &&
                     (!_defenseNeedsNewLoopExpansion ||
                      !_bridge.HasCommand("drawRailPath") ||
                      !_bridge.HasCommand("placeVehicleOnLine"))))
                {
                    FinishDefenseMaintenance(
                        "当前游戏构建缺少玩家等价的轨道扩建接口；已保留现有防线继续游玩。",
                        warning: true);
                    return true;
                }

                _defenseTrainCountBeforeExpansion = CountTrainEntries(_defenseTrain);
                _defenseMaintenanceStep = DefenseMaintenanceStep.QueryCatapults;
                ScheduleDefenseMaintenanceStep(
                    _defenseNeedsNewLoopExpansion
                        ? "现有车列已满，正在查找未占用的合法轨道站点。"
                        : "正在读取未占用站点，准备按回转周期计算扩轨收益。");
                return true;

            case DefenseMaintenanceStep.RunMerge:
                RunMergeAutomationStep();
                return true;

            case DefenseMaintenanceStep.ObserveMergeSettlement:
                ObserveMergeSettlement();
                return true;

            case DefenseMaintenanceStep.ConfirmMergeSettlement:
                ConfirmMergeSettlement();
                return true;

            case DefenseMaintenanceStep.CloseMergePanel:
                CloseMergePanel();
                return true;

            case DefenseMaintenanceStep.ReconcileMerge:
                ReconcileMergeState();
                return true;

            case DefenseMaintenanceStep.QueryCatapults:
                if (!TryInvokeOptionalReadOnly("queryCatapults", null, out JObject catapults))
                {
                    FinishDefenseMaintenance("无法读取可用轨道站点，已跳过本轮扩建。", warning: true);
                    return true;
                }

                _defenseCatapults = catapults;
                _defenseExpansionAction = _defenseNeedsNewLoopExpansion
                    ? _battleDecisionEngine.DecideDefenseExpansion(
                        _defenseTrain,
                        _defenseVehicle,
                        catapults,
                        _rejectedDefenseExpansionPaths)
                    : null;
                if (_defenseExpansionAction == null)
                {
                    _defensePlacementDisposableEnum =
                        _defenseNeedsNewLoopExpansion
                            ? _battleDecisionEngine.RequiredExpansionDisposable(catapults)
                            : string.Empty;
                    if (!string.IsNullOrWhiteSpace(_defensePlacementDisposableEnum))
                    {
                        if (!_bridge.HasCommand("queryDisposable") ||
                            !_bridge.HasCommand("confirmDisposableGrid") ||
                            !_bridge.HasCommand("cancelDisposable"))
                        {
                            FinishDefenseMaintenance(
                                "当前缺少组成新闭环的站点，但游戏构建缺少弹射点道具放置接口；已跳过本轮扩建。",
                                warning: true);
                            return true;
                        }

                        _defensePlacementCountBefore =
                            _battleDecisionEngine.CountAvailableExpansionStations(
                                catapults,
                                _defensePlacementDisposableEnum);
                        _defenseAttributeCountBeforePlacement = _defensePlacementCountBefore;
                        _defenseMaintenanceStep = DefenseMaintenanceStep.QueryExpansionAttributeDisposable;
                        ScheduleDefenseMaintenanceStep(
                            $"缺少 {_defensePlacementDisposableEnum} 站点，正在检查左下角道具库存。");
                        return true;
                    }

                    if (!_defenseBattleSpecialMoveOnly && !_bridge.HasCommand("insertPointFromLine"))
                    {
                        FinishDefenseMaintenance(
                            _defenseNeedsNewLoopExpansion
                                ? "没有足够站点组成新闭环，且当前构建不支持已有轨道插点。"
                                : "当前构建不支持已有轨道插点。",
                            warning: true);
                        return true;
                    }

                    _defenseMaintenanceStep = DefenseMaintenanceStep.QueryRailExpansionCandidates;
                    ScheduleDefenseMaintenanceStep("正在读取现有轨道的站点数和回转周期。");
                    return true;
                }

                _defenseMaintenanceStep = DefenseMaintenanceStep.PreviewExpansion;
                ScheduleDefenseMaintenanceStep("正在只读预览额外闭环，确认拖线不会污染当前防线。");
                return true;

            case DefenseMaintenanceStep.QueryRailExpansionCandidates:
                if (!TryInvokeOptionalReadOnly("queryRail", null, out JObject expansionRailState))
                {
                    FinishDefenseMaintenance("无法读取轨道回转周期，已跳过本轮扩轨。", warning: true);
                    return true;
                }

                _defenseRailExpansionBaseline = expansionRailState;
                _defenseRailMaintenanceLayoutFingerprint =
                    BuildDefenseRailMaintenanceLayoutFingerprint(
                        expansionRailState,
                        _defenseCatapults,
                        _defenseBattleSpecialMoveOnly ? _battleTrain : _defenseTrain);
                if (_defenseBattleSpecialMoveOnly &&
                    !string.IsNullOrWhiteSpace(_defenseRailMaintenanceStableLayoutFingerprint) &&
                    string.Equals(
                        _defenseRailMaintenanceStableLayoutFingerprint,
                        _defenseRailMaintenanceLayoutFingerprint,
                        StringComparison.Ordinal))
                {
                    FinishDefenseMaintenance(
                        "轨道与可移动弹射点布局没有变化；保持当前最优结果并等待布局变化后再评估。",
                        warning: false);
                    return true;
                }

                if (_bridge.HasCommand("queryMovableStationState") &&
                    _bridge.HasCommand("queryDisposableGridOptions") &&
                    _bridge.HasCommand("startStationMove") &&
                    _bridge.HasCommand("confirmStationMoveGrid") &&
                    _bridge.HasCommand("cancelDisposable"))
                {
                    StationSpacingRules spacingRules = default;
                    if (!_defenseStationGridProbe.TryReadSpacingRules(out spacingRules, out string spacingError))
                    {
                        AddWarning("无法读取当前关卡弹射点最小间距；本轮不会用猜测间距移动站点：" + spacingError);
                    }
                    foreach (RailStationMoveCandidate moveCandidate in
                             _railExpansionPlanner.BuildExistingSpecialMoveCandidates(
                                 expansionRailState,
                                 _defenseCatapults,
                                 spacingRules))
                    {
                        string moveCandidateFingerprint =
                            BuildDefenseRailMoveCandidateFingerprint(moveCandidate);
                        if (_defenseRailMaintenanceActionFingerprints.Contains(
                                moveCandidateFingerprint))
                        {
                            continue;
                        }

                        if (_defenseStationGridProbe.TryInitializeMove(
                                moveCandidate,
                                out string moveGridInitializationError))
                        {
                            _defenseMoveGridInitializationRetryAttempts = 0;
                            _defenseSpecialMoveCandidate = moveCandidate;
                            _defenseMaintenanceStep = DefenseMaintenanceStep.ProbeSpecialStationMoveGrid;
                            ScheduleDefenseMaintenanceStep(
                                $"正在为能量/特殊弹射点 {moveCandidate.StationName} 分帧检查周向覆盖或 N/T 更优的合法位置。");
                            return true;
                        }

                        if (_defenseStationGridProbe.InitializationFailure !=
                            DefenseStationGridProbeInitializationFailure.NoBeneficialCandidate)
                        {
                            HandleTransientMoveGridInitializationFailure(
                                moveGridInitializationError);
                            return true;
                        }

                        _defenseRailMaintenanceActionFingerprints.Add(
                            moveCandidateFingerprint);
                    }
                }

                if (_defenseBattleSpecialMoveOnly)
                {
                    _defenseRailRebuildCandidates =
                        _railRebuildPlanner.BuildSpecialInsertionCandidates(
                            expansionRailState,
                            _battleTrain,
                            _defenseCatapults);
                    if (_bridge.HasCommand("deleteLinePoint") &&
                        _defenseRailRebuildCandidates.Count > 0)
                    {
                        _defenseRailRebuildScores.Clear();
                        _defenseRailRebuildCandidateIndex = 0;
                        _defenseMaintenanceStep = DefenseMaintenanceStep.PreviewBattleSpecialRebuildCandidate;
                        ScheduleDefenseMaintenanceStep(
                            $"发现 {_defenseRailRebuildCandidates.Count} 个特殊中继站玩家重连候选；将逐帧比较真实回转周期。");
                        return true;
                    }
                    if (_bridge.HasCommand("queryDisposable") &&
                        _bridge.HasCommand("confirmDisposableGrid") &&
                        _bridge.HasCommand("cancelDisposable"))
                    {
                        _defensePlacementDisposableEnum = "__runtime_movable_station__";
                        _defenseMaintenanceStep = DefenseMaintenanceStep.QueryExpansionAttributeDisposable;
                        ScheduleDefenseMaintenanceStep(
                            "已有可移动站点没有更优位置；正在从当前背包运行时行为发现可放置特殊站点。");
                        return true;
                    }
                    MarkDefenseRailMaintenanceStable();
                    FinishDefenseMaintenance("本波没有可安全改善周向覆盖或站点触发率 N/T 的能量/特殊弹射点。", warning: false);
                    return true;
                }

                _defenseRailInsertionCandidates = _railExpansionPlanner.BuildCandidates(
                    expansionRailState,
                    _defenseCatapults,
                    _defenseTrain);
                _defenseRailInsertionScores.Clear();
                _defenseRailInsertionPreviewIndex = 0;
                if (_defenseRailInsertionCandidates.Count == 0)
                {
                    if (!_defenseNeedsNewLoopExpansion &&
                        _battleDecisionEngine.CountAvailableExpansionStations(
                            _defenseCatapults,
                            "FreePoint") == 0 &&
                        _bridge.HasCommand("queryDisposable") &&
                        _bridge.HasCommand("confirmDisposableGrid") &&
                        _bridge.HasCommand("cancelDisposable"))
                    {
                        _defensePlacementDisposableEnum = "FreePoint";
                        _defensePlacementCountBefore = 0;
                        _defenseAttributeCountBeforePlacement = 0;
                        _defenseMaintenanceStep = DefenseMaintenanceStep.QueryExpansionAttributeDisposable;
                        ScheduleDefenseMaintenanceStep(
                            "没有未占用普通弹射点；正在检查左下角 FreePoint 道具以补足扩轨闭环。");
                        return true;
                    }

                    FinishDefenseMaintenance("没有携带可验证车头身份的合法扩轨候选。");
                    return true;
                }

                _defenseMaintenanceStep = DefenseMaintenanceStep.PreviewRailInsertionCandidate;
                ScheduleDefenseMaintenanceStep(
                    $"准备逐帧预览 {_defenseRailInsertionCandidates.Count} 个扩轨候选。");
                return true;

            case DefenseMaintenanceStep.PreviewBattleSpecialRebuildCandidate:
                if (_defenseRailRebuildCandidateIndex >= _defenseRailRebuildCandidates.Count)
                {
                    _defenseMaintenanceStep = DefenseMaintenanceStep.SelectBattleSpecialRebuild;
                    ScheduleDefenseMaintenanceStep("特殊中继站重连候选预览完成；正在选择站点触发率严格改善的方案。");
                    return true;
                }
                RailRebuildSnapshot battleRebuildCandidate =
                    _defenseRailRebuildCandidates[_defenseRailRebuildCandidateIndex++];
                AutomationAction battleRebuildPreview = _railRebuildPlanner.BuildPreviewAction(battleRebuildCandidate);
                if (TryInvokeOptionalReadOnly(
                        battleRebuildPreview.Command,
                        battleRebuildPreview.Arguments,
                        out JObject battleRebuildPreviewResult) &&
                    _railRebuildPlanner.IsLegalPreview(
                        battleRebuildPreviewResult,
                        battleRebuildCandidate,
                        out double battleRebuildCycle) &&
                    ImprovesRailTriggerRate(battleRebuildCandidate, battleRebuildCycle))
                {
                    _defenseRailRebuildScores.Add((battleRebuildCandidate, battleRebuildCycle));
                }
                ScheduleDefenseMaintenanceStep(
                    $"已预览 {_defenseRailRebuildCandidateIndex}/{_defenseRailRebuildCandidates.Count} 个特殊站点重连候选。");
                return true;

            case DefenseMaintenanceStep.SelectBattleSpecialRebuild:
                (RailRebuildSnapshot Snapshot, double Cycle)? selectedBattleRebuild =
                    _defenseRailRebuildScores
                        .OrderBy(item => item.Cycle)
                        .ThenByDescending(item => item.Snapshot.OrderedLinePointInstanceIds.Count / item.Cycle)
                        .ThenBy(item => string.Join(",", item.Snapshot.OrderedLinePointInstanceIds))
                        .Select(item => ((RailRebuildSnapshot Snapshot, double Cycle)?)item)
                        .FirstOrDefault();
                if (!selectedBattleRebuild.HasValue)
                {
                    _defenseRailRebuildCandidates = Array.Empty<RailRebuildSnapshot>();
                    _defenseRailRebuildScores.Clear();
                    if (_bridge.HasCommand("queryDisposable") &&
                        _bridge.HasCommand("confirmDisposableGrid") &&
                        _bridge.HasCommand("cancelDisposable"))
                    {
                        _defensePlacementDisposableEnum = "__runtime_movable_station__";
                        _defenseMaintenanceStep = DefenseMaintenanceStep.QueryExpansionAttributeDisposable;
                        ScheduleDefenseMaintenanceStep(
                            "现有特殊站点没有触发率严格改善的重连方案；正在检查背包中的可移动特殊站点。");
                        return true;
                    }
                    MarkDefenseRailMaintenanceStable();
                    FinishDefenseMaintenance("本波没有站点触发率严格改善的特殊站点重连方案。", warning: false);
                    return true;
                }
                _defenseRailRebuildSnapshot = selectedBattleRebuild.Value.Snapshot;
                _defenseRailRebuildPreviewCycleSeconds = selectedBattleRebuild.Value.Cycle;
                _defenseRailRebuildRecoveryAttempted = false;
                _defenseRailRebuildExplicitPollution = false;
                _defenseSpecialMoveCandidate = null;
                _defenseRailRebuildCandidates = Array.Empty<RailRebuildSnapshot>();
                _defenseRailRebuildScores.Clear();
                _defenseMaintenanceStep = DefenseMaintenanceStep.DisconnectRailForRebuild;
                ScheduleDefenseMaintenanceStep(
                    $"已选择特殊中继站重连，预测周期 {_defenseRailRebuildPreviewCycleSeconds:0.###} 秒；下一帧从始发站断环。");
                return true;

            case DefenseMaintenanceStep.PreviewRailInsertionCandidate:
                if (_defenseRailInsertionPreviewIndex >= _defenseRailInsertionCandidates.Count)
                {
                    _defenseMaintenanceStep = DefenseMaintenanceStep.SelectRailInsertion;
                    ScheduleDefenseMaintenanceStep("扩轨候选预览完成，正在按站点触发率 N/T 排序。");
                    return true;
                }

                RailInsertionCandidate insertionCandidate =
                    _defenseRailInsertionCandidates[_defenseRailInsertionPreviewIndex++];
                if (TryInvokeOptionalReadOnly(
                        "previewRailPath",
                        insertionCandidate.PreviewArguments,
                        out JObject insertionPreview) &&
                    _railExpansionPlanner.TryScorePreview(
                        insertionCandidate,
                        insertionPreview,
                        out RailInsertionPreviewScore insertionScore))
                {
                    _defenseRailInsertionScores.Add(insertionScore);
                }

                ScheduleDefenseMaintenanceStep(
                    $"已预览 {_defenseRailInsertionPreviewIndex}/{_defenseRailInsertionCandidates.Count} 个扩轨候选。");
                return true;

            case DefenseMaintenanceStep.SelectRailInsertion:
                _defenseSelectedRailInsertion =
                    _railExpansionPlanner.SelectBest(
                        _defenseRailInsertionScores.Where(score =>
                            !_defenseRailMaintenanceActionFingerprints.Contains(
                                BuildDefenseRailInsertionActionFingerprint(score))));
                if (_defenseSelectedRailInsertion == null)
                {
                    MarkDefenseRailMaintenanceStable();
                    FinishDefenseMaintenance("没有候选能够修复周向覆盖，或在相同覆盖层级下提高站点触发率 N/T。");
                    return true;
                }

                _defenseRailMaintenanceActionFingerprints.Add(
                    BuildDefenseRailInsertionActionFingerprint(
                        _defenseSelectedRailInsertion));

                AddTimeline(
                    "rail-plan",
                    $"选定轨道 {_defenseSelectedRailInsertion.Candidate.RailInternalId}，" +
                    $"N={_defenseSelectedRailInsertion.Candidate.StationCount}，" +
                    $"T={_defenseSelectedRailInsertion.Candidate.CurrentLoopCycleSeconds:0.###} 秒，" +
                    $"预测 T'={_defenseSelectedRailInsertion.PredictedLoopCycleSeconds:0.###} 秒，" +
                    $"触发率变化 {_defenseSelectedRailInsertion.RelativeGain:P2}。");
                _defenseStructuralVerificationAttempts = 0;
                _defenseMaintenanceStep = DefenseMaintenanceStep.InsertRailPoint;
                ScheduleDefenseMaintenanceStep("已记录预测周期，下一帧只提交一次轨道插点命令。");
                return true;

            case DefenseMaintenanceStep.InsertRailPoint:
                if (_defenseSelectedRailInsertion == null)
                {
                    FinishDefenseMaintenance("扩轨候选身份已丢失，未发送写命令。", warning: true);
                    return true;
                }

                AutomationAction insertAction =
                    _railExpansionPlanner.BuildInsertAction(_defenseSelectedRailInsertion);
                if (!IssueGuardedDefenseMutation(
                        insertAction,
                        "insert:" + _defenseSelectedRailInsertion.Candidate.Identity,
                        out RuntimeResultDisposition insertDisposition,
                        out _))
                {
                    return true;
                }

                if (insertDisposition == RuntimeResultDisposition.Failure)
                {
                    _defenseStructuralMutationGuard.Reset();
                    ContinueDefenseRailOptimization(
                        "轨道插点被游戏明确拒绝；相同布局和动作不会重发，将尝试其他正收益候选。");
                    return true;
                }

                _defenseMaintenanceStep = DefenseMaintenanceStep.VerifyRailInsertion;
                ScheduleDefenseMaintenanceStep("插点命令已锁定，后续只读验证，不会重复发送。");
                return true;

            case DefenseMaintenanceStep.VerifyRailInsertion:
                if (!TryInvokeOptionalReadOnly("queryRail", null, out JObject verifiedInsertionRails))
                {
                    if (_defenseStructuralMutationGuard.HasTimedOut(
                            Time.realtimeSinceStartup,
                            RewardSelectionSettlementTimeoutSeconds))
                    {
                        FaultRequiringProcessRestart("扩轨写入结果在安全时限内无法只读对账。");
                    }
                    else
                    {
                        ScheduleDefenseMaintenanceStep("扩轨写入仍在对账；不会重复发送插点命令。");
                    }
                    return true;
                }

                RailInsertionVerification insertionVerification =
                    _railExpansionPlanner.VerifyInsertion(
                        _defenseRailExpansionBaseline,
                        verifiedInsertionRails,
                        _defenseSelectedRailInsertion);
                if (insertionVerification.Verified)
                {
                    _defenseStructuralMutationGuard.Reset();
                    _pendingActionKey = string.Empty;
                    ContinueDefenseRailOptimization(
                        insertionVerification.Beneficial
                            ? $"扩轨完成；实测回转周期 {insertionVerification.ObservedLoopCycleSeconds:0.###} 秒。"
                            : insertionVerification.Detail +
                              " 结构写入已完整对账，不要求重启；将从当前布局继续寻找下一项优化。");
                    return true;
                }

                _defenseStructuralVerificationAttempts++;
                if (!insertionVerification.Pending ||
                    _defenseStructuralMutationGuard.HasTimedOut(
                        Time.realtimeSinceStartup,
                        RewardSelectionSettlementTimeoutSeconds))
                {
                    FaultRequiringProcessRestart(
                        insertionVerification.Detail + " 扩轨命令不会重发，请彻底重启游戏后再继续。");
                    return true;
                }

                ScheduleDefenseMaintenanceStep(
                    $"尚未观察到扩轨结果（{_defenseStructuralVerificationAttempts}/{MaxDefenseExpansionVerificationAttempts}），继续只读对账。");
                return true;

            case DefenseMaintenanceStep.ProbeSpecialStationMoveGrid:
                IncrementalGridProbeResult specialMoveProbe = _defenseStationGridProbe.ProbeNext();
                if (specialMoveProbe.Status == IncrementalGridProbeStatus.Probing)
                {
                    ScheduleDefenseMaintenanceStep(specialMoveProbe.Detail);
                    return true;
                }

                if (specialMoveProbe.Status != IncrementalGridProbeStatus.Found ||
                    !specialMoveProbe.Grid.HasValue ||
                    _defenseSpecialMoveCandidate == null)
                {
                    if (_defenseSpecialMoveCandidate != null)
                    {
                        _defenseRailMaintenanceActionFingerprints.Add(
                            BuildDefenseRailMoveCandidateFingerprint(
                                _defenseSpecialMoveCandidate));
                    }
                    _defenseStationGridProbe.Reset();
                    _defenseMaintenanceStep = DefenseMaintenanceStep.QueryRailExpansionCandidates;
                    ScheduleDefenseMaintenanceStep(
                        "当前弹射点没有周向覆盖或 N/T 更优的合法位置；重新读取布局并检查其他可移动弹射点。");
                    return true;
                }

                AutoPlayerGrid moveGrid = specialMoveProbe.Grid.Value;
                string moveActionFingerprint = BuildDefenseRailMoveActionFingerprint(
                    _defenseSpecialMoveCandidate,
                    moveGrid);
                string movePairFingerprint = BuildDefenseRailMovePairFingerprint(
                    _defenseSpecialMoveCandidate,
                    moveGrid);
                if (_defenseRailMaintenanceActionFingerprints.Contains(moveActionFingerprint) ||
                    _defenseRailMaintenanceActionFingerprints.Contains(movePairFingerprint))
                {
                    ScheduleDefenseMaintenanceStep(
                        "相同布局下的同一移动候选已经评估或提交过；继续检查下一个目标格。");
                    return true;
                }

                _defenseSpecialMoveGrid = JObject.FromObject(new { x = moveGrid.X, y = moveGrid.Y });
                _defenseSpecialMovePredictedCycleSeconds =
                    _railExpansionPlanner.PredictCycleAfterMove(_defenseSpecialMoveCandidate, moveGrid);
                if (double.IsNaN(_defenseSpecialMovePredictedCycleSeconds) ||
                    double.IsInfinity(_defenseSpecialMovePredictedCycleSeconds) ||
                    !_railExpansionPlanner.IsBeneficialMove(
                        _defenseSpecialMoveCandidate,
                        moveGrid))
                {
                    _defenseSpecialMoveGrid = null;
                    _defenseSpecialMovePredictedCycleSeconds = 0d;
                    ScheduleDefenseMaintenanceStep(
                        "目标格既不能修复周向覆盖，也不能在保持覆盖层级时提高 N/T；已拒绝移动并继续检查下一个目标格。");
                    return true;
                }

                _defenseStationGridProbe.Reset();
                if (_defenseBattleSpecialMoveOnly && _bridge.HasCommand("deleteLinePoint"))
                {
                    _defenseRailRebuildSnapshot = _railRebuildPlanner.Capture(
                        _defenseRailExpansionBaseline,
                        _defenseSpecialMoveCandidate.RailInstanceId,
                        _battleTrain);
                    if (_defenseRailRebuildSnapshot == null)
                    {
                        _defenseRailMaintenanceActionFingerprints.Add(moveActionFingerprint);
                        _defenseMaintenanceStep = DefenseMaintenanceStep.QueryRailExpansionCandidates;
                        ScheduleDefenseMaintenanceStep(
                            "无法为目标闭环建立始发站、站点顺序和车列身份快照；未断环，将检查其他候选。");
                        return true;
                    }

                    _defenseRailRebuildRecoveryAttempted = false;
                    _defenseRailRebuildExplicitPollution = false;
                    _defenseRailRebuildPreviewCycleSeconds = 0d;
                    _defenseMaintenanceStep = DefenseMaintenanceStep.DisconnectRailForRebuild;
                    ScheduleDefenseMaintenanceStep(
                        $"已预计算完整闭环；预测移动后回转周期 {_defenseSpecialMovePredictedCycleSeconds:0.###} 秒，" +
                        "下一帧从始发站按玩家右键语义断环并缓存原车列。");
                    return true;
                }

                _defenseMaintenanceStep = DefenseMaintenanceStep.QueryFreshMovableStation;
                ScheduleDefenseMaintenanceStep(
                    _defenseBattleSpecialMoveOnly
                        ? "当前游戏构建没有始发站断环接口；已安全降级为战斗中只移动可移动特殊站点。"
                        : $"预测移动后回转周期 {_defenseSpecialMovePredictedCycleSeconds:0.###} 秒；下一帧重新读取站点身份。");
                return true;

            case DefenseMaintenanceStep.DisconnectRailForRebuild:
                if (_defenseRailRebuildSnapshot == null)
                {
                    ContinueDefenseRailOptimization("始发站重连快照已经失效；没有发送断环命令。");
                    return true;
                }

                AutomationAction disconnectAction =
                    _railRebuildPlanner.BuildDisconnectAction(_defenseRailRebuildSnapshot);
                if (!IssueGuardedDefenseMutation(
                        disconnectAction,
                        "rail-rebuild-disconnect:" + _defenseRailRebuildSnapshot.RailInstanceId,
                        out RuntimeResultDisposition disconnectDisposition,
                        out JObject disconnectResult))
                {
                    return true;
                }

                if (disconnectDisposition == RuntimeResultDisposition.Failure)
                {
                    RailRebuildVerification rejectedDisconnect =
                        _railRebuildPlanner.VerifyDisconnect(disconnectResult, _defenseRailRebuildSnapshot);
                    _defenseRailRebuildExplicitPollution |= rejectedDisconnect.ExplicitStatePolluted;
                    _defenseVerifiedRailResult = disconnectResult;
                    _defenseMaintenanceStep = DefenseMaintenanceStep.VerifyRailRebuildDisconnected;
                    ScheduleDefenseMaintenanceStep(
                        "游戏拒绝了始发站断环；下一帧先确认原闭环是否仍在，未知结果不会继续移动站点。");
                    return true;
                }

                RailRebuildVerification disconnectVerification =
                    _railRebuildPlanner.VerifyDisconnect(disconnectResult, _defenseRailRebuildSnapshot);
                _defenseRailRebuildExplicitPollution = disconnectVerification.ExplicitStatePolluted;
                if (disconnectVerification.Verified)
                {
                    _defenseStructuralMutationGuard.Reset();
                    _defenseMaintenanceStep = _defenseSpecialMoveCandidate == null
                        ? DefenseMaintenanceStep.PreviewRailRebuild
                        : DefenseMaintenanceStep.QueryFreshMovableStation;
                    ScheduleDefenseMaintenanceStep(
                        disconnectVerification.Detail +
                        (_defenseSpecialMoveCandidate == null
                            ? " 下一帧预览包含新特殊中继站的完整闭环。"
                            : " 下一帧移动已规划的可移动站点。"));
                    return true;
                }

                _defenseVerifiedRailResult = disconnectResult;
                _defenseMaintenanceStep = DefenseMaintenanceStep.VerifyRailRebuildDisconnected;
                ScheduleDefenseMaintenanceStep(
                    disconnectVerification.Detail +
                    " 未取得完整车列缓存证明；后续只读确认并优先恢复原闭环，不会继续移动站点或重发断环命令。");
                return true;

            case DefenseMaintenanceStep.VerifyRailRebuildDisconnected:
                if (_defenseRailRebuildSnapshot == null)
                {
                    FinishDefenseMaintenance("始发站重连快照已丢失，停止本轮维护。", warning: true);
                    return true;
                }
                if (!TryInvokeOptionalReadOnly("queryRail", null, out JObject disconnectedRails))
                {
                    ScheduleDefenseMaintenanceStep("正在只读等待原闭环清空，不会重发断环命令。");
                    return true;
                }
                bool originalRailStillVisible = (State(disconnectedRails)["rails"] as JArray)?
                    .OfType<JObject>()
                    .Any(item => item["instanceId"]?.Value<int?>() ==
                                 _defenseRailRebuildSnapshot.RailInstanceId) == true;
                if (originalRailStillVisible)
                {
                    if (_defenseStructuralMutationGuard.HasTimedOut(
                            Time.realtimeSinceStartup,
                            RewardSelectionSettlementTimeoutSeconds))
                    {
                        _defenseStructuralMutationGuard.Reset();
                        FinishDefenseMaintenance(
                            "始发站断环没有在安全时限内形成可确认结果；命令不会重放，稍后重新读取当前防线。",
                            warning: true);
                    }
                    else ScheduleDefenseMaintenanceStep("原闭环仍在退出动画中；继续只读等待。");
                    return true;
                }
                _defenseStructuralMutationGuard.Reset();
                _defenseRailRebuildRecoveryAttempted = true;
                _railRebuildPlanner.RestoreOriginalOrder(_defenseRailRebuildSnapshot);
                _defenseMaintenanceStep = DefenseMaintenanceStep.PreviewRailRebuild;
                ScheduleDefenseMaintenanceStep(
                    "已确认旧轨消失，但没有取得原车列完整缓存证明；只按原站点顺序恢复闭环，不执行目标布局。");
                return true;

            case DefenseMaintenanceStep.QueryFreshMovableStation:
                if (!TryInvokeOptionalReadOnly("queryCatapults", null, out JObject freshMoveCatapults))
                {
                    FinishDefenseMaintenance("移动能量/特殊弹射点前无法刷新站点身份，未发送写命令。", warning: true);
                    return true;
                }

                _defenseCatapults = freshMoveCatapults;
                _defenseMaintenanceStep = DefenseMaintenanceStep.QueryFreshMovableStationState;
                ScheduleDefenseMaintenanceStep("站点身份已刷新；下一帧读取正式移动交互状态。");
                return true;

            case DefenseMaintenanceStep.QueryFreshMovableStationState:
                if (!TryInvokeOptionalReadOnly(
                        "queryMovableStationState",
                        null,
                        out JObject freshMovableState))
                {
                    FinishDefenseMaintenance(
                        "暂时无法读取弹射点移动交互；未发送写命令，稍后会重新评估。",
                        warning: true);
                    return true;
                }

                if (State(freshMovableState).SelectToken("currentMoveInteraction.active")?.Value<bool>() == true)
                {
                    FinishDefenseMaintenance(
                        "玩家或其他交互正在移动弹射点；未发送写命令，稍后会重新评估。",
                        warning: false);
                    return true;
                }

                RailStationMoveCandidate? freshCandidate = _defenseSpecialMoveCandidate;
                if (!_railExpansionPlanner.IsFreshMovableSpecial(
                        _defenseCatapults,
                        freshMovableState,
                        freshCandidate))
                {
                    if (_defenseRailRebuildSnapshot != null && freshCandidate != null &&
                        IsFreshDisconnectedMovableStation(
                            _defenseCatapults,
                            freshMovableState,
                            freshCandidate))
                    {
                        _defenseFreshMovableStationRetryAttempts = 0;
                        _defenseMaintenanceStep = DefenseMaintenanceStep.StartSpecialStationMove;
                        ScheduleDefenseMaintenanceStep(
                            "已确认断环后的特殊站点身份稳定且 canMove=true；下一帧启动正式移动交互。");
                        return true;
                    }
                    HandleTransientFreshMovableStationMismatch();
                    return true;
                }

                _defenseFreshMovableStationRetryAttempts = 0;
                _defenseMaintenanceStep = DefenseMaintenanceStep.StartSpecialStationMove;
                ScheduleDefenseMaintenanceStep("已确认能量/特殊弹射点 canMove=true 且交互空闲；下一帧启动正式移动交互。");
                return true;

            case DefenseMaintenanceStep.StartSpecialStationMove:
                if (_defenseSpecialMoveCandidate == null ||
                    _defenseSpecialMoveGrid == null)
                {
                    ContinueDefenseRailOptimization(
                        "弹射点或目标格身份已丢失，未发送移动命令；将重新读取布局。");
                    return true;
                }

                AutomationAction startMoveAction = new(
                    "startStationMove",
                    JObject.FromObject(new
                    {
                        instanceId = _defenseSpecialMoveCandidate.StationCatapultInstanceId
                    }),
                    AutomationStage.PreparingDefense,
                    "按正式玩家移动入口启动能量/特殊弹射点移动交互。");
                _defenseRailMaintenanceActionFingerprints.Add(
                    BuildDefenseRailMoveActionFingerprint(
                        _defenseSpecialMoveCandidate,
                        new AutoPlayerGrid(
                            _defenseSpecialMoveGrid["x"]?.Value<int>() ?? 0,
                            _defenseSpecialMoveGrid["y"]?.Value<int>() ?? 0)));
                if (!IssueGuardedDefenseMutation(
                        startMoveAction,
                        "station-move-start:" + _defenseSpecialMoveCandidate.StationCatapultInstanceId,
                        out RuntimeResultDisposition startMoveDisposition,
                        out _))
                {
                    return true;
                }

                if (startMoveDisposition == RuntimeResultDisposition.Failure)
                {
                    _defenseStructuralMutationGuard.Reset();
                    ContinueDefenseRailOptimization(
                        "游戏明确拒绝启动本次弹射点移动；相同动作不会重发，将尝试其他正收益候选。");
                    return true;
                }

                _defenseStructuralVerificationAttempts = 0;
                _defenseMaintenanceStep = DefenseMaintenanceStep.VerifySpecialStationMoveStarted;
                ScheduleDefenseMaintenanceStep("移动启动命令已锁定；下一帧只读验证交互归属。");
                return true;

            case DefenseMaintenanceStep.VerifySpecialStationMoveStarted:
                if (!TryInvokeOptionalReadOnly(
                        "queryMovableStationState",
                        null,
                        out JObject startedMoveState))
                {
                    if (_defenseStructuralMutationGuard.HasTimedOut(
                            Time.realtimeSinceStartup,
                            RewardSelectionSettlementTimeoutSeconds))
                    {
                        FaultRequiringProcessRestart("无法确认能量/特殊弹射点移动交互归属。");
                    }
                    else
                    {
                        ScheduleDefenseMaintenanceStep("正在只读确认能量/特殊弹射点移动交互归属。");
                    }
                    return true;
                }

                if (BeginSpecialStationMoveRollbackVerificationIfInactive(
                        startedMoveState,
                        "游戏阶段切换或运行时已取消能量/特殊弹射点移动预览；正在验证原站点与轨道已恢复。"))
                {
                    return true;
                }

                if (!_railExpansionPlanner.IsOwnedMoveInteraction(
                        startedMoveState,
                        _defenseSpecialMoveCandidate,
                        _defenseSpecialMoveInteractionInstanceId))
                {
                    if (_defenseStructuralMutationGuard.HasTimedOut(
                            Time.realtimeSinceStartup,
                            RewardSelectionSettlementTimeoutSeconds))
                    {
                        FaultRequiringProcessRestart("移动启动结果未知，且未观察到属于本次能量/特殊弹射点的交互。");
                    }
                    else
                    {
                        ScheduleDefenseMaintenanceStep("尚未观察到本次移动交互；不会重复发送启动命令。");
                    }
                    return true;
                }

                _defenseSpecialMoveInteractionInstanceId =
                    _railExpansionPlanner.ReadMoveInteractionInstanceId(startedMoveState);
                if (_defenseSpecialMoveCancelRequested)
                {
                    AutomationAction preparedCancelMove = BuildSpecialStationMoveCancellation();
                    if (!_defenseStructuralMutationGuard.TryAdvance(
                            preparedCancelMove,
                            "station-move-cancel:" + _defenseSpecialMoveInteractionInstanceId,
                            Time.realtimeSinceStartup))
                    {
                        FaultRequiringProcessRestart("无法把能量/特殊弹射点移动事务切换到安全取消阶段。");
                        return true;
                    }
                    _defenseMaintenanceStep = DefenseMaintenanceStep.CancelSpecialStationMove;
                    ScheduleDefenseMaintenanceStep("确认移动未完成且交互仍归本次所有；下一帧安全取消。");
                }
                else
                {
                    _defenseMaintenanceStep = DefenseMaintenanceStep.ValidateSpecialStationMoveGrid;
                    ScheduleDefenseMaintenanceStep("已锁定移动交互身份；下一帧用实时移动条件复核目标格。");
                }
                return true;

            case DefenseMaintenanceStep.ValidateSpecialStationMoveGrid:
                if (_defenseSpecialMoveGrid == null ||
                    _defenseSpecialMoveCandidate == null ||
                    !TryInvokeOptionalReadOnly(
                        "queryDisposableGridOptions",
                        JObject.FromObject(new
                        {
                            disposableEnum = _defenseSpecialMoveCandidate.StationDisposableEnum,
                            grid = _defenseSpecialMoveGrid,
                            maxResults = 1
                        }),
                        out JObject liveMoveGridState) ||
                    State(liveMoveGridState)["hasLiveInteraction"]?.Value<bool>() != true ||
                    State(liveMoveGridState).SelectToken("targetGrid.pass")?.Value<bool>() != true)
                {
                    _defenseSpecialMoveCancelRequested = true;
                    _defenseMaintenanceStep = DefenseMaintenanceStep.VerifySpecialStationMoveStarted;
                    ScheduleDefenseMaintenanceStep("目标格未通过当前移动交互的实时条件；将验证归属后安全取消。");
                    return true;
                }

                AutomationAction preparedConfirmMove = BuildSpecialStationMoveConfirmation();
                if (!_defenseStructuralMutationGuard.TryAdvance(
                        preparedConfirmMove,
                        BuildSpecialStationMoveConfirmationIdentity(),
                        Time.realtimeSinceStartup))
                {
                    FaultRequiringProcessRestart("无法把能量/特殊弹射点移动事务切换到目标格确认阶段。");
                    return true;
                }
                _defenseMaintenanceStep = DefenseMaintenanceStep.ConfirmSpecialStationMove;
                ScheduleDefenseMaintenanceStep("目标格已通过实时条件；下一帧再次确认交互身份后提交。");
                return true;

            case DefenseMaintenanceStep.ConfirmSpecialStationMove:
                if (_defenseSpecialMoveGrid == null ||
                    _defenseSpecialMoveInteractionInstanceId == 0)
                {
                    FinishDefenseMaintenance("移动交互或目标格身份已丢失，未发送确认命令。", warning: true);
                    return true;
                }

                if (!TryInvokeOptionalReadOnly(
                        "queryMovableStationState",
                        null,
                        out JObject preConfirmMoveState))
                {
                    if (_defenseStructuralMutationGuard.HasTimedOut(
                            Time.realtimeSinceStartup,
                            RewardSelectionSettlementTimeoutSeconds))
                    {
                        FaultRequiringProcessRestart("确认能量/特殊弹射点目标格前无法再次读取当前移动交互。");
                    }
                    else
                    {
                        ScheduleDefenseMaintenanceStep("确认前尚未读取到移动交互状态，未发送确认命令。");
                    }
                    return true;
                }

                if (BeginSpecialStationMoveRollbackVerificationIfInactive(
                        preConfirmMoveState,
                        "确认目标格前游戏已取消能量/特殊弹射点移动预览；正在验证原站点与轨道已恢复。"))
                {
                    return true;
                }

                if (!_railExpansionPlanner.IsOwnedMoveInteraction(
                        preConfirmMoveState,
                        _defenseSpecialMoveCandidate,
                        _defenseSpecialMoveInteractionInstanceId))
                {
                    if (_defenseStructuralMutationGuard.HasTimedOut(
                            Time.realtimeSinceStartup,
                            RewardSelectionSettlementTimeoutSeconds))
                    {
                        FaultRequiringProcessRestart("确认能量/特殊弹射点目标格前无法再次证明当前移动交互归属。");
                    }
                    else
                    {
                        ScheduleDefenseMaintenanceStep("确认前的移动交互归属尚不稳定，未发送确认命令。");
                    }
                    return true;
                }

                AutomationAction confirmMoveAction = BuildSpecialStationMoveConfirmation();
                if (!IssueGuardedDefenseMutation(
                        confirmMoveAction,
                        BuildSpecialStationMoveConfirmationIdentity(),
                        out RuntimeResultDisposition confirmMoveDisposition,
                        out _))
                {
                    return true;
                }

                _defenseStructuralVerificationAttempts = 0;
                if (confirmMoveDisposition == RuntimeResultDisposition.Failure)
                {
                    _defenseSpecialMoveCancelRequested = true;
                    _defenseMaintenanceStep = DefenseMaintenanceStep.VerifySpecialStationMoveStarted;
                    ScheduleDefenseMaintenanceStep("目标格确认被明确拒绝；下一帧验证归属后取消本次移动交互。");
                    return true;
                }

                _defenseSpecialMoveConfirmationAccepted =
                    confirmMoveDisposition == RuntimeResultDisposition.Success;
                _defenseMaintenanceStep = DefenseMaintenanceStep.VerifySpecialStationMoved;
                ScheduleDefenseMaintenanceStep("移动确认已锁定；后续只读验证站点归属和回转周期。");
                return true;

            case DefenseMaintenanceStep.VerifySpecialStationMoved:
                if (!TryInvokeOptionalReadOnly("queryRail", null, out JObject movedRailState))
                {
                    if (_defenseStructuralMutationGuard.HasTimedOut(
                            Time.realtimeSinceStartup,
                            RewardSelectionSettlementTimeoutSeconds))
                    {
                        if (_defenseSpecialMoveConfirmationAccepted)
                        {
                            FinishCommittedSpecialStationMove(new RailInsertionVerification
                            {
                                Detail = "确认移动后暂时无法读取轨道状态。"
                            });
                        }
                        else
                        {
                            FaultRequiringProcessRestart("能量/特殊弹射点移动后无法读取轨道状态。");
                        }
                    }
                    else
                    {
                        ScheduleDefenseMaintenanceStep("正在等待能量/特殊弹射点移动后的轨道状态。");
                    }
                    return true;
                }

                _defenseVerifiedRailResult = movedRailState;
                _defenseMaintenanceStep = DefenseMaintenanceStep.VerifySpecialStationMoveResult;
                ScheduleDefenseMaintenanceStep("已读取移动后的轨道；下一帧读取站点并完成交叉验证。");
                return true;

            case DefenseMaintenanceStep.VerifySpecialStationMoveResult:
                if (!TryInvokeOptionalReadOnly("queryCatapults", null, out JObject movedCatapultState))
                {
                    if (_defenseStructuralMutationGuard.HasTimedOut(
                            Time.realtimeSinceStartup,
                            RewardSelectionSettlementTimeoutSeconds))
                    {
                        if (_defenseSpecialMoveConfirmationAccepted)
                        {
                            FinishCommittedSpecialStationMove(new RailInsertionVerification
                            {
                                Detail = "确认移动后暂时无法读取站点状态。"
                            });
                        }
                        else
                        {
                            FaultRequiringProcessRestart("能量/特殊弹射点移动后无法读取站点状态。");
                        }
                    }
                    else
                    {
                        ScheduleDefenseMaintenanceStep("正在等待能量/特殊弹射点移动后的站点状态。");
                    }
                    return true;
                }

                if (_defenseRailRebuildSnapshot != null &&
                    TryRefreshDisconnectedStationAtTarget(
                        movedCatapultState,
                        _defenseSpecialMoveCandidate,
                        _defenseSpecialMoveGrid,
                        _defenseRailRebuildSnapshot))
                {
                    _defenseStructuralMutationGuard.Reset();
                    _defenseMaintenanceStep = DefenseMaintenanceStep.PreviewRailRebuild;
                    ScheduleDefenseMaintenanceStep(
                        "已确认断环后的特殊站点到达目标格；下一帧按原站点身份顺序预览重新闭环。");
                    return true;
                }

                RailInsertionVerification moveVerification = _railExpansionPlanner.VerifyMove(
                    _defenseRailExpansionBaseline,
                    _defenseVerifiedRailResult,
                    movedCatapultState,
                    _defenseSpecialMoveCandidate,
                    _defenseSpecialMoveGrid);
                if (moveVerification.Verified)
                {
                    if (_defenseRailRebuildSnapshot != null)
                    {
                        _defenseStructuralMutationGuard.Reset();
                        _defenseMaintenanceStep = DefenseMaintenanceStep.PreviewRailRebuild;
                        ScheduleDefenseMaintenanceStep(
                            "站点已到达目标格；下一帧按原站点顺序只读预览完整闭环。");
                        return true;
                    }
                    RememberCommittedSpecialStationMove();
                    _defenseStructuralMutationGuard.Reset();
                    ContinueDefenseRailOptimization(
                        $"能量/特殊弹射点移动完成；回转周期由 " +
                        $"{_defenseSpecialMoveCandidate?.CurrentLoopCycleSeconds:0.###} 秒变为 " +
                        $"{moveVerification.ObservedLoopCycleSeconds:0.###} 秒，周向覆盖或 N/T 已验证改善。");
                    return true;
                }

                if (moveVerification.Pending &&
                    !_defenseStructuralMutationGuard.HasTimedOut(
                        Time.realtimeSinceStartup,
                        RewardSelectionSettlementTimeoutSeconds))
                {
                    _defenseStructuralVerificationAttempts++;
                    _defenseMaintenanceStep = DefenseMaintenanceStep.VerifySpecialStationMoved;
                    ScheduleDefenseMaintenanceStep("能量/特殊弹射点尚未到达目标格，继续只读对账。");
                    return true;
                }

                if (_defenseSpecialMoveConfirmationAccepted || moveVerification.MoveObserved)
                {
                    if (_defenseRailRebuildSnapshot != null)
                    {
                        _defenseStructuralMutationGuard.Reset();
                        _defenseMaintenanceStep = DefenseMaintenanceStep.PreviewRailRebuild;
                        ScheduleDefenseMaintenanceStep(
                            moveVerification.Detail + " 站点移动已提交，继续以当前站点位置恢复合法闭环。");
                        return true;
                    }
                    FinishCommittedSpecialStationMove(moveVerification);
                    return true;
                }

                _defenseSpecialMoveCancelRequested = true;
                _defenseMaintenanceStep = DefenseMaintenanceStep.VerifySpecialStationMoveStarted;
                ScheduleDefenseMaintenanceStep(
                    moveVerification.Detail + " 正在确认原移动交互是否仍归本次所有。");
                return true;

            case DefenseMaintenanceStep.CancelSpecialStationMove:
                AutomationAction cancelMoveAction = BuildSpecialStationMoveCancellation();
                if (!IssueGuardedDefenseMutation(
                        cancelMoveAction,
                        "station-move-cancel:" + _defenseSpecialMoveInteractionInstanceId,
                        out _,
                        out _))
                {
                    return true;
                }

                _defenseMaintenanceStep = DefenseMaintenanceStep.VerifySpecialStationMoveCancelled;
                _defenseVerifiedRailResult = null;
                ScheduleDefenseMaintenanceStep("取消命令只发送一次；下一帧验证移动交互已退出。");
                return true;

            case DefenseMaintenanceStep.VerifySpecialStationMoveCancelled:
                if (!TryInvokeOptionalReadOnly(
                        "queryMovableStationState",
                        null,
                        out JObject cancelledMoveState))
                {
                    if (_defenseStructuralMutationGuard.HasTimedOut(
                            Time.realtimeSinceStartup,
                            RewardSelectionSettlementTimeoutSeconds))
                    {
                        FaultRequiringProcessRestart("取消能量/特殊弹射点移动后无法确认交互已退出。");
                    }
                    else
                    {
                        ScheduleDefenseMaintenanceStep("正在验证能量/特殊弹射点移动交互已退出。");
                    }
                    return true;
                }

                if (State(cancelledMoveState).SelectToken("currentMoveInteraction.active")?.Value<bool>() != true)
                {
                    BeginSpecialStationMoveRollbackVerificationIfInactive(
                        cancelledMoveState,
                        "移动交互已退出；正在验证取消操作确实恢复了原站点与轨道。" );
                    return true;
                }

                if (!_railExpansionPlanner.IsOwnedMoveInteraction(
                        cancelledMoveState,
                        _defenseSpecialMoveCandidate,
                        _defenseSpecialMoveInteractionInstanceId) ||
                    _defenseStructuralMutationGuard.HasTimedOut(
                        Time.realtimeSinceStartup,
                        RewardSelectionSettlementTimeoutSeconds))
                {
                    FaultRequiringProcessRestart("取消命令已发送一次，但移动交互仍存在或归属已变化。");
                    return true;
                }

                ScheduleDefenseMaintenanceStep("取消结果尚未生效，继续只读验证且不会重发取消命令。");
                return true;

            case DefenseMaintenanceStep.VerifySpecialStationMoveRollbackRail:
                if (!TryInvokeOptionalReadOnly("queryRail", null, out JObject rollbackRailState))
                {
                    if (_defenseStructuralMutationGuard.HasTimedOut(
                            Time.realtimeSinceStartup,
                            RewardSelectionSettlementTimeoutSeconds))
                    {
                        FaultRequiringProcessRestart("移动预览退出后无法读取轨道，不能证明原结构已恢复。");
                    }
                    else
                    {
                        ScheduleDefenseMaintenanceStep("正在只读等待取消移动后的轨道基线恢复。");
                    }
                    return true;
                }

                _defenseVerifiedRailResult = rollbackRailState;
                _defenseMaintenanceStep = DefenseMaintenanceStep.VerifySpecialStationMoveRollbackResult;
                ScheduleDefenseMaintenanceStep("已读取取消移动后的轨道；下一帧读取原站点身份并交叉验证。");
                return true;

            case DefenseMaintenanceStep.VerifySpecialStationMoveRollbackResult:
                if (!TryInvokeOptionalReadOnly("queryCatapults", null, out JObject rollbackCatapultState))
                {
                    if (_defenseStructuralMutationGuard.HasTimedOut(
                            Time.realtimeSinceStartup,
                            RewardSelectionSettlementTimeoutSeconds))
                    {
                        FaultRequiringProcessRestart("移动预览退出后无法读取原站点，不能证明结构已恢复。");
                    }
                    else
                    {
                        ScheduleDefenseMaintenanceStep("正在只读等待取消移动后的原站点恢复。");
                    }
                    return true;
                }

                RailInsertionVerification rollbackVerification =
                    _railExpansionPlanner.VerifyMoveCancellationRollback(
                        _defenseRailExpansionBaseline,
                        _defenseVerifiedRailResult,
                        rollbackCatapultState,
                        _defenseSpecialMoveCandidate);
                if (rollbackVerification.Verified)
                {
                    _defenseStructuralMutationGuard.Reset();
                    ContinueDefenseRailOptimization(
                        rollbackVerification.Detail +
                        " 本次事务已安全解锁；相同动作不会重发，将检查其他正收益候选。");
                    return true;
                }

                if (_defenseStructuralMutationGuard.HasTimedOut(
                        Time.realtimeSinceStartup,
                        RewardSelectionSettlementTimeoutSeconds))
                {
                    FaultRequiringProcessRestart(
                        rollbackVerification.Detail +
                        " 取消后的原站点或轨道在限定时间内未恢复到基线，不能继续自动游玩。");
                    return true;
                }

                _defenseStructuralVerificationAttempts++;
                _defenseMaintenanceStep = DefenseMaintenanceStep.VerifySpecialStationMoveRollbackRail;
                ScheduleDefenseMaintenanceStep(
                    rollbackVerification.Detail +
                    $" 正在继续只读对账（{_defenseStructuralVerificationAttempts}/{MaxDefenseExpansionVerificationAttempts}）。");
                return true;

            case DefenseMaintenanceStep.PreviewRailRebuild:
                if (_defenseRailRebuildSnapshot == null)
                {
                    FinishDefenseMaintenance("缺少始发站重连快照，无法恢复闭环。", warning: true);
                    return true;
                }
                AutomationAction rebuildPreviewAction =
                    _railRebuildPlanner.BuildPreviewAction(_defenseRailRebuildSnapshot);
                if (!TryInvokeOptionalReadOnly(
                        rebuildPreviewAction.Command,
                        rebuildPreviewAction.Arguments,
                        out JObject rebuildPreview) ||
                    !_railRebuildPlanner.IsLegalPreview(
                        rebuildPreview,
                        _defenseRailRebuildSnapshot,
                        out _defenseRailRebuildPreviewCycleSeconds))
                {
                    _defenseMaintenanceStep = DefenseMaintenanceStep.RecoverRailRebuild;
                    ScheduleDefenseMaintenanceStep(
                        "目标站点顺序未通过合法闭环预览；将按原顺序和当前合法位置恢复闭环。");
                    return true;
                }

                if (!ImprovesRailTriggerRate(
                        _defenseRailRebuildSnapshot,
                        _defenseRailRebuildPreviewCycleSeconds) &&
                    !_defenseRailRebuildRecoveryAttempted)
                {
                    AddWarning(
                        $"目标闭环预测触发率未严格优于原回路（新周期 {_defenseRailRebuildPreviewCycleSeconds:0.###} 秒）；" +
                        "仍会先恢复合法闭环，但不会把本方案记为收益。");
                }
                _defenseMaintenanceStep = DefenseMaintenanceStep.DrawRailRebuild;
                ScheduleDefenseMaintenanceStep(
                    $"闭环预览合法，预测回转周期 {_defenseRailRebuildPreviewCycleSeconds:0.###} 秒；" +
                    "下一帧从始发站依次连接并回到始发站。");
                return true;

            case DefenseMaintenanceStep.DrawRailRebuild:
                if (_defenseRailRebuildSnapshot == null)
                {
                    FinishDefenseMaintenance("重连前始发站快照丢失，停止本轮维护。", warning: true);
                    return true;
                }
                AutomationAction rebuildDrawAction =
                    _railRebuildPlanner.BuildDrawAction(_defenseRailRebuildSnapshot);
                if (!IssueGuardedDefenseMutation(
                        rebuildDrawAction,
                        "rail-rebuild-draw:" + _defenseRailRebuildSnapshot.OriginLinePointInstanceId + ":" +
                        _defenseRailRebuildRecoveryAttempted,
                        out RuntimeResultDisposition rebuildDrawDisposition,
                        out JObject rebuildDrawResult))
                {
                    return true;
                }
                if (rebuildDrawDisposition == RuntimeResultDisposition.Failure)
                {
                    _defenseStructuralMutationGuard.Reset();
                    _defenseRailRebuildExplicitPollution |=
                        State(rebuildDrawResult)["statePolluted"]?.Value<bool>() == true;
                    _defenseMaintenanceStep = DefenseMaintenanceStep.RecoverRailRebuild;
                    ScheduleDefenseMaintenanceStep(
                        "重新闭环被游戏明确拒绝；将只尝试一次安全恢复，不会重复当前绘制动作。");
                    return true;
                }
                _defenseMaintenanceStep = _defenseRailRebuildRecoveryAttempted
                    ? DefenseMaintenanceStep.VerifyRailRebuildRecovery
                    : DefenseMaintenanceStep.VerifyRailRebuild;
                ScheduleDefenseMaintenanceStep("重新闭环命令已锁定；下一帧只读验证轨道与原车列身份。");
                return true;

            case DefenseMaintenanceStep.VerifyRailRebuild:
            case DefenseMaintenanceStep.VerifyRailRebuildRecovery:
                if (_defenseRailRebuildSnapshot == null)
                {
                    FinishDefenseMaintenance("重连验证快照已丢失，停止本轮维护。", warning: true);
                    return true;
                }
                if (!TryInvokeOptionalReadOnly("queryRail", null, out JObject rebuiltRails))
                {
                    if (_defenseStructuralMutationGuard.HasTimedOut(
                            Time.realtimeSinceStartup,
                            RewardSelectionSettlementTimeoutSeconds))
                    {
                        _defenseStructuralMutationGuard.Reset();
                        if (_defenseRailRebuildRecoveryAttempted)
                            FinishDefenseMaintenance("恢复闭环后无法读取轨道；未报告 statePolluted，已停止本轮优化。", warning: true);
                        else
                        {
                            _defenseMaintenanceStep = DefenseMaintenanceStep.RecoverRailRebuild;
                            ScheduleDefenseMaintenanceStep("目标闭环验证超时；将按原顺序只尝试一次安全恢复。");
                        }
                        return true;
                    }
                    ScheduleDefenseMaintenanceStep("正在只读等待重连后的轨道和原车列恢复。");
                    return true;
                }
                _defenseVerifiedRailResult = rebuiltRails;
                _defenseMaintenanceStep = DefenseMaintenanceStep.VerifyRailRebuildVehicles;
                ScheduleDefenseMaintenanceStep("已读取重连轨道；下一帧核对始发站恢复的原战车身份。");
                return true;

            case DefenseMaintenanceStep.VerifyRailRebuildVehicles:
                if (_defenseRailRebuildSnapshot == null ||
                    _defenseVerifiedRailResult == null)
                {
                    FinishDefenseMaintenance("车列恢复验证快照已丢失，停止本轮维护。", warning: true);
                    return true;
                }
                if (!TryInvokeOptionalReadOnly("queryTrain", null, out JObject rebuiltTrains))
                {
                    if (_defenseStructuralMutationGuard.HasTimedOut(
                            Time.realtimeSinceStartup,
                            RewardSelectionSettlementTimeoutSeconds))
                    {
                        _defenseStructuralMutationGuard.Reset();
                        if (_defenseRailRebuildRecoveryAttempted)
                            FinishDefenseMaintenance("恢复闭环后无法核对原战车身份；未报告 statePolluted，已停止本轮优化。", warning: true);
                        else
                        {
                            _defenseMaintenanceStep = DefenseMaintenanceStep.RecoverRailRebuild;
                            ScheduleDefenseMaintenanceStep("原战车身份验证超时；将按原顺序只尝试一次安全恢复。");
                        }
                        return true;
                    }
                    ScheduleDefenseMaintenanceStep("正在读取重连后的车列与战车身份，不会重复绘制命令。");
                    return true;
                }
                RailRebuildVerification rebuildVerification =
                    _railRebuildPlanner.VerifyRestored(
                        _defenseVerifiedRailResult,
                        _defenseRailRebuildSnapshot,
                        rebuiltTrains);
                if (rebuildVerification.Verified)
                {
                    bool strictImprovement = ImprovesRailTriggerRate(
                        _defenseRailRebuildSnapshot,
                        rebuildVerification.LoopCycleSeconds);
                    _defenseStructuralMutationGuard.Reset();
                    RememberCommittedSpecialStationMove();
                    ContinueDefenseRailOptimization(
                        rebuildVerification.Detail +
                        (strictImprovement
                            ? $" 实测站点触发率严格改善，新周期为 {rebuildVerification.LoopCycleSeconds:0.###} 秒。"
                            : " 实测站点触发率没有严格改善；已接受游戏当前合法状态并排除本动作指纹。"));
                    return true;
                }
                if (rebuildVerification.Pending &&
                    !_defenseStructuralMutationGuard.HasTimedOut(
                        Time.realtimeSinceStartup,
                        RewardSelectionSettlementTimeoutSeconds))
                {
                    ScheduleDefenseMaintenanceStep(rebuildVerification.Detail + " 不会重复绘制命令。");
                    _defenseMaintenanceStep = _defenseRailRebuildRecoveryAttempted
                        ? DefenseMaintenanceStep.VerifyRailRebuildRecovery
                        : DefenseMaintenanceStep.VerifyRailRebuild;
                    return true;
                }
                _defenseStructuralMutationGuard.Reset();
                if (_defenseRailRebuildRecoveryAttempted)
                {
                    if (_defenseRailRebuildExplicitPollution)
                    {
                        FaultRequiringProcessRestart(
                            rebuildVerification.Detail + " 运行时已明确报告 statePolluted，且原闭环恢复失败。");
                    }
                    else
                    {
                        FinishDefenseMaintenance(
                            rebuildVerification.Detail +
                            " 安全恢复未能收敛，但没有明确 statePolluted；已停止本轮优化，稍后按当前状态重算。",
                            warning: true);
                    }
                    return true;
                }
                _defenseMaintenanceStep = DefenseMaintenanceStep.RecoverRailRebuild;
                ScheduleDefenseMaintenanceStep(
                    rebuildVerification.Detail + " 将按原站点顺序只尝试一次恢复闭环。");
                return true;

            case DefenseMaintenanceStep.RecoverRailRebuild:
                if (_defenseRailRebuildSnapshot == null || _defenseRailRebuildRecoveryAttempted)
                {
                    if (_defenseRailRebuildExplicitPollution)
                        FaultRequiringProcessRestart("始发站重连明确污染且安全恢复已经失败。");
                    else
                        FinishDefenseMaintenance(
                            "始发站重连未能恢复，但运行时没有明确 statePolluted；已停止本轮并等待下次只读重算。",
                            warning: true);
                    return true;
                }
                _defenseRailRebuildRecoveryAttempted = true;
                _railRebuildPlanner.RestoreOriginalOrder(_defenseRailRebuildSnapshot);
                _defenseMaintenanceStep = DefenseMaintenanceStep.PreviewRailRebuild;
                ScheduleDefenseMaintenanceStep(
                    "正在按原站点身份顺序和当前合法位置预览恢复闭环；不会移动或传送车列。");
                return true;

            case DefenseMaintenanceStep.QueryExpansionAttributeDisposable:
                if (!TryInvokeOptionalReadOnly("queryDisposable", null, out JObject attributeDisposableState))
                {
                    FinishDefenseMaintenance("无法读取动力弹射点道具库存，已跳过本轮扩建。", warning: true);
                    return true;
                }

                bool discoverAnyMovableStation = string.Equals(
                    _defensePlacementDisposableEnum,
                    "__runtime_movable_station__",
                    StringComparison.Ordinal);
                _defenseAttributeUseAction = discoverAnyMovableStation
                    ? _battleDecisionEngine.DecideMovableStationDisposableUse(
                        attributeDisposableState,
                        requireAttribute: false)
                    : _battleDecisionEngine.DecideExpansionDisposableUse(
                        attributeDisposableState,
                        _defensePlacementDisposableEnum);
                string initiallyDiscoveredEnum = _defenseAttributeUseAction?
                    .Arguments["disposableEnum"]?.Value<string>() ?? string.Empty;
                if (discoverAnyMovableStation && !string.IsNullOrWhiteSpace(initiallyDiscoveredEnum))
                {
                    _defensePlacementDisposableEnum = initiallyDiscoveredEnum;
                    _defensePlacementCountBefore =
                        _battleDecisionEngine.CountAvailableExpansionStations(
                            _defenseCatapults,
                            initiallyDiscoveredEnum);
                    _defenseAttributeCountBeforePlacement = _defensePlacementCountBefore;
                    AddTimeline(
                        "special-station",
                        "从 queryDisposable 运行时行为发现可移动特殊站点道具 " + initiallyDiscoveredEnum +
                        "，将按合法最小间距和闭环周期参与规划。");
                }
                if (_defenseAttributeUseAction == null)
                {
                    bool needAttribute = string.Equals(
                        _defensePlacementDisposableEnum,
                        "FreePoint_Attribute",
                        StringComparison.Ordinal);
                    _defenseAttributeUseAction = _battleDecisionEngine.DecideMovableStationDisposableUse(
                        attributeDisposableState,
                        requireAttribute: needAttribute,
                        requireCommon: !needAttribute);
                    string discoveredEnum = _defenseAttributeUseAction?
                        .Arguments["disposableEnum"]?.Value<string>() ?? string.Empty;
                    if (!string.IsNullOrWhiteSpace(discoveredEnum))
                    {
                        _defensePlacementDisposableEnum = discoveredEnum;
                        _defensePlacementCountBefore =
                            _battleDecisionEngine.CountAvailableExpansionStations(
                                _defenseCatapults,
                                discoveredEnum);
                        _defenseAttributeCountBeforePlacement = _defensePlacementCountBefore;
                        AddTimeline(
                            "special-station",
                            "从 queryDisposable 运行时行为发现可移动特殊站点道具 " + discoveredEnum +
                            "，将按合法最小间距和闭环周期参与规划。");
                    }
                }
                if (_defenseAttributeUseAction == null)
                {
                    if (discoverAnyMovableStation)
                    {
                        MarkDefenseRailMaintenanceStable();
                    }
                    FinishDefenseMaintenance(
                        $"没有可用的 {_defensePlacementDisposableEnum} 弹射点道具，已结束本轮扩建。");
                    return true;
                }

                if (!_defenseStationGridProbe.TryInitializePlacement(
                        _defensePlacementDisposableEnum,
                        _defenseCatapults,
                        out string expansionProbeError,
                        _defenseAttributeUseAction?.Arguments["stationKind"]?.Value<string>() switch
                        {
                            "AttributeCatapult" => true,
                            "CommonCatapult" => false,
                            _ => null
                        }))
                {
                    FinishDefenseMaintenance(
                        "无法启动动力弹射点增量探测，已跳过本轮扩建：" + expansionProbeError,
                        warning: true);
                    return true;
                }

                _defenseMaintenanceStep = DefenseMaintenanceStep.ProbeExpansionAttributeGrid;
                ScheduleDefenseMaintenanceStep("正在分帧检查动力弹射点的合法放置格子。");
                return true;

            case DefenseMaintenanceStep.ProbeExpansionAttributeGrid:
                IncrementalGridProbeResult expansionGridProbe =
                    _defenseStationGridProbe.ProbeNext();
                if (expansionGridProbe.Status == IncrementalGridProbeStatus.Probing)
                {
                    ScheduleDefenseMaintenanceStep(expansionGridProbe.Detail);
                    return true;
                }

                if (expansionGridProbe.Status != IncrementalGridProbeStatus.Found ||
                    !expansionGridProbe.Grid.HasValue)
                {
                    FinishDefenseMaintenance(
                        "动力弹射点增量探测未找到安全落点，已结束本轮扩建：" +
                        expansionGridProbe.Detail,
                        warning: true);
                    return true;
                }

                AutoPlayerGrid expansionGrid = expansionGridProbe.Grid.Value;
                _defenseAttributeGrid = JObject.FromObject(new
                {
                    x = expansionGrid.X,
                    y = expansionGrid.Y
                });
                _defenseStationGridProbe.Reset();
                _defenseMaintenanceStep = DefenseMaintenanceStep.UseExpansionAttributeDisposable;
                ScheduleDefenseMaintenanceStep(
                    "已选择靠近未占用普通站的合法格子；下一帧仅构造带稳定背包身份的确认命令。");
                return true;

            case DefenseMaintenanceStep.UseExpansionAttributeDisposable:
                _defenseAttributeConfirmAction =
                    _battleDecisionEngine.DecideExpansionDirectConfirmation(
                        _defenseAttributeUseAction,
                        _defenseAttributeGrid,
                        _defensePlacementDisposableEnum);
                if (_defenseAttributeConfirmAction == null)
                {
                    FinishDefenseMaintenance(
                        "动力弹射点缺少可验证的背包身份或目标格，已安全结束本轮扩建。",
                        warning: true);
                    return true;
                }

                ResetOwnedPreviewCancellationTracking();
                _defenseAttributeSettlementObservationAttempts = 0;
                _defenseMaintenanceStep = DefenseMaintenanceStep.ConfirmExpansionAttributeDisposable;
                ScheduleDefenseMaintenanceStep(
                    "确认命令已准备；下一帧先证明没有玩家道具预览，再以单次命令打开并确认目标格。");
                return true;

            case DefenseMaintenanceStep.ConfirmExpansionAttributeDisposable:
                AutomationAction? deferredAttributeConfirmation = _defenseAttributeConfirmAction;
                if (deferredAttributeConfirmation == null)
                {
                    _defenseAttributeFailureDetail = "动力弹射点确认操作丢失。";
                    _defenseMaintenanceStep = DefenseMaintenanceStep.QueryExpansionAttributeCleanup;
                    ScheduleDefenseMaintenanceStep("确认操作丢失，正在只读确认并清理本次预览。");
                    return true;
                }

                JObject attributeOwnership = _openingDefenseInteractionGuard.Query();
                if (RuntimeResultInspector.ClassifyReadOnly(attributeOwnership) != RuntimeResultDisposition.Success)
                {
                    _defenseAttributeSettlementObservationAttempts++;
                    if (_defenseAttributeSettlementObservationAttempts >= MaxOpeningDefenseConfirmGuardFailures)
                    {
                        _defenseAttributeFailureDetail = "连续无法在确认前验证道具交互执行器为空闲状态。";
                        _defenseMaintenanceStep = DefenseMaintenanceStep.QueryExpansionAttributeCleanup;
                        ScheduleDefenseMaintenanceStep("交互守卫无法稳定，未发送确认命令；正在安全收尾。");
                    }
                    else
                    {
                        ScheduleDefenseMaintenanceStep(
                            $"确认前的交互守卫尚未稳定（{_defenseAttributeSettlementObservationAttempts}/" +
                            $"{MaxOpeningDefenseConfirmGuardFailures}）；下一帧继续只读检查。");
                    }
                    return true;
                }

                bool cleanAttributeInteractionIdle =
                    _battleDecisionEngine.IsCleanDisposableInteractionIdle(attributeOwnership);
                if (!cleanAttributeInteractionIdle)
                {
                    _defenseAttributeSettlementObservationAttempts++;
                    if (_defenseAttributeSettlementObservationAttempts >=
                        MaxDisposableSettlementObservationAttempts)
                    {
                        FinishDefenseMaintenance(
                            "玩家道具预览持续占用交互执行器；自动扩建没有发送任何写命令，已保留玩家操作并结束本轮维护。",
                            warning: true);
                    }
                    else
                    {
                        ScheduleDefenseMaintenanceStep(
                            "检测到玩家或其他系统的活动道具预览；保持等待，不会接管或确认该交互。");
                    }
                    return true;
                }

                _defenseAttributeSettlementObservationAttempts = 0;
                _defenseAttributeConfirmAction = null;
                JObject attributeConfirmResult = new();
                bool attributeConfirmed = ExecuteWithResult(
                    deferredAttributeConfirmation,
                    optional: true,
                    out attributeConfirmResult);
                if (_runState != AutoPlayerRunState.Running) return true;
                RuntimeResultDisposition attributeConfirmDisposition =
                    RuntimeResultInspector.Classify(attributeConfirmResult);
                int confirmedAttributeInteractionInstanceId =
                    _battleDecisionEngine.ReadExpansionInteractionId(
                        attributeConfirmResult,
                        _defensePlacementDisposableEnum);
                if (State(attributeConfirmResult)["isInPreview"]?.Value<bool>() == true &&
                    confirmedAttributeInteractionInstanceId == 0)
                {
                    FaultRequiringProcessRestart(
                        "动力弹射点确认已经创建活动预览，但结果缺少可验证的交互实例身份；" +
                        "为防止错误取消玩家交互，当前进程必须重启。");
                    return true;
                }
                if (confirmedAttributeInteractionInstanceId != 0)
                {
                    _defenseAttributeInteractionInstanceId = confirmedAttributeInteractionInstanceId;
                }
                _ownedPreviewConfirmationOutcomeUncertain =
                    attributeConfirmDisposition == RuntimeResultDisposition.Pending;
                if (!attributeConfirmed &&
                    attributeConfirmDisposition != RuntimeResultDisposition.Pending &&
                    !RuntimeResultInspector.IsSuccess(attributeConfirmResult))
                {
                    _defenseAttributeFailureDetail = "动力弹射点格子确认失败。";
                    _defenseMaintenanceStep = DefenseMaintenanceStep.QueryExpansionAttributeCleanup;
                    ScheduleDefenseMaintenanceStep("格子确认未成功，正在重新确认本次预览后清理。");
                    return true;
                }

                if (attributeConfirmDisposition == RuntimeResultDisposition.Pending &&
                    _defenseAttributeInteractionInstanceId == 0)
                {
                    if (!_defensePendingDisposableMutationGuard.TryArm(
                            deferredAttributeConfirmation,
                            _defensePlacementDisposableEnum,
                            Time.realtimeSinceStartup))
                    {
                        FaultRequiringProcessRestart(
                            "动力弹射点确认返回 pending，但没有交互实例身份且无法建立目标格写入锁；" +
                            "为防止重复确认，当前进程必须重启。");
                        return true;
                    }

                    _defensePendingDisposableQueryCatapults = false;
                    _defensePendingDisposableObservation = null;
                }

                if (attributeConfirmDisposition != RuntimeResultDisposition.Pending &&
                    _defenseAttributeInteractionInstanceId == 0)
                {
                    _ownedPreviewConfirmationOutcomeUncertain = false;
                    _defenseAttributeVerificationAttempts = 0;
                    _defenseMaintenanceStep = DefenseMaintenanceStep.VerifyExpansionAttribute;
                    ScheduleDefenseMaintenanceStep(
                        "动力弹射点确认已同步完成且没有残留预览；下一帧只读验证目标动力站。");
                    return true;
                }

                _defenseAttributeSettlementObservationAttempts = 0;
                _defenseMaintenanceStep = DefenseMaintenanceStep.WaitForExpansionAttributeSettlement;
                ScheduleDefenseMaintenanceStep(
                    "动力弹射点已按稳定背包身份在单次命令中打开并确认；正在等待生成动画完全退出。");
                return true;

            case DefenseMaintenanceStep.WaitForExpansionAttributeSettlement:
                if (_defensePendingDisposableMutationGuard.IsArmed)
                {
                    HandlePendingDefenseDisposableMutation();
                    return true;
                }

                if (!TryInvokeOptionalReadOnly(
                        "queryDisposable",
                        null,
                        out JObject settlingAttributePreview))
                {
                    _defenseAttributeSettlementObservationAttempts++;
                    if (_defenseAttributeSettlementObservationAttempts >=
                        MaxOwnedPreviewReleaseVerificationAttempts)
                    {
                        Fault(
                            "动力弹射点确认后连续无法只读确认预览是否退出；" +
                            "正在按已记录身份清理，本轮会以可恢复故障停止。");
                        return true;
                    }

                    ScheduleDefenseMaintenanceStep(
                        $"动力弹射点预览状态暂时不可读" +
                        $"（{_defenseAttributeSettlementObservationAttempts}/" +
                        $"{MaxOwnedPreviewReleaseVerificationAttempts}），下一帧继续确认。");
                    return true;
                }

                JObject settlingAttributeState = State(settlingAttributePreview);
                if (_battleDecisionEngine.IsOwnedExpansionPreview(
                        settlingAttributePreview,
                        _defenseAttributeInteractionInstanceId,
                        _defensePlacementDisposableEnum,
                        requireGridInteraction: false))
                {
                    _defenseAttributeSettlementObservationAttempts++;
                    if (_defenseAttributeSettlementObservationAttempts >=
                        MaxDisposableSettlementObservationAttempts)
                    {
                        Fault(
                            "动力弹射点确认后预览和生成动画长时间未退出；" +
                            "正在按已记录身份清理，本轮会以可恢复故障停止。");
                        return true;
                    }

                    ScheduleDefenseMaintenanceStep(
                        "动力弹射点生成动画仍在播放；保留当前预览身份，不发送轨道或战车写命令。");
                    return true;
                }

                _ownedPreviewConfirmationOutcomeUncertain = false;
                if (settlingAttributeState["isInPreview"]?.Value<bool>() == true)
                {
                    ScheduleDefenseMaintenanceStep(
                        "自动扩建预览已经退出，但玩家正在进行新的道具预览；等待玩家完成后再验证站点。");
                    return true;
                }

                _defenseAttributeInteractionInstanceId = 0;
                _defenseAttributeVerificationAttempts = 0;
                _defenseMaintenanceStep = DefenseMaintenanceStep.VerifyExpansionAttribute;
                ScheduleDefenseMaintenanceStep("动力弹射点预览已完全退出，正在验证新的未占用动力站。");
                return true;

            case DefenseMaintenanceStep.VerifyExpansionAttribute:
                if (!TryInvokeOptionalReadOnly("queryCatapults", null, out JObject placedAttributeCatapults))
                {
                    _defenseAttributeFailureDetail = "动力弹射点确认后无法读取站点状态。";
                    _defenseMaintenanceStep = DefenseMaintenanceStep.QueryExpansionAttributeCleanup;
                    ScheduleDefenseMaintenanceStep("站点验证失败，正在确认是否仍有本次道具预览需要清理。");
                    return true;
                }

                if (_battleDecisionEngine.CountAvailableExpansionStations(
                        placedAttributeCatapults,
                        _defensePlacementDisposableEnum) >
                    _defensePlacementCountBefore &&
                    HasAvailableExpansionStationAtGrid(
                        placedAttributeCatapults,
                        _defenseAttributeGrid,
                        _defensePlacementDisposableEnum,
                        _defenseAttributeUseAction?.Arguments["stationKind"]?.Value<string>(),
                        _defenseAttributeUseAction?.Arguments["effectIdentity"]?.Value<string>()))
                {
                    bool placedRuntimeSpecial = !string.IsNullOrWhiteSpace(
                        _defenseAttributeUseAction?.Arguments["stationKind"]?.Value<string>());
                    ClearDefenseAttributePlacementState();
                    _defenseCatapults = placedAttributeCatapults;
                    _defenseMaintenanceStep = placedRuntimeSpecial && _defenseBattleSpecialMoveOnly
                        ? DefenseMaintenanceStep.QueryRailExpansionCandidates
                        : DefenseMaintenanceStep.QueryCatapults;
                    ScheduleDefenseMaintenanceStep(
                        placedRuntimeSpecial
                            ? "已按站点类型、canMove 和运行时效果标签验证新特殊站点；下一帧重新计算防线。"
                            : "已验证新的未占用动力站，继续准备额外合法闭环。");
                    return true;
                }

                _defenseAttributeVerificationAttempts++;
                if (_defenseAttributeVerificationAttempts >= MaxDefenseExpansionVerificationAttempts)
                {
                    _defenseAttributeFailureDetail = "动力弹射点确认后未在安全时限内出现新的可用动力站。";
                    _defenseMaintenanceStep = DefenseMaintenanceStep.QueryExpansionAttributeCleanup;
                    ScheduleDefenseMaintenanceStep("未观察到新动力站，正在确认是否仍有本次预览需要清理。");
                    return true;
                }

                ScheduleDefenseMaintenanceStep(
                    $"正在等待新动力站（{_defenseAttributeVerificationAttempts}/{MaxDefenseExpansionVerificationAttempts}）。");
                return true;

            case DefenseMaintenanceStep.QueryExpansionAttributeCleanup:
                if (!TryInvokeOptionalReadOnly("queryDisposable", null, out JObject cleanupAttributePreview))
                {
                    _defenseAttributeCleanupVerificationAttempts++;
                    if (_defenseAttributeCleanupVerificationAttempts >=
                        MaxOwnedPreviewReleaseVerificationAttempts)
                    {
                        Fault(
                            _defenseAttributeFailureDetail +
                            " 连续无法只读确认扩建预览所有权；正在保留身份并进入安全清理故障流程。");
                        return true;
                    }

                    ScheduleDefenseMaintenanceStep(
                        _defenseAttributeFailureDetail +
                        $" 正在重试只读确认预览所有权" +
                        $"（{_defenseAttributeCleanupVerificationAttempts}/" +
                        $"{MaxOwnedPreviewReleaseVerificationAttempts}）。");
                    return true;
                }

                AutomationAction? cleanupAttribute =
                    _battleDecisionEngine.DecideExpansionCancellation(
                        cleanupAttributePreview,
                        _defenseAttributeInteractionInstanceId,
                        _defensePlacementDisposableEnum);
                if (cleanupAttribute == null)
                {
                    bool playerCleanupPreviewActive =
                        State(cleanupAttributePreview)["isInPreview"]?.Value<bool>() == true;
                    FinishDefenseMaintenance(
                        _defenseAttributeFailureDetail +
                        (playerCleanupPreviewActive
                            ? " 当前预览不属于本次扩建，已保留玩家交互且未发送取消命令。"
                            : " 当前没有属于本次扩建的预览，无需取消。"),
                        warning: true);
                    return true;
                }

                _defenseMaintenanceStep = DefenseMaintenanceStep.CancelExpansionAttributeDisposable;
                MarkOwnedPreviewCancellationIssued();
                ExecuteWithResult(
                    cleanupAttribute,
                    optional: true,
                    out JObject defenseCancellationResult);
                ObserveOwnedPreviewCancellationResult(defenseCancellationResult);
                if (_runState == AutoPlayerRunState.Running)
                {
                    _defenseAttributeCleanupVerificationAttempts = 0;
                    _defenseMaintenanceStep = DefenseMaintenanceStep.VerifyExpansionAttributeCleanup;
                    ScheduleDefenseMaintenanceStep(
                        "扩建预览取消命令只发送一次；下一帧只读验证预览是否完全退出。");
                }
                return true;

            case DefenseMaintenanceStep.CancelExpansionAttributeDisposable:
                _defenseMaintenanceStep = DefenseMaintenanceStep.QueryExpansionAttributeCleanup;
                ScheduleDefenseMaintenanceStep("取消前将重新读取预览并在同一帧复核所有权。");
                return true;

            case DefenseMaintenanceStep.VerifyExpansionAttributeCleanup:
                if (!TryInvokeOptionalReadOnly(
                        "queryDisposable",
                        null,
                        out JObject cleanupVerification))
                {
                    _defenseAttributeCleanupVerificationAttempts++;
                    if (_defenseAttributeCleanupVerificationAttempts >=
                        MaxOwnedPreviewReleaseVerificationAttempts)
                    {
                        Fault(
                            _defenseAttributeFailureDetail +
                            " 取消命令提交后无法确认预览是否退出；正在进入安全清理故障流程。");
                        return true;
                    }

                    ScheduleDefenseMaintenanceStep(
                        $"取消后预览状态暂时不可读" +
                        $"（{_defenseAttributeCleanupVerificationAttempts}/" +
                        $"{MaxOwnedPreviewReleaseVerificationAttempts}），不会重复发送取消命令。");
                    return true;
                }

                if (_battleDecisionEngine.IsOwnedExpansionPreview(
                        cleanupVerification,
                        _defenseAttributeInteractionInstanceId,
                        _defensePlacementDisposableEnum,
                        requireGridInteraction: false))
                {
                    _defenseAttributeCleanupVerificationAttempts++;
                    if (_defenseAttributeCleanupVerificationAttempts >=
                        MaxOwnedPreviewReleaseVerificationAttempts)
                    {
                        Fault(
                            _defenseAttributeFailureDetail +
                            " 取消命令只发送一次，但相同扩建预览仍未退出；正在进入安全清理故障流程。");
                        return true;
                    }

                    ScheduleDefenseMaintenanceStep(
                        $"正在等待本次扩建预览退出" +
                        $"（{_defenseAttributeCleanupVerificationAttempts}/" +
                        $"{MaxOwnedPreviewReleaseVerificationAttempts}），不会重复发送取消命令。");
                    return true;
                }

                bool playerPreviewAfterCleanup =
                    State(cleanupVerification)["isInPreview"]?.Value<bool>() == true;
                FinishDefenseMaintenance(
                    _defenseAttributeFailureDetail +
                    (playerPreviewAfterCleanup
                        ? " 已确认本次扩建预览退出；保留玩家的新预览。"
                        : " 已确认本次扩建预览完全退出。"),
                    warning: true);
                return true;

            case DefenseMaintenanceStep.PreviewExpansion:
                if (_defenseExpansionAction == null ||
                    !TryInvokeOptionalReadOnly(
                        "previewRailPath",
                        _defenseExpansionAction.Arguments,
                        out JObject preview))
                {
                    FinishDefenseMaintenance("额外闭环预览失败，已取消本轮扩建。", warning: true);
                    return true;
                }

                if (!_battleDecisionEngine.IsLegalDefenseExpansionPreview(preview))
                {
                    RememberRejectedDefenseExpansionPath(_defenseExpansionAction);
                    FinishDefenseMaintenance("额外闭环未通过玩家合法性或无副作用检查，已取消本轮扩建。", warning: true);
                    return true;
                }

                AddTimeline(
                    "rail-plan",
                    $"额外闭环已携带选定战车身份完成预览；预测回转周期 " +
                    $"{State(preview)["predictedLoopCycleSeconds"]?.Value<double>() ?? 0d:0.###} 秒。");

                _defenseMaintenanceStep = DefenseMaintenanceStep.QueryExpansionRailBaseline;
                ScheduleDefenseMaintenanceStep("额外闭环已通过只读预览，正在记录绘制前的轨道身份基线。");
                return true;

            case DefenseMaintenanceStep.QueryExpansionRailBaseline:
                if (!TryInvokeOptionalReadOnly("queryRail", null, out JObject railBaseline))
                {
                    FinishDefenseMaintenance("无法读取绘制前轨道基线，已取消本轮扩建。", warning: true);
                    return true;
                }

                if (!_battleDecisionEngine.IsUsableDefenseExpansionRailBaseline(railBaseline))
                {
                    FinishDefenseMaintenance("绘制前轨道基线缺少唯一身份或数量不一致，已取消本轮扩建。", warning: true);
                    return true;
                }

                _defenseRailBaselineResult = railBaseline;
                _defenseMaintenanceStep = DefenseMaintenanceStep.DrawExpansion;
                ScheduleDefenseMaintenanceStep("已记录绘制前轨道数量和身份，下一帧按玩家拖线流程创建。");
                return true;

            case DefenseMaintenanceStep.DrawExpansion:
                AutomationAction? expansion = _defenseExpansionAction;
                if (expansion == null || !ExecuteWithResult(expansion, optional: true, out JObject drawResult))
                {
                    RememberRejectedDefenseExpansionPath(expansion);
                    if (_runState == AutoPlayerRunState.Running)
                    {
                        FinishDefenseMaintenance("玩家拖线扩建没有成功提交；已停止本轮扩建，稍后继续正常游玩。", warning: true);
                    }
                    return true;
                }

                _defenseExpansionDrawResult = drawResult;
                _defenseExpectedRailInstanceId = _battleDecisionEngine.ReadDrawnRailInstanceId(drawResult);
                _defenseRailVerificationAttempts = 0;
                _defenseExpansionVerificationAttempts = 0;
                _defenseMaintenanceStep = DefenseMaintenanceStep.VerifyExpansionRail;
                ScheduleDefenseMaintenanceStep("额外闭环已提交，正在验证唯一新增轨道的身份和站点集合。");
                return true;

            case DefenseMaintenanceStep.VerifyExpansionRail:
                if (!TryInvokeOptionalReadOnly("queryRail", null, out JObject currentRails))
                {
                    _defenseExpansionSuspended = true;
                    FinishDefenseMaintenance(
                        "绘制后无法读取轨道状态；为避免重复画线，本局暂停后续自动扩建。",
                        warning: true);
                    return true;
                }

                DefenseExpansionRailVerification railVerification =
                    _battleDecisionEngine.VerifyDefenseExpansionRail(
                        _defenseRailBaselineResult,
                        _defenseExpansionDrawResult,
                        currentRails,
                        _defenseExpansionAction,
                        _defenseExpectedRailInstanceId);
                if (railVerification.Verified && railVerification.Rail != null)
                {
                    _defenseVerifiedRailResult = new JObject
                    {
                        ["rail"] = railVerification.Rail.DeepClone()
                    };
                    _defenseMaintenanceStep = DefenseMaintenanceStep.VerifyExpansion;
                    ScheduleDefenseMaintenanceStep(
                        $"已验证唯一新增轨道 {railVerification.RailInstanceId}，下一帧检查车列。");
                    return true;
                }

                if (railVerification.Pending)
                {
                    _defenseRailVerificationAttempts++;
                    if (_defenseRailVerificationAttempts < MaxDefenseExpansionVerificationAttempts)
                    {
                        ScheduleDefenseMaintenanceStep(
                            $"尚未观察到新增轨道，继续验证（{_defenseRailVerificationAttempts}/{MaxDefenseExpansionVerificationAttempts}）。");
                        return true;
                    }

                    railVerification.Detail = "等待新增轨道超过安全时限。";
                }

                _defenseExpansionSuspended = true;
                FinishDefenseMaintenance(
                    railVerification.Detail + " 为避免重复画线，本局暂停后续自动扩建。",
                    warning: true);
                return true;

            case DefenseMaintenanceStep.VerifyExpansion:
                if (!TryInvokeOptionalReadOnly("queryTrain", null, out JObject expandedTrains))
                {
                    _defenseExpansionSuspended = true;
                    FinishDefenseMaintenance("扩建后无法读取车列，已停止本轮维护以避免重复拖线。", warning: true);
                    return true;
                }

                int expandedTrainCount = CountTrainEntries(expandedTrains);
                if (expandedTrainCount > _defenseTrainCountBeforeExpansion)
                {
                    _defenseTrain = expandedTrains;
                    _defenseVehicle = null;
                    _defenseExpansionVerificationAttempts = 0;
                    _defenseMaintenanceStep = DefenseMaintenanceStep.QueryVehicle;
                    ScheduleDefenseMaintenanceStep("已验证额外闭环生成了新车列，准备把背包战车编入新车列。");
                    return true;
                }

                if (_defenseExpansionVerificationAttempts == 0)
                {
                    AutomationAction? placeVehicle = _battleDecisionEngine.DecideExpansionVehiclePlacement(
                        _defenseVehicle,
                        _defenseVerifiedRailResult);
                    if (placeVehicle != null)
                    {
                        _defensePendingAction = placeVehicle;
                        _defenseMaintenanceStep = DefenseMaintenanceStep.PlaceExpansionVehicle;
                        ScheduleDefenseMaintenanceStep(placeVehicle.Reason);
                        return true;
                    }
                }

                _defenseExpansionVerificationAttempts++;
                if (_defenseExpansionVerificationAttempts >= MaxDefenseExpansionVerificationAttempts)
                {
                    _defenseExpansionSuspended = true;
                    FinishDefenseMaintenance(
                        "额外闭环提交后未在安全时限内观察到新车列；已停止本轮维护以避免重复创建轨道。",
                        warning: true);
                    return true;
                }

                ScheduleDefenseMaintenanceStep(
                    $"额外闭环已提交，正在等待新车列（{_defenseExpansionVerificationAttempts}/{MaxDefenseExpansionVerificationAttempts}）。");
                return true;

            case DefenseMaintenanceStep.PlaceExpansionVehicle:
                AutomationAction? placement = _defensePendingAction;
                _defensePendingAction = null;
                if (placement == null || !ExecuteWithResult(placement, optional: true, out _))
                {
                    _defenseExpansionSuspended = true;
                    if (_runState == AutoPlayerRunState.Running)
                    {
                        FinishDefenseMaintenance(
                            "新闭环没有自动车头，且玩家放车流程未能创建车列；已停止本轮维护以避免重复操作。",
                            warning: true);
                    }
                    return true;
                }

                _defenseExpansionVerificationAttempts = 1;
                _defenseMaintenanceStep = DefenseMaintenanceStep.VerifyExpansion;
                ScheduleDefenseMaintenanceStep("战车已放入新闭环，正在验证新车列已经创建。");
                return true;

            case DefenseMaintenanceStep.MoveVehicle:
                AutomationAction? pendingAction = _defensePendingAction;
                _defensePendingAction = null;
                bool executed = pendingAction != null && Execute(pendingAction, optional: true);
                ResetDefenseMaintenanceState();
                _defenseMaintenanceReady = false;
                if (!executed) _defenseMaintenanceRequested = false;
                return true;

            default:
                FinishDefenseMaintenance("防线维护状态无效，已安全重置。", warning: true);
                return true;
        }
    }

    private bool HasMergeAutomationContract()
    {
        string[] commands =
        {
            "openMergePanel",
            "queryMergeState",
            "selectMergeVehicle",
            "submitMergeSelection",
            "chooseMergeFetter",
            "queryMergeUiState",
            "closeMergePanel",
            "confirmMergeSettlement"
        };
        return commands.All(_bridge.HasCommand);
    }

    private void RunMergeAutomationStep()
    {
        if (_mergePassStartedAt >= 0f &&
            Time.realtimeSinceStartup - _mergePassStartedAt >= MergePassTimeoutSeconds)
        {
            BeginMergeReconciliation("本次原生合成流程 30 秒内没有完成，正在只读对账面板阶段。");
            return;
        }

        MergeAutomationDecision decision = _mergeAutomationPlanner.Decide(
            _mergeAutomationQueryResult,
            _mergeAutomationState);
        AutomationAction? action = decision.Action;
        if (action == null)
        {
            _mergeAutomationState = decision.NextState;
            _mergeAutomationQueryResult = null;
            if (decision.CompletionKind == MergeAutomationCompletionKind.SafeEmptyPanel)
            {
                _defenseMaintenanceStep = DefenseMaintenanceStep.CloseMergePanel;
                ScheduleDefenseMaintenanceStep(decision.Detail + " 正在按原生关闭动作退出空白合成面板。");
            }
            else
            {
                BeginMergeReconciliation(decision.Detail);
            }
            return;
        }

        if (string.Equals(action.Command, "wait", StringComparison.OrdinalIgnoreCase))
        {
            _mergeAutomationState = decision.NextState;
            _mergeAutomationQueryResult = null;
            ScheduleDefenseMaintenanceStep(decision.Detail);
            return;
        }

        if (string.Equals(action.Command, "selectMergeVehicle", StringComparison.OrdinalIgnoreCase))
        {
            JObject mergeState = State(_mergeAutomationQueryResult);
            int itemInstanceId = action.Arguments["itemInstanceId"]?.Value<int>() ?? 0;
            int itemIndex = action.Arguments["index"]?.Value<int>() ?? -1;
            JObject? item = (mergeState["mergeVehicles"] as JArray)?.OfType<JObject>()
                .FirstOrDefault(candidate => itemInstanceId != 0
                    ? candidate["instanceId"]?.Value<int>() == itemInstanceId
                    : candidate["index"]?.Value<int>() == itemIndex);
            int targetInstanceId = item?["instanceId"]?.Value<int>() ?? itemInstanceId;
            string targetPath = item?["path"]?.Value<string>() ?? string.Empty;
            NativeSelectionTarget? target = targetInstanceId == 0 && string.IsNullOrWhiteSpace(targetPath)
                ? null
                : NativeSelectionTarget.ByInstance(
                    "MetroTD.UISystem.RebuildUI_MergeRebuildPanel_VehicleItem",
                    targetInstanceId,
                    targetPath);
            if (target != null && TryWaitForSelectionPreview(
                    "merge-vehicle",
                    action,
                    target,
                    "已用绿色边框标出下一辆升星素材战车；观察时间结束后再选择。"))
            {
                return;
            }
        }

        if (string.Equals(action.Command, "chooseMergeFetter", StringComparison.OrdinalIgnoreCase))
        {
            JObject mergeState = State(_mergeAutomationQueryResult);
            int optionIndex = action.Arguments["index"]?.Value<int>() ?? -1;
            JObject? option = (mergeState["mergeOptions"] as JArray)?.OfType<JObject>()
                .FirstOrDefault(candidate => candidate["index"]?.Value<int>() == optionIndex);
            int targetInstanceId = option?["instanceId"]?.Value<int>() ?? 0;
            string targetPath = option?["path"]?.Value<string>() ?? string.Empty;
            NativeSelectionTarget? target = targetInstanceId == 0 && string.IsNullOrWhiteSpace(targetPath)
                ? null
                : NativeSelectionTarget.ByInstance(
                    "MetroTD.UISystem.RebuildUI_Option_Merge",
                    targetInstanceId,
                    targetPath);
            if (target != null && TryWaitForSelectionPreview(
                    "merge-fetter",
                    action,
                    target,
                    "已用绿色边框标出将选择的合成附魔；观察时间结束后再选择。"))
            {
                return;
            }
        }

        if (string.Equals(action.Command, "queryMergeState", StringComparison.OrdinalIgnoreCase))
        {
            if (!TryInvokeOptionalReadOnly(action.Command, action.Arguments, out JObject queryResult))
            {
                BeginMergeReconciliation("自动合成无法读取游戏原生合成面板。");
                return;
            }

            _mergeAutomationState = decision.NextState;
            _mergeAutomationQueryResult = queryResult;
            ScheduleDefenseMaintenanceStep(decision.Detail);
            return;
        }

        bool executed = ExecuteWithResult(action, optional: true, out JObject result);
        if (!executed)
        {
            if (_runState == AutoPlayerRunState.Running)
            {
                if (_mergeMutationSettlementGuard.IsArmed)
                {
                    _mergeAutomationState = decision.NextState;
                    _mergeAutomationQueryResult = null;
                }

                BeginMergeReconciliation(
                    "自动合成命令 " + action.Command + " 未得到最终成功确认。");
            }

            return;
        }

        _mergeAutomationState = decision.NextState;
        _mergeAutomationQueryResult = string.Equals(
            action.Command,
            "selectMergeVehicle",
            StringComparison.OrdinalIgnoreCase)
            ? result
            : null;
        if (string.Equals(action.Command, "chooseMergeFetter", StringComparison.OrdinalIgnoreCase))
        {
            _mergeSettlementWaitStartedAt = Time.realtimeSinceStartup;
            _mergeSettlementObservedAt = -1f;
            _mergeSettlementQueryFailures = 0;
            _defenseMaintenanceStep = DefenseMaintenanceStep.ObserveMergeSettlement;
            ScheduleDefenseMaintenanceStep("已选择合成附魔，正在等待游戏原生结算画面出现。");
            return;
        }

        ScheduleDefenseMaintenanceStep(decision.Detail);
    }

    private void ObserveMergeSettlement()
    {
        float now = Time.realtimeSinceStartup;
        if (_mergeSettlementWaitStartedAt < 0f)
        {
            _mergeSettlementWaitStartedAt = now;
        }

        if (!TryInvokeOptionalReadOnly("queryMergeUiState", null, out JObject queryResult))
        {
            _mergeSettlementQueryFailures++;
            if (_mergeSettlementQueryFailures >= 3)
            {
                PauseForRecoverableRuntimeState(
                    "连续三次无法读取合成结算阶段。已暂停并保留当前界面，不要求重启游戏。" );
                return;
            }

            ScheduleDefenseMaintenanceStep(
                $"合成结算阶段暂时不可读（{_mergeSettlementQueryFailures}/3），下一帧继续确认。");
            return;
        }

        _mergeSettlementQueryFailures = 0;
        JObject state = State(queryResult);
        if (state["mergeOpen"]?.Value<bool>() != true)
        {
            RecoverFromClosedMergePanel(
                "选择合成附魔后原生面板已经关闭；将以实际战车状态判断合成结果。");
            return;
        }

        if (state["settlementVisible"]?.Value<bool>() == true)
        {
            if (_mergeSettlementObservedAt < 0f)
            {
                _mergeSettlementObservedAt = now;
                AddTimeline("merge-settlement", "已观察到游戏原生合成结算，保留画面 0.75 秒供观察。");
            }

            float confirmationAt = _mergeSettlementObservedAt + MergeSettlementObservationSeconds;
            if (now < confirmationAt)
            {
                _nextTickAt = Math.Max(_nextTickAt, confirmationAt);
                SetStage(AutomationStage.PreparingDefense, "合成结算已经稳定，正在保留 0.75 秒观察时间。");
                return;
            }

            _defenseMaintenanceStep = DefenseMaintenanceStep.ConfirmMergeSettlement;
            ScheduleDefenseMaintenanceStep("合成结算观察时间已结束，准备按原生确认按钮完成结算。");
            return;
        }

        if (now - _mergeSettlementWaitStartedAt >= MergeSettlementAppearanceTimeoutSeconds)
        {
            BeginMergeReconciliation(
                "选择合成附魔后 10 秒内没有观察到原生结算画面。");
            return;
        }

        ScheduleDefenseMaintenanceStep("正在等待合成动画完成并显示原生结算画面。");
    }

    private void ConfirmMergeSettlement()
    {
        AutomationAction action = new(
            "confirmMergeSettlement",
            null,
            AutomationStage.PreparingDefense,
            "按游戏原生确认按钮完成合成结算。");
        bool executed = ExecuteWithResult(action, optional: true, out JObject result);
        if (!executed || State(result)["mergeOpen"]?.Value<bool>() == true)
        {
            if (_runState == AutoPlayerRunState.Running)
            {
                BeginMergeReconciliation("游戏未确认合成结算已经关闭。");
            }

            return;
        }

        _mergePassCount++;
        AddTimeline("merge", $"已通过玩家等价流程完成第 {_mergePassCount} 次战车合成。");
        ResetCurrentMergePass();
        _defenseMaintenanceStep = DefenseMaintenanceStep.QueryTrain;
        ScheduleDefenseMaintenanceStep("合成已经完成，正在重新读取战车与车列，检查是否还能继续合成。");
    }

    private void CloseMergePanel()
    {
        AutomationAction action = new(
            "closeMergePanel",
            null,
            AutomationStage.PreparingDefense,
            "按游戏原生关闭按钮退出空白合成面板。");
        bool executed = ExecuteWithResult(action, optional: true, out JObject result);
        if (!executed || State(result)["mergeOpen"]?.Value<bool>() == true)
        {
            if (_runState == AutoPlayerRunState.Running)
            {
                BeginMergeReconciliation("合成面板无法在安全的空白选车阶段关闭。");
            }

            return;
        }

        _mergeExhausted = true;
        ResetCurrentMergePass();
        _defenseMaintenanceStep = DefenseMaintenanceStep.QueryTrain;
        ScheduleDefenseMaintenanceStep("当前没有更多合法合成组，已关闭原生面板并重新读取防线。");
    }

    private void BeginMergeReconciliation(string reason)
    {
        _mergeRecoveryReason = reason;
        _mergeAutomationQueryResult = null;
        _defenseMaintenanceStep = DefenseMaintenanceStep.ReconcileMerge;
        ScheduleDefenseMaintenanceStep(reason + " 正在只读确认面板实际阶段。");
    }

    private void ReconcileMergeState()
    {
        if (_mergeMutationSettlementGuard.IsArmed)
        {
            ReconcileUnknownMergeMutation();
            return;
        }

        _mergeRecoveryAttempts++;
        if (!TryInvokeOptionalReadOnly("queryMergeUiState", null, out JObject queryResult))
        {
            if (_mergeRecoveryAttempts >= 3)
            {
                PauseForRecoverableRuntimeState(
                    _mergeRecoveryReason +
                    " 连续三次无法只读确认合成面板，自动游玩已暂停；游戏进程无需重启。" );
                return;
            }

            ScheduleDefenseMaintenanceStep(
                $"合成面板对账暂时失败（{_mergeRecoveryAttempts}/3），下一帧重试。");
            return;
        }

        JObject state = State(queryResult);
        if (state["mergeOpen"]?.Value<bool>() != true)
        {
            RecoverFromClosedMergePanel(_mergeRecoveryReason + " 面板现已关闭。");
            return;
        }

        if (state["settlementVisible"]?.Value<bool>() == true)
        {
            _mergeSettlementWaitStartedAt = Time.realtimeSinceStartup;
            _mergeSettlementObservedAt = -1f;
            _mergeSettlementQueryFailures = 0;
            _defenseMaintenanceStep = DefenseMaintenanceStep.ObserveMergeSettlement;
            ScheduleDefenseMaintenanceStep(
                _mergeRecoveryReason + " 已对账到原生结算阶段，将重新观察后安全确认。");
            return;
        }

        bool emptySelection = state["isInSelect"]?.Value<bool>() == true &&
                              state["selectedVehicleCount"]?.Value<int?>() == 0;
        if (emptySelection && _mergeRecoveryAttempts < 3)
        {
            _defenseMaintenanceStep = DefenseMaintenanceStep.CloseMergePanel;
            ScheduleDefenseMaintenanceStep(
                _mergeRecoveryReason + " 已对账到空白选车阶段，准备按原生关闭动作退出。");
            return;
        }

        string phase = state["phase"]?.Value<string>() ?? "unknown";
        int selectedCount = state["selectedVehicleCount"]?.Value<int?>() ?? -1;
        PauseForRecoverableRuntimeState(
            _mergeRecoveryReason +
            $" 当前面板阶段为 {phase}，已选素材数为 {selectedCount}；自动游玩不会接管半完成选择。" +
            " 请在游戏中完成或关闭该面板后继续，游戏进程无需重启。");
    }

    private void ReconcileUnknownMergeMutation()
    {
        _mergeRecoveryAttempts++;
        JObject? queryResult = null;
        if (TryInvokeOptionalReadOnly("queryMergeState", null, out JObject observedState))
        {
            queryResult = observedState;
        }

        MergeMutationSettlementStatus status = _mergeMutationSettlementGuard.Observe(
            queryResult,
            Time.realtimeSinceStartup,
            MergeMutationSettlementTimeoutSeconds);
        if (status == MergeMutationSettlementStatus.Waiting)
        {
            ScheduleDefenseMaintenanceStep(
                "合成写命令结果未知，正在用轻量面板状态只读对账；已锁定且不会重放。" +
                $"（第 {_mergeRecoveryAttempts} 次观察）");
            return;
        }

        if (status == MergeMutationSettlementStatus.TimedOut)
        {
            string timedOutCommand = _mergeMutationSettlementGuard.Command;
            if (_mergeMutationSettlementGuard.OutcomeUnknown)
            {
                FaultRequiringProcessRestart(
                    "合成写命令 " + timedOutCommand +
                    " 已禁止重放，但 20 秒内无法通过面板身份、名单、车辆选择或阶段变化证明最终结果；" +
                    "这是未能对账的写入结果未知，请彻底重启游戏进程。");
            }
            else
            {
                PauseForRecoverableRuntimeState(
                    "合成写命令 " + timedOutCommand +
                    " 长时间没有收敛；已保留写入锁且不会重放。请在游戏中完成或关闭合成面板后继续。");
            }

            return;
        }

        if (status != MergeMutationSettlementStatus.Settled || queryResult == null)
        {
            ScheduleDefenseMaintenanceStep(
                "合成写命令对账尚未取得完整面板快照；继续保持锁定。");
            return;
        }

        string command = _mergeMutationSettlementGuard.Command;
        _mergeMutationSettlementGuard.Reset();
        _pendingActionKey = string.Empty;
        _mergeRecoveryAttempts = 0;
        _mergeAutomationQueryResult = queryResult;
        switch (command)
        {
            case "confirmMergeSettlement":
                _mergePassCount++;
                AddTimeline("merge", $"已通过只读对账确认第 {_mergePassCount} 次战车合成结算完成。");
                ResetCurrentMergePass();
                _defenseMaintenanceStep = DefenseMaintenanceStep.QueryTrain;
                ScheduleDefenseMaintenanceStep("合成结算已经通过面板状态对账，正在重新读取实际战车。");
                return;

            case "closeMergePanel":
                _mergeExhausted = true;
                ResetCurrentMergePass();
                _defenseMaintenanceStep = DefenseMaintenanceStep.QueryTrain;
                ScheduleDefenseMaintenanceStep("合成面板关闭已经通过只读状态确认，正在重新读取防线。");
                return;

            case "chooseMergeFetter":
                _mergeSettlementWaitStartedAt = Time.realtimeSinceStartup;
                _mergeSettlementObservedAt = -1f;
                _mergeSettlementQueryFailures = 0;
                _defenseMaintenanceStep = DefenseMaintenanceStep.ObserveMergeSettlement;
                ScheduleDefenseMaintenanceStep("合成附魔选择已经对账，正在观察原生结算画面。");
                return;

            default:
                _defenseMaintenanceStep = DefenseMaintenanceStep.RunMerge;
                ScheduleDefenseMaintenanceStep(
                    "合成写命令已经通过只读面板状态对账；按最新状态继续规划，不会重放原命令。");
                return;
        }
    }

    private void RecoverFromClosedMergePanel(string detail)
    {
        _mergeExhausted = true;
        AddWarning(detail);
        ResetCurrentMergePass();
        _defenseMaintenanceStep = DefenseMaintenanceStep.QueryTrain;
        ScheduleDefenseMaintenanceStep(detail + " 正在重新读取实际战车与车列状态。");
    }

    private void ResetCurrentMergePass()
    {
        _mergeAutomationState = MergeAutomationState.Initial;
        _mergeAutomationQueryResult = null;
        _mergeSettlementWaitStartedAt = -1f;
        _mergeSettlementObservedAt = -1f;
        _mergeSettlementQueryFailures = 0;
        _mergePassStartedAt = -1f;
        _mergeRecoveryReason = string.Empty;
        _mergeRecoveryAttempts = 0;
    }

    private void ScheduleDefenseMaintenanceStep(string detail)
    {
        _nextTickAt = Time.realtimeSinceStartup + BattleTacticFrameDelaySeconds;
        SetStage(AutomationStage.PreparingDefense, detail);
    }

    private void HandleTransientFreshMovableStationMismatch()
    {
        const string detail =
            "弹射点身份或可移动状态与上一份只读快照暂时不一致；未发送写命令，也不会把候选或布局标记为已完成。";

        if (_defenseBattleSpecialMoveOnly)
        {
            FinishDefenseMaintenance(
                detail + " 本轮战术维护已安全结束，将由下一正常战术周期重新读取。",
                warning: false);
            return;
        }

        _defenseFreshMovableStationRetryAttempts++;
        if (_defenseFreshMovableStationRetryAttempts <= MaxFreshMovableStationRetryAttempts)
        {
            _defenseMaintenanceStep = DefenseMaintenanceStep.QueryFreshMovableStation;
            _nextTickAt = Time.realtimeSinceStartup + FreshMovableStationRetryDelaySeconds;
            SetStage(
                AutomationStage.PreparingDefense,
                detail +
                $" 将在 {FreshMovableStationRetryDelaySeconds:0.##} 秒后重新读取" +
                $"（{_defenseFreshMovableStationRetryAttempts}/{MaxFreshMovableStationRetryAttempts}）。");
            return;
        }

        FinishDefenseMaintenance(
            detail + " 连续只读刷新仍未收敛，已结束本轮维护，避免阻塞开战。",
            warning: true);
    }

    private void HandleTransientMoveGridInitializationFailure(string error)
    {
        string detail =
            "弹射点候选网格运行时暂时不可用；未发送写命令，也不会消费候选或把当前布局标记为稳定。" +
            (string.IsNullOrWhiteSpace(error) ? string.Empty : " " + error);

        _defenseMoveGridInitializationRetryAttempts++;
        if (_defenseMoveGridInitializationRetryAttempts <= MaxMoveGridInitializationRetryAttempts)
        {
            _defenseMaintenanceStep = DefenseMaintenanceStep.QueryRailExpansionCandidates;
            _nextTickAt = Time.realtimeSinceStartup + MoveGridInitializationRetryDelaySeconds;
            SetStage(
                _defenseBattleSpecialMoveOnly
                    ? AutomationStage.Battle
                    : AutomationStage.PreparingDefense,
                detail +
                $" 将在 {MoveGridInitializationRetryDelaySeconds:0.##} 秒后只读重读" +
                $"（{_defenseMoveGridInitializationRetryAttempts}/{MaxMoveGridInitializationRetryAttempts}）。");
            return;
        }

        FinishDefenseMaintenance(
            detail + " 连续重读仍不可用，已结束本轮维护；后续正常维护周期仍会重新评估。",
            warning: true);
    }

    private void ContinueDefenseRailOptimization(string detail)
    {
        if (_defenseStructuralMutationGuard.IsArmed ||
            _defensePendingDisposableMutationGuard.IsArmed)
        {
            FaultRequiringProcessRestart(
                "轨道结构事务尚未完成只读对账，不能开始下一次轨道优化。");
            return;
        }

        _defenseMaintenanceRequested = true;
        _defenseMaintenanceReady = true;
        _defenseMaintenanceStep = DefenseMaintenanceStep.QueryCatapults;
        _defenseCatapults = null;
        _defenseExpansionAction = null;
        _defenseRailExpansionBaseline = null;
        _defenseVerifiedRailResult = null;
        _defenseRailInsertionCandidates = Array.Empty<RailInsertionCandidate>();
        _defenseRailInsertionScores.Clear();
        _defenseRailInsertionPreviewIndex = 0;
        _defenseSelectedRailInsertion = null;
        _defenseSpecialMoveCandidate = null;
        _defenseSpecialMoveGrid = null;
        _defenseSpecialMoveInteractionInstanceId = 0;
        _defenseSpecialMovePredictedCycleSeconds = 0d;
        _defenseSpecialMoveCancelRequested = false;
        _defenseSpecialMoveConfirmationAccepted = false;
        _defenseRailRebuildSnapshot = null;
        _defenseRailRebuildRecoveryAttempted = false;
        _defenseRailRebuildExplicitPollution = false;
        _defenseRailRebuildPreviewCycleSeconds = 0d;
        _defenseRailRebuildCandidates = Array.Empty<RailRebuildSnapshot>();
        _defenseRailRebuildScores.Clear();
        _defenseRailRebuildCandidateIndex = 0;
        _defenseFreshMovableStationRetryAttempts = 0;
        _defenseMoveGridInitializationRetryAttempts = 0;
        _defenseStructuralVerificationAttempts = 0;
        _defenseStationGridProbe.Reset();
        ScheduleDefenseMaintenanceStep(
            detail +
            " 本次动作已得到确定结果且没有未对账的结构写事务；正在重新读取站点与轨道并继续寻找下一项正收益优化。");
    }

    private void ResetDefenseRailMaintenanceSession()
    {
        _defenseRailMaintenanceActionFingerprints.Clear();
        _defenseRailMaintenanceLayoutFingerprint = string.Empty;
        _defenseRailMaintenanceStableLayoutFingerprint = string.Empty;
    }

    private void MarkDefenseRailMaintenanceStable()
    {
        if (!string.IsNullOrWhiteSpace(_defenseRailMaintenanceLayoutFingerprint))
        {
            _defenseRailMaintenanceStableLayoutFingerprint =
                _defenseRailMaintenanceLayoutFingerprint;
        }
    }

    private static string BuildDefenseRailMaintenanceLayoutFingerprint(
        JObject? railResult,
        JObject? catapultResult,
        JObject? trainResult)
    {
        IEnumerable<string> rails =
            (State(railResult)["rails"] as JArray)?.OfType<JObject>()
                .Select(rail =>
                {
                    IEnumerable<string> lines =
                        (rail["lines"] as JArray)?.OfType<JObject>()
                            .Select(line =>
                            {
                                string from = GridFingerprint(line["from"] as JObject);
                                string to = GridFingerprint(line["to"] as JObject);
                                return string.CompareOrdinal(from, to) <= 0
                                    ? from + ">" + to
                                    : to + ">" + from;
                            })
                            .OrderBy(value => value, StringComparer.Ordinal)
                        ?? Enumerable.Empty<string>();
                    return string.Join(
                        ":",
                        "rail",
                        rail["instanceId"]?.Value<int?>() ?? 0,
                        rail["railInternalId"]?.Value<int?>() ??
                        rail["id"]?.Value<int?>() ?? 0,
                        rail["stationCount"]?.Value<int?>() ??
                        rail["pointCount"]?.Value<int?>() ?? 0,
                        rail["isLegalPlayerLoop"]?.Value<bool?>() == true ? 1 : 0,
                        FingerprintNumber(rail["railLength"]),
                        string.Join(",", lines));
                })
                .OrderBy(value => value, StringComparer.Ordinal)
            ?? Enumerable.Empty<string>();

        IEnumerable<string> catapults =
            (State(catapultResult)["catapults"] as JArray)?.OfType<JObject>()
                .Select(catapult => string.Join(
                    ":",
                    "station",
                    catapult["catapultInstanceId"]?.Value<int?>() ??
                    catapult["instanceId"]?.Value<int?>() ?? 0,
                    catapult["linePointInstanceId"]?.Value<int?>() ?? 0,
                    catapult["path"]?.Value<string>() ?? string.Empty,
                    catapult["recycleDisposableEnum"]?.Value<string>() ?? string.Empty,
                    GridFingerprint(catapult["grid"] as JObject),
                    catapult["railId"]?.Value<int?>() ??
                    catapult["railInternalId"]?.Value<int?>() ?? 0,
                    catapult["railMembershipCount"]?.Value<int?>() ?? 0,
                    catapult["isAttribute"]?.Value<bool?>() == true ? 1 : 0,
                    catapult["isSpecial"]?.Value<bool?>() == true ? 1 : 0,
                    catapult["canMove"]?.Value<bool?>() == true ? 1 : 0,
                    catapult["active"]?.Value<bool?>() == false ? 0 : 1))
                .OrderBy(value => value, StringComparer.Ordinal)
            ?? Enumerable.Empty<string>();

        IEnumerable<string> trains =
            (State(trainResult)["trains"] as JArray)?.OfType<JObject>()
                .Select(train =>
                {
                    IEnumerable<string> vehicles =
                        (train["vehicles"] as JArray)?.OfType<JObject>()
                            .Select(vehicle => string.Join(
                                ",",
                                vehicle["instanceId"]?.Value<int?>() ?? 0,
                                vehicle["level"]?.Value<int?>() ?? 0,
                                vehicle["isFixedHead"]?.Value<bool?>() == true ? 1 : 0))
                            .OrderBy(value => value, StringComparer.Ordinal)
                        ?? Enumerable.Empty<string>();
                    return string.Join(
                        ":",
                        "train",
                        train["railId"]?.Value<int?>() ?? 0,
                        train["index"]?.Value<int?>() ?? 0,
                        string.Join(";", vehicles));
                })
                .OrderBy(value => value, StringComparer.Ordinal)
            ?? Enumerable.Empty<string>();

        return string.Join("|", rails.Concat(catapults).Concat(trains));
    }

    private string BuildDefenseRailMoveCandidateFingerprint(RailStationMoveCandidate candidate) =>
        string.Join(
            "|",
            _defenseRailMaintenanceLayoutFingerprint,
            "move-candidate",
            candidate.RailInstanceId,
            candidate.StationFingerprint,
            candidate.CurrentGrid.X,
            candidate.CurrentGrid.Y);

    private string BuildDefenseRailMoveActionFingerprint(
        RailStationMoveCandidate candidate,
        AutoPlayerGrid targetGrid) =>
        string.Join(
            "|",
            _defenseRailMaintenanceLayoutFingerprint,
            "move",
            candidate.RailInstanceId,
            candidate.StationFingerprint,
            candidate.CurrentGrid.X,
            candidate.CurrentGrid.Y,
            targetGrid.X,
            targetGrid.Y);

    private string BuildDefenseRailMovePairFingerprint(
        RailStationMoveCandidate candidate,
        AutoPlayerGrid targetGrid)
    {
        string source = candidate.CurrentGrid.X + "," + candidate.CurrentGrid.Y;
        string target = targetGrid.X + "," + targetGrid.Y;
        string first = string.CompareOrdinal(source, target) <= 0 ? source : target;
        string second = string.CompareOrdinal(source, target) <= 0 ? target : source;
        return string.Join(
            "|",
            "move-pair",
            candidate.RailInternalId,
            candidate.StationFingerprint,
            first,
            second);
    }

    private void RememberCommittedSpecialStationMove()
    {
        if (_defenseSpecialMoveCandidate == null || _defenseSpecialMoveGrid == null) return;
        AutoPlayerGrid target = new(
            _defenseSpecialMoveGrid["x"]?.Value<int>() ?? _defenseSpecialMoveCandidate.CurrentGrid.X,
            _defenseSpecialMoveGrid["y"]?.Value<int>() ?? _defenseSpecialMoveCandidate.CurrentGrid.Y);
        _defenseRailMaintenanceActionFingerprints.Add(
            BuildDefenseRailMoveActionFingerprint(_defenseSpecialMoveCandidate, target));
        _defenseRailMaintenanceActionFingerprints.Add(
            BuildDefenseRailMovePairFingerprint(_defenseSpecialMoveCandidate, target));
    }

    private void FinishCommittedSpecialStationMove(RailInsertionVerification verification)
    {
        RememberCommittedSpecialStationMove();
        _defenseStructuralMutationGuard.Reset();
        string detail = verification.StructureValid
            ? verification.Detail + " 已保留游戏实际结果并结束本轮轨道调整，后续布局变化时会重新评估。"
            : verification.Detail + " 确认命令已经成功，无法再取消；已停止本轮结构写入并继续正常游玩。";
        FinishDefenseMaintenance(detail, warning: true);
    }

    private string BuildDefenseRailInsertionActionFingerprint(
        RailInsertionPreviewScore score) =>
        string.Join(
            "|",
            _defenseRailMaintenanceLayoutFingerprint,
            "insert",
            score.Candidate.Identity);

    private static bool ImprovesRailTriggerRate(
        RailRebuildSnapshot candidate,
        double candidateCycleSeconds)
    {
        if (candidateCycleSeconds <= 0d || candidate.LoopCycleSeconds <= 0d) return false;
        int originalStationCount = candidate.OriginalOrderedLinePointInstanceIds.Count;
        if (originalStationCount == 0)
        {
            originalStationCount = Math.Max(1, candidate.OrderedLinePointInstanceIds.Count - 1);
        }
        double originalRate = originalStationCount / candidate.LoopCycleSeconds;
        double candidateRate = candidate.OrderedLinePointInstanceIds.Count / candidateCycleSeconds;
        return candidateRate > originalRate + 0.000001d;
    }

    private static string GridFingerprint(JObject? grid) =>
        (grid?["x"]?.ToString(Newtonsoft.Json.Formatting.None) ?? "?") + "," +
        (grid?["y"]?.ToString(Newtonsoft.Json.Formatting.None) ?? "?");

    private static string FingerprintNumber(JToken? value) =>
        value?.Value<double?>()?.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture)
        ?? "?";

    private void FinishDefenseMaintenance(string detail, bool warning = false)
    {
        if (_defenseStructuralMutationGuard.IsArmed)
        {
            _defenseMaintenanceRequested = true;
            _defenseMaintenanceReady = true;
            if (warning) AddWarning(detail);
            SetStage(
                AutomationStage.Recovery,
                detail + " 结构写命令仍在只读对账中，因此保留事务状态且不会重发命令。");
            return;
        }

        if (_defensePendingDisposableMutationGuard.IsArmed)
        {
            _defenseMaintenanceRequested = true;
            _defenseMaintenanceReady = true;
            ResetDefenseMaintenanceState();
            if (warning) AddWarning(detail);
            SetStage(
                AutomationStage.Recovery,
                detail + " 动力弹射点确认仍在只读对账中，因此保留写入账本且不会重发命令。");
            return;
        }

        bool battleSpecialMove = _defenseBattleSpecialMoveOnly;
        _defenseMaintenanceRequested = false;
        _defenseMaintenanceReady = false;
        ResetDefenseMaintenanceState();
        if (warning) AddWarning(detail);
        if (battleSpecialMove)
        {
            _battleTacticStep = BattleTacticStep.Complete;
            SetStage(AutomationStage.Battle, detail);
        }
        else
        {
            SetStage(AutomationStage.PreparingDefense, detail);
        }
    }

    private bool BeginSpecialStationMoveRollbackVerificationIfInactive(
        JObject moveState,
        string detail)
    {
        if (State(moveState).SelectToken("currentMoveInteraction.active")?.Value<bool>() == true)
        {
            return false;
        }

        _defenseVerifiedRailResult = null;
        _defenseStructuralVerificationAttempts = 0;
        _defenseMaintenanceStep = DefenseMaintenanceStep.VerifySpecialStationMoveRollbackRail;
        ScheduleDefenseMaintenanceStep(detail);
        return true;
    }

    private void ResetDefenseMaintenanceState()
    {
        if (_defenseStructuralMutationGuard.IsArmed)
        {
            _defenseMaintenanceRequested = true;
            _defenseMaintenanceReady = true;
            return;
        }

        bool preservePendingDisposableMutation = _defensePendingDisposableMutationGuard.IsArmed;
        bool preserveUnknownMerge = _mergeMutationSettlementGuard.IsArmed;
        _defenseMaintenanceStep = preservePendingDisposableMutation
            ? DefenseMaintenanceStep.WaitForExpansionAttributeSettlement
            : preserveUnknownMerge
                ? DefenseMaintenanceStep.ReconcileMerge
                : DefenseMaintenanceStep.QueryTrain;
        _defenseTrain = null;
        _defenseVehicle = null;
        if (!preserveUnknownMerge)
        {
            ResetCurrentMergePass();
            _mergeExhausted = false;
            _mergePassCount = 0;
        }
        _defenseCatapults = null;
        _defensePendingAction = null;
        _defenseExpansionAction = null;
        _defenseExpansionDrawResult = null;
        _defenseRailBaselineResult = null;
        _defenseVerifiedRailResult = null;
        _defenseExpectedRailInstanceId = 0;
        _defenseRailVerificationAttempts = 0;
        _defenseTrainCountBeforeExpansion = 0;
        _defenseExpansionVerificationAttempts = 0;
        _defenseNeedsNewLoopExpansion = false;
        _defensePlacementDisposableEnum = "FreePoint_Attribute";
        _defensePlacementCountBefore = 0;
        _defenseRailExpansionBaseline = null;
        _defenseRailInsertionCandidates = Array.Empty<RailInsertionCandidate>();
        _defenseRailInsertionScores.Clear();
        _defenseRailInsertionPreviewIndex = 0;
        _defenseSelectedRailInsertion = null;
        _defenseSpecialMoveCandidate = null;
        _defenseSpecialMoveGrid = null;
        _defenseSpecialMoveInteractionInstanceId = 0;
        _defenseSpecialMovePredictedCycleSeconds = 0d;
        _defenseBattleSpecialMoveOnly = false;
        _defenseSpecialMoveCancelRequested = false;
        _defenseSpecialMoveConfirmationAccepted = false;
        _defenseRailRebuildSnapshot = null;
        _defenseRailRebuildRecoveryAttempted = false;
        _defenseRailRebuildExplicitPollution = false;
        _defenseRailRebuildPreviewCycleSeconds = 0d;
        _defenseRailRebuildCandidates = Array.Empty<RailRebuildSnapshot>();
        _defenseRailRebuildScores.Clear();
        _defenseRailRebuildCandidateIndex = 0;
        _defenseFreshMovableStationRetryAttempts = 0;
        _defenseMoveGridInitializationRetryAttempts = 0;
        _defenseStructuralVerificationAttempts = 0;
        _defenseRailMaintenanceLayoutFingerprint = string.Empty;
        _defenseStationGridProbe.Reset();
        if (!preservePendingDisposableMutation)
        {
            ClearDefenseAttributePlacementState();
        }
    }

    private void ClearDefenseAttributePlacementState()
    {
        if (_defensePendingDisposableMutationGuard.IsArmed)
        {
            return;
        }

        _defenseExpansionAttributeGridProbe.Reset();
        _defenseAttributeUseAction = null;
        _defenseAttributeConfirmAction = null;
        _defenseAttributeGrid = null;
        _defenseAttributeInteractionInstanceId = 0;
        _defenseAttributeCountBeforePlacement = 0;
        _defenseAttributeVerificationAttempts = 0;
        _defenseAttributeSettlementObservationAttempts = 0;
        _defenseAttributeCleanupVerificationAttempts = 0;
        _defenseAttributeFailureDetail = string.Empty;
        ResetPendingDefenseDisposableObservation();
        ResetOwnedPreviewCancellationTrackingIfNoIdentity();
    }

    private void RememberRejectedDefenseExpansionPath(AutomationAction? action)
    {
        string key = BattleDecisionEngine.BuildDefenseExpansionPathKey(
            action?.Arguments["linePointInstanceIds"]);
        if (!string.IsNullOrWhiteSpace(key)) _rejectedDefenseExpansionPaths.Add(key);
    }

    private static int CountTrainEntries(JObject? trainResult) =>
        (State(trainResult)["trains"] as JArray)?.OfType<JObject>().Count() ?? 0;

    private bool TryInvokeOptionalReadOnly(string command, JObject? arguments, out JObject result)
    {
        result = new JObject();
        if (!command.StartsWith("query", StringComparison.OrdinalIgnoreCase) &&
            !command.StartsWith("preview", StringComparison.OrdinalIgnoreCase))
        {
            AddWarning("内部拒绝把写命令 " + command + " 当作只读查询执行。");
            return false;
        }

        if (!_bridge.HasCommand(command))
        {
            AddWarning("当前游戏构建不支持可选自动战术命令 " + command + "，已跳过该战术。");
            return false;
        }

        JObject invocation = _bridge.Invoke(command, arguments);
        switch (RuntimeResultInspector.ClassifyReadOnly(invocation))
        {
            case RuntimeResultDisposition.Pending:
            case RuntimeResultDisposition.Failure:
                AddWarning("可选自动战术命令 " + command + " 未执行：" + Message(invocation));
                return false;
            default:
                result = invocation;
                return true;
        }
    }

    private bool IssueGuardedDefenseMutation(
        AutomationAction action,
        string mutationIdentity,
        out RuntimeResultDisposition disposition,
        out JObject result)
    {
        disposition = RuntimeResultDisposition.Failure;
        result = new JObject();
        if (ShouldBlockActiveBattleTrainMutation(action.Command, out string blockedDetail))
        {
            AddWarning(blockedDetail);
            SetStage(AutomationStage.Battle, blockedDetail);
            return false;
        }
        if (_defenseStructuralMutationGuard.IsArmed)
        {
            if (!_defenseStructuralMutationGuard.IsPreparedFor(action, mutationIdentity))
            {
                SetStage(
                    AutomationStage.Recovery,
                    "上一条结构写命令仍在只读对账，已拒绝发送新的写命令。");
                return false;
            }
        }
        else if (!_defenseStructuralMutationGuard.TryArm(
                action,
                mutationIdentity,
                Time.realtimeSinceStartup))
        {
            FaultRequiringProcessRestart("无法为结构写命令建立写一次事务身份。");
            return false;
        }

        SetStage(action.Stage, action.Reason);
        _pendingActionKey = string.Empty;
        _lastCommand = action.Command;
        _lastActionAtUtc = DateTime.UtcNow;
        _defenseStructuralMutationGuard.MarkInvocationIssued();
        result = _bridge.Invoke(action.Command, action.Arguments);
        InvalidateFullWaveQueryCache();
        _lastRuntimeResult = result;
        _lastMessage = Message(result);
        disposition = RuntimeResultInspector.Classify(result);
        if (disposition is RuntimeResultDisposition.Pending or RuntimeResultDisposition.Unsafe)
        {
            _defenseStructuralMutationGuard.MarkOutcomeUnknown();
        }

        AddTimeline(
            disposition is RuntimeResultDisposition.Pending or RuntimeResultDisposition.Unsafe
                ? "defense-mutation-pending"
                : "defense-mutation",
            action.Reason + " " + _lastMessage);
        return true;
    }

    private AutomationAction BuildSpecialStationMoveConfirmation() => new(
        "confirmStationMoveGrid",
        new JObject
        {
            ["grid"] = _defenseSpecialMoveGrid?.DeepClone() ?? JValue.CreateNull()
        },
        AutomationStage.PreparingDefense,
        "通过正式确认接口把能量/特殊弹射点移动到已校验格子。");

    private string BuildSpecialStationMoveConfirmationIdentity() =>
        "station-move-confirm:" +
        _defenseSpecialMoveInteractionInstanceId + ":" +
        (_defenseSpecialMoveGrid?.ToString(Newtonsoft.Json.Formatting.None) ?? "missing-grid");

    private AutomationAction BuildSpecialStationMoveCancellation() => new(
        "cancelDisposable",
        JObject.FromObject(new
        {
            interactionInstanceId = _defenseSpecialMoveInteractionInstanceId
        }),
        AutomationStage.Recovery,
        "取消经身份验证仍属于本次自动游玩的能量/特殊弹射点移动预览。");

    private void RequestDefenseMaintenance()
    {
        ResetDefenseRailMaintenanceSession();
        _defenseMaintenanceRequested = true;
        _defenseMaintenanceReady = false;
        ResetDefenseMaintenanceState();
    }

    private void ResetOpeningDefensePreparation()
    {
        _openingDefensePreparationActive = false;
        _deferOpeningDefenseCommandOnce = false;
        _openingDefenseInteractionInstanceId = 0;
        _openingDefenseWaitingForForeignPreview = false;
        _openingDefenseConfirmGuardFailures = 0;
        _openingPendingDisposableMutationGuard.Reset();
        ResetPendingOpeningDisposableObservation();
        _openingDefensePreparationPlanner.Reset();
        ResetOwnedPreviewCancellationTrackingIfNoIdentity();
    }

    private void ResumeOrResetOpeningDefensePreparation()
    {
        _openingDefensePreparationActive = false;
        _deferOpeningDefenseCommandOnce = false;
        _openingDefenseInteractionInstanceId = 0;
        _openingDefenseWaitingForForeignPreview = false;
        _openingDefenseConfirmGuardFailures = 0;
        if (_openingPendingDisposableMutationGuard.IsArmed)
        {
            // The planner remains at WaitForPlacementSettlement. Preserving it is the
            // write-once ledger that prevents a pending grid confirmation from replaying.
        }
        else if (_openingDefensePreparationPlanner.HasCommittedWrite)
        {
            _openingDefensePreparationPlanner.ResumeCommittedTransaction();
        }
        else
        {
            _openingDefensePreparationPlanner.Reset();
            ResetPendingOpeningDisposableObservation();
        }

        ResetOwnedPreviewCancellationTrackingIfNoIdentity();
    }

    private void ResetBattleTactics()
    {
        ResetDefenseRailMaintenanceSession();
        _battleLiveDisposableGridProbe.Reset();
        _nextBattleWaveProbeAt = 0f;
        _nextBattleTacticCycleAt = 0f;
        _battleDisposableUsedThisWave = false;
        _battleDisposableUnavailableThisWave = false;
        ClearOwnedDisposable();
        _battleThreats = null;
        _battleWaveEndPendingPreviewRelease = false;
        BeginBattleTacticCycle();
    }

    private void BeginBattleTacticCycle()
    {
        _battleLiveDisposableGridProbe.Reset();
        _battleThreats = null;
        _battleTacticStep = BattleTacticStep.QueryThreats;
        _battleTacticPending = false;
        _battleWaveSnapshot = null;
        _battlePendingAction = null;
        _battleDisposable = null;
        _battleRail = null;
        _battleTrain = null;
        _battleConfirmationArguments = null;
        _battleDisposableSettlementObservationAttempts = 0;
    }

    private bool TryBeginBattleSpecialStationMaintenance()
    {
        if (_defenseStructuralMutationGuard.IsArmed ||
            _defensePendingDisposableMutationGuard.IsArmed ||
            _mergeMutationSettlementGuard.IsArmed ||
            _defenseAttributeInteractionInstanceId != 0 ||
            _ownedDisposableInteractionInstanceId != 0 ||
            State(_battleDisposable)["isInPreview"]?.Value<bool>() == true)
        {
            return false;
        }

        if (!_bridge.HasCommand("queryCatapults") ||
            !_bridge.HasCommand("queryRail") ||
            !_bridge.HasCommand("queryMovableStationState") ||
            !_bridge.HasCommand("queryDisposableGridOptions") ||
            !_bridge.HasCommand("startStationMove") ||
            !_bridge.HasCommand("confirmStationMoveGrid") ||
            !_bridge.HasCommand("cancelDisposable"))
        {
            return false;
        }

        ResetDefenseMaintenanceState();
        _defenseBattleSpecialMoveOnly = true;
        _defenseNeedsNewLoopExpansion = false;
        _defenseMaintenanceRequested = true;
        _defenseMaintenanceReady = true;
        _defenseMaintenanceStep = DefenseMaintenanceStep.QueryCatapults;
        SetStage(
            AutomationStage.Battle,
            "本波列车调度已完成；准备持续移动可随时调整的弹射点，直到周向覆盖与站点触发率 N/T 都无法继续改善。");
        return true;
    }

    private bool BeginOwnedPreviewRelease(
        OwnedPreviewReleaseOperation operation,
        string faultReason,
        out string message)
    {
        if (_ownedPreviewReleaseOperation != OwnedPreviewReleaseOperation.None)
        {
            if (operation == OwnedPreviewReleaseOperation.Fault ||
                operation == OwnedPreviewReleaseOperation.Stop &&
                _ownedPreviewReleaseOperation == OwnedPreviewReleaseOperation.Pause)
            {
                _ownedPreviewReleaseOperation = operation;
            }

            if (!string.IsNullOrWhiteSpace(faultReason))
            {
                _ownedPreviewReleaseFaultReason = faultReason;
            }
            if (operation == OwnedPreviewReleaseOperation.Fault)
            {
                ArmFaultWhilePreviewReleaseRuns();
            }

            message = "正在确认并清理由自动游玩创建的道具预览；完成后会自动执行" +
                      OwnedPreviewReleaseOperationName(_ownedPreviewReleaseOperation) + "。";
            _lastMessage = message;
            return true;
        }

        if (!HasOwnedAutomationPreviewIdentity())
        {
            message = string.Empty;
            return false;
        }

        _ownedPreviewReleaseOperation = operation;
        _ownedPreviewReleaseStep = _ownedPreviewCancellationAlreadyIssued
            ? OwnedPreviewReleaseStep.VerifyReleased
            : OwnedPreviewReleaseStep.QueryOwnership;
        _ownedPreviewReleaseCancelAction = null;
        _ownedPreviewReleaseFaultReason = faultReason;
        _ownedPreviewReleaseCancelFailure = string.Empty;
        _ownedPreviewReleaseQueryFailureAttempts = 0;
        _ownedPreviewReleaseVerificationAttempts = 0;
        _nextTickAt = 0f;
        if (operation == OwnedPreviewReleaseOperation.Fault)
        {
            ArmFaultWhilePreviewReleaseRuns();
        }
        message = _ownedPreviewCancellationAlreadyIssued
            ? "自动游玩已经发送过一次取消命令；现在只会逐帧验证预览是否退出，不会重放写命令。"
            : "检测到由自动游玩创建的道具预览；将在同一帧确认所有权并只发送一次取消命令，完成后会自动执行" +
              OwnedPreviewReleaseOperationName(operation) + "。";
        _lastMessage = message;
        AddTimeline("preview-cleanup", message);
        return true;
    }

    private void ProcessOwnedPreviewRelease()
    {
        lock (_sync)
        {
            if (_ownedPreviewReleaseOperation == OwnedPreviewReleaseOperation.None) return;
            if (_ownedPreviewReleaseStep != OwnedPreviewReleaseStep.VerifyReleased &&
                !_bridge.HasCommand("cancelDisposable"))
            {
                FailOwnedPreviewRelease("当前游戏构建缺少道具预览取消命令。");
                return;
            }

            switch (_ownedPreviewReleaseStep)
            {
                case OwnedPreviewReleaseStep.QueryOwnership:
                    if (!TryQueryOwnedPreviewForRelease(out JObject current)) return;

                    AutomationAction? cancelAction = BuildOwnedPreviewCancellation(current);
                    if (cancelAction == null)
                    {
                        CompleteOwnedPreviewRelease(
                            "当前道具预览已消失或不属于自动游玩，未发送取消命令。");
                        return;
                    }

                    _ownedPreviewReleaseCancelAction = cancelAction;
                    _ownedPreviewReleaseStep = OwnedPreviewReleaseStep.CancelOwnedPreview;
                    ExecuteOwnedPreviewCancellation(cancelAction);
                    return;

                case OwnedPreviewReleaseStep.CancelOwnedPreview:
                    if (!TryQueryOwnedPreviewForRelease(out JObject refreshedPreview)) return;
                    AutomationAction? refreshedCancel = BuildOwnedPreviewCancellation(refreshedPreview);
                    if (refreshedCancel == null)
                    {
                        CompleteOwnedPreviewRelease(
                            "取消前重新确认时，自动游玩预览已经消失或被玩家交互替换；未发送取消命令。");
                        return;
                    }

                    _ownedPreviewReleaseCancelAction = refreshedCancel;
                    ExecuteOwnedPreviewCancellation(refreshedCancel);
                    return;

                case OwnedPreviewReleaseStep.VerifyReleased:
                    if (!TryQueryOwnedPreviewForRelease(out JObject verification)) return;
                    bool defensePreviewRemains =
                        _battleDecisionEngine.IsOwnedExpansionPreview(
                            verification,
                            _defenseAttributeInteractionInstanceId,
                            _defensePlacementDisposableEnum,
                            requireGridInteraction: false);
                    bool openingPreviewRemains =
                        _battleDecisionEngine.IsOwnedExpansionAttributePreview(
                            verification,
                            _openingDefenseInteractionInstanceId,
                            requireGridInteraction: false);
                    bool battlePreviewRemains = IsOwnedDisposablePreview(verification);
                    if (!openingPreviewRemains && !defensePreviewRemains && !battlePreviewRemains)
                    {
                        CompleteOwnedPreviewRelease("已验证自动游玩创建的道具预览完全退出。");
                        return;
                    }

                    _ownedPreviewReleaseVerificationAttempts++;
                    if (_ownedPreviewReleaseVerificationAttempts >= MaxOwnedPreviewReleaseVerificationAttempts)
                    {
                        string failure = string.IsNullOrWhiteSpace(_ownedPreviewReleaseCancelFailure)
                            ? "取消后仍能观察到相同枚举和交互身份的道具预览。"
                            : "取消命令失败且相同道具预览仍然存在：" + _ownedPreviewReleaseCancelFailure;
                        FailOwnedPreviewRelease(failure);
                        return;
                    }

                    ScheduleOwnedPreviewRelease(
                        $"正在等待道具预览退出（{_ownedPreviewReleaseVerificationAttempts}/{MaxOwnedPreviewReleaseVerificationAttempts}）；不会重复发送取消命令。");
                    return;
            }
        }
    }

    private AutomationAction? BuildOwnedPreviewCancellation(JObject current)
    {
        AutomationAction? cancelAction =
            _battleDecisionEngine.DecideExpansionAttributeCancellation(
                current,
                _openingDefenseInteractionInstanceId);
        cancelAction ??=
            _battleDecisionEngine.DecideExpansionAttributeCancellation(
                current,
                _defenseAttributeInteractionInstanceId);
        if (cancelAction == null && IsOwnedDisposablePreview(current))
        {
            cancelAction = new AutomationAction(
                "cancelDisposable",
                JObject.FromObject(new
                {
                    disposableEnum = _ownedDisposableEnum,
                    interactionInstanceId = _ownedDisposableInteractionInstanceId
                }),
                AutomationStage.Recovery,
                "取消由自动游玩创建的战斗道具预览，恢复玩家输入。");
        }

        return cancelAction;
    }

    private void ExecuteOwnedPreviewCancellation(AutomationAction ownedCancel)
    {
        MarkOwnedPreviewCancellationIssued();
        _lastCommand = ownedCancel.Command;
        _lastActionAtUtc = DateTime.UtcNow;
        JObject cancelResult = _bridge.Invoke(ownedCancel.Command, ownedCancel.Arguments);
        _lastRuntimeResult = cancelResult;
        _lastMessage = Message(cancelResult);
        RuntimeResultDisposition cancelDisposition = RuntimeResultInspector.Classify(cancelResult);
        if (cancelDisposition == RuntimeResultDisposition.Unsafe)
        {
            _ownedPreviewReleaseCancellationOutcomeUncertain = true;
            FailOwnedPreviewRelease(UnsafeWriteMessage(ownedCancel.Command, cancelResult));
            return;
        }

        ObserveOwnedPreviewCancellationResult(cancelResult);
        if (cancelDisposition == RuntimeResultDisposition.Failure)
        {
            _ownedPreviewReleaseCancelFailure = Message(cancelResult);
        }

        _ownedPreviewReleaseStep = OwnedPreviewReleaseStep.VerifyReleased;
        _ownedPreviewReleaseVerificationAttempts = 0;
        ScheduleOwnedPreviewRelease(
            cancelDisposition == RuntimeResultDisposition.Pending
                ? "取消命令已提交并等待游戏确认；下一帧只读验证预览是否退出。"
                : "取消命令已在同一帧复核所有权后只发送一次；下一帧只读验证预览是否退出。");
    }

    private bool TryQueryOwnedPreviewForRelease(out JObject result)
    {
        result = _openingDefenseInteractionGuard.Query();
        RuntimeResultDisposition disposition = RuntimeResultInspector.ClassifyReadOnly(result);
        if (disposition == RuntimeResultDisposition.Success)
        {
            _ownedPreviewReleaseQueryFailureAttempts = 0;
            return true;
        }

        _ownedPreviewReleaseQueryFailureAttempts++;
        if (_ownedPreviewReleaseQueryFailureAttempts >= MaxOwnedPreviewReleaseVerificationAttempts)
        {
            FailOwnedPreviewRelease("无法确认当前道具预览所有权：" + Message(result));
            return false;
        }

        ScheduleOwnedPreviewRelease(
            $"道具预览查询尚未就绪（{_ownedPreviewReleaseQueryFailureAttempts}/{MaxOwnedPreviewReleaseVerificationAttempts}）；下一帧继续只读确认。");
        return false;
    }

    private void ScheduleOwnedPreviewRelease(string detail)
    {
        _nextTickAt = Time.realtimeSinceStartup + BattleTacticFrameDelaySeconds;
        SetStage(
            _ownedPreviewReleaseOperation == OwnedPreviewReleaseOperation.Fault
                ? AutomationStage.Recovery
                : _stage,
            detail);
    }

    private void CompleteOwnedPreviewRelease(string detail)
    {
        OwnedPreviewReleaseOperation operation = _ownedPreviewReleaseOperation;
        string faultReason = _ownedPreviewReleaseFaultReason;
        bool resetOpeningDefense = _openingDefenseInteractionInstanceId != 0;
        bool resetDefense = _defenseAttributeInteractionInstanceId != 0;
        if (resetOpeningDefense) ResumeOrResetOpeningDefensePreparation();
        ClearOwnedDisposable();
        ResetBattleTactics();
        if (resetDefense) RequestDefenseMaintenance();
        ResetOwnedPreviewReleaseState();
        ResetOwnedPreviewCancellationTrackingIfNoIdentity();
        AddTimeline("preview-cleanup", detail);

        switch (operation)
        {
            case OwnedPreviewReleaseOperation.Pause:
                ApplyPause();
                break;
            case OwnedPreviewReleaseOperation.Stop:
                ApplyStop();
                break;
            case OwnedPreviewReleaseOperation.Fault:
                CommitFault(faultReason);
                break;
        }
    }

    private void FailOwnedPreviewRelease(string detail)
    {
        OwnedPreviewReleaseOperation operation = _ownedPreviewReleaseOperation;
        string faultReason = _ownedPreviewReleaseFaultReason;
        bool cancellationAlreadyIssued = _ownedPreviewCancellationAlreadyIssued;
        bool confirmationOutcomeUncertain = _ownedPreviewConfirmationOutcomeUncertain;
        bool mutationOutcomeUncertain =
            _ownedPreviewReleaseCancellationOutcomeUncertain ||
            confirmationOutcomeUncertain ||
            _needsProcessRestart;
        ResetOwnedPreviewReleaseState();
        string reason = operation == OwnedPreviewReleaseOperation.Fault &&
                        !string.IsNullOrWhiteSpace(faultReason)
            ? faultReason + " 预览清理未完成：" + detail
            : OwnedPreviewReleaseOperationName(operation) + "前无法安全清理自动游玩创建的道具预览：" + detail;
        if (mutationOutcomeUncertain)
        {
            RequireProcessRestart();
            CommitFault(
                reason +
                (confirmationOutcomeUncertain
                    ? " 确认写命令曾返回等待状态，后续未能只读证明其最终结果；请彻底重启游戏进程。"
                    : " 取消写命令的最终结果无法确认，请彻底重启游戏进程。"));
            return;
        }

        CommitFault(
            reason +
            (cancellationAlreadyIssued
                ? " 取消命令已经返回可验证的确定结果，但后续只读核验失败；"
                : " 当前仅有只读所有权确认失败，尚未发送可能生效的取消写命令；") +
            "无需重启游戏进程，可在状态恢复后重新开始以再次清理。");
    }

    private bool HasOwnedAutomationPreviewIdentity() =>
        _openingDefenseInteractionInstanceId != 0 ||
        _defenseAttributeInteractionInstanceId != 0 ||
        !string.IsNullOrWhiteSpace(_ownedDisposableEnum) &&
        _ownedDisposableInteractionInstanceId != 0;

    private void ArmFaultWhilePreviewReleaseRuns()
    {
        _runState = AutoPlayerRunState.Faulted;
        _stage = AutomationStage.Recovery;
        if (_outcome is AutomationOutcome.Unknown or AutomationOutcome.InProgress)
        {
            _outcome = AutomationOutcome.Error;
        }
    }

    private void ResetOwnedPreviewReleaseState()
    {
        _ownedPreviewReleaseOperation = OwnedPreviewReleaseOperation.None;
        _ownedPreviewReleaseStep = OwnedPreviewReleaseStep.QueryOwnership;
        _ownedPreviewReleaseCancelAction = null;
        _ownedPreviewReleaseFaultReason = string.Empty;
        _ownedPreviewReleaseCancelFailure = string.Empty;
        _ownedPreviewReleaseQueryFailureAttempts = 0;
        _ownedPreviewReleaseVerificationAttempts = 0;
    }

    private static string OwnedPreviewReleaseOperationName(OwnedPreviewReleaseOperation operation) => operation switch
    {
        OwnedPreviewReleaseOperation.Pause => "暂停",
        OwnedPreviewReleaseOperation.Stop => "停止",
        OwnedPreviewReleaseOperation.Fault => "故障停止",
        _ => "退出"
    };

    private void ApplyPause()
    {
        if (_defenseStructuralMutationGuard.IsArmed)
        {
            _runState = AutoPlayerRunState.Running;
            _defenseMaintenanceRequested = true;
            _defenseMaintenanceReady = true;
            SetStage(
                AutomationStage.Recovery,
                "结构写命令仍在只读对账中；内部暂停请求已拒绝，事务保持锁定。");
            return;
        }

        if (_defensePendingDisposableMutationGuard.IsArmed)
        {
            _runState = AutoPlayerRunState.Running;
            _defenseMaintenanceRequested = true;
            _defenseMaintenanceReady = true;
            _defenseMaintenanceStep = DefenseMaintenanceStep.WaitForExpansionAttributeSettlement;
            SetStage(
                AutomationStage.Recovery,
                "动力弹射点确认仍在只读对账中；内部暂停请求已拒绝，写入账本保持锁定。");
            return;
        }

        ClearDeferredReadDecisions();
        _runState = AutoPlayerRunState.Paused;
        _pausedAtUtc = DateTime.UtcNow;
        _stageDetail = "自动游玩命令已暂停；游戏本身并未暂停。";
        AddTimeline("pause", _stageDetail);
    }

    private void ApplyStop()
    {
        if (_defenseStructuralMutationGuard.IsArmed)
        {
            _runState = AutoPlayerRunState.Running;
            _defenseMaintenanceRequested = true;
            _defenseMaintenanceReady = true;
            SetStage(
                AutomationStage.Recovery,
                "结构写命令仍在只读对账中；内部停止请求已拒绝，事务保持锁定。");
            return;
        }

        if (_defensePendingDisposableMutationGuard.IsArmed)
        {
            _runState = AutoPlayerRunState.Running;
            _defenseMaintenanceRequested = true;
            _defenseMaintenanceReady = true;
            _defenseMaintenanceStep = DefenseMaintenanceStep.WaitForExpansionAttributeSettlement;
            SetStage(
                AutomationStage.Recovery,
                "动力弹射点确认仍在只读对账中；内部停止请求已拒绝，写入账本保持锁定。");
            return;
        }

        ClearDeferredReadDecisions();
        ResumeOrResetOpeningDefensePreparation();
        _runState = AutoPlayerRunState.Standby;
        if (_outcome is AutomationOutcome.Unknown or AutomationOutcome.InProgress)
        {
            _outcome = AutomationOutcome.Stopped;
        }
        _stage = AutomationStage.WaitingForGame;
        _stageDetail = "已停止，不会再向游戏发送命令。";
        AddTimeline("stop", _stageDetail);
        _evidence.WriteStatus(EnsureEvidenceDirectory(), Snapshot());
    }

    private bool TryExecuteActiveBattleAction(AutomationAction action, out JObject result)
    {
        result = new JObject();
        if (ShouldBlockActiveBattleTrainMutation(action.Command, out string blockedDetail))
        {
            AddWarning(blockedDetail);
            SetStage(AutomationStage.Battle, blockedDetail);
            return false;
        }
        if (_bridge.TryGetWavePulse(out bool pulseInWave, out bool pulseGameOver, out int pulseRemaining))
        {
            if (!pulseInWave || pulseGameOver)
            {
                SetStage(AutomationStage.Battle, "波次已经结束，已取消尚未执行的战术动作。");
                return false;
            }

            _battleWaveSnapshot = UpdateBattleWaveSnapshot(pulseInWave, pulseRemaining);
            return ExecuteWithResult(action, optional: true, out result);
        }

        const string detail =
            "战术动作执行前无法读取轻量波次脉冲；为避免在同一帧叠加完整查询与写入，已跳过本次动作。";
        AddWarning(detail);
        SetStage(AutomationStage.Battle, detail);
        return false;
    }

    private static bool IsForbiddenActiveBattleTrainMutation(string command) =>
        string.Equals(command, "moveTrainToLine", StringComparison.OrdinalIgnoreCase) ||
        string.Equals(command, "moveVehicleInTrain", StringComparison.OrdinalIgnoreCase) ||
        string.Equals(command, "placeVehicleOnLine", StringComparison.OrdinalIgnoreCase) ||
        string.Equals(command, "openMergePanel", StringComparison.OrdinalIgnoreCase) ||
        string.Equals(command, "selectMergeVehicle", StringComparison.OrdinalIgnoreCase) ||
        string.Equals(command, "submitMergeSelection", StringComparison.OrdinalIgnoreCase) ||
        string.Equals(command, "chooseMergeFetter", StringComparison.OrdinalIgnoreCase);

    private bool ShouldBlockActiveBattleTrainMutation(string command, out string detail)
    {
        detail = string.Empty;
        if (!IsForbiddenActiveBattleTrainMutation(command))
        {
            return false;
        }

        if (_bridge.TryGetWavePulse(out bool inWave, out bool gameOver, out _))
        {
            if (!inWave || gameOver) return false;
        }
        else if (!_wasInWave)
        {
            return false;
        }

        detail = "战斗中已拒绝直接车列写命令 " + command +
                 "；只允许站点道具、可移动站点和从始发站断环后的玩家原生重连。";
        return true;
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
        _battleLiveDisposableGridProbe.Reset();
        _ownedDisposableEnum = string.Empty;
        _ownedDisposableInteractionInstanceId = 0;
        _battlePendingAction = null;
        _battleDisposableSettlementObservationAttempts = 0;
        ResetOwnedPreviewCancellationTrackingIfNoIdentity();
    }

    private void MarkOwnedPreviewCancellationIssued()
    {
        _ownedPreviewCancellationAlreadyIssued = true;
        _ownedPreviewReleaseCancellationOutcomeUncertain = true;
    }

    private void ObserveOwnedPreviewCancellationResult(JObject result)
    {
        _ownedPreviewCancellationAlreadyIssued = true;
        RuntimeResultDisposition disposition = RuntimeResultInspector.Classify(result);
        _ownedPreviewReleaseCancellationOutcomeUncertain = disposition switch
        {
            RuntimeResultDisposition.Unsafe => true,
            RuntimeResultDisposition.Pending => true,
            RuntimeResultDisposition.Success =>
                State(result)["isInPreview"]?.Type != JTokenType.Boolean ||
                State(result)["isInPreview"]!.Value<bool>(),
            _ => false
        };
    }

    private void ResetOwnedPreviewCancellationTrackingIfNoIdentity()
    {
        if (!HasOwnedAutomationPreviewIdentity())
        {
            ResetOwnedPreviewCancellationTracking();
        }
    }

    private void ResetOwnedPreviewCancellationTracking()
    {
        _ownedPreviewCancellationAlreadyIssued = false;
        _ownedPreviewReleaseCancellationOutcomeUncertain = false;
        _ownedPreviewConfirmationOutcomeUncertain = false;
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

    private static bool TryReadWorldPosition(
        JObject? arguments,
        out double x,
        out double y,
        out double z)
    {
        x = 0d;
        y = 0d;
        z = 0d;
        if (arguments?["world"] is not JObject world ||
            !TryReadFiniteNumber(world["x"], out x) ||
            !TryReadFiniteNumber(world["y"], out y))
        {
            return false;
        }

        if (world["z"] != null && !TryReadFiniteNumber(world["z"], out z))
        {
            return false;
        }

        return true;
    }

    private static bool TryReadFiniteNumber(JToken? token, out double value)
    {
        value = 0d;
        if (token == null || token.Type == JTokenType.Null)
        {
            return false;
        }

        bool parsed = token.Type is JTokenType.Integer or JTokenType.Float
            ? TryReadJsonNumber(token, out value)
            : double.TryParse(
                token.Value<string>(),
                System.Globalization.NumberStyles.Float,
                System.Globalization.CultureInfo.InvariantCulture,
                out value);
        return parsed && !double.IsNaN(value) && !double.IsInfinity(value);
    }

    private static bool TryReadJsonNumber(JToken token, out double value)
    {
        try
        {
            value = token.Value<double>();
            return true;
        }
        catch (Exception)
        {
            value = 0d;
            return false;
        }
    }

    private static bool DidTrainReachMovementTarget(
        JObject moveResult,
        JObject railResult,
        AutomationAction movement)
    {
        int trainIndex = movement.Arguments["trainIndex"]?.Value<int?>() ?? -1;
        int targetLineInstanceId = movement.Arguments["lineInstanceId"]?.Value<int?>() ?? 0;
        if (trainIndex < 0 || targetLineInstanceId == 0)
        {
            return false;
        }

        JObject? targetRail = (State(railResult)["rails"] as JArray)?.OfType<JObject>()
            .FirstOrDefault(rail => (rail["lines"] as JArray)?.OfType<JObject>().Any(line =>
                (line["lineInstanceId"]?.Value<int?>()
                 ?? line["instanceId"]?.Value<int?>()
                 ?? 0) == targetLineInstanceId) == true);
        JObject? targetLine = (targetRail?["lines"] as JArray)?.OfType<JObject>()
            .FirstOrDefault(line =>
                (line["lineInstanceId"]?.Value<int?>()
                 ?? line["instanceId"]?.Value<int?>()
                 ?? 0) == targetLineInstanceId);
        string targetLineName = targetLine?["name"]?.Value<string>() ?? string.Empty;
        int? targetRailId = targetRail?["railInternalId"]?.Value<int?>()
                            ?? targetRail?["id"]?.Value<int?>();
        if (string.IsNullOrWhiteSpace(targetLineName))
        {
            return false;
        }

        JObject? movedTrain = (State(moveResult)["trains"] as JArray)?.OfType<JObject>()
            .FirstOrDefault(train => train["index"]?.Value<int?>() == trainIndex);
        if (movedTrain == null ||
            !string.Equals(movedTrain["line"]?.Value<string>(), targetLineName, StringComparison.Ordinal) ||
            targetRailId.HasValue && movedTrain["railId"]?.Value<int?>() != targetRailId.Value)
        {
            return false;
        }

        bool? expectedForward = movement.Arguments["forward"]?.Value<bool?>();
        return !expectedForward.HasValue || movedTrain["forward"]?.Value<bool?>() == expectedForward.Value;
    }

    private static bool IsFreshDisconnectedMovableStation(
        JObject? catapultResult,
        JObject? movableResult,
        RailStationMoveCandidate candidate)
    {
        JObject[] catapults = (State(catapultResult)["catapults"] as JArray)?.OfType<JObject>()
            .Where(item => (item["catapultInstanceId"]?.Value<int?>() ?? item["instanceId"]?.Value<int?>()) ==
                           candidate.StationCatapultInstanceId)
            .ToArray() ?? Array.Empty<JObject>();
        JObject[] movable = (State(movableResult)["stations"] as JArray)?.OfType<JObject>()
            .Where(item => item["instanceId"]?.Value<int?>() == candidate.StationCatapultInstanceId)
            .ToArray() ?? Array.Empty<JObject>();
        return catapults.Length == 1 && movable.Length == 1 &&
               catapults[0]["canMove"]?.Value<bool>() == true &&
               movable[0]["canMove"]?.Value<bool>() == true &&
               (catapults[0]["railMembershipCount"]?.Value<int?>() ?? 0) == 0 &&
               catapults[0]["gameObjectInstanceId"]?.Value<int?>() == candidate.StationGameObjectInstanceId &&
               catapults[0]["linePointInstanceId"]?.Value<int?>() == candidate.StationLinePointInstanceId &&
               string.Equals(catapults[0]["path"]?.Value<string>(), candidate.StationPath, StringComparison.Ordinal);
    }

    private bool TryRefreshDisconnectedStationAtTarget(
        JObject? catapultResult,
        RailStationMoveCandidate? candidate,
        JObject? expectedGrid,
        RailRebuildSnapshot snapshot)
    {
        if (candidate == null) return false;
        int? x = expectedGrid?["x"]?.Value<int?>();
        int? y = expectedGrid?["y"]?.Value<int?>();
        if (!x.HasValue || !y.HasValue) return false;
        JObject[] matches = (State(catapultResult)["catapults"] as JArray)?.OfType<JObject>()
            .Where(item =>
                string.Equals(item["name"]?.Value<string>(), candidate.StationName, StringComparison.Ordinal) &&
                string.Equals(item["recycleDisposableEnum"]?.Value<string>(),
                    candidate.StationDisposableEnum, StringComparison.Ordinal) &&
                (item["isAttribute"]?.Value<bool>() == true) == candidate.StationIsAttribute &&
                (item["railMembershipCount"]?.Value<int?>() ?? 0) == 0 &&
                item.SelectToken("grid.x")?.Value<int?>() == x.Value &&
                item.SelectToken("grid.y")?.Value<int?>() == y.Value)
            .ToArray() ?? Array.Empty<JObject>();
        if (matches.Length != 1) return false;
        int newPointId = matches[0]["linePointInstanceId"]?.Value<int?>() ?? 0;
        if (newPointId == 0) return false;
        _railRebuildPlanner.RefreshMovedStationIdentity(
            snapshot,
            candidate.StationLinePointInstanceId,
            newPointId);
        return true;
    }

    private static JObject State(JObject? result) =>
        result?.SelectToken("data.state") as JObject
        ?? result?["state"] as JObject
        ?? result
        ?? new JObject();

    private bool HandleWaveObservation(
        bool inWave,
        bool gameOver,
        int remainingEnemies,
        JObject? fullWaveResult)
    {
        ObserveWaveTransition(inWave);
        if (gameOver || GameOutcomeObserver.Outcome is AutomationOutcome.Victory or AutomationOutcome.Defeat)
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

        _battleWaveSnapshot = fullWaveResult ?? UpdateBattleWaveSnapshot(true, remainingEnemies);
        JObject waveState = State(_battleWaveSnapshot);
        SetStage(AutomationStage.Battle, BuildWaveStageDetail(waveState));
        if (_battleTacticStep == BattleTacticStep.Complete &&
            Time.realtimeSinceStartup >= _nextBattleTacticCycleAt)
        {
            BeginBattleTacticCycle();
            _battleWaveSnapshot = fullWaveResult ?? UpdateBattleWaveSnapshot(true, remainingEnemies);
        }

        _nextBattleWaveProbeAt = Time.realtimeSinceStartup + Math.Max(
            MinimumBattlePollIntervalSeconds,
            _settings.TickIntervalSeconds.Value);
        _battleTacticPending = _battleTacticStep != BattleTacticStep.Complete;
        if (_battleTacticPending)
        {
            _nextTickAt = Time.realtimeSinceStartup + BattleTacticFrameDelaySeconds;
        }

        return true;
    }

    private JObject UpdateBattleWaveSnapshot(bool inWave, int remainingEnemies)
    {
        JObject snapshot = _battleWaveSnapshot ?? new JObject
        {
            ["success"] = true,
            ["data"] = new JObject
            {
                ["state"] = new JObject
                {
                    ["isInWaving"] = inWave,
                    ["enemy"] = new JObject()
                }
            }
        };
        JObject state = State(snapshot);
        if (state["isInWaving"]?.Value<bool>() != inWave)
        {
            state["isInWaving"] = inWave;
        }

        JObject enemy = state["enemy"] as JObject ?? new JObject();
        if (state["enemy"] == null) state["enemy"] = enemy;
        if (remainingEnemies >= 0 && enemy["remaining"]?.Value<int?>() != remainingEnemies)
        {
            enemy["remaining"] = remainingEnemies;
        }

        return snapshot;
    }

    private bool TryQueryAdaptiveWaveState(
        string command,
        AutomationStage pendingStage,
        out JObject result,
        out JObject state)
    {
        float now = Time.realtimeSinceStartup;
        if (now < _nextFullWaveQueryAt)
        {
            if (_cachedFullWaveQueryResult == null)
            {
                _nextTickAt = Math.Max(_nextTickAt, _nextFullWaveQueryAt);
                result = new JObject();
                state = new JObject();
                return false;
            }

            result = _cachedFullWaveQueryResult;
            state = State(result);
            return true;
        }

        _freshFullWaveQueryIssued = true;
        bool success = TryQueryState(command, pendingStage, out result, out state);
        UpdateFullWaveQuerySchedule(result);
        return success;
    }

    private void UpdateFullWaveQuerySchedule(JObject result)
    {
        if (RuntimeResultInspector.ClassifyReadOnly(result) == RuntimeResultDisposition.Success)
        {
            _cachedFullWaveQueryResult = result;
        }

        double measuredSeconds = Math.Max(0, _bridge.LastCommandDurationMs) / 1000.0;
        float desired = (float)(measuredSeconds / FullWaveQueryTimeBudgetRatio);
        desired = Math.Max(
            MinimumFullWaveQueryIntervalSeconds,
            Math.Min(MaximumFullWaveQueryIntervalSeconds, desired));
        _adaptiveFullWaveQueryInterval = desired >= _adaptiveFullWaveQueryInterval
            ? desired
            : Math.Max(desired, _adaptiveFullWaveQueryInterval * 0.8f);
        _nextFullWaveQueryAt = Time.realtimeSinceStartup + _adaptiveFullWaveQueryInterval;
    }

    private void ResetFullWaveQueryPolling()
    {
        _nextFullWaveQueryAt = 0f;
        _adaptiveFullWaveQueryInterval = MinimumFullWaveQueryIntervalSeconds;
        _cachedFullWaveQueryResult = null;
        _pendingMapDecisionState = null;
        _pendingOpeningVehicleState = null;
    }

    private void InvalidateFullWaveQueryCache()
    {
        _nextFullWaveQueryAt = 0f;
        _cachedFullWaveQueryResult = null;
        _pendingMapDecisionState = null;
        _pendingOpeningVehicleState = null;
    }

    private void ScheduleContinuationFrame()
    {
        float continuationAt = Time.realtimeSinceStartup + RewardVehicleContextFrameDelaySeconds;
        if (_nextTickAt <= Time.realtimeSinceStartup || _nextTickAt > continuationAt)
        {
            _nextTickAt = continuationAt;
        }
    }

    internal void ClearNativeSelectionHighlight() => ClearSelectionHighlight();

    private bool TryWaitForFrontEndSelectionPreview(AutomationAction action)
    {
        NativeSelectionTarget? target = null;
        string detail = string.Empty;
        if (string.Equals(action.Command, "selectRandomVehicle", StringComparison.OrdinalIgnoreCase))
        {
            string vehicleType = action.Arguments["vehicleType"]?.Value<string>() ?? string.Empty;
            if (!string.IsNullOrWhiteSpace(vehicleType))
            {
                target = NativeSelectionTarget.ByMember(
                    "Systems.UISystem.RandomMode_Selected_Vehicle",
                    "m_vehicleType",
                    vehicleType);
                detail = "已用绿色边框标出将选择的初始战车；观察时间结束后再选择。";
            }
        }
        else if (string.Equals(action.Command, "selectRandomFetter", StringComparison.OrdinalIgnoreCase))
        {
            string fetter = action.Arguments["fetterEnum"]?.Value<string>() ?? string.Empty;
            if (!string.IsNullOrWhiteSpace(fetter))
            {
                target = NativeSelectionTarget.ByMember(
                    "Systems.UISystem.RandomMode_Selected_Fetter",
                    "m_fetter",
                    fetter);
                detail = "已用绿色边框标出将选择的初始附魔；观察时间结束后再选择。";
            }
        }

        return target != null && TryWaitForSelectionPreview("front-end", action, target, detail);
    }

    private bool TryWaitForSelectionPreview(
        string owner,
        AutomationAction action,
        NativeSelectionTarget target,
        string detail)
    {
        string fingerprint = string.Join(
            "|",
            owner,
            action.Command,
            action.Arguments.ToString(Newtonsoft.Json.Formatting.None),
            target.Key);
        float now = Time.realtimeSinceStartup;
        if (!string.Equals(_selectionPreviewFingerprint, fingerprint, StringComparison.Ordinal))
        {
            _selectionPreviewFingerprint = fingerprint;
            _selectionPreviewReadyAt = now + SelectionPreviewObservationSeconds;
            ShowSelectionHighlight(owner, fingerprint, target);
            _nextTickAt = Math.Max(_nextTickAt, _selectionPreviewReadyAt);
            SetStage(action.Stage, detail);
            AddTimeline("selection-preview", detail);
            return true;
        }

        if (!string.Equals(_selectionHighlightOwner, owner, StringComparison.Ordinal) ||
            !string.Equals(_selectionHighlightFingerprint, fingerprint, StringComparison.Ordinal))
        {
            ShowSelectionHighlight(owner, fingerprint, target);
        }

        if (now < _selectionPreviewReadyAt)
        {
            ShowSelectionHighlight(owner, fingerprint, target);
            _nextTickAt = Math.Max(_nextTickAt, _selectionPreviewReadyAt);
            SetStage(action.Stage, detail);
            return true;
        }

        ClearSelectionHighlight(owner);
        _selectionPreviewFingerprint = fingerprint;
        _selectionPreviewReadyAt = 0f;
        return false;
    }

    private void ShowSelectionHighlight(string owner, string fingerprint, NativeSelectionTarget target)
    {
        if (!string.Equals(_selectionHighlightOwner, owner, StringComparison.Ordinal) ||
            !string.Equals(_selectionHighlightFingerprint, fingerprint, StringComparison.Ordinal))
        {
            _selectionHighlighter.Clear();
        }

        _selectionHighlightOwner = owner;
        _selectionHighlightFingerprint = fingerprint;
        if (!_selectionHighlighter.Show(target, out string error) && !string.IsNullOrWhiteSpace(error))
        {
            _log.LogDebug("下一步选择绿色边框暂不可用：" + error);
        }
    }

    private void ClearSelectionHighlight(string owner)
    {
        if (!string.Equals(_selectionHighlightOwner, owner, StringComparison.Ordinal)) return;
        _selectionHighlighter.Clear();
        _selectionHighlightOwner = string.Empty;
        _selectionHighlightFingerprint = string.Empty;
    }

    private void ClearSelectionHighlight()
    {
        _selectionHighlighter.Clear();
        _selectionHighlightOwner = string.Empty;
        _selectionHighlightFingerprint = string.Empty;
        _selectionPreviewFingerprint = string.Empty;
        _selectionPreviewReadyAt = -1f;
        ResetMapPreviewOpenWait();
    }

    private static NativeSelectionTarget? BuildInstanceSelectionTarget(
        AutomationAction action,
        string componentType,
        string instanceProperty,
        string pathProperty)
    {
        int instanceId = action.Arguments[instanceProperty]?.Value<int>() ?? 0;
        string path = action.Arguments[pathProperty]?.Value<string>() ?? string.Empty;
        return instanceId == 0 && string.IsNullOrWhiteSpace(path)
            ? null
            : NativeSelectionTarget.ByInstance(componentType, instanceId, path);
    }

    private void ClearDeferredReadDecisions()
    {
        ClearSelectionHighlight();
        _deferredFrontEndAction = null;
        _deferredNormalEventAction = null;
        _deferredNormalEventChoosingOption = false;
        _deferredRewardAction = null;
        _deferredSettlementAction = null;
        _pendingMapAction = null;
        _pendingMapDecisionState = null;
        _pendingOpeningVehicleState = null;
    }

    private void ScheduleNormalPoll()
    {
        InvalidateFullWaveQueryCache();
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

        if (_deferredSettlementAction != null)
        {
            AutomationAction deferred = _deferredSettlementAction;
            _deferredSettlementAction = null;
            bool clicked = Execute(deferred);
            if (clicked && _runState == AutoPlayerRunState.Running)
            {
                _wishReturnClicked = true;
                SetStage(AutomationStage.Completed, "愿望清单提示已关闭，正在等待结算界面。");
            }

            return;
        }

        JObject interactables = _bridge.Invoke("queryUiInteractables");
        switch (RuntimeResultInspector.ClassifyReadOnly(interactables))
        {
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
            _deferredSettlementAction = new AutomationAction(
                "uiClick",
                JObject.FromObject(new { instanceId = returnButtonInstanceId }),
                AutomationStage.Completed,
                "通过返回按钮关闭愿望清单提示。");
            ScheduleContinuationFrame();
            SetStage(
                AutomationStage.Completed,
                "已读取愿望清单返回按钮；下一帧再点击，避免同帧叠加命令。");

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
        if (string.Equals(action.Command, "selectMapNode", StringComparison.OrdinalIgnoreCase))
        {
            ClearSelectionHighlight("map");
        }
        else if (string.Equals(action.Command, "selectMergeVehicle", StringComparison.OrdinalIgnoreCase))
        {
            ClearSelectionHighlight("merge-vehicle");
        }
        else if (string.Equals(action.Command, "chooseMergeFetter", StringComparison.OrdinalIgnoreCase))
        {
            ClearSelectionHighlight("merge-fetter");
        }
        if (ShouldBlockActiveBattleTrainMutation(action.Command, out string blockedDetail))
        {
            AddWarning(blockedDetail);
            SetStage(AutomationStage.Battle, blockedDetail);
            return false;
        }
        SetStage(action.Stage, action.Reason);
        if (string.Equals(action.Command, "wait", StringComparison.Ordinal))
        {
            _pendingActionKey = string.Empty;
            return false;
        }

        bool rewardSelectionCommand = string.Equals(
            action.Command,
            "chooseRewardOption",
            StringComparison.OrdinalIgnoreCase);
        bool waveFunctionOptionCommand = string.Equals(
            action.Command,
            "chooseWaveFunctionOption",
            StringComparison.OrdinalIgnoreCase);
        bool rewardObjectCollectionCommand = string.Equals(
            action.Command,
            "collectRewardObject",
            StringComparison.OrdinalIgnoreCase);
        int rewardObjectInstanceId = rewardObjectCollectionCommand
            ? action.Arguments["instanceId"]?.Value<int>() ?? 0
            : 0;
        if (rewardObjectCollectionCommand &&
            rewardObjectInstanceId != 0 &&
            _rewardObjectCollectionLedger.Contains(rewardObjectInstanceId) &&
            !_rewardObjectSettlementGuard.IsArmed)
        {
            _nextTickAt = Math.Max(
                _nextTickAt,
                Time.realtimeSinceStartup + RewardSelectionSettlementPollSeconds);
            SetStage(
                action.Stage,
                "该奖励物实例已经领取过；等待旧对象从奖励列表移除，不会再次发送领取命令。");
            return false;
        }

        bool mergeMutationCommand = IsMergeMutationCommand(action.Command);
        if (mergeMutationCommand && _mergeMutationSettlementGuard.IsArmed)
        {
            _nextTickAt = Math.Max(
                _nextTickAt,
                Time.realtimeSinceStartup + BattleTacticFrameDelaySeconds);
            SetStage(
                action.Stage,
                "上一条合成写命令仍在只读对账；锁定解除前不会发送任何新的合成写命令。");
            return false;
        }

        if (rewardObjectCollectionCommand && _rewardObjectSettlementGuard.IsArmed)
        {
            _nextTickAt = Math.Max(
                _nextTickAt,
                Time.realtimeSinceStartup + RewardSelectionSettlementPollSeconds);
            SetStage(
                action.Stage,
                "当前奖励物已经发送过一次领取命令，正在等待对象消失或奖励阶段推进；不会重复领取。");
            return false;
        }

        if (rewardSelectionCommand && _rewardSelectionSettlementGuard.IsArmed)
        {
            _nextTickAt = Math.Max(
                _nextTickAt,
                Time.realtimeSinceStartup + RewardSelectionSettlementPollSeconds);
            SetStage(
                action.Stage,
                "\u5df2\u53d1\u9001\u5f53\u524d\u5956\u52b1\u9009\u62e9\uff0c\u6b63\u5728\u7b49\u5f85\u754c\u9762\u5207\u6362\uff1b\u4e0d\u4f1a\u91cd\u590d\u70b9\u51fb\u3002");
            return false;
        }

        if (waveFunctionOptionCommand && _waveFunctionOptionSettlementGuard.IsArmed)
        {
            _nextTickAt = Math.Max(
                _nextTickAt,
                Time.realtimeSinceStartup + RewardSelectionSettlementPollSeconds);
            SetStage(
                AutomationStage.ManagingEvent,
                "上一条事件选项点击仍在只读对账；锁定解除前不会再次发送 EventUI 或 RepairUI 点击。");
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
        if (IsRewardPanelCommand(action.Command)) ClearRewardVehicleContext();
        result = _bridge.Invoke(action.Command, action.Arguments);
        InvalidateFullWaveQueryCache();
        _lastRuntimeResult = result;
        _lastMessage = Message(result);
        RuntimeResultDisposition disposition = RuntimeResultInspector.Classify(result);
        if (disposition == RuntimeResultDisposition.Unsafe)
        {
            if (mergeMutationCommand && TryArmMergeMutation(action, result, outcomeUnknown: true))
            {
                _pendingActionKey = string.Empty;
                AddWarning(
                    "合成写命令 " + action.Command +
                    " 的调用结果未知；已保留完整身份并切换到只读对账，不会重复执行。");
                return false;
            }

            if (rewardObjectCollectionCommand && TryArmRewardObjectSettlement(action))
            {
                _pendingActionKey = string.Empty;
                AddWarning(
                    "奖励物领取调用已经开始，但写入结果未知；已按对象实例身份锁定，" +
                    "只会查询对象是否消失，不会重复领取。");
                SetStage(
                    AutomationStage.ManagingRewards,
                    "奖励物领取结果未知，正在通过对象消失或奖励阶段推进进行只读对账。");
                return false;
            }

            if (rewardSelectionCommand && TryArmRewardSelection(action))
            {
                _pendingActionKey = string.Empty;
                AddWarning(
                    "奖励选择调用已经开始，但写入结果未知；已按阶段和选项身份锁定，" +
                    "只会查询面板是否收敛，不会重复点击，也不会把未知结果直接报告为已证实污染。");
                SetStage(
                    AutomationStage.ManagingRewards,
                    "奖励选择结果未知，正在通过选项消失、阶段变化或面板关闭进行只读对账。");
                return false;
            }


            if (waveFunctionOptionCommand &&
                TryArmWaveFunctionOptionSettlement(action, result, outcomeUnknown: true))
            {
                _pendingActionKey = string.Empty;
                AddWarning(
                    "事件选项点击已经开始，但结果未知；已按面板和按钮身份锁定，" +
                    "只会读取界面或波次状态进行对账，不会重复点击。");
                SetStage(
                    AutomationStage.ManagingEvent,
                    "事件选项结果未知，正在等待只读状态证明 EventUI 或 RepairUI 已经推进或关闭。");
                return false;
            }

            FaultRequiringProcessRestart(UnsafeWriteMessage(action.Command, result));
            return false;
        }

        if (mergeMutationCommand &&
            disposition == RuntimeResultDisposition.Pending &&
            !_mergeMutationSettlementGuard.IsArmed)
        {
            TryArmMergeMutation(action, result, outcomeUnknown: false);
        }

        bool rewardObjectCollectionIssued = rewardObjectCollectionCommand &&
                                            RuntimeResultInspector.IsSuccess(result) &&
                                            disposition is RuntimeResultDisposition.Pending or
                                                RuntimeResultDisposition.Success;
        if (rewardObjectCollectionIssued && !TryArmRewardObjectSettlement(action))
        {
            Fault(
                "奖励物领取命令已发送，但缺少可验证的对象实例身份；本轮已停止且不会重放该命令。");
            return false;
        }

        bool rewardSelectionIssued = rewardSelectionCommand &&
                                     RuntimeResultInspector.IsSuccess(result) &&
                                     disposition is RuntimeResultDisposition.Pending or RuntimeResultDisposition.Success;
        if (rewardSelectionIssued && !TryArmRewardSelection(action))
        {
            Fault("\u5956\u52b1\u9009\u62e9\u5df2\u53d1\u9001\uff0c\u4f46\u7f3a\u5c11\u53ef\u9a8c\u8bc1\u7684\u9636\u6bb5\u6216\u9009\u9879\u8eab\u4efd\uff1b\u672c\u8f6e\u5df2\u505c\u6b62\u4e14\u4e0d\u4f1a\u91cd\u653e\u3002");
            return false;
        }


        bool waveFunctionOptionIssued = waveFunctionOptionCommand &&
                                        RuntimeResultInspector.IsSuccess(result) &&
                                        disposition is RuntimeResultDisposition.Pending or
                                            RuntimeResultDisposition.Success;
        if (waveFunctionOptionIssued &&
            !TryArmWaveFunctionOptionSettlement(action, result, outcomeUnknown: false))
        {
            Fault("事件选项点击已经发送，但无法建立面板与按钮身份锁；本轮已停止且不会重放该命令。");
            return false;
        }

        if (disposition == RuntimeResultDisposition.Pending)
        {
            if (RuntimeResultInspector.IsSuccess(result))
            {
                _pendingActionKey = actionKey;
                if (IsRewardPanelCommand(action.Command) && !rewardSelectionCommand) ResetRewardOptionObservation();
                if (IsRewardAcquisitionCommand(action.Command) && !rewardSelectionCommand) RequestDefenseMaintenance();
                if (!rewardSelectionCommand) MarkProgress();
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

            RegisterFailure("命令 " + action.Command + " 失败：" + _lastMessage);
            return false;
        }

        if (string.Equals(action.Command, "selectMapNode", StringComparison.OrdinalIgnoreCase) &&
            !RuntimeResultInspector.HasCommittedMapNode(result))
        {
            _consecutiveFailures = 0;
            _mapSelectionPending = true;
            _mapSelectionPendingAt = Time.realtimeSinceStartup;
            ResetEventOptionObservation();
            _pendingMapAction = null;
            MarkProgress();
            AddTimeline("pending", "地图节点已收到点击，正在等待轨神事件或关卡状态提交。");
            SetStage(AutomationStage.SelectingRoute, "地图节点已点击，正在等待轨神事件或关卡状态提交。");
            return true;
        }

        _consecutiveFailures = 0;
        if (IsRewardPanelCommand(action.Command) && !rewardSelectionCommand) ResetRewardOptionObservation();
        if (IsRewardAcquisitionCommand(action.Command) && !rewardSelectionCommand) RequestDefenseMaintenance();
        if (!rewardSelectionCommand) MarkProgress();
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
            ResetEventOptionObservation();
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

    private static bool IsMergeMutationCommand(string command) =>
        string.Equals(command, "openMergePanel", StringComparison.OrdinalIgnoreCase) ||
        string.Equals(command, "selectMergeVehicle", StringComparison.OrdinalIgnoreCase) ||
        string.Equals(command, "submitMergeSelection", StringComparison.OrdinalIgnoreCase) ||
        string.Equals(command, "chooseMergeFetter", StringComparison.OrdinalIgnoreCase) ||
        string.Equals(command, "confirmMergeSettlement", StringComparison.OrdinalIgnoreCase) ||
        string.Equals(command, "closeMergePanel", StringComparison.OrdinalIgnoreCase);

    private bool TryArmMergeMutation(
        AutomationAction action,
        JObject result,
        bool outcomeUnknown) =>
        _mergeMutationSettlementGuard.TryArm(
            action,
            _mergeAutomationQueryResult ?? result,
            outcomeUnknown,
            Time.realtimeSinceStartup);

    private static bool IsRewardPanelCommand(string command) =>
        string.Equals(command, "collectRewardObject", StringComparison.OrdinalIgnoreCase) ||
        string.Equals(command, "chooseRewardOption", StringComparison.OrdinalIgnoreCase) ||
        string.Equals(command, "skipReward", StringComparison.OrdinalIgnoreCase);

    private bool TryArmRewardSelection(AutomationAction action)
    {
        string phaseToken = action.Arguments["phaseToken"]?.Value<string>() ?? string.Empty;
        int itemInstanceId = action.Arguments["itemInstanceId"]?.Value<int>() ?? 0;
        bool armed = _rewardSelectionSettlementGuard.TryArm(
            phaseToken,
            itemInstanceId,
            Time.realtimeSinceStartup);
        if (armed)
        {
            _nextTickAt = Math.Max(
                _nextTickAt,
                Time.realtimeSinceStartup + RewardSelectionSettlementPollSeconds);
        }

        return armed;
    }

    private bool TryArmWaveFunctionOptionSettlement(
        AutomationAction action,
        JObject result,
        bool outcomeUnknown)
    {
        bool armed = _waveFunctionOptionSettlementGuard.TryArm(
            action,
            result,
            outcomeUnknown,
            Time.realtimeSinceStartup);
        if (armed)
        {
            _nextTickAt = Math.Max(
                _nextTickAt,
                Time.realtimeSinceStartup + RewardSelectionSettlementPollSeconds);
        }

        return armed;
    }

    private bool HandleWaveFunctionOptionSettlementFromOptions(JObject eventResult)
    {
        if (!_waveFunctionOptionSettlementGuard.IsArmed)
        {
            return false;
        }

        string panelName = WaveFunctionPanelDisplayName(_waveFunctionOptionSettlementGuard.Panel);
        WaveFunctionOptionSettlementStatus status = _waveFunctionOptionSettlementGuard.ObserveOptions(
            eventResult,
            Time.realtimeSinceStartup,
            WaveFunctionOptionSettlementTimeoutSeconds);
        if (status == WaveFunctionOptionSettlementStatus.Settled)
        {
            CompleteWaveFunctionOptionSettlement(
                panelName + "选项已从完整界面快照中消失，点击结果已完成只读对账。");
            return false;
        }

        if (status == WaveFunctionOptionSettlementStatus.TimedOut)
        {
            Fault(
                panelName + "选项点击后的只读对账超过安全时限；本轮已软停止且不会重复点击，" +
                "游戏进程无需重启。可在界面推进后重新开始自动游玩继续对账。");
            return true;
        }

        _nextTickAt = Math.Max(
            _nextTickAt,
            Time.realtimeSinceStartup + RewardSelectionSettlementPollSeconds);
        SetStage(
            AutomationStage.ManagingEvent,
            panelName + "选项已经点击，正在等待完整界面快照证明目标选项消失；不会重复点击。");
        return true;
    }

    private bool HandleWaveFunctionOptionSettlementFromWaveState(JObject state, JArray blockers)
    {
        if (!_waveFunctionOptionSettlementGuard.IsArmed)
        {
            return false;
        }

        bool repairPanel = string.Equals(
            _waveFunctionOptionSettlementGuard.Panel,
            "RepairUI",
            StringComparison.Ordinal);
        string blockerName = repairPanel ? "RepairUI" : "EventUI";
        string blockedProperty = repairPanel ? "repairBlocked" : "eventBlocked";
        bool snapshotComplete = state["blockers"] is JArray &&
                                state[blockedProperty]?.Type == JTokenType.Boolean;
        bool panelOpen = HasBlocker(blockers, blockerName) ||
                         state[blockedProperty]?.Value<bool>() == true;
        WaveFunctionOptionSettlementStatus status = _waveFunctionOptionSettlementGuard.ObservePanelVisibility(
            snapshotComplete,
            panelOpen,
            Time.realtimeSinceStartup,
            WaveFunctionOptionSettlementTimeoutSeconds);
        if (status == WaveFunctionOptionSettlementStatus.Settled)
        {
            CompleteWaveFunctionOptionSettlement(
                "波次状态已确认" + WaveFunctionPanelDisplayName(blockerName) +
                "界面关闭，点击结果已完成只读对账。");
            return false;
        }

        if (status == WaveFunctionOptionSettlementStatus.TimedOut)
        {
            Fault(
                WaveFunctionPanelDisplayName(blockerName) +
                "选项点击后的只读对账超过安全时限；本轮已软停止且不会重复点击，" +
                "游戏进程无需重启。可在界面推进后重新开始自动游玩继续对账。");
            return true;
        }

        return false;
    }

    private void CompleteWaveFunctionOptionSettlement(string detail)
    {
        if (!_waveFunctionOptionSettlementGuard.IsArmed)
        {
            return;
        }

        _waveFunctionOptionSettlementGuard.Reset();
        _pendingActionKey = string.Empty;
        AddTimeline("settlement", detail);
        MarkProgress();
    }

    private void CompleteWaveFunctionOptionSettlementFromWavePulse(bool inWave, bool gameOver)
    {
        if (!_waveFunctionOptionSettlementGuard.IsArmed || (!inWave && !gameOver))
        {
            return;
        }

        string panelName = WaveFunctionPanelDisplayName(_waveFunctionOptionSettlementGuard.Panel);
        CompleteWaveFunctionOptionSettlement(
            gameOver
                ? "轻量波次状态已确认游戏结算，" + panelName + "选项点击结果已完成只读对账。"
                : "轻量波次状态已确认战斗开始，" + panelName +
                  "界面不再阻塞，点击结果已完成只读对账。");
    }

    private static string WaveFunctionPanelDisplayName(string panel) =>
        string.Equals(panel, "RepairUI", StringComparison.Ordinal) ? "修整" : "轨神事件";

    private bool TryArmRewardObjectSettlement(AutomationAction action)
    {
        int instanceId = action.Arguments["instanceId"]?.Value<int>() ?? 0;
        bool armed = _rewardObjectSettlementGuard.TryArm(
            instanceId,
            Time.realtimeSinceStartup);
        if (armed)
        {
            _rewardObjectCollectionLedger.Add(instanceId);
            _nextTickAt = Math.Max(
                _nextTickAt,
                Time.realtimeSinceStartup + RewardSelectionSettlementPollSeconds);
        }

        return armed;
    }

    private bool HandleRewardObjectSettlement(JObject rewardResult)
    {
        if (!_rewardObjectSettlementGuard.IsArmed)
        {
            return false;
        }

        JObject state = State(rewardResult);
        IEnumerable<int>? activeObjectIds = state["spawnerAvailable"]?.Value<bool>() == true &&
                                             state["rewardObjects"] is JArray rewardObjects
            ? rewardObjects.OfType<JObject>()
                .Where(item => item["active"]?.Value<bool>() != false)
                .Select(item => item["instanceId"]?.Value<int>() ?? 0)
                .Where(instanceId => instanceId != 0)
                .ToArray()
            : null;
        bool rewardPanelOrOptionsVisible = state["panelOpen"]?.Value<bool>() == true ||
                                           (state["options"] as JArray)?.Count > 0;
        RewardObjectSettlementStatus status = _rewardObjectSettlementGuard.Observe(
            activeObjectIds,
            rewardPanelOrOptionsVisible,
            rewardBlockerVisible: true,
            Time.realtimeSinceStartup,
            RewardSelectionSettlementTimeoutSeconds);
        if (status == RewardObjectSettlementStatus.Settled)
        {
            CompleteRewardObjectSettlement(
                "已验证奖励物消失或奖励界面进入下一阶段。");
            ClearRewardObjectsObservation();
            return false;
        }

        if (status == RewardObjectSettlementStatus.TimedOut)
        {
            Fault(
                "奖励物领取已经发送，但同一对象在 " +
                RewardSelectionSettlementTimeoutSeconds.ToString("0") +
                " 秒内仍未消失。已停止本轮自动游玩，不会重复领取，也不把它直接报告为状态污染。");
            return true;
        }

        _nextTickAt = Math.Max(
            _nextTickAt,
            Time.realtimeSinceStartup + RewardSelectionSettlementPollSeconds);
        SetStage(
            AutomationStage.ManagingRewards,
            "已锁定刚领取的奖励物实例，正在等待对象消失或奖励阶段推进；不会重复发送领取命令。");
        return true;
    }

    private void CompleteRewardObjectSettlement(string detail)
    {
        if (!_rewardObjectSettlementGuard.IsArmed)
        {
            return;
        }

        int completedInstanceId = _rewardObjectSettlementGuard.RewardObjectInstanceId;
        _rewardObjectSettlementGuard.Reset();
        _rewardObjectCollectionLedger.Remove(completedInstanceId);
        _pendingActionKey = string.Empty;
        _consecutiveFailures = 0;
        RequestDefenseMaintenance();
        MarkProgress();
        AddTimeline("reward-object-settled", detail);
    }

    private bool HandleRewardSelectionSettlement(JObject rewardResult)
    {
        if (!_rewardSelectionSettlementGuard.IsArmed)
        {
            return false;
        }

        JObject state = State(rewardResult);
        bool panelOpen = state["panelOpen"]?.Value<bool>() == true;
        bool panelDefinitelyClosed = state["panelAvailable"]?.Value<bool>() == true && !panelOpen;
        bool stableObservation = IsStableRewardSelectionObservation(state);
        if (!panelDefinitelyClosed && !stableObservation)
        {
            if (Time.realtimeSinceStartup - _rewardSelectionSettlementGuard.StartedAt >=
                RewardSelectionSettlementTimeoutSeconds)
            {
                Fault(
                    "奖励选择已发送，但奖励界面在 " +
                    RewardSelectionSettlementTimeoutSeconds.ToString("0") +
                    " 秒内始终处于动画、刷新或不完整状态。已停止本轮自动游玩，不会重复点击，也不要求重启游戏。");
                return true;
            }

            _nextTickAt = Math.Max(
                _nextTickAt,
                Time.realtimeSinceStartup + RewardSelectionSettlementPollSeconds);
            SetStage(
                AutomationStage.ManagingRewards,
                "奖励界面仍在动画或刷新，当前快照不完整；继续保持选项写锁，不会用空选项误判为已结算。");
            return true;
        }

        string phaseToken = state["phaseToken"]?.Value<string>() ?? string.Empty;
        IEnumerable<int>? visibleItemInstanceIds = state["options"] is JArray options
            ? options.OfType<JObject>()
                .Select(option => option["instanceId"]?.Value<int>() ?? 0)
                .Where(instanceId => instanceId != 0)
                .ToArray()
            : null;
        RewardSelectionSettlementStatus status = _rewardSelectionSettlementGuard.Observe(
            panelOpen,
            phaseToken,
            visibleItemInstanceIds,
            Time.realtimeSinceStartup,
            RewardSelectionSettlementTimeoutSeconds);
        if (status == RewardSelectionSettlementStatus.Settled)
        {
            CompleteRewardSelectionSettlement("\u5df2\u9a8c\u8bc1\u5956\u52b1\u9009\u9879\u6d88\u5931\u3001\u9636\u6bb5\u53d8\u5316\u6216\u9762\u677f\u5173\u95ed\u3002");
            ResetRewardOptionObservation();
            return false;
        }

        if (status == RewardSelectionSettlementStatus.TimedOut)
        {
            Fault(
                "\u5956\u52b1\u9009\u62e9\u5df2\u53d1\u9001\uff0c\u4f46\u540c\u4e00\u5956\u52b1\u9009\u9879\u5728 " +
                RewardSelectionSettlementTimeoutSeconds.ToString("0") +
                " \u79d2\u5185\u672a\u6d88\u5931\u3002\u5df2\u505c\u6b62\u672c\u8f6e\u81ea\u52a8\u6e38\u73a9\uff0c\u4e0d\u4f1a\u91cd\u590d\u70b9\u51fb\uff0c\u4e14\u4e0d\u8981\u6c42\u91cd\u542f\u6e38\u620f\u3002");
            return true;
        }

        _nextTickAt = Math.Max(
            _nextTickAt,
            Time.realtimeSinceStartup + RewardSelectionSettlementPollSeconds);
        string transient = state["busy"]?.Value<bool>() == true
            ? "\u5956\u52b1\u4e92\u65a5\u9501\u6b63\u5fd9"
            : state["refresh"]?.Value<bool>() == true || state["finished"]?.Value<bool>() == true
                ? "\u5956\u52b1\u961f\u5217\u6b63\u5728\u5207\u6362"
                : "\u5956\u52b1\u754c\u9762\u5c1a\u672a\u63d0\u4ea4\u65b0\u9636\u6bb5";
        SetStage(
            AutomationStage.ManagingRewards,
            transient + "\uff1b\u5df2\u9501\u5b9a\u5f53\u524d\u9009\u9879\u5e76\u53ea\u7b49\u5f85\u6536\u655b\uff0c\u4e0d\u4f1a\u518d\u6b21\u70b9\u51fb\u3002");
        return true;
    }

    private static bool IsStableRewardSelectionObservation(JObject state)
    {
        if (state["panelOpen"]?.Value<bool>() != true ||
            state["pending"]?.Value<bool>() == true ||
            state["needsPolling"]?.Value<bool>() == true ||
            state["busy"]?.Value<bool>() == true ||
            state["refresh"]?.Value<bool>() == true ||
            state["finished"]?.Value<bool>() == true ||
            state["mutexAvailable"]?.Value<bool>() != true ||
            state["options"] is not JArray options ||
            options.Count == 0)
        {
            return false;
        }

        return options.OfType<JObject>().All(option => option["instanceId"]?.Value<int>() != 0);
    }

    private void CompleteRewardSelectionSettlement(string detail)
    {
        if (!_rewardSelectionSettlementGuard.IsArmed)
        {
            return;
        }

        _rewardSelectionSettlementGuard.Reset();
        _pendingActionKey = string.Empty;
        _consecutiveFailures = 0;
        RequestDefenseMaintenance();
        MarkProgress();
        AddTimeline("reward-settled", detail);
    }

    private static bool IsSceneTransitionCommand(string command) =>
        string.Equals(command, "submitCommonMode", StringComparison.OrdinalIgnoreCase) ||
        string.Equals(command, "continueGame", StringComparison.OrdinalIgnoreCase) ||
        string.Equals(command, "enterRandomMode", StringComparison.OrdinalIgnoreCase) ||
        string.Equals(command, "submitRandomMode", StringComparison.OrdinalIgnoreCase);

    private AutomationAction DecideObservedReward(JObject rewardResult)
    {
        JObject rewardState = State(rewardResult);
        if (rewardState["options"] is JArray &&
            (rewardState["busy"]?.Value<bool>() == true ||
             rewardState["refresh"]?.Value<bool>() == true ||
             rewardState["finished"]?.Value<bool>() == true ||
             rewardState["mutexAvailable"]?.Value<bool>() != true))
        {
            return AutomationAction.Wait(
                AutomationStage.ManagingRewards,
                "\u5956\u52b1\u754c\u9762\u6b63\u5728\u5e94\u7528\u6216\u5207\u6362\u5f53\u524d\u5956\u52b1\uff0c\u7b49\u5f85\u961f\u5217\u7a33\u5b9a\u3002");
        }

        AutomationAction fallback = _decisionEngine.DecideReward(
            rewardResult,
            null,
            _options.DecisionPriority);
        if (!string.Equals(fallback.Command, "chooseRewardOption", StringComparison.OrdinalIgnoreCase))
        {
            ClearRewardVehicleContext();
            return fallback;
        }

        JArray options = State(rewardResult)["options"] as JArray ?? new JArray();
        if (!RewardSelectionNeedsVehicleContext(options))
        {
            ClearRewardVehicleContext();
            return BindRewardSelectionIdentity(fallback, rewardState);
        }

        string fingerprint = BuildRewardOptionsFingerprint(options);
        if (!string.Equals(fingerprint, _rewardVehicleContextFingerprint, StringComparison.Ordinal))
        {
            ClearRewardVehicleContext();
            _rewardVehicleContextFingerprint = fingerprint;
        }

        if (!_rewardVehicleContextAttempted)
        {
            _rewardVehicleContextAttempted = true;
            if (!_bridge.HasCommand("queryVehicle"))
            {
                _rewardVehicleContextFailed = true;
                AddWarning("奖励选择前无法读取车辆状态，将沿用无车辆上下文的战略评分：当前游戏构建缺少 queryVehicle。");
            }
            else
            {
                JObject vehicleResult = _bridge.Invoke("queryVehicle");
                if (RuntimeResultInspector.ClassifyReadOnly(vehicleResult) != RuntimeResultDisposition.Success)
                {
                    _rewardVehicleContextFailed = true;
                    AddWarning("奖励选择前读取车辆状态失败，将沿用无车辆上下文的战略评分：" + Message(vehicleResult));
                }
                else if (State(vehicleResult)["vehicles"] is not JArray)
                {
                    _rewardVehicleContextFailed = true;
                    AddWarning("奖励选择前读取车辆状态失败，将沿用无车辆上下文的战略评分：结果缺少 vehicles 数组。");
                }
                else
                {
                    _rewardVehicleContextResult = vehicleResult;
                }
            }

            _nextTickAt = Math.Max(
                _nextTickAt,
                Time.realtimeSinceStartup + RewardVehicleContextFrameDelaySeconds);
            return AutomationAction.Wait(
                AutomationStage.ManagingRewards,
                _rewardVehicleContextFailed
                    ? "车辆上下文不可用，下一帧将沿用原有奖励评分。"
                    : "车辆上下文已读取，下一帧再结合合成、等级和羁绊选择奖励。");
        }

        AutomationAction decision = _rewardVehicleContextFailed || _rewardVehicleContextResult == null
            ? fallback
            : _decisionEngine.DecideReward(
                rewardResult,
                _rewardVehicleContextResult,
                _options.DecisionPriority);
        return BindRewardSelectionIdentity(decision, rewardState);
    }

    private static AutomationAction BindRewardSelectionIdentity(AutomationAction action, JObject rewardState)
    {
        if (!string.Equals(action.Command, "chooseRewardOption", StringComparison.OrdinalIgnoreCase))
        {
            return action;
        }

        int index = action.Arguments["index"]?.Value<int>() ?? -1;
        string phaseToken = rewardState["phaseToken"]?.Value<string>() ?? string.Empty;
        JObject? option = (rewardState["options"] as JArray)?.OfType<JObject>()
            .FirstOrDefault(candidate => candidate["index"]?.Value<int>() == index);
        int itemInstanceId = option?["instanceId"]?.Value<int>() ?? 0;
        if (index < 0 || string.IsNullOrWhiteSpace(phaseToken) || itemInstanceId == 0)
        {
            return AutomationAction.Wait(
                AutomationStage.ManagingRewards,
                "\u5956\u52b1\u9009\u9879\u7f3a\u5c11\u7a33\u5b9a\u7684\u9636\u6bb5\u6216\u5b9e\u4f8b\u8eab\u4efd\uff0c\u7b49\u5f85\u4e0b\u4e00\u6b21\u67e5\u8be2\u3002");
        }

        JObject arguments = (JObject)action.Arguments.DeepClone();
        arguments["phaseToken"] = phaseToken;
        arguments["itemInstanceId"] = itemInstanceId;
        return new AutomationAction(action.Command, arguments, action.Stage, action.Reason);
    }

    private bool TryWaitForRewardOptions(JObject rewardResult)
    {
        JObject state = rewardResult.SelectToken("data.state") as JObject ?? new JObject();
        if ((state["activeRewardObjectCount"]?.Value<int>() ?? 0) > 0)
        {
            ClearRewardOptionsObservation();
            JArray rewardObjects = state["rewardObjects"] as JArray ?? new JArray();
            if (rewardObjects.Count == 0)
            {
                ClearRewardObjectsObservation();
                SetStage(AutomationStage.ManagingRewards, "奖励物品正在出现，等待运行时提供稳定的对象标识。");
                return true;
            }

            string objectFingerprint = BuildRewardObjectsFingerprint(rewardObjects);
            float now = Time.realtimeSinceStartup;
            if (!string.Equals(objectFingerprint, _rewardObjectsFingerprint, StringComparison.Ordinal))
            {
                _rewardObjectsFingerprint = objectFingerprint;
                _rewardObjectsAppearanceReadyAt = now + RewardObjectAppearanceGraceSeconds;
                _rewardObjectsReadyAt = -1f;
                _nextTickAt = Math.Max(_nextTickAt, _rewardObjectsAppearanceReadyAt);
                SetStage(AutomationStage.ManagingRewards, "奖励物品已经注册，正在等待出现动画和对象池复用状态稳定。");
                MarkProgress();
                return true;
            }

            if (now < _rewardObjectsAppearanceReadyAt)
            {
                _nextTickAt = Math.Max(_nextTickAt, _rewardObjectsAppearanceReadyAt);
                SetStage(AutomationStage.ManagingRewards, "奖励物品正在播放出现动画，尚未开始录像观察倒计时。");
                return true;
            }

            bool appearanceReady = rewardObjects.OfType<JObject>()
                .All(rewardObject => rewardObject["appearanceReady"]?.Value<bool>() != false);
            if (!appearanceReady)
            {
                _rewardObjectsReadyAt = -1f;
                _nextTickAt = Math.Max(_nextTickAt, now + RewardObjectAppearancePollSeconds);
                SetStage(AutomationStage.ManagingRewards, "奖励物品的出现动画仍在播放；动画结束后才开始 0.75 秒观察时间。");
                return true;
            }

            if (_rewardObjectsReadyAt < 0f)
            {
                _rewardObjectsReadyAt = now + RewardCollectionObservationSeconds;
                _nextTickAt = Math.Max(_nextTickAt, _rewardObjectsReadyAt);
                SetStage(AutomationStage.ManagingRewards, "奖励物品出现动画已结束，保留 0.75 秒观察时间后再收取。");
                AddTimeline("observation", "奖励物品出现动画已结束；将在 0.75 秒观察时间结束后收取。");
                MarkProgress();
                return true;
            }

            if (now < _rewardObjectsReadyAt)
            {
                _nextTickAt = Math.Max(_nextTickAt, _rewardObjectsReadyAt);
                SetStage(AutomationStage.ManagingRewards, "奖励物品保持显示，正在等待动画结束后的 0.75 秒观察时间。");
                return true;
            }

            return false;
        }

        ClearRewardObjectsObservation();
        JArray options = state["options"] as JArray ?? new JArray();
        if (options.Count == 0)
        {
            ClearRewardOptionsObservation();
            return false;
        }

        if (RewardSelectionNeedsVehicleContext(options) && !_rewardVehicleContextAttempted)
        {
            AutomationAction contextPreparation = DecideObservedReward(rewardResult);
            if (string.Equals(contextPreparation.Command, "wait", StringComparison.OrdinalIgnoreCase))
            {
                SetStage(contextPreparation.Stage, contextPreparation.Reason);
                return true;
            }
        }

        string fingerprint = BuildRewardOptionsFingerprint(options);
        bool rewardUiTransient = state["busy"]?.Value<bool>() == true ||
                                 state["refresh"]?.Value<bool>() == true ||
                                 state["finished"]?.Value<bool>() == true ||
                                 state["mutexAvailable"]?.Value<bool>() != true;
        RewardOptionObservationDecision observation = RewardOptionObservationGate.Observe(
            _rewardOptionsFingerprint,
            _rewardOptionsReadyAt,
            fingerprint,
            rewardUiTransient,
            Time.realtimeSinceStartup,
            SelectionPreviewObservationSeconds);
        if (observation.FingerprintChanged)
        {
            if (!string.Equals(_rewardVehicleContextFingerprint, fingerprint, StringComparison.Ordinal))
            {
                ClearRewardVehicleContext();
            }
        }

        _rewardOptionsFingerprint = observation.Fingerprint;
        _rewardOptionsReadyAt = observation.ReadyAt;
        if (observation.Status == RewardOptionObservationStatus.WaitingForStableUi)
        {
            ClearSelectionHighlight("reward");
            _nextTickAt = Math.Max(
                _nextTickAt,
                Time.realtimeSinceStartup + RewardSelectionSettlementPollSeconds);
            SetStage(
                AutomationStage.ManagingRewards,
                "\u5956\u52b1\u9009\u9879\u5df2\u51fa\u73b0\uff0c\u4f46\u52a8\u753b\u3001\u4e92\u65a5\u9501\u6216\u961f\u5217\u5207\u6362\u5c1a\u672a\u7ed3\u675f\uff1b\u7ee7\u7eed\u7b49\u5f85\u4e14\u4e0d\u6267\u884c\u70b9\u51fb\u3002");
            return true;
        }

        if (observation.Status == RewardOptionObservationStatus.RecordingStarted)
        {
            ShowRewardSelectionHighlight(rewardResult);
            _nextTickAt = Math.Max(_nextTickAt, _rewardOptionsReadyAt);
            SetStage(AutomationStage.ManagingRewards, "奖励选项已完整出现，保留 1 秒观察时间后再选择。");
            AddTimeline("observation", "奖励选项已稳定显示；将在 1 秒观察时间结束后选择。");
            MarkProgress();
            return true;
        }

        if (observation.Status == RewardOptionObservationStatus.Recording)
        {
            ShowRewardSelectionHighlight(rewardResult);
            _nextTickAt = Math.Max(_nextTickAt, _rewardOptionsReadyAt);
            SetStage(AutomationStage.ManagingRewards, "奖励选项保持显示，正在等待 1 秒观察时间结束。");
            return true;
        }

        return false;
    }

    private bool TryWaitForEventOptions(JObject eventResult, string panel)
    {
        string path = string.Equals(panel, "RepairUI", StringComparison.OrdinalIgnoreCase)
            ? "data.state.repairPanel.options"
            : "data.state.eventPanel.options";
        JArray options = eventResult.SelectToken(path) as JArray ?? new JArray();
        if (options.Count == 0)
        {
            ClearSelectionHighlight("event");
            _eventOptionSelectionReadyAt = -1f;
            _eventOptionsFingerprint = string.Empty;
            return false;
        }

        string fingerprint = BuildEventOptionsFingerprint(options);
        string panelName = string.Equals(panel, "RepairUI", StringComparison.OrdinalIgnoreCase)
            ? "修整选项"
            : "轨神事件选项";
        if (!string.Equals(fingerprint, _eventOptionsFingerprint, StringComparison.Ordinal))
        {
            _eventOptionsFingerprint = fingerprint;
            ShowEventSelectionHighlight(eventResult, panel, fingerprint);
            _eventOptionSelectionReadyAt = Time.realtimeSinceStartup + SelectionPreviewObservationSeconds;
            _nextTickAt = Math.Max(_nextTickAt, _eventOptionSelectionReadyAt);
            SetStage(AutomationStage.ManagingEvent, panelName + "已完整出现，保留 1 秒观察时间后再选择。");
            AddTimeline("observation", panelName + "已稳定显示；将在 1 秒观察时间结束后选择。");
            MarkProgress();
            return true;
        }

        if (Time.realtimeSinceStartup < _eventOptionSelectionReadyAt)
        {
            ShowEventSelectionHighlight(eventResult, panel, fingerprint);
            _nextTickAt = Math.Max(_nextTickAt, _eventOptionSelectionReadyAt);
            SetStage(AutomationStage.ManagingEvent, panelName + "保持显示，正在等待 1 秒观察时间结束。");
            return true;
        }

        return false;
    }

    private static string BuildRewardObjectsFingerprint(JArray rewardObjects) => string.Join(
        ";",
        rewardObjects.OfType<JObject>().Select((rewardObject, index) => string.Join(
            "|",
            index,
            rewardObject["instanceId"]?.ToString() ?? string.Empty,
            rewardObject["index"]?.ToString() ?? string.Empty,
            rewardObject["type"]?.ToString() ?? string.Empty,
            rewardObject["path"]?.ToString() ?? string.Empty,
            rewardObject["active"]?.ToString() ?? string.Empty)));

    private static string BuildRewardOptionsFingerprint(JArray options) => string.Join(
        ";",
        options.OfType<JObject>().Select((option, index) => string.Join(
            "|",
            index,
            option["instanceId"]?.ToString() ?? string.Empty,
            option["index"]?.ToString() ?? string.Empty,
            option["rewardKind"]?.ToString() ?? string.Empty,
            option["rewardRare"]?.ToString() ?? string.Empty,
            option["vehicleType"]?.ToString() ?? string.Empty,
            option["disposableEnum"]?.ToString() ?? string.Empty,
            option["superModuleEnum"]?.ToString() ?? string.Empty,
            option["effectiveFetters"]?.ToString(Newtonsoft.Json.Formatting.None) ?? string.Empty,
            option["buttonActive"]?.ToString() ?? string.Empty,
            option["canAcquire"]?.ToString() ?? string.Empty)));

    private static bool RewardSelectionNeedsVehicleContext(JArray options) => options
        .OfType<JObject>()
        .Where(option => option["buttonActive"]?.Value<bool>() != false)
        .Where(option => option["canAcquire"]?.Value<bool>() != false)
        .Where(option => option["index"]?.Type == JTokenType.Integer && option["index"]!.Value<int>() >= 0)
        .Count(option => string.Equals(
            option["rewardKind"]?.Value<string>(),
            "vehicle",
            StringComparison.OrdinalIgnoreCase)) >= 1;

    private static string BuildEventOptionsFingerprint(JArray options) => string.Join(
        ";",
        options.OfType<JObject>().Select((option, index) => string.Join(
            "|",
            index,
            option["instanceId"]?.ToString() ?? string.Empty,
            option["buttonActive"]?.ToString() ?? string.Empty,
            option["conditionPass"]?.ToString() ?? string.Empty,
            option["displayText"]?.ToString() ?? string.Empty,
            option["currentItemType"]?.ToString() ?? string.Empty,
            JoinStringArray(option, "behaviourTypeIds"),
            JoinStringArray(option, "behaviourTypes"),
            JoinStringArray(option, "behaviourNames"))));

    private static string JoinStringArray(JObject option, string propertyName) =>
        string.Join(",", (option[propertyName] as JArray)?.Values<string>() ?? Enumerable.Empty<string>());

    private void ShowRewardSelectionHighlight(JObject rewardResult)
    {
        JObject rewardState = State(rewardResult);
        AutomationAction decision = _rewardVehicleContextFailed || _rewardVehicleContextResult == null
            ? _decisionEngine.DecideReward(rewardResult, null, _options.DecisionPriority)
            : _decisionEngine.DecideReward(rewardResult, _rewardVehicleContextResult, _options.DecisionPriority);
        if (!string.Equals(decision.Command, "chooseRewardOption", StringComparison.OrdinalIgnoreCase))
        {
            ClearSelectionHighlight("reward");
            return;
        }

        int index = decision.Arguments["index"]?.Value<int>() ?? -1;
        JObject? option = (rewardState["options"] as JArray)?.OfType<JObject>()
            .FirstOrDefault(candidate => candidate["index"]?.Value<int>() == index);
        int instanceId = option?["instanceId"]?.Value<int>() ?? 0;
        if (instanceId == 0)
        {
            ClearSelectionHighlight("reward");
            return;
        }

        ShowSelectionHighlight(
            "reward",
            _rewardOptionsFingerprint + "|" + instanceId,
            NativeSelectionTarget.ByInstance(
                "MetroTD.RewardSystem.GeneralRewardItemUI",
                instanceId,
                option?["path"]?.Value<string>()));
    }

    private void ShowEventSelectionHighlight(JObject eventResult, string panel, string fingerprint)
    {
        AutomationAction decision = _decisionEngine.DecideEvent(eventResult, panel);
        if (!string.Equals(decision.Command, "chooseWaveFunctionOption", StringComparison.OrdinalIgnoreCase))
        {
            ClearSelectionHighlight("event");
            return;
        }

        int instanceId = decision.Arguments["instanceId"]?.Value<int>() ?? 0;
        if (instanceId == 0)
        {
            ClearSelectionHighlight("event");
            return;
        }

        ShowSelectionHighlight(
            "event",
            fingerprint + "|" + instanceId,
            NativeSelectionTarget.ByInstance(
                "MetroTD.UISystem.WaveFunctionUI_Item_Behaviour",
                instanceId));
    }

    private void ResetRewardOptionObservation()
    {
        ClearRewardObjectsObservation();
        ClearRewardOptionsObservation();
    }

    private void ClearRewardObjectsObservation()
    {
        _rewardObjectsReadyAt = -1f;
        _rewardObjectsAppearanceReadyAt = -1f;
        _rewardObjectsFingerprint = string.Empty;
    }

    private void ClearRewardOptionsObservation()
    {
        ClearSelectionHighlight("reward");
        _rewardOptionsReadyAt = -1f;
        _rewardOptionsFingerprint = string.Empty;
        ClearRewardVehicleContext();
    }

    private void ClearRewardVehicleContext()
    {
        _rewardVehicleContextFingerprint = string.Empty;
        _rewardVehicleContextAttempted = false;
        _rewardVehicleContextFailed = false;
        _rewardVehicleContextResult = null;
    }

    private void ResetEventOptionObservation()
    {
        ClearSelectionHighlight("event");
        _eventOptionsReadyAt = -1f;
        _eventOptionSelectionReadyAt = -1f;
        _eventOptionsFingerprint = string.Empty;
        _pendingEventPanel = string.Empty;
    }

    private void ResetNormalEventObservation()
    {
        _normalEventObserved = false;
        _normalEventProbeFailures = 0;
        _deferredNormalEventAction = null;
        _deferredNormalEventChoosingOption = false;
        ResetNormalEventActionObservation();
    }

    private void ResetNormalEventActionObservation()
    {
        ClearSelectionHighlight("normal-event");
        _normalEventActionReadyAt = -1f;
        _normalEventFingerprint = string.Empty;
    }

    private void ObserveMapProgress()
    {
        if (Time.realtimeSinceStartup < _nextMapProgressProbeAt) return;
        _nextMapProgressProbeAt = Time.realtimeSinceStartup + MapProgressProbeIntervalSeconds;
        if (!_bridge.TryGetMapProgress(out int stage, out int layer)) return;

        bool stageChanged = stage != _currentMapStage;
        bool layerChanged = layer != _currentMapLayer;
        _currentMapStage = stage;
        _currentMapLayer = layer;
        if (!stageChanged && !layerChanged) return;

        MarkProgress();
        if (stageChanged)
        {
            AddTimeline(
                "chapter",
                "地图进度已进入第 " + (stage + 1) + " 章，当前层为 " + layer + "。");
        }
    }

    private bool TryQueryState(
        string command,
        AutomationStage pendingStage,
        out JObject result,
        out JObject state)
    {
        result = _bridge.Invoke(command);
        state = new JObject();
        switch (RuntimeResultInspector.ClassifyReadOnly(result))
        {
            case RuntimeResultDisposition.Pending:
                SetStage(pendingStage, Message(result));
                return false;
            case RuntimeResultDisposition.Failure:
                RegisterFailure("命令 " + command + " 查询失败：" + Message(result));
                return false;
            default:
                _consecutiveFailures = 0;
                state = result.SelectToken("data.state") as JObject ?? new JObject();
                return true;
        }
    }

    private void BeginEventPanelObservation(string panel)
    {
        float now = Time.realtimeSinceStartup;
        _mapSelectionPending = true;
        _mapSelectionPendingAt = now;
        _pendingEventPanel = panel;
        _eventOptionsReadyAt = now + (string.Equals(panel, "EventUI", StringComparison.OrdinalIgnoreCase)
            ? EventOptionGenerationDelaySeconds
            : RepairPanelAnimationSeconds);
        _eventOptionSelectionReadyAt = -1f;
        _eventOptionsFingerprint = string.Empty;
        _nextTickAt = Math.Max(_nextTickAt, _eventOptionsReadyAt);
        AddTimeline(
            "observation",
            string.Equals(panel, "EventUI", StringComparison.OrdinalIgnoreCase)
                ? "已观察到轨神事件面板；等待入场动画和选项生成完成。"
                : "已观察到修整面板；等待入场动画完成。");
        SetStage(
            AutomationStage.ManagingEvent,
            string.Equals(panel, "EventUI", StringComparison.OrdinalIgnoreCase)
                ? "轨神事件正在播放入场动画，等待游戏生成可选项。"
                : "修整界面正在播放入场动画，等待界面稳定。");
    }

    private static string BuildBlockerDetail(JArray blockers)
    {
        string detail = string.Join(
            "；",
            blockers.OfType<JObject>()
                .Select(blocker => blocker["reason"]?.Value<string>())
                .Where(reason => !string.IsNullOrWhiteSpace(reason)));
        return string.IsNullOrWhiteSpace(detail)
            ? "当前存在尚未识别的游戏界面阻塞，等待状态稳定。"
            : "正在等待游戏解除当前阻塞：" + detail;
    }

    private static JObject BuildLightweightAffordanceResult(
        JArray blockers,
        JObject mapState,
        bool canStartWave,
        bool canSelectNextNode) => new()
    {
        ["success"] = true,
        ["message"] = "已组合轻量波次与地图状态。",
        ["suggestion"] = JValue.CreateNull(),
        ["data"] = new JObject
        {
            ["state"] = new JObject
            {
                ["gameOver"] = false,
                ["blockers"] = blockers.DeepClone(),
                ["map"] = new JObject
                {
                    ["mapOpen"] = mapState["mapOpen"]?.DeepClone() ?? false,
                    ["canStartWave"] = canStartWave,
                    ["canSelectNextNode"] = canSelectNextNode,
                    ["selectableNodes"] = mapState["readyNodes"]?.DeepClone() ?? new JArray()
                },
                ["wave"] = new JObject { ["isInWaving"] = false }
            }
        }
    };

    private void ObserveWaveTransition(bool inWave)
    {
        if (inWave && !_wasInWave)
        {
            ResetWaveStartObservation();
            _wasInWave = true;
            _battleTrainIdentitiesMovedThisWave.Clear();
            _wavesStarted++;
            ResetBattleTactics();
            AddTimeline("wave-start", "已观察到第 " + _wavesStarted + " 个波次开始。");
            MarkProgress();
        }
        else if (!inWave && _wasInWave)
        {
            if (!string.IsNullOrWhiteSpace(_ownedDisposableEnum) &&
                _ownedDisposableInteractionInstanceId != 0)
            {
                _battleWaveEndPendingPreviewRelease = true;
                if (_battleTacticStep is not BattleTacticStep.WaitForDisposableSettlement and
                    not BattleTacticStep.CancelDisposable and
                    not BattleTacticStep.VerifyDisposableCancellation)
                {
                    _battleTacticStep = BattleTacticStep.CancelDisposable;
                }

                _battleWaveSnapshot ??= UpdateBattleWaveSnapshot(false, 0);
                _battleTacticPending = true;
                _nextTickAt = Time.realtimeSinceStartup + BattleTacticFrameDelaySeconds;
                SetStage(
                    AutomationStage.Battle,
                    "波次已经结束，但自动游玩道具预览尚未证明完全退出；先只读确认或安全清理，再提交波次完成状态。");
                return;
            }

            ResetWaveStartObservation();
            _wasInWave = false;
            _battleTrainIdentitiesMovedThisWave.Clear();
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
            if (BeginOwnedPreviewRelease(
                    OwnedPreviewReleaseOperation.Fault,
                    reason,
                    out _))
            {
                return;
            }

            CommitFault(reason);
        }
    }

    private void FaultRequiringProcessRestart(string reason)
    {
        lock (_sync)
        {
            RequireProcessRestart();
            Fault(reason);
        }
    }

    private void RequireProcessRestart()
    {
        _needsProcessRestart = true;
    }

    private void CommitFault(string reason)
    {
        ClearSelectionHighlight();
        _runState = AutoPlayerRunState.Faulted;
        _stage = AutomationStage.Recovery;
        _stageDetail = reason;
        _lastMessage = reason;
        if (_outcome is AutomationOutcome.Unknown or AutomationOutcome.InProgress)
        {
            _outcome = AutomationOutcome.Error;
        }
        AddTimeline("fault", reason);
        _evidence.CaptureFailure(EnsureEvidenceDirectory(), reason, Snapshot(), _lastRuntimeResult);
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
            ClearSelectionHighlight();
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
        ((state["vehicles"] as JArray) ?? (state.SelectToken("vehicle.vehicles") as JArray))
        ?.OfType<JObject>().Any(vehicle =>
            vehicle["active"]?.Value<bool>() == true &&
            vehicle["inBag"]?.Value<bool>() != true &&
            vehicle["isFixedHead"]?.Value<bool>() != true) == true;

    private static bool HasAvailableExpansionStationAtGrid(
        JObject catapultResult,
        JObject? expectedGrid,
        string disposableEnum,
        string? expectedStationKind = null,
        string? expectedEffectIdentity = null)
    {
        int? expectedX = expectedGrid?["x"]?.Value<int?>();
        int? expectedY = expectedGrid?["y"]?.Value<int?>();
        if (!expectedX.HasValue || !expectedY.HasValue)
        {
            return false;
        }

        return (State(catapultResult)["catapults"] as JArray)?.OfType<JObject>().Any(catapult =>
            catapult["active"]?.Value<bool>() != false &&
            catapult["canUseForNewRail"]?.Value<bool>() == true &&
            catapult["canPickLine"]?.Value<bool>() != false &&
            catapult["frozen"]?.Value<bool>() != true &&
            catapult["railReachMax"]?.Value<bool>() != true &&
            (catapult["railMembershipCount"]?.Value<int?>() ?? 0) == 0 &&
            (catapult["linePointInstanceId"]?.Value<int?>() ?? 0) != 0 &&
            (string.Equals(
                 catapult["recycleDisposableEnum"]?.Value<string>(),
                 disposableEnum,
                 StringComparison.Ordinal) ||
             string.Equals(disposableEnum, "FreePoint_Attribute", StringComparison.Ordinal) &&
              catapult["isAttribute"]?.Value<bool>() == true) &&
            (string.IsNullOrWhiteSpace(expectedStationKind) ||
             string.Equals(expectedStationKind, "AttributeCatapult", StringComparison.Ordinal) ==
             (catapult["isAttribute"]?.Value<bool>() == true)) &&
            (string.IsNullOrWhiteSpace(expectedStationKind) ||
             catapult["canMove"]?.Value<bool>() == true) &&
            MatchesPlacedStationEffect(catapult, expectedEffectIdentity) &&
            catapult.SelectToken("grid.x")?.Value<int?>() == expectedX.Value &&
            catapult.SelectToken("grid.y")?.Value<int?>() == expectedY.Value) == true;
    }

    private static bool MatchesPlacedStationEffect(JObject catapult, string? expectedEffectIdentity)
    {
        if (string.IsNullOrWhiteSpace(expectedEffectIdentity)) return true;
        IEnumerable<string?> identities = new string?[]
            {
                catapult["specialSource"]?.Value<string>(),
                catapult["effectEnum"]?.Value<string>(),
                catapult["specialEffectEnum"]?.Value<string>()
            }
            .Concat((catapult["runtimeBuffIdentities"] as JArray)?.Values<string>() ?? Enumerable.Empty<string?>())
            .Concat((catapult["effectTags"] as JArray)?.Values<string>() ?? Enumerable.Empty<string?>());
        return identities.Any(value => string.Equals(value, expectedEffectIdentity, StringComparison.Ordinal));
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
        if (!Enum.IsDefined(typeof(AutomationDecisionPriority), options.DecisionPriority))
        {
            options.DecisionPriority = AutomationDecisionPriority.CatapultPoints;
        }
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

    private static string UnsafeWriteMessage(string command, JObject result) =>
        "命令 " + command + " 的游戏内写入一致性无法确认（" +
        RuntimeResultInspector.UnsafeMutationReason(result) +
        "）。这表示该操作可能只完成了一部分，不是报告文件或游戏存档损坏。" +
        "为防止重复写入，当前游戏进程禁止继续自动游玩；请彻底关闭游戏后重启。运行时详情：" +
        Message(result);

    private static string Message(JObject result) => RuntimeResultInspector.Message(result);
    private static bool HasBlocker(JArray blockers, string key) => blockers.OfType<JObject>()
        .Any(item => string.Equals(item["key"]?.Value<string>(), key, StringComparison.OrdinalIgnoreCase));
}
