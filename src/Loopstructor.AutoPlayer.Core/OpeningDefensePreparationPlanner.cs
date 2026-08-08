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
            .Select(grid => new RankedGrid(grid, Score(grid, anchors)))
            .OrderBy(item => item.Score)
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

    private sealed class RankedGrid
    {
        public RankedGrid(OpeningDefenseGrid grid, double score)
        {
            Grid = grid;
            Score = score;
        }

        public OpeningDefenseGrid Grid { get; }
        public double Score { get; }
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
    bool TryInitialize(IReadOnlyList<OpeningDefenseGrid> commonPointAnchors, out string error);
    OpeningDefenseGridProbeResult ProbeNext();
    void Reset();
}

public enum OpeningDefensePreparationPhase
{
    QueryCatapults,
    ProbeAttributeGrid,
    QueryInteractionGuard,
    ConfirmAttributeGrid,
    WaitForPlacementSettlement,
    VerifyAttributePlacement,
    QueryVehicle,
    PreviewRailPath,
    QueryRailBaseline,
    DrawRailPath,
    VerifyRail,
    QueryPlacementTrain,
    PlaceVehicle,
    VerifyTrain,
    VerifyVehicle,
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
    private const string AttributeDisposableEnum = "FreePoint_Attribute";
    private const int MaximumPlacementVerificationAttempts = 12;
    private const int MaximumGridProbeSlices = 256;
    private const int MaximumRailVerificationAttempts = 12;
    private const int MaximumTrainObservationAttempts = 12;
    private const int MaximumFinalVerificationAttempts = 12;
    private const int MaximumPreWriteReadAttempts = 12;

    private readonly IOpeningDefenseGridProbe _probe;
    private readonly BattleDecisionEngine _railVerifier = new();
    private OpeningDefensePreparationPhase _phase = OpeningDefensePreparationPhase.QueryCatapults;
    private OpeningDefenseGrid? _selectedGrid;
    private JObject? _catapultResult;
    private AutomationAction? _drawAction;
    private JObject? _railBaselineResult;
    private JObject? _drawResult;
    private JObject? _verifiedRailResult;
    private AutomationAction? _vehiclePlacementAction;
    private JObject? _verifiedTrainResult;
    private JObject? _verifiedVehicleResult;
    private int _selectedVehicleInstanceId;
    private int _expectedRailInstanceId;
    private int _placementVerificationAttempts;
    private int _gridProbeSlices;
    private int _railVerificationAttempts;
    private int _trainObservationAttempts;
    private int _finalTrainVerificationAttempts;
    private int _finalVehicleVerificationAttempts;
    private int _finalRailVerificationAttempts;
    private int _catapultReadAttempts;
    private int _interactionGuardReadAttempts;
    private int _vehicleReadAttempts;
    private int _railPreviewReadAttempts;
    private int _railBaselineReadAttempts;
    private OpeningDefenseGrid? _submittedAttributeGrid;
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
    public bool HasCommittedWrite =>
        _submittedAttributeGrid.HasValue || _drawSubmitted || _vehiclePlacementSubmitted;
    public string VehiclePlacementCommand => _vehiclePlacementAction?.Command ?? string.Empty;
    public IReadOnlyList<int> SelectedLinePointInstanceIds =>
        (_drawAction?.Arguments["linePointInstanceIds"] as JArray)?.Values<int>().ToArray()
        ?? Array.Empty<int>();

    public void Reset()
    {
        _probe.Reset();
        _selectedGrid = null;
        _catapultResult = null;
        _drawAction = null;
        _railBaselineResult = null;
        _drawResult = null;
        _verifiedRailResult = null;
        _vehiclePlacementAction = null;
        _verifiedTrainResult = null;
        _verifiedVehicleResult = null;
        _selectedVehicleInstanceId = 0;
        _expectedRailInstanceId = 0;
        _placementVerificationAttempts = 0;
        _gridProbeSlices = 0;
        _railVerificationAttempts = 0;
        _trainObservationAttempts = 0;
        _finalTrainVerificationAttempts = 0;
        _finalVehicleVerificationAttempts = 0;
        _finalRailVerificationAttempts = 0;
        _catapultReadAttempts = 0;
        _interactionGuardReadAttempts = 0;
        _vehicleReadAttempts = 0;
        _railPreviewReadAttempts = 0;
        _railBaselineReadAttempts = 0;
        _submittedAttributeGrid = null;
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
            _phase = OpeningDefensePreparationPhase.VerifyTrain;
            return;
        }

        if (_drawSubmitted)
        {
            ResetCommittedVerificationAttempts();
            _failureDetail = string.Empty;
            _phase = OpeningDefensePreparationPhase.VerifyRail;
            return;
        }

        if (_submittedAttributeGrid.HasValue)
        {
            _selectedGrid = _submittedAttributeGrid;
            _placementVerificationAttempts = 0;
            _failureDetail = string.Empty;
            if (_phase != OpeningDefensePreparationPhase.WaitForPlacementSettlement)
            {
                _phase = OpeningDefensePreparationPhase.VerifyAttributePlacement;
            }

            return;
        }

        Reset();
    }

    private void ResetCommittedVerificationAttempts()
    {
        _railVerificationAttempts = 0;
        _trainObservationAttempts = 0;
        _finalTrainVerificationAttempts = 0;
        _finalVehicleVerificationAttempts = 0;
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
            _phase = OpeningDefensePreparationPhase.VerifyAttributePlacement;
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

            case OpeningDefensePreparationPhase.ProbeAttributeGrid:
                return DecideProbe();

            case OpeningDefensePreparationPhase.QueryInteractionGuard:
                return Command(
                    "queryOpeningDefenseInteractionGuard",
                    null,
                    "确认当前没有玩家或其他系统创建的活动道具交互。");

            case OpeningDefensePreparationPhase.ConfirmAttributeGrid:
                if (!_selectedGrid.HasValue)
                {
                    return Failure("增量探测选中的属性弹射点网格丢失。");
                }

                OpeningDefenseGrid selected = _selectedGrid.Value;
                return Command(
                    "confirmDisposableGrid",
                    JObject.FromObject(new
                    {
                        disposableEnum = AttributeDisposableEnum,
                        grid = new { x = selected.X, y = selected.Y }
                    }),
                    $"在网格 {selected} 放置开局属性弹射点。");

            case OpeningDefensePreparationPhase.WaitForPlacementSettlement:
                return Wait("属性弹射点已提交，正在逐帧等待预览和生成动画完全退出。");

            case OpeningDefensePreparationPhase.VerifyAttributePlacement:
                return Command("queryCatapults", null, "验证目标网格已生成可用属性弹射点。");

            case OpeningDefensePreparationPhase.QueryVehicle:
                return Command("queryVehicle", null, "读取背包战车并持久化首辆战车的实例身份。");

            case OpeningDefensePreparationPhase.PreviewRailPath:
                return CloneRailAction("previewRailPath", "只读预览开局三点闭环。");

            case OpeningDefensePreparationPhase.QueryRailBaseline:
                return Command("queryRail", null, "记录画轨前的轨道数量与实例身份基线。");

            case OpeningDefensePreparationPhase.DrawRailPath:
                if (_drawSubmitted)
                {
                    return Failure("开局轨道写入已经提交过；拒绝重复画轨。");
                }

                return CloneRailAction("drawRailPath", "按已验证的三个站点身份创建开局闭环。");

            case OpeningDefensePreparationPhase.VerifyRail:
                return Command("queryRail", null, "验证唯一新增轨道的身份、点集与合法性。");

            case OpeningDefensePreparationPhase.QueryPlacementTrain:
                return Command("queryTrain", null, "读取新闭环自动生成的固定车头并选择安全入列方式。");

            case OpeningDefensePreparationPhase.PlaceVehicle:
                if (_vehiclePlacementSubmitted)
                {
                    return Failure("开局战车入列命令已经提交过；拒绝盲目重复写入。");
                }

                return _vehiclePlacementAction == null
                    ? Failure("缺少经过身份验证的开局战车入列动作。")
                    : new OpeningDefensePreparationDecision(
                        _phase,
                        _vehiclePlacementAction,
                        _vehiclePlacementAction.Reason);

            case OpeningDefensePreparationPhase.VerifyTrain:
                return Command("queryTrain", null, "验证目标闭环只有一个未超载车列且选中战车已入列。");

            case OpeningDefensePreparationPhase.VerifyVehicle:
                return Command("queryVehicle", null, "验证同一实例战车已离开背包并位于目标闭环。");

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
                    "开局闭环、车列和战车身份已完成分帧复核。");

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
                if (_phase == OpeningDefensePreparationPhase.VerifyAttributePlacement)
                {
                    ObservePlacementVerification(result, accepted);
                }
                else
                {
                    ObserveCatapults(result, accepted);
                }
                break;

            case "queryOpeningDefenseInteractionGuard":
                ObserveInteractionGuard(result, accepted);
                break;

            case "confirmDisposableGrid":
                if (accepted)
                {
                    if (_selectedGrid.HasValue)
                    {
                        _submittedAttributeGrid = _selectedGrid;
                        _phase = OpeningDefensePreparationPhase.WaitForPlacementSettlement;
                    }
                    else
                    {
                        Fail("属性弹射点确认已被接受，但提交时的目标网格身份已经丢失；已停止且不会重复确认。");
                    }
                }
                else
                {
                    Fail("开局属性弹射点确认失败；不会继续画轨或调用旧版整图宏。");
                }
                break;

            case "queryVehicle":
                if (_phase == OpeningDefensePreparationPhase.QueryVehicle)
                {
                    ObserveInitialVehicle(result, accepted);
                }
                else if (_phase == OpeningDefensePreparationPhase.VerifyVehicle)
                {
                    ObserveFinalVehicle(result, accepted);
                }
                break;

            case "previewRailPath":
                ObserveRailPreview(result, accepted);
                break;

            case "queryRail":
                ObserveRailQuery(result, accepted);
                break;

            case "drawRailPath":
                ObserveRailDraw(result);
                break;

            case "queryTrain":
                if (_phase == OpeningDefensePreparationPhase.QueryPlacementTrain)
                {
                    ObservePlacementTrain(result, accepted);
                }
                else if (_phase == OpeningDefensePreparationPhase.VerifyTrain)
                {
                    ObserveFinalTrain(result, accepted);
                }
                break;

            case "moveVehicleInTrain":
            case "placeVehicleOnLine":
                _vehiclePlacementSubmitted = true;
                _phase = OpeningDefensePreparationPhase.VerifyTrain;
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
                return Failure($"增量候选网格已耗尽（检查 {probeResult.TotalProbed} 个），不会回退整图宏。");

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

        if (usable.Any(point => point["isAttribute"]?.Value<bool>() == true))
        {
            _phase = OpeningDefensePreparationPhase.QueryVehicle;
            return;
        }

        if (commonAnchors.Count < 2)
        {
            Fail($"可用普通弹射点不足（需要 2 个，当前 {commonAnchors.Count} 个）。");
            return;
        }

        try
        {
            if (!_probe.TryInitialize(commonAnchors, out string error))
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
        _phase = OpeningDefensePreparationPhase.ProbeAttributeGrid;
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
            _phase = OpeningDefensePreparationPhase.ConfirmAttributeGrid;
        }
    }

    private void ObservePlacementVerification(JObject? result, bool accepted)
    {
        if (accepted &&
            _selectedGrid.HasValue &&
            TryReadCatapults(result, out List<JObject> catapults) &&
            catapults.Any(point =>
                IsAvailablePoint(point) &&
                point["isAttribute"]?.Value<bool>() == true &&
                TryReadGrid(point, out OpeningDefenseGrid grid) &&
                grid.Equals(_selectedGrid.Value)))
        {
            _catapultResult = result?.DeepClone() as JObject;
            _placementVerificationAttempts = 0;
            _phase = OpeningDefensePreparationPhase.QueryVehicle;
            return;
        }

        _placementVerificationAttempts++;
        if (_placementVerificationAttempts >= MaximumPlacementVerificationAttempts)
        {
            Fail($"连续 {MaximumPlacementVerificationAttempts} 次未能验证目标网格的属性弹射点；为避免重复放置已停止。");
        }
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
            .OrderByDescending(item => ReadInt(item["level"], 0))
            .ThenBy(item => ReadInt(item["index"], int.MaxValue))
            .ThenBy(item => ReadInt(item["instanceId"], int.MaxValue))
            .FirstOrDefault();
        if (vehicle == null)
        {
            Fail("没有找到带稳定 instanceId 的可用背包战车。");
            return;
        }

        _selectedVehicleInstanceId = ReadInt(vehicle["instanceId"], 0);
        if (!TryBuildRailAction(_catapultResult, _selectedGrid, out AutomationAction? action, out string error))
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

    private void ObserveRailDraw(JObject? result)
    {
        _drawSubmitted = true;
        _drawResult = result?.DeepClone() as JObject ?? new JObject();
        _expectedRailInstanceId = _railVerifier.ReadDrawnRailInstanceId(result);
        _railVerificationAttempts = 0;
        _phase = OpeningDefensePreparationPhase.VerifyRail;
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
            _expectedRailInstanceId = verification.RailInstanceId;
            _verifiedRailResult = new JObject { ["rail"] = verification.Rail.DeepClone() };
            if (finalCheck)
            {
                _phase = OpeningDefensePreparationPhase.Completed;
            }
            else
            {
                _trainObservationAttempts = 0;
                _phase = OpeningDefensePreparationPhase.QueryPlacementTrain;
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

    private void ObservePlacementTrain(JObject? result, bool accepted)
    {
        if (!accepted)
        {
            RetryPlacementTrain("读取新闭环车列连续失败；不会尝试盲目放车。");
            return;
        }

        JObject? rail = _verifiedRailResult?["rail"] as JObject;
        if (rail == null)
        {
            Fail("已验证轨道身份丢失；不会尝试放车。");
            return;
        }

        JObject? train = FindTrainForRail(result, rail);
        if (train != null)
        {
            JObject? relative = (train["vehicles"] as JArray)?.OfType<JObject>()
                .Where(item => ReadInt(item["instanceId"], 0) != 0)
                .LastOrDefault();
            int relativeInstanceId = ReadInt(relative?["instanceId"], 0);
            if (relativeInstanceId == 0)
            {
                RetryPlacementTrain("新闭环已有 driver，但车列没有返回可验证的相邻战车身份。");
                return;
            }

            _vehiclePlacementAction = new AutomationAction(
                "moveVehicleInTrain",
                JObject.FromObject(new
                {
                    instanceId = _selectedVehicleInstanceId,
                    relative = new { instanceId = relativeInstanceId }
                }),
                AutomationStage.PreparingDefense,
                "新闭环已自动创建固定车头；把已锁定身份的背包战车编入该车列。");
            _phase = OpeningDefensePreparationPhase.PlaceVehicle;
            return;
        }

        JObject? emptyLine = (rail["lines"] as JArray)?.OfType<JObject>()
            .FirstOrDefault(line =>
                line["hasDriver"]?.Value<bool>() != true &&
                ReadInt(line["driverCount"], 0) == 0 &&
                ReadInt(line["lineInstanceId"], ReadInt(line["instanceId"], 0)) != 0);
        bool anyDriver = (rail["lines"] as JArray)?.OfType<JObject>()
            .Any(line => line["hasDriver"]?.Value<bool>() == true || ReadInt(line["driverCount"], 0) > 0) == true;
        if (emptyLine != null && !anyDriver)
        {
            int lineInstanceId = ReadInt(
                emptyLine["lineInstanceId"],
                ReadInt(emptyLine["instanceId"], 0));
            _vehiclePlacementAction = new AutomationAction(
                "placeVehicleOnLine",
                JObject.FromObject(new
                {
                    instanceId = _selectedVehicleInstanceId,
                    lineInstanceId,
                    forward = true
                }),
                AutomationStage.PreparingDefense,
                "新闭环已确认没有 driver；把已锁定身份的背包战车放到空线段。");
            _phase = OpeningDefensePreparationPhase.PlaceVehicle;
            return;
        }

        RetryPlacementTrain("轨道显示已有 driver，但 queryTrain 尚未返回对应固定车头。");
    }

    private void RetryPlacementTrain(string detail)
    {
        _trainObservationAttempts++;
        if (_trainObservationAttempts >= MaximumTrainObservationAttempts)
        {
            Fail(detail + " 已达到观察上限；不会错误调用 placeVehicleOnLine。");
        }
    }

    private void ObserveFinalTrain(JObject? result, bool accepted)
    {
        if (accepted && IsFinalTrainVerified(result))
        {
            _verifiedTrainResult = result?.DeepClone() as JObject;
            _phase = OpeningDefensePreparationPhase.VerifyVehicle;
            return;
        }

        _finalTrainVerificationAttempts++;
        if (_finalTrainVerificationAttempts >= MaximumFinalVerificationAttempts)
        {
            Fail("战车入列命令已提交，但未能在安全时限内验证唯一且未超载的目标车列；不会重发写命令。");
        }
    }

    private void ObserveFinalVehicle(JObject? result, bool accepted)
    {
        if (accepted && IsFinalVehicleVerified(result))
        {
            _verifiedVehicleResult = result?.DeepClone() as JObject;
            _phase = OpeningDefensePreparationPhase.VerifyRailFinal;
            return;
        }

        _finalVehicleVerificationAttempts++;
        if (_finalVehicleVerificationAttempts >= MaximumFinalVerificationAttempts)
        {
            Fail("未能验证锁定实例的战车已离开背包并进入目标闭环；不会重发入列命令。");
        }
    }

    private bool IsFinalTrainVerified(JObject? result)
    {
        JArray? trains = State(result)["trains"] as JArray;
        if (trains == null || trains.OfType<JObject>().Count() != 1)
        {
            return false;
        }

        JObject train = trains.OfType<JObject>().Single();
        JObject? rail = _verifiedRailResult?["rail"] as JObject;
        if (rail == null || !TrainMatchesRail(train, rail) || train["isOverCapacity"]?.Value<bool>() == true)
        {
            return false;
        }

        return (train["vehicles"] as JArray)?.OfType<JObject>().Any(vehicle =>
            ReadInt(vehicle["instanceId"], 0) == _selectedVehicleInstanceId &&
            vehicle["isFixedHead"]?.Value<bool>() != true &&
            vehicle["inBag"]?.Value<bool>() != true) == true;
    }

    private bool IsFinalVehicleVerified(JObject? result)
    {
        JObject? vehicle = (State(result)["vehicles"] as JArray)?.OfType<JObject>()
            .SingleOrDefault(item => ReadInt(item["instanceId"], 0) == _selectedVehicleInstanceId);
        if (vehicle == null ||
            vehicle["inBag"]?.Value<bool>() == true ||
            vehicle["active"]?.Value<bool>() != true ||
            vehicle["isFixedHead"]?.Value<bool>() == true)
        {
            return false;
        }

        int expectedRailInternalId = ReadRailInternalId(_verifiedRailResult?["rail"] as JObject);
        int actualRailInternalId = ReadInt(vehicle["railId"], 0);
        return expectedRailInternalId == 0 || actualRailInternalId == expectedRailInternalId;
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
            error = "弹射点快照丢失，无法创建稳定的三点计划。";
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
            error = "没有 1 个匹配的属性站和 2 个可用普通站组成开局闭环。";
            return false;
        }

        RailCandidate? best = null;
        foreach (JObject attribute in attributes)
        {
            if (!TryReadGrid(attribute, out OpeningDefenseGrid attributeGrid)) continue;
            int attributeId = ReadInt(attribute["linePointInstanceId"], 0);
            if (attributeId == 0) continue;

            for (int first = 0; first < commons.Count - 1; first++)
            {
                if (!TryReadGrid(commons[first], out OpeningDefenseGrid firstGrid)) continue;
                int firstId = ReadInt(commons[first]["linePointInstanceId"], 0);
                if (firstId == 0 || firstId == attributeId) continue;

                for (int second = first + 1; second < commons.Count; second++)
                {
                    if (!TryReadGrid(commons[second], out OpeningDefenseGrid secondGrid)) continue;
                    int secondId = ReadInt(commons[second]["linePointInstanceId"], 0);
                    if (secondId == 0 || secondId == attributeId || secondId == firstId) continue;

                    double area = Math.Abs(
                        ((double)firstGrid.X - attributeGrid.X) * (secondGrid.Y - attributeGrid.Y) -
                        ((double)firstGrid.Y - attributeGrid.Y) * (secondGrid.X - attributeGrid.X));
                    if (area <= 0.000001d) continue;

                    double distance = DistanceSquared(attributeGrid, firstGrid) +
                                      DistanceSquared(attributeGrid, secondGrid) +
                                      DistanceSquared(firstGrid, secondGrid);
                    RailCandidate candidate = new(attributeId, firstId, secondId, distance, area);
                    if (best == null || candidate.CompareTo(best) < 0)
                    {
                        best = candidate;
                        _selectedGrid = attributeGrid;
                    }
                }
            }
        }

        if (best == null)
        {
            error = "可用站点无法组成非共线的三点闭环。";
            return false;
        }

        if (_selectedVehicleInstanceId == 0)
        {
            error = "缺少已锁定的背包战车实例，无法预测新闭环回转周期。";
            return false;
        }

        action = new AutomationAction(
            "drawRailPath",
            new JObject
            {
                ["linePointInstanceIds"] = new JArray(
                    best.AttributeInstanceId,
                    best.FirstInstanceId,
                    best.SecondInstanceId),
                ["vehicle"] = new JObject { ["instanceId"] = _selectedVehicleInstanceId },
                ["vehicleInstanceId"] = _selectedVehicleInstanceId
            },
            AutomationStage.PreparingDefense,
            "使用已持久化的三个站点实例身份创建最短合法开局闭环。");
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

    private static JObject? FindTrainForRail(JObject? result, JObject rail) =>
        (State(result)["trains"] as JArray)?.OfType<JObject>()
            .FirstOrDefault(train => TrainMatchesRail(train, rail));

    private static bool TrainMatchesRail(JObject train, JObject rail)
    {
        int expected = ReadRailInternalId(rail);
        int actual = ReadInt(train["railId"], 0);
        return expected != 0 && actual == expected;
    }

    private static int ReadRailInternalId(JObject? rail) =>
        ReadInt(rail?["railInternalId"], ReadInt(rail?["id"], 0));

    private static bool TryReadCatapults(JObject? result, out List<JObject> catapults)
    {
        JArray? items = State(result)["catapults"] as JArray;
        catapults = items?.OfType<JObject>().ToList() ?? new List<JObject>();
        return items != null;
    }

    private static bool IsAvailablePoint(JObject point) =>
        point["active"]?.Value<bool>() != false &&
        point["canUseForNewRail"]?.Value<bool>() == true &&
        point["canPickLine"]?.Value<bool>() != false &&
        point["frozen"]?.Value<bool>() != true &&
        point["railReachMax"]?.Value<bool>() != true &&
        ReadInt(point["railMembershipCount"], 0) == 0 &&
        ReadInt(point["linePointInstanceId"], 0) != 0;

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
               state["requiresSpeedSource"]?.Type == JTokenType.Boolean &&
               state["predictedLoopCycleSeconds"]?.Type is JTokenType.Integer or JTokenType.Float &&
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

    private sealed class RailCandidate
    {
        public RailCandidate(
            int attributeInstanceId,
            int firstInstanceId,
            int secondInstanceId,
            double distance,
            double area)
        {
            AttributeInstanceId = attributeInstanceId;
            FirstInstanceId = firstInstanceId;
            SecondInstanceId = secondInstanceId;
            Distance = distance;
            Area = area;
        }

        public int AttributeInstanceId { get; }
        public int FirstInstanceId { get; }
        public int SecondInstanceId { get; }
        public double Distance { get; }
        public double Area { get; }

        public int CompareTo(RailCandidate other)
        {
            int byDistance = Distance.CompareTo(other.Distance);
            if (byDistance != 0) return byDistance;
            int byArea = other.Area.CompareTo(Area);
            if (byArea != 0) return byArea;
            int byAttribute = AttributeInstanceId.CompareTo(other.AttributeInstanceId);
            if (byAttribute != 0) return byAttribute;
            int byFirst = FirstInstanceId.CompareTo(other.FirstInstanceId);
            return byFirst != 0 ? byFirst : SecondInstanceId.CompareTo(other.SecondInstanceId);
        }
    }
}
