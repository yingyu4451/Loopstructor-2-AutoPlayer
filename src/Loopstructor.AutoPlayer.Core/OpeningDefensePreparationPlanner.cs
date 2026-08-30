using System;
using System.Collections.Generic;
using System.Linq;
using Newtonsoft.Json.Linq;

namespace Loopstructor.AutoPlayer.Core;

/// <summary>
/// A game-grid coordinate used while planning the initial attribute catapult placement.
/// This deliberately lives in Core so the planner does not need a compile-time dependency on Unity.
/// </summary>
public readonly struct OpeningDefenseGrid : IEquatable<OpeningDefenseGrid>
{
    public OpeningDefenseGrid(int x, int y)
    {
        X = x;
        Y = y;
    }

    public int X { get; }
    public int Y { get; }

    public bool Equals(OpeningDefenseGrid other) => X == other.X && Y == other.Y;
    public override bool Equals(object? obj) => obj is OpeningDefenseGrid other && Equals(other);
    public override int GetHashCode() => unchecked((X * 397) ^ Y);
    public override string ToString() => $"({X},{Y})";
}

/// <summary>
/// Reproduces the default-defense candidate order without evaluating every grid in one frame.
/// </summary>
public static class OpeningDefenseGridRanker
{
    public static IReadOnlyList<OpeningDefenseGrid> Rank(
        IEnumerable<OpeningDefenseGrid>? candidates,
        IEnumerable<OpeningDefenseGrid>? commonPointAnchors)
    {
        OpeningDefenseGrid[] anchors = commonPointAnchors?
            .Distinct()
            .ToArray() ?? Array.Empty<OpeningDefenseGrid>();

        return candidates?
            .Distinct()
            .Select(grid => new RankedGrid(
                grid,
                Score(grid, anchors),
                ScoreCandidateLayout(grid, anchors)))
            .OrderBy(
                item => item.Layout,
                Comparer<RailLayoutScore?>.Create(RailLayoutStrategyPlanner.CompareForDefense))
            .ThenBy(item => item.Score)
            .ThenBy(item => item.Grid.X)
            .ThenBy(item => item.Grid.Y)
            .Select(item => item.Grid)
            .ToArray() ?? Array.Empty<OpeningDefenseGrid>();
    }

    public static double Score(OpeningDefenseGrid grid, IReadOnlyList<OpeningDefenseGrid>? anchors)
    {
        if (anchors == null || anchors.Count < 2)
        {
            return MagnitudeSquared(grid);
        }

        double nearest = double.MaxValue;
        double secondNearest = double.MaxValue;
        for (int i = 0; i < anchors.Count; i++)
        {
            double distance = DistanceSquared(grid, anchors[i]);
            if (distance < nearest)
            {
                secondNearest = nearest;
                nearest = distance;
            }
            else if (distance < secondNearest)
            {
                secondNearest = distance;
            }
        }

        return nearest + secondNearest + MagnitudeSquared(grid) * 0.001d;
    }

    private static double DistanceSquared(OpeningDefenseGrid left, OpeningDefenseGrid right)
    {
        double x = (double)left.X - right.X;
        double y = (double)left.Y - right.Y;
        return x * x + y * y;
    }

    private static double MagnitudeSquared(OpeningDefenseGrid grid) =>
        (double)grid.X * grid.X + (double)grid.Y * grid.Y;

    /// <summary>
    /// Ranks an attribute-grid candidate without running the full greedy rail planner. The grid
    /// probe can contain hundreds of candidates, while the common stations are stable for the
    /// whole probe. Sorting the candidate plus those anchors once is O(N log N) per candidate and
    /// still exposes the coverage, blind-arc, radius and estimated-perimeter facts needed here.
    /// The selected grid is planned in full (and previewed by the game) in TryBuildRailAction.
    /// </summary>
    private static RailLayoutScore? ScoreCandidateLayout(
        OpeningDefenseGrid attribute,
        IReadOnlyList<OpeningDefenseGrid> anchors)
    {
        if (anchors.Count < 2) return null;

        RailLayoutPoint[] ordered = anchors
            .Select(anchor => new RailLayoutPoint(anchor.X, anchor.Y))
            .Append(new RailLayoutPoint(attribute.X, attribute.Y))
            .Distinct()
            .OrderBy(PolarAngle)
            .ThenBy(point => point.X * point.X + point.Y * point.Y)
            .ThenBy(point => point.X)
            .ThenBy(point => point.Y)
            .ToArray();
        return ordered.Length >= 3
            ? RailLayoutStrategyPlanner.EvaluateEstimated(ordered)
            : null;
    }

    private static double PolarAngle(RailLayoutPoint point)
    {
        double angle = Math.Atan2(point.Y, point.X);
        return angle < 0d ? angle + Math.PI * 2d : angle;
    }

    private sealed class RankedGrid
    {
        public RankedGrid(OpeningDefenseGrid grid, double score, RailLayoutScore? layout)
        {
            Grid = grid;
            Score = score;
            Layout = layout;
        }

        public OpeningDefenseGrid Grid { get; }
        public double Score { get; }
        public RailLayoutScore? Layout { get; }
    }
}

public enum OpeningDefenseGridProbeStatus
{
    Probing,
    Found,
    Exhausted,
    Unavailable
}

public sealed class OpeningDefenseGridProbeResult
{
    public OpeningDefenseGridProbeResult(
        OpeningDefenseGridProbeStatus status,
        OpeningDefenseGrid? grid = null,
        int totalProbed = 0,
        string? detail = null)
    {
        Status = status;
        Grid = grid;
        TotalProbed = totalProbed;
        Detail = detail ?? string.Empty;
    }

    public OpeningDefenseGridProbeStatus Status { get; }
    public OpeningDefenseGrid? Grid { get; }
    public int TotalProbed { get; }
    public string Detail { get; }
}

/// <summary>
/// Implementations must only perform read-only validation. ProbeNext is one bounded frame slice.
/// </summary>
public interface IOpeningDefenseGridProbe
{
    bool TryInitialize(
        string disposableEnum,
        JObject? catapultResult,
        bool placementIsAttribute,
        out string error);
    OpeningDefenseGridProbeResult ProbeNext();
    void Reset();
}

public enum OpeningDefensePreparationPhase
{
    QueryCatapults,
    QueryPlacementDisposable,
    QuerySpecialStationDisposable,
    ProbeStationGrid,
    QueryInteractionGuard,
    ConfirmStationGrid,
    WaitForPlacementSettlement,
    VerifyStationPlacement,
    QueryIndependentVehicles,
    PreviewRailPath,
    QueryRailBaseline,
    DrawRailPath,
    VerifyRail,
    QueryDeploymentState,
    PlaceVehicle,
    VerifyDeployment,
    VerifyRailFinal,
    PlacementVerificationFailed,
    Completed
}

public sealed class OpeningDefensePreparationDecision
{
    internal OpeningDefensePreparationDecision(
        OpeningDefensePreparationPhase phase,
        AutomationAction? action,
        string detail)
    {
        Phase = phase;
        Action = action;
        Detail = detail;
    }

    public OpeningDefensePreparationPhase Phase { get; }
    public AutomationAction? Action { get; }
    public string Detail { get; }

    // Kept for protocol/test compatibility. The compatibility macro no longer exists in this flow.
    public bool UsesLegacyFallback => false;
    public bool IsComplete => Phase == OpeningDefensePreparationPhase.Completed;
}

/// <summary>
/// Builds the initial defense as a verified, forward-only transaction. Every decision returns at
/// most one runtime command. The planner never calls the monolithic default-defense command, never
/// resubmits a rail draw, and never deletes a legal rail after it has been committed.
/// </summary>
public sealed class OpeningDefensePreparationPlanner
{
    private const string CommonDisposableEnum = "FreePoint";
    private const string AttributeDisposableEnum = "FreePoint_Attribute";
    private const int MaximumPlacementVerificationAttempts = 12;
    private const int MaximumGridProbeSlices = 256;
    private const int MaximumRailVerificationAttempts = 12;
    private const int MaximumDeploymentObservationAttempts = 12;
    private const int MaximumFinalVerificationAttempts = 12;
    private const int MaximumPreWriteReadAttempts = 12;

    private readonly IOpeningDefenseGridProbe _probe;
    private readonly BattleDecisionEngine _railVerifier = new();
    private OpeningDefensePreparationPhase _phase = OpeningDefensePreparationPhase.QueryCatapults;
    private OpeningDefenseGrid? _selectedGrid;
    private OpeningDefenseGrid? _attributeGrid;
    private string _placementDisposableEnum = string.Empty;
    private bool _placementIsAttribute;
    private bool _commonInventoryExhausted;
    private bool _specialInventoryExhausted;
    private readonly HashSet<string> _unplaceableSpecialDisposables = new(StringComparer.Ordinal);
    private JObject? _placementDisposableIdentity;
    private JObject? _catapultResult;
    private JObject? _vehicleResult;
    private AutomationAction? _drawAction;
    private JObject? _railBaselineResult;
    private JObject? _drawResult;
    private JObject? _verifiedRailResult;
    private AutomationAction? _vehiclePlacementAction;
    private int _selectedVehicleInstanceId;
    private int _expectedRailInstanceId;
    private int _placementVerificationAttempts;
    private int _gridProbeSlices;
    private int _railVerificationAttempts;
    private int _deploymentObservationAttempts;
    private int _finalDeploymentVerificationAttempts;
    private int _finalRailVerificationAttempts;
    private int _catapultReadAttempts;
    private int _interactionGuardReadAttempts;
    private int _vehicleReadAttempts;
    private int _railPreviewReadAttempts;
    private int _railBaselineReadAttempts;
    private int _cleanDrawRetryAttempts;
    private double _recommendedRetryDelaySeconds;
    private string _lastCleanDrawFailureFingerprint = string.Empty;
    private OpeningDefenseGrid? _submittedPlacementGrid;
    private string _submittedPlacementDisposableEnum = string.Empty;
    private bool _drawSubmitted;
    private bool _vehiclePlacementSubmitted;
    private string _failureDetail = string.Empty;

    public OpeningDefensePreparationPlanner(IOpeningDefenseGridProbe probe)
    {
        _probe = probe ?? throw new ArgumentNullException(nameof(probe));
    }

    public OpeningDefensePreparationPhase Phase => _phase;
    public int SelectedVehicleInstanceId => _selectedVehicleInstanceId;
    public int VerifiedRailInstanceId => _expectedRailInstanceId;
    public bool DrawSubmitted => _drawSubmitted;
    public int CleanDrawRetryAttempts => _cleanDrawRetryAttempts;
    public double RecommendedRetryDelaySeconds => _recommendedRetryDelaySeconds;
    public string PlacementDisposableEnum => _placementDisposableEnum;
    public bool HasCommittedWrite =>
        _submittedPlacementGrid.HasValue || _drawSubmitted || _vehiclePlacementSubmitted;
    public string VehiclePlacementCommand => _vehiclePlacementAction?.Command ?? string.Empty;
    public IReadOnlyList<int> SelectedLinePointInstanceIds =>
        (_drawAction?.Arguments["linePointInstanceIds"] as JArray)?.Values<int>().ToArray()
        ?? Array.Empty<int>();

    public void Reset()
    {
        _probe.Reset();
        _selectedGrid = null;
        _attributeGrid = null;
        _placementDisposableEnum = string.Empty;
        _placementIsAttribute = false;
        _commonInventoryExhausted = false;
        _specialInventoryExhausted = false;
        _unplaceableSpecialDisposables.Clear();
        _placementDisposableIdentity = null;
        _catapultResult = null;
        _vehicleResult = null;
        _drawAction = null;
        _railBaselineResult = null;
        _drawResult = null;
        _verifiedRailResult = null;
        _vehiclePlacementAction = null;
        _selectedVehicleInstanceId = 0;
        _expectedRailInstanceId = 0;
        _placementVerificationAttempts = 0;
        _gridProbeSlices = 0;
        _railVerificationAttempts = 0;
        _deploymentObservationAttempts = 0;
        _finalDeploymentVerificationAttempts = 0;
        _finalRailVerificationAttempts = 0;
        _catapultReadAttempts = 0;
        _interactionGuardReadAttempts = 0;
        _vehicleReadAttempts = 0;
        _railPreviewReadAttempts = 0;
        _railBaselineReadAttempts = 0;
        _cleanDrawRetryAttempts = 0;
        _recommendedRetryDelaySeconds = 0d;
        _lastCleanDrawFailureFingerprint = string.Empty;
        _submittedPlacementGrid = null;
        _submittedPlacementDisposableEnum = string.Empty;
        _drawSubmitted = false;
        _vehiclePlacementSubmitted = false;
        _failureDetail = string.Empty;
        _phase = OpeningDefensePreparationPhase.QueryCatapults;
    }

    /// <summary>
    /// Resumes a transaction after its owner stopped or faulted. Once a write has been
    /// submitted, recovery is read-only and must preserve every identity needed to
    /// reconcile the result instead of issuing the write again.
    /// </summary>
    public void ResumeCommittedTransaction()
    {
        if (_phase == OpeningDefensePreparationPhase.Completed)
        {
            return;
        }

        if (_vehiclePlacementSubmitted)
        {
            ResetCommittedVerificationAttempts();
            _failureDetail = string.Empty;
            _phase = OpeningDefensePreparationPhase.VerifyDeployment;
            return;
        }

        if (_drawSubmitted)
        {
            ResetCommittedVerificationAttempts();
            _failureDetail = string.Empty;
            _phase = OpeningDefensePreparationPhase.VerifyRail;
            return;
        }

        if (_submittedPlacementGrid.HasValue)
        {
            _selectedGrid = _submittedPlacementGrid;
            _placementDisposableEnum = _submittedPlacementDisposableEnum;
            _placementVerificationAttempts = 0;
            _failureDetail = string.Empty;
            if (_phase != OpeningDefensePreparationPhase.WaitForPlacementSettlement)
            {
                _phase = OpeningDefensePreparationPhase.VerifyStationPlacement;
            }

            return;
        }

        Reset();
    }

    private void ResetCommittedVerificationAttempts()
    {
        _railVerificationAttempts = 0;
        _deploymentObservationAttempts = 0;
        _finalDeploymentVerificationAttempts = 0;
        _finalRailVerificationAttempts = 0;
    }

    public void MarkPlacementPreviewReleased()
    {
        if (_phase != OpeningDefensePreparationPhase.WaitForPlacementSettlement)
        {
            return;
        }

        _placementVerificationAttempts = 0;
        if (_selectedGrid.HasValue)
        {
            _phase = OpeningDefensePreparationPhase.VerifyStationPlacement;
            return;
        }

        Fail("开局属性弹射点预览已结束，但目标网格身份丢失；已安全停止且不会调用旧版整图宏。");
    }

    public OpeningDefensePreparationDecision Decide()
    {
        switch (_phase)
        {
            case OpeningDefensePreparationPhase.QueryCatapults:
                return Command("queryCatapults", null, "读取可用弹射点并持久化站点身份。");

            case OpeningDefensePreparationPhase.QueryPlacementDisposable:
                return Command(
                    "queryDisposable",
                    null,
                    _placementDisposableEnum == CommonDisposableEnum
                        ? "读取背包普通弹射点数量与稳定道具身份。"
                        : "读取背包动力弹射点数量与稳定道具身份。");

            case OpeningDefensePreparationPhase.QuerySpecialStationDisposable:
                return Command(
                    "queryDisposable",
                    null,
                    "读取背包可移动特殊弹射点；开波前必须把它们纳入防御圆环。");

            case OpeningDefensePreparationPhase.ProbeStationGrid:
                return DecideProbe();

            case OpeningDefensePreparationPhase.QueryInteractionGuard:
                return Command(
                    "queryOpeningDefenseInteractionGuard",
                    null,
                    "确认当前没有玩家或其他系统创建的活动道具交互。");

            case OpeningDefensePreparationPhase.ConfirmStationGrid:
                if (!_selectedGrid.HasValue)
                {
                    return Failure("增量探测选中的弹射点网格丢失。");
                }
                if (string.IsNullOrWhiteSpace(_placementDisposableEnum) ||
                    _placementDisposableIdentity == null)
                {
                    return Failure("背包弹射点的稳定道具身份丢失，尚未提交放置命令。");
                }

                OpeningDefenseGrid selected = _selectedGrid.Value;
                JObject confirmationArguments = (JObject)_placementDisposableIdentity.DeepClone();
                confirmationArguments["disposableEnum"] = _placementDisposableEnum;
                confirmationArguments["grid"] = JObject.FromObject(new { x = selected.X, y = selected.Y });
                return Command(
                    "confirmDisposableGrid",
                    confirmationArguments,
                    _placementDisposableEnum == CommonDisposableEnum
                        ? $"从背包在网格 {selected} 放置开局普通弹射点。"
                        : _placementDisposableEnum == AttributeDisposableEnum
                            ? $"从背包在网格 {selected} 放置开局动力弹射点。"
                            : $"从背包在网格 {selected} 放置开局特殊弹射点 {_placementDisposableEnum}。");

            case OpeningDefensePreparationPhase.WaitForPlacementSettlement:
                return Wait("弹射点已提交，正在逐帧等待预览和生成动画完全退出。");

            case OpeningDefensePreparationPhase.VerifyStationPlacement:
                return Command(
                    "queryCatapults",
                    null,
                    _placementDisposableEnum == CommonDisposableEnum
                        ? "验证背包中的普通弹射点已在目标网格生成。"
                        : "验证背包中的动力弹射点已在目标网格生成。");

            case OpeningDefensePreparationPhase.QueryIndependentVehicles:
                return Command("queryIndependentVehicleState", null, "读取背包战车、独立运行状态与轨道动态容量。");

            case OpeningDefensePreparationPhase.PreviewRailPath:
                return CloneRailAction("previewRailPath", "只读预览开局四向优先闭环。");

            case OpeningDefensePreparationPhase.QueryRailBaseline:
                return Command("queryRail", null, "记录画轨前的轨道数量与实例身份基线。");

            case OpeningDefensePreparationPhase.DrawRailPath:
                if (_drawSubmitted)
                {
                    return Failure("开局轨道写入已经提交过；拒绝重复画轨。");
                }

                string currentFingerprint = BuildDrawRetryFingerprint();
                if (_cleanDrawRetryAttempts >= 3 &&
                    string.Equals(currentFingerprint, _lastCleanDrawFailureFingerprint, StringComparison.Ordinal))
                {
                    _recommendedRetryDelaySeconds = 3d;
                    _phase = OpeningDefensePreparationPhase.QueryCatapults;
                    return Wait("同一画轨快照连续未提交；只读等待站点、战车、轨道或交互状态变化后再尝试。");
                }

                if (_cleanDrawRetryAttempts > 0 &&
                    !string.Equals(currentFingerprint, _lastCleanDrawFailureFingerprint, StringComparison.Ordinal))
                {
                    _cleanDrawRetryAttempts = 0;
                    _recommendedRetryDelaySeconds = 0d;
                    _lastCleanDrawFailureFingerprint = string.Empty;
                }

                return CloneRailAction("drawRailPath", "按已验证的站点身份创建开局闭环。");

            case OpeningDefensePreparationPhase.VerifyRail:
                return Command("queryRail", null, "验证唯一新增轨道的身份、点集与合法性。");

            case OpeningDefensePreparationPhase.QueryDeploymentState:
                return Command("queryIndependentVehicleState", null, "读取新闭环唯一能量点与动态容量，准备按实例投放。");

            case OpeningDefensePreparationPhase.PlaceVehicle:
                if (_vehiclePlacementSubmitted)
                {
                    return Failure("开局战车投放命令已经提交过；拒绝盲目重复写入。");
                }

                return _vehiclePlacementAction == null
                    ? Failure("缺少经过身份验证的开局战车投放动作。")
                    : new OpeningDefensePreparationDecision(
                        _phase,
                        _vehiclePlacementAction,
                        _vehiclePlacementAction.Reason);

            case OpeningDefensePreparationPhase.VerifyDeployment:
                return Command("queryIndependentVehicleState", null, "只读验证同一实例战车已运行或进入目标轨道 FIFO 等待队列。");

            case OpeningDefensePreparationPhase.VerifyRailFinal:
                return Command("queryRail", null, "最终复核合法闭环仍与提交的站点身份完全一致。");

            case OpeningDefensePreparationPhase.PlacementVerificationFailed:
                return Wait(string.IsNullOrWhiteSpace(_failureDetail)
                    ? "开局防线逐帧准备失败；未重画也未删除任何合法轨道。"
                    : _failureDetail);

            case OpeningDefensePreparationPhase.Completed:
                return new OpeningDefensePreparationDecision(
                    _phase,
                    null,
                    "开局闭环、动态容量和独立战车身份已完成分帧复核。");

            default:
                return Failure("遇到未知的开局防线阶段；已安全停止。");
        }
    }

    /// <summary>
    /// Records one completed runtime command. For writes, accepted means the normal safety policy
    /// accepted the result; draw/placement submissions still advance to read-only reconciliation
    /// after a clean failure or pending result so the write can never be sent twice blindly.
    /// </summary>
    public void Observe(AutomationAction action, JObject? result, bool accepted)
    {
        if (action == null) throw new ArgumentNullException(nameof(action));

        switch (action.Command)
        {
            case "queryCatapults":
                if (_phase == OpeningDefensePreparationPhase.VerifyStationPlacement)
                {
                    ObservePlacementVerification(result, accepted);
                }
                else
                {
                    ObserveCatapults(result, accepted);
                }
                break;

            case "queryDisposable":
                if (_phase == OpeningDefensePreparationPhase.QuerySpecialStationDisposable)
                    ObserveSpecialStationDisposable(result, accepted);
                else
                    ObservePlacementDisposable(result, accepted);
                break;

            case "queryOpeningDefenseInteractionGuard":
                ObserveInteractionGuard(result, accepted);
                break;

            case "confirmDisposableGrid":
                if (accepted)
                {
                    if (_selectedGrid.HasValue)
                    {
                        _submittedPlacementGrid = _selectedGrid;
                        _submittedPlacementDisposableEnum = _placementDisposableEnum;
                        _phase = OpeningDefensePreparationPhase.WaitForPlacementSettlement;
                    }
                    else
                    {
                        Fail("弹射点确认已被接受，但提交时的目标网格身份已经丢失；已停止且不会重复确认。");
                    }
                }
                else
                {
                    Fail("开局弹射点确认失败；不会继续画轨或调用旧版整图宏。");
                }
                break;

            case "queryIndependentVehicleState":
                if (_phase == OpeningDefensePreparationPhase.QueryIndependentVehicles)
                {
                    ObserveInitialVehicle(result, accepted);
                }
                else if (_phase == OpeningDefensePreparationPhase.QueryDeploymentState)
                {
                    ObserveDeploymentState(result, accepted);
                }
                else if (_phase == OpeningDefensePreparationPhase.VerifyDeployment)
                {
                    ObserveFinalDeployment(result, accepted);
                }
                break;

            case "previewRailPath":
                ObserveRailPreview(result, accepted);
                break;

            case "queryRail":
                ObserveRailQuery(result, accepted);
                break;

            case "drawRailPath":
                ObserveRailDraw(result, accepted);
                break;

            case "deployVehicleToEnergyPoint":
                _vehiclePlacementSubmitted = true;
                _phase = OpeningDefensePreparationPhase.VerifyDeployment;
                break;
        }
    }

    private OpeningDefensePreparationDecision DecideProbe()
    {
        OpeningDefenseGridProbeResult? probeResult;
        try
        {
            probeResult = _probe.ProbeNext();
            _gridProbeSlices++;
        }
        catch (Exception ex)
        {
            return Failure("增量候选网格探测发生异常：" + ex.Message);
        }

        if (probeResult == null)
        {
            return Failure("增量候选网格探测没有返回结果。");
        }

        switch (probeResult.Status)
        {
            case OpeningDefenseGridProbeStatus.Found when probeResult.Grid.HasValue:
                _selectedGrid = probeResult.Grid.Value;
                _interactionGuardReadAttempts = 0;
                _phase = OpeningDefensePreparationPhase.QueryInteractionGuard;
                return Decide();

            case OpeningDefenseGridProbeStatus.Probing:
                if (_gridProbeSlices >= MaximumGridProbeSlices)
                {
                    return Failure($"增量候选网格探测达到 {MaximumGridProbeSlices} 个分帧周期上限。");
                }

                return Wait($"已分批检查 {probeResult.TotalProbed} 个候选格；下一帧继续。");

            case OpeningDefenseGridProbeStatus.Exhausted:
                if (string.Equals(_placementDisposableEnum, CommonDisposableEnum, StringComparison.Ordinal))
                {
                    _commonInventoryExhausted = true;
                    _selectedGrid = null;
                    _placementDisposableIdentity = null;
                    _phase = OpeningDefensePreparationPhase.QueryCatapults;
                    return Wait(
                        $"普通弹射点仍在背包，但游戏当前真实放置规则已穷尽 {probeResult.TotalProbed} 个候选格；" +
                        "已记录为本轮不可放置，不会盲点禁区。");
                }

                if (!string.Equals(_placementDisposableEnum, AttributeDisposableEnum, StringComparison.Ordinal))
                {
                    _unplaceableSpecialDisposables.Add(_placementDisposableEnum);
                    _selectedGrid = null;
                    _placementDisposableIdentity = null;
                    _phase = OpeningDefensePreparationPhase.QuerySpecialStationDisposable;
                    return Wait(
                        $"特殊弹射点 {_placementDisposableEnum} 在游戏当前真实放置规则下没有合法格；" +
                        "已跳过该种道具并继续检查其余库存。");
                }

                return Failure($"动力始发站候选格已耗尽（检查 {probeResult.TotalProbed} 个），无法创建开局闭环。");

            default:
                return Failure(
                    "当前游戏运行时无法使用增量候选网格探测。" +
                    (string.IsNullOrWhiteSpace(probeResult.Detail) ? string.Empty : " " + probeResult.Detail));
        }
    }

    private void ObserveCatapults(JObject? result, bool accepted)
    {
        if (!accepted)
        {
            RetryPreWriteRead(
                ref _catapultReadAttempts,
                "读取开局弹射点状态连续失败。");
            return;
        }

        _catapultReadAttempts = 0;
        if (!TryReadCatapults(result, out List<JObject> catapults))
        {
            Fail("读取开局弹射点成功，但响应缺少 catapults 数组。");
            return;
        }

        _catapultResult = result?.DeepClone() as JObject;
        List<JObject> usable = catapults.Where(IsAvailablePoint).ToList();
        List<OpeningDefenseGrid> commonAnchors = usable
            .Where(point => point["isAttribute"]?.Value<bool>() != true)
            .Select(point => TryReadGrid(point, out OpeningDefenseGrid grid) ? grid : (OpeningDefenseGrid?)null)
            .Where(grid => grid.HasValue)
            .Select(grid => grid!.Value)
            .Distinct()
            .ToList();

        bool hasAttribute = usable.Any(point => point["isAttribute"]?.Value<bool>() == true);
        if (!hasAttribute)
        {
            // The live energy candidate pool already incorporates the current base and station
            // forbidden regions. Rank its closest feasible coverage tier; do not invent an extra
            // radius band that pushes the origin away from the base.
            PreparePlacementDisposableQuery(AttributeDisposableEnum, placementIsAttribute: true);
            return;
        }

        // Inventory is part of the opening-defense gate. Two common points are merely the
        // minimum needed to draw a loop; they are not proof that the backpack has been drained.
        // Re-query after each placement until the real inventory stack reaches zero.
        if (!_commonInventoryExhausted)
        {
            PreparePlacementDisposableQuery(CommonDisposableEnum);
            return;
        }

        if (!_specialInventoryExhausted)
        {
            _phase = OpeningDefensePreparationPhase.QuerySpecialStationDisposable;
            return;
        }

        if (commonAnchors.Count < 2)
        {
            Fail("普通和可移动特殊弹射点库存已检查完毕，但场上仍不足 2 个可连中继站，无法创建开局闭环。");
            return;
        }

        _phase = OpeningDefensePreparationPhase.QueryIndependentVehicles;
    }

    private void ObservePlacementDisposable(JObject? result, bool accepted)
    {
        if (_phase != OpeningDefensePreparationPhase.QueryPlacementDisposable)
        {
            Fail("背包弹射点响应出现在错误的准备阶段。");
            return;
        }

        if (!accepted)
        {
            RetryPreWriteRead(
                ref _catapultReadAttempts,
                "读取背包弹射点状态连续失败。");
            return;
        }

        JObject state = State(result);
        List<JObject> matchingInventory = (state["items"] as JArray)?.OfType<JObject>()
            .Where(candidate =>
                string.Equals(
                    candidate["disposableEnum"]?.Value<string>(),
                    _placementDisposableEnum,
                    StringComparison.Ordinal))
            .Where(candidate => ReadInt(candidate["count"], 0) > 0)
            .ToList() ?? new List<JObject>();
        JObject? item = matchingInventory
            .Where(candidate =>
                candidate["active"]?.Value<bool>() != false &&
                candidate["buttonActive"]?.Value<bool>() != false &&
                string.Equals(
                    candidate["interactionType"]?.Value<string>(),
                    "GridChooseInteraction",
                    StringComparison.Ordinal))
            .OrderBy(candidate => ReadInt(candidate["index"], int.MaxValue))
            .FirstOrDefault();
        if (item == null)
        {
            if (matchingInventory.Count > 0)
            {
                RetryPreWriteRead(
                    ref _catapultReadAttempts,
                    $"背包中的 {_placementDisposableEnum} 仍有库存，但游戏当前尚未开放格子放置按钮。");
                return;
            }

            if (string.Equals(_placementDisposableEnum, CommonDisposableEnum, StringComparison.Ordinal))
            {
                _commonInventoryExhausted = true;
                _placementDisposableIdentity = null;
                _phase = OpeningDefensePreparationPhase.QueryCatapults;
                return;
            }

            int placed = TryReadCatapults(_catapultResult, out List<JObject> catapults)
                ? catapults.Count(point =>
                    IsAvailablePoint(point) &&
                    string.Equals(
                        point["recycleDisposableEnum"]?.Value<string>(),
                        _placementDisposableEnum,
                        StringComparison.Ordinal))
                : 0;
            string pointName = _placementDisposableEnum == CommonDisposableEnum
                ? "普通弹射点"
                : "动力弹射点";
            Fail($"{pointName}背包库存不可用（场上可连 {placed} 个）；尚未提交放置命令。");
            return;
        }

        JObject identity = BuildDisposableIdentity(item);
        if (!identity.HasValues)
        {
            Fail("已读到背包弹射点，但缺少 itemInstanceId、path 或 index 等稳定身份；尚未提交放置命令。");
            return;
        }

        _placementDisposableIdentity = identity;
        BeginPlacementProbe(_placementDisposableEnum, _catapultResult, _placementIsAttribute);
    }

    private void ObserveSpecialStationDisposable(JObject? result, bool accepted)
    {
        if (!accepted)
        {
            RetryPreWriteRead(ref _catapultReadAttempts, "读取背包特殊弹射点连续失败。");
            return;
        }

        _catapultReadAttempts = 0;
        RuntimeSpecialStationDisposable? special = _railVerifier
            .DiscoverMovableStationDisposables(result)
            .Where(item => !string.Equals(item.DisposableEnum, CommonDisposableEnum, StringComparison.Ordinal) &&
                           !string.Equals(item.DisposableEnum, AttributeDisposableEnum, StringComparison.Ordinal))
            // One player loop can contain exactly one origin station. Once the opening origin is
            // on the field, consume movable relay stations into that ring; a special origin is
            // reserved for the separate-loop expansion transaction instead of being stranded.
            .Where(item => !item.IsAttribute)
            .Where(item => !_unplaceableSpecialDisposables.Contains(item.DisposableEnum))
            .FirstOrDefault();
        if (special == null)
        {
            bool unavailableInventoryExists = (State(result)["items"] as JArray)?.OfType<JObject>()
                .Where(item => ReadInt(item["count"], 0) > 0)
                .Where(BattleDecisionEngine.IsMovableStationDisposable)
                .Any(item =>
                    !string.Equals(item.SelectToken("effectFacts.stationKind")?.Value<string>(),
                        "AttributeCatapult", StringComparison.Ordinal) &&
                    !_unplaceableSpecialDisposables.Contains(
                        item["disposableEnum"]?.Value<string>() ?? string.Empty)) == true;
            if (unavailableInventoryExists)
            {
                RetryPreWriteRead(
                    ref _catapultReadAttempts,
                    "背包仍有可移动特殊弹射点，但游戏当前尚未开放放置按钮。");
                return;
            }

            _specialInventoryExhausted = true;
            _phase = OpeningDefensePreparationPhase.QueryCatapults;
            return;
        }

        _placementDisposableEnum = special.DisposableEnum;
        _placementIsAttribute = special.IsAttribute;
        _placementDisposableIdentity = (JObject)special.ItemIdentity.DeepClone();
        BeginPlacementProbe(_placementDisposableEnum, _catapultResult, _placementIsAttribute);
    }

    private void BeginPlacementProbe(
        string disposableEnum,
        JObject? catapultResult,
        bool placementIsAttribute)
    {
        _placementDisposableEnum = disposableEnum;
        try
        {
            if (!_probe.TryInitialize(
                    disposableEnum,
                    catapultResult,
                    placementIsAttribute,
                    out string error))
            {
                Fail("无法初始化增量候选网格探测。" +
                     (string.IsNullOrWhiteSpace(error) ? string.Empty : " " + error));
                return;
            }
        }
        catch (Exception ex)
        {
            Fail("初始化增量候选网格探测时发生异常：" + ex.Message);
            return;
        }

        _gridProbeSlices = 0;
        _phase = OpeningDefensePreparationPhase.ProbeStationGrid;
    }

    private void ObserveInteractionGuard(JObject? result, bool accepted)
    {
        if (_phase != OpeningDefensePreparationPhase.QueryInteractionGuard)
        {
            Fail("开局交互守卫响应出现在错误的准备阶段。");
            return;
        }

        if (!accepted)
        {
            RetryPreWriteRead(
                ref _interactionGuardReadAttempts,
                "开局属性弹射点确认前连续无法读取道具交互守卫。");
            return;
        }

        _interactionGuardReadAttempts = 0;
        JObject state = State(result);
        if (!TryReadBool(state["noActiveInteraction"], out bool noActiveInteraction) ||
            !TryReadBool(state["observationConsistent"], out bool observationConsistent) ||
            !TryReadBool(state["isInPreview"], out bool isInPreview) ||
            !TryReadBool(state["hasLastInteraction"], out bool hasLastInteraction))
        {
            Fail("开局交互守卫响应缺少明确的活动交互布尔状态。");
            return;
        }

        bool expectedNoActiveInteraction = observationConsistent && !isInPreview && !hasLastInteraction;
        if (!observationConsistent || noActiveInteraction != expectedNoActiveInteraction)
        {
            Fail("开局交互守卫返回了互相矛盾的活动交互状态。");
            return;
        }

        if (noActiveInteraction)
        {
            _phase = OpeningDefensePreparationPhase.ConfirmStationGrid;
        }
    }

    private void ObservePlacementVerification(JObject? result, bool accepted)
    {
        if (accepted &&
            _selectedGrid.HasValue &&
            TryReadCatapults(result, out List<JObject> catapults) &&
            catapults.Any(point => MatchesSubmittedPlacement(point, _selectedGrid.Value)))
        {
            _catapultResult = result?.DeepClone() as JObject;
            _placementVerificationAttempts = 0;
            if (_placementIsAttribute)
            {
                _attributeGrid = _selectedGrid;
            }
            if (string.Equals(_placementDisposableEnum, CommonDisposableEnum, StringComparison.Ordinal))
            {
                // Query the same inventory again: a stack represents multiple independently
                // placeable stations, and every successful confirmation consumes only one.
                _phase = OpeningDefensePreparationPhase.QueryCatapults;
                return;
            }

            if (!string.Equals(_placementDisposableEnum, AttributeDisposableEnum, StringComparison.Ordinal))
            {
                _catapultResult = result?.DeepClone() as JObject;
                _phase = OpeningDefensePreparationPhase.QuerySpecialStationDisposable;
                return;
            }

            _phase = OpeningDefensePreparationPhase.QueryCatapults;
            return;
        }

        _placementVerificationAttempts++;
        if (_placementVerificationAttempts >= MaximumPlacementVerificationAttempts)
        {
            Fail($"连续 {MaximumPlacementVerificationAttempts} 次未能验证目标网格的{PlacementDisplayName()}；为避免重复放置已停止。");
        }
    }

    private bool MatchesSubmittedPlacement(JObject point, OpeningDefenseGrid expectedGrid)
    {
        bool expectedAttribute = _placementIsAttribute;
        return IsAvailablePoint(point) &&
               (point["isAttribute"]?.Value<bool>() == true) == expectedAttribute &&
               (string.Equals(
                    point["recycleDisposableEnum"]?.Value<string>(),
                    _placementDisposableEnum,
                    StringComparison.Ordinal) ||
                expectedAttribute && point["isAttribute"]?.Value<bool>() == true) &&
               TryReadGrid(point, out OpeningDefenseGrid grid) &&
               grid.Equals(expectedGrid);
    }

    private string PlacementDisplayName() =>
        string.Equals(_placementDisposableEnum, CommonDisposableEnum, StringComparison.Ordinal)
            ? "普通弹射点"
            : string.Equals(_placementDisposableEnum, AttributeDisposableEnum, StringComparison.Ordinal)
                ? "动力弹射点"
                : "特殊弹射点 " + _placementDisposableEnum;

    private void PreparePlacementDisposableQuery(string disposableEnum, bool placementIsAttribute = false)
    {
        _placementDisposableEnum = disposableEnum;
        _placementIsAttribute = placementIsAttribute ||
                                string.Equals(disposableEnum, AttributeDisposableEnum, StringComparison.Ordinal);
        _placementDisposableIdentity = null;
        _selectedGrid = null;
        _phase = OpeningDefensePreparationPhase.QueryPlacementDisposable;
    }

    private void ObserveInitialVehicle(JObject? result, bool accepted)
    {
        if (!accepted)
        {
            RetryPreWriteRead(
                ref _vehicleReadAttempts,
                "读取开局背包战车状态连续失败。");
            return;
        }

        _vehicleReadAttempts = 0;
        if (State(result)["vehicles"] is not JArray vehicles)
        {
            Fail("读取开局背包战车成功，但响应缺少 vehicles 数组。");
            return;
        }

        JObject? vehicle = vehicles.OfType<JObject>()
            .Where(IsBagVehicle)
            .Where(item => ReadInt(item["instanceId"], 0) != 0)
            .OrderByDescending(item => item["baseCombatPower"]?.Value<double?>() ?? 0d)
            .ThenBy(item => ReadInt(item["instanceId"], int.MaxValue))
            .FirstOrDefault();
        if (vehicle == null)
        {
            Fail("没有找到带稳定 instanceId 的可用背包战车。");
            return;
        }

        _selectedVehicleInstanceId = ReadInt(vehicle["instanceId"], 0);
        _vehicleResult = result?.DeepClone() as JObject;
        if (!TryBuildRailAction(_catapultResult, _attributeGrid, out AutomationAction? action, out string error))
        {
            Fail(error);
            return;
        }

        _drawAction = action;
        _phase = OpeningDefensePreparationPhase.PreviewRailPath;
    }

    private void ObserveRailPreview(JObject? result, bool accepted)
    {
        if (!accepted)
        {
            RetryPreWriteRead(
                ref _railPreviewReadAttempts,
                "读取开局闭环只读预览连续失败。");
            return;
        }

        _railPreviewReadAttempts = 0;
        if (!HasCompleteRailPreviewState(result))
        {
            Fail("开局闭环预览成功响应缺少完整的合法性或无副作用状态。");
            return;
        }

        if (!_railVerifier.IsLegalDefenseExpansionPreview(result))
        {
            Fail("开局闭环未通过只读合法性或无副作用检查。");
            return;
        }

        _phase = OpeningDefensePreparationPhase.QueryRailBaseline;
    }

    private void ObserveRailQuery(JObject? result, bool accepted)
    {
        switch (_phase)
        {
            case OpeningDefensePreparationPhase.QueryRailBaseline:
                if (!accepted)
                {
                    RetryPreWriteRead(
                        ref _railBaselineReadAttempts,
                        "读取画轨前轨道基线连续失败。");
                    return;
                }

                _railBaselineReadAttempts = 0;
                if (!_railVerifier.IsUsableDefenseExpansionRailBaseline(result))
                {
                    Fail("画轨前基线缺少一致的轨道数量或唯一实例身份。");
                    return;
                }

                _railBaselineResult = result?.DeepClone() as JObject;
                _phase = OpeningDefensePreparationPhase.DrawRailPath;
                break;

            case OpeningDefensePreparationPhase.VerifyRail:
                ObserveCommittedRail(result, accepted, finalCheck: false);
                break;

            case OpeningDefensePreparationPhase.VerifyRailFinal:
                ObserveCommittedRail(result, accepted, finalCheck: true);
                break;
        }
    }

    private void ObserveRailDraw(JObject? result, bool accepted)
    {
        if (!accepted && RuntimeResultInspector.IsCleanUncommittedRailDrawFailure(result))
        {
            string fingerprint = BuildDrawRetryFingerprint();
            _cleanDrawRetryAttempts = string.Equals(
                fingerprint,
                _lastCleanDrawFailureFingerprint,
                StringComparison.Ordinal)
                ? _cleanDrawRetryAttempts + 1
                : 1;
            _lastCleanDrawFailureFingerprint = fingerprint;
            _recommendedRetryDelaySeconds = _cleanDrawRetryAttempts < 3 ? 0.75d : 3d;
            _drawSubmitted = false;
            _drawResult = null;
            _expectedRailInstanceId = 0;
            _railBaselineResult = null;
            _railVerificationAttempts = 0;
            _catapultReadAttempts = 0;
            _vehicleReadAttempts = 0;
            _railPreviewReadAttempts = 0;
            _railBaselineReadAttempts = 0;
            _phase = OpeningDefensePreparationPhase.QueryCatapults;
            return;
        }

        _drawSubmitted = true;
        _recommendedRetryDelaySeconds = 0d;
        _drawResult = result?.DeepClone() as JObject ?? new JObject();
        _expectedRailInstanceId = _railVerifier.ReadDrawnRailInstanceId(result);
        _railVerificationAttempts = 0;
        _phase = OpeningDefensePreparationPhase.VerifyRail;
    }

    private string BuildDrawRetryFingerprint()
    {
        JObject fingerprint = new()
        {
            ["catapults"] = State(_catapultResult).DeepClone(),
            ["vehicle"] = State(_vehicleResult).DeepClone(),
            ["railBaseline"] = State(_railBaselineResult).DeepClone(),
            ["drawArguments"] = _drawAction?.Arguments.DeepClone() ?? new JObject()
        };
        return fingerprint.ToString(Newtonsoft.Json.Formatting.None);
    }

    private void ObserveCommittedRail(JObject? result, bool accepted, bool finalCheck)
    {
        if (!accepted)
        {
            int failedReadAttempts = finalCheck
                ? ++_finalRailVerificationAttempts
                : ++_railVerificationAttempts;
            int failedReadMaximum = finalCheck
                ? MaximumFinalVerificationAttempts
                : MaximumRailVerificationAttempts;
            if (failedReadAttempts >= failedReadMaximum)
            {
                Fail(finalCheck
                    ? "最终读取轨道状态连续失败；合法轨道保持原样且不会自动删除。"
                    : "画轨提交后连续无法读取轨道状态；为避免重复画轨已停止。");
            }
            return;
        }

        DefenseExpansionRailVerification verification = _railVerifier.VerifyDefenseExpansionRail(
            _railBaselineResult,
            _drawResult,
            result,
            _drawAction,
            _expectedRailInstanceId);
        if (verification.Verified && verification.Rail != null)
        {
            RailRuntimeValidation topology = RailRuntimeTopologyInspector.InspectRail(verification.Rail);
            if (!HasVerifiableRuntimeGeometry(verification.Rail))
            {
                Fail("游戏返回的开局轨道缺少完整站点和真实线段端点；无法证明它是闭合圆环。");
                return;
            }
            if (!topology.Loop.IsValid)
            {
                Fail("游戏返回的开局轨道不是包围基地的简单闭环：" +
                     string.Join("；", topology.Loop.Errors));
                return;
            }
            if (!RailLayoutStrategyPlanner.IsBalancedDefenseRing(topology.Layout))
            {
                Fail(
                    "游戏返回的开局轨道虽然闭合，但站点分布塌缩成细长形状，不能作为全向防御圆环。");
                return;
            }
            _expectedRailInstanceId = verification.RailInstanceId;
            _verifiedRailResult = new JObject { ["rail"] = verification.Rail.DeepClone() };
            if (finalCheck)
            {
                _phase = OpeningDefensePreparationPhase.Completed;
            }
            else
            {
                _deploymentObservationAttempts = 0;
                _phase = OpeningDefensePreparationPhase.QueryDeploymentState;
            }
            return;
        }

        if (verification.Pending)
        {
            int attempts = finalCheck
                ? ++_finalRailVerificationAttempts
                : ++_railVerificationAttempts;
            int maximum = finalCheck
                ? MaximumFinalVerificationAttempts
                : MaximumRailVerificationAttempts;
            if (attempts < maximum)
            {
                return;
            }

            Fail("画轨命令已提交，但在安全时限内没有证明唯一新增轨道；拒绝重画。");
            return;
        }

        Fail(verification.Detail + " 已保留现场并拒绝重画或删除轨道。");
    }

    private static bool HasVerifiableRuntimeGeometry(JObject rail)
    {
        JObject[] stations = ((rail["orderedStations"] as JArray) ?? (rail["points"] as JArray))?
            .OfType<JObject>().ToArray() ?? Array.Empty<JObject>();
        JObject[] lines = (rail["lines"] as JArray)?.OfType<JObject>().ToArray() ?? Array.Empty<JObject>();
        return stations.Length >= 3 && lines.Length >= 3 &&
               stations.All(station => station.SelectToken("grid.x") != null && station.SelectToken("grid.y") != null) &&
               lines.All(line => line.SelectToken("from.x") != null && line.SelectToken("from.y") != null &&
                                 line.SelectToken("to.x") != null && line.SelectToken("to.y") != null);
    }

    private void ObserveDeploymentState(JObject? result, bool accepted)
    {
        if (!accepted)
        {
            RetryDeploymentState("读取新闭环容量连续失败；不会盲目投放战车。");
            return;
        }

        JObject? verifiedRail = _verifiedRailResult?["rail"] as JObject;
        int expectedRailInstanceId = ReadInt(
            verifiedRail?["instanceId"],
            ReadInt(verifiedRail?["railInstanceId"], _expectedRailInstanceId));
        JObject? rail = (State(result)["rails"] as JArray)?.OfType<JObject>()
            .SingleOrDefault(item =>
                ReadInt(item["instanceId"], ReadInt(item["railInstanceId"], 0)) == expectedRailInstanceId);
        if (rail == null)
        {
            RetryDeploymentState("独立战车快照尚未返回已验证的新闭环。");
            return;
        }

        int energyPointInstanceId = ReadInt(rail["energyPointInstanceId"], 0);
        int freeCapacity = ReadInt(rail["freeCapacity"], 0);
        if (ReadInt(rail["energyPointCount"], 0) != 1 || energyPointInstanceId == 0)
        {
            Fail("新闭环没有且仅有一个可验证的能量弹射点；拒绝投放。");
            return;
        }
        if (freeCapacity <= 0)
        {
            Fail("新闭环动态容量已经满载；拒绝超容量投放。");
            return;
        }

        _vehiclePlacementAction = new AutomationAction(
            "deployVehicleToEnergyPoint",
            JObject.FromObject(new
            {
                vehicleInstanceId = _selectedVehicleInstanceId,
                energyPointInstanceId,
                railInstanceId = expectedRailInstanceId
            }),
            AutomationStage.PreparingDefense,
            "按战车与唯一能量点实例提交投放；发射点占用时由游戏 FIFO 排队。");
        _phase = OpeningDefensePreparationPhase.PlaceVehicle;
    }

    private void RetryDeploymentState(string detail)
    {
        _deploymentObservationAttempts++;
        if (_deploymentObservationAttempts >= MaximumDeploymentObservationAttempts)
        {
            Fail(detail + " 已达到观察上限；不会重复投放。");
        }
    }

    private void ObserveFinalDeployment(JObject? result, bool accepted)
    {
        JObject state = State(result);
        JObject? vehicle = (state["vehicles"] as JArray)?.OfType<JObject>()
            .SingleOrDefault(item => ReadInt(item["instanceId"], 0) == _selectedVehicleInstanceId);
        JObject? rail = (state["rails"] as JArray)?.OfType<JObject>()
            .SingleOrDefault(item =>
                ReadInt(item["instanceId"], ReadInt(item["railInstanceId"], 0)) == _expectedRailInstanceId);
        int actualRailId = ReadInt(vehicle?["railId"], 0);
        int expectedRailId = ReadRailInternalId(rail ?? _verifiedRailResult?["rail"] as JObject);
        bool settled = accepted && vehicle != null &&
                       (vehicle["running"]?.Value<bool>() == true || vehicle["queued"]?.Value<bool>() == true) &&
                       vehicle["inBag"]?.Value<bool>() != true &&
                       (expectedRailId == 0 || actualRailId == expectedRailId);
        if (settled)
        {
            _phase = OpeningDefensePreparationPhase.VerifyRailFinal;
            return;
        }

        _finalDeploymentVerificationAttempts++;
        if (_finalDeploymentVerificationAttempts >= MaximumFinalVerificationAttempts)
        {
            Fail("投放写入后未能证明同一战车已运行或进入 FIFO 等待队列；已锁定写入且不会重发。");
        }
    }

    private bool TryBuildRailAction(
        JObject? catapultResult,
        OpeningDefenseGrid? requiredAttributeGrid,
        out AutomationAction? action,
        out string error)
    {
        action = null;
        error = string.Empty;
        if (!TryReadCatapults(catapultResult, out List<JObject> catapults))
        {
            error = "弹射点快照丢失，无法创建稳定的闭环计划。";
            return false;
        }

        List<JObject> available = catapults.Where(IsAvailablePoint).ToList();
        List<JObject> attributes = available
            .Where(point => point["isAttribute"]?.Value<bool>() == true)
            .Where(point => !requiredAttributeGrid.HasValue ||
                            (TryReadGrid(point, out OpeningDefenseGrid grid) &&
                             grid.Equals(requiredAttributeGrid.Value)))
            .OrderBy(point => ReadInt(point["linePointInstanceId"], int.MaxValue))
            .ToList();
        List<JObject> commons = available
            .Where(point => point["isAttribute"]?.Value<bool>() != true)
            .Where(point => TryReadGrid(point, out _))
            .OrderBy(point => ReadInt(point["linePointInstanceId"], int.MaxValue))
            .ToList();
        if (attributes.Count == 0 || commons.Count < 2)
        {
            error = "没有 1 个匹配的属性站和至少 2 个可用普通站组成开局闭环。";
            return false;
        }

        List<RailLoopPointCandidate> loopCandidates = attributes
            .Concat(commons)
            .Select(point =>
            {
                if (!TryReadGrid(point, out OpeningDefenseGrid grid)) return null;
                int instanceId = ReadInt(point["linePointInstanceId"], 0);
                return instanceId == 0
                    ? null
                    : new RailLoopPointCandidate
                    {
                        InstanceId = instanceId,
                        IsAttribute = point["isAttribute"]?.Value<bool>() == true,
                        // Every station deployed by the opening transaction belongs to its final
                        // ring. Do not silently leave ordinary inventory points unconnected merely
                        // because a shorter three-point loop has a higher estimated N/T.
                        MustInclude = true,
                        Grid = new RailLayoutPoint(grid.X, grid.Y)
                    };
            })
            .Where(candidate => candidate != null)
            .Cast<RailLoopPointCandidate>()
            .ToList();
        RailLoopPlan? best = RailLayoutStrategyPlanner.PlanPlayerLoop(loopCandidates);

        if (best == null || !best.Score.IsValid ||
            !RailLayoutStrategyPlanner.IsBalancedDefenseRing(best.Score) ||
            best.OrderedPointInstanceIds.Count < 3)
        {
            error = "可用站点无法组成分布均衡、包围基地的防御圆环。";
            return false;
        }

        int selectedAttributeId = best.OrderedPointInstanceIds[0];
        JObject? selectedAttribute = attributes.SingleOrDefault(point =>
            ReadInt(point["linePointInstanceId"], 0) == selectedAttributeId);
        if (selectedAttribute == null ||
            !TryReadGrid(selectedAttribute, out OpeningDefenseGrid selectedAttributeGrid))
        {
            return false;
        }
        _selectedGrid = selectedAttributeGrid;

        if (_selectedVehicleInstanceId == 0)
        {
            error = "缺少已锁定的背包战车实例，无法提交独立战车开局布防。";
            return false;
        }

        action = new AutomationAction(
            "drawRailPath",
            new JObject
            {
                ["linePointInstanceIds"] = new JArray(best.OrderedPointInstanceIds)
            },
            AutomationStage.PreparingDefense,
            "使用已持久化的站点实例身份创建四向优先的合法开局闭环。");
        return true;
    }

    private OpeningDefensePreparationDecision CloneRailAction(string command, string detail)
    {
        if (_drawAction == null)
        {
            return Failure("开局闭环计划丢失。");
        }

        return new OpeningDefensePreparationDecision(
            _phase,
            new AutomationAction(
                command,
                _drawAction.Arguments.DeepClone() as JObject,
                AutomationStage.PreparingDefense,
                detail),
            detail);
    }

    private static int ReadRailInternalId(JObject? rail) =>
        ReadInt(rail?["railInternalId"], ReadInt(rail?["id"], 0));

    private static bool TryReadCatapults(JObject? result, out List<JObject> catapults)
    {
        JArray? items = State(result)["catapults"] as JArray;
        catapults = items?.OfType<JObject>().ToList() ?? new List<JObject>();
        return items != null;
    }

    private static JObject BuildDisposableIdentity(JObject item)
    {
        JObject identity = new();
        int itemInstanceId = ReadInt(item["itemInstanceId"], ReadInt(item["instanceId"], 0));
        if (itemInstanceId != 0)
        {
            identity["itemInstanceId"] = itemInstanceId;
            identity["instanceId"] = itemInstanceId;
            return identity;
        }

        string path = item["itemPath"]?.Value<string>() ?? item["path"]?.Value<string>() ?? string.Empty;
        if (!string.IsNullOrWhiteSpace(path))
        {
            identity["itemPath"] = path.Trim();
            identity["path"] = path.Trim();
            return identity;
        }

        int index = ReadInt(item["index"], -1);
        if (index >= 0)
        {
            identity["index"] = index;
        }

        return identity;
    }

    private static bool IsAvailablePoint(JObject point) =>
        point["active"]?.Value<bool>() != false &&
        point["canUseForNewRail"]?.Value<bool>() == true &&
        point["canPickLine"]?.Value<bool>() != false &&
        point["frozen"]?.Value<bool>() != true &&
        point["railReachMax"]?.Value<bool>() != true &&
        ReadInt(point["railMembershipCount"], 0) == 0 &&
        ReadInt(point["linePointInstanceId"], 0) != 0;

    private static bool IsSpecialPoint(JObject point)
    {
        if (point["isSpecial"]?.Value<bool>() == true) return true;
        string disposable = point["recycleDisposableEnum"]?.Value<string>() ?? string.Empty;
        return !string.IsNullOrWhiteSpace(disposable) &&
               !string.Equals(disposable, CommonDisposableEnum, StringComparison.Ordinal) &&
               !string.Equals(disposable, AttributeDisposableEnum, StringComparison.Ordinal);
    }

    private static bool IsBagVehicle(JObject vehicle) =>
        vehicle["inBag"]?.Value<bool>() == true &&
        vehicle["isFixedHead"]?.Value<bool>() != true;

    private static bool TryReadGrid(JObject point, out OpeningDefenseGrid grid)
    {
        grid = default;
        int x = ReadInt(point.SelectToken("grid.x"), int.MinValue);
        int y = ReadInt(point.SelectToken("grid.y"), int.MinValue);
        if (x == int.MinValue || y == int.MinValue) return false;
        grid = new OpeningDefenseGrid(x, y);
        return true;
    }

    private static double DistanceSquared(OpeningDefenseGrid left, OpeningDefenseGrid right)
    {
        double x = (double)left.X - right.X;
        double y = (double)left.Y - right.Y;
        return x * x + y * y;
    }

    private static JObject State(JObject? result) =>
        result?.SelectToken("data.state") as JObject ??
        result?["state"] as JObject ??
        result ??
        new JObject();

    private static int ReadInt(JToken? token, int fallback)
    {
        if (token == null || token.Type == JTokenType.Null) return fallback;
        if (token.Type == JTokenType.Integer) return token.Value<int>();
        return int.TryParse(token.Value<string>(), out int value) ? value : fallback;
    }

    private static bool TryReadBool(JToken? token, out bool value)
    {
        value = false;
        if (token?.Type != JTokenType.Boolean) return false;
        value = token.Value<bool>();
        return true;
    }

    private static bool HasCompleteRailPreviewState(JObject? result)
    {
        JObject state = State(result);
        return state["wouldBeLegal"]?.Type == JTokenType.Boolean &&
               state["sideEffectCheckPassed"]?.Type == JTokenType.Boolean &&
               state["statePolluted"]?.Type == JTokenType.Boolean &&
               state["beforeRailCount"]?.Type == JTokenType.Integer &&
               state["afterRailCount"]?.Type == JTokenType.Integer;
    }

    private void RetryPreWriteRead(ref int attempts, string failureDetail)
    {
        attempts++;
        if (attempts >= MaximumPreWriteReadAttempts)
        {
            Fail(failureDetail + $" 已达到 {MaximumPreWriteReadAttempts} 次上限；尚未提交后续写命令。");
        }
    }

    private void Fail(string detail)
    {
        try
        {
            _probe.Reset();
        }
        catch
        {
            // Failure handling remains fail-soft even when a runtime-backed probe cannot reset.
        }

        _failureDetail = string.IsNullOrWhiteSpace(detail)
            ? "开局防线逐帧准备失败；已安全停止。"
            : detail.Trim();
        _phase = OpeningDefensePreparationPhase.PlacementVerificationFailed;
    }

    private OpeningDefensePreparationDecision Failure(string detail)
    {
        Fail(detail);
        return Wait(_failureDetail);
    }

    private OpeningDefensePreparationDecision Command(
        string command,
        JObject? arguments,
        string detail) =>
        new(
            _phase,
            new AutomationAction(command, arguments, AutomationStage.PreparingDefense, detail),
            detail);

    private OpeningDefensePreparationDecision Wait(string detail) =>
        new(
            _phase,
            AutomationAction.Wait(AutomationStage.PreparingDefense, detail),
            detail);

}
