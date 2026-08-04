using System;
using System.Collections.Generic;

namespace Loopstructor.AutoPlayer.Core;

/// <summary>
/// 与 Unity 类型无关的地图节点坐标，用于在执行反射调用前验证跳转路径。
/// </summary>
public readonly struct MapJumpCoordinate : IEquatable<MapJumpCoordinate>
{
    public MapJumpCoordinate(int x, int y)
    {
        X = x;
        Y = y;
    }

    public int X { get; }
    public int Y { get; }

    public bool Equals(MapJumpCoordinate other) => X == other.X && Y == other.Y;

    public override bool Equals(object? obj) => obj is MapJumpCoordinate other && Equals(other);

    public override int GetHashCode()
    {
        unchecked
        {
            return (X * 397) ^ Y;
        }
    }

    public override string ToString() => $"({X}, {Y})";

    public static bool operator ==(MapJumpCoordinate left, MapJumpCoordinate right) => left.Equals(right);

    public static bool operator !=(MapJumpCoordinate left, MapJumpCoordinate right) => !left.Equals(right);
}

/// <summary>
/// 地图跳转规划所需的最小节点快照。
/// </summary>
public sealed class MapJumpNode
{
    public MapJumpNode(MapJumpCoordinate coordinate, IReadOnlyList<MapJumpCoordinate>? nextCoordinates = null)
    {
        Coordinate = coordinate;
        NextCoordinates = nextCoordinates ?? Array.Empty<MapJumpCoordinate>();
    }

    public MapJumpCoordinate Coordinate { get; }
    public IReadOnlyList<MapJumpCoordinate> NextCoordinates { get; }
}

/// <summary>
/// 地图跳转规划失败原因。调用方遇到任何失败都应拒绝修改游戏状态。
/// </summary>
public enum MapJumpPlanFailure
{
    None,
    InvalidStageStep,
    MapUnavailable,
    InvalidTargetCoordinate,
    TargetLayerOutOfRange,
    TargetNodeNotFound,
    PreviousLayerUnavailable,
    ConnectedPredecessorNotFound
}

/// <summary>
/// 通过游戏原生 LoadPath 进入目标节点前所需的阶段和前置路径。
/// </summary>
public sealed class MapJumpPlan
{
    internal MapJumpPlan(int targetStage, IReadOnlyList<MapJumpCoordinate> predecessorPath)
    {
        TargetStage = targetStage;
        PredecessorPath = predecessorPath;
    }

    public int TargetStage { get; }
    public IReadOnlyList<MapJumpCoordinate> PredecessorPath { get; }
}

/// <summary>
/// 按游戏 JumpWaveOrder 使用的规则，为任意已生成节点构造最小前置路径。
/// </summary>
public static class MapJumpPlanner
{
    public static bool TryCreatePlan(
        IReadOnlyList<IReadOnlyList<MapJumpNode>>? layers,
        MapJumpCoordinate target,
        int stageStep,
        out MapJumpPlan? plan,
        out MapJumpPlanFailure failure)
    {
        plan = null;

        if (stageStep <= 0)
        {
            failure = MapJumpPlanFailure.InvalidStageStep;
            return false;
        }

        if (layers == null || layers.Count == 0)
        {
            failure = MapJumpPlanFailure.MapUnavailable;
            return false;
        }

        if (target.X < 0 || target.Y < 0)
        {
            failure = MapJumpPlanFailure.InvalidTargetCoordinate;
            return false;
        }

        if (target.Y >= layers.Count)
        {
            failure = MapJumpPlanFailure.TargetLayerOutOfRange;
            return false;
        }

        IReadOnlyList<MapJumpNode>? targetLayer = layers[target.Y];
        if (targetLayer == null || !ContainsNode(targetLayer, target))
        {
            failure = MapJumpPlanFailure.TargetNodeNotFound;
            return false;
        }

        int targetStage = target.Y / stageStep;
        if (target.Y == 0)
        {
            plan = new MapJumpPlan(targetStage, Array.Empty<MapJumpCoordinate>());
            failure = MapJumpPlanFailure.None;
            return true;
        }

        IReadOnlyList<MapJumpNode>? previousLayer = layers[target.Y - 1];
        if (previousLayer == null || previousLayer.Count == 0)
        {
            failure = MapJumpPlanFailure.PreviousLayerUnavailable;
            return false;
        }

        MapJumpCoordinate predecessor;
        if (target.Y % stageStep == 0)
        {
            if (!TryGetFirstValidNode(previousLayer, out predecessor))
            {
                failure = MapJumpPlanFailure.PreviousLayerUnavailable;
                return false;
            }
        }
        else if (!TryFindConnectedPredecessor(previousLayer, target, out predecessor))
        {
            failure = MapJumpPlanFailure.ConnectedPredecessorNotFound;
            return false;
        }

        plan = new MapJumpPlan(targetStage, new[] { predecessor });
        failure = MapJumpPlanFailure.None;
        return true;
    }

    private static bool ContainsNode(IReadOnlyList<MapJumpNode> nodes, MapJumpCoordinate coordinate)
    {
        for (int index = 0; index < nodes.Count; index++)
        {
            MapJumpNode? node = nodes[index];
            if (node != null && node.Coordinate == coordinate)
            {
                return true;
            }
        }

        return false;
    }

    private static bool TryFindConnectedPredecessor(
        IReadOnlyList<MapJumpNode> previousLayer,
        MapJumpCoordinate target,
        out MapJumpCoordinate predecessor)
    {
        for (int nodeIndex = 0; nodeIndex < previousLayer.Count; nodeIndex++)
        {
            MapJumpNode? node = previousLayer[nodeIndex];
            if (node == null) continue;

            for (int nextIndex = 0; nextIndex < node.NextCoordinates.Count; nextIndex++)
            {
                if (node.NextCoordinates[nextIndex] == target)
                {
                    predecessor = node.Coordinate;
                    return true;
                }
            }
        }

        predecessor = default;
        return false;
    }

    private static bool TryGetFirstValidNode(
        IReadOnlyList<MapJumpNode> nodes,
        out MapJumpCoordinate coordinate)
    {
        for (int index = 0; index < nodes.Count; index++)
        {
            MapJumpNode? node = nodes[index];
            if (node != null && node.Coordinate.X >= 0 && node.Coordinate.Y >= 0)
            {
                coordinate = node.Coordinate;
                return true;
            }
        }

        coordinate = default;
        return false;
    }
}
