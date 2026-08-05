namespace Loopstructor.AutoPlayer.Core;

/// <summary>
/// 地图自由跳转的显示边界。游戏已经经过的层继续沿用原生隐藏规则，
/// 只有当前进度之后的层可以临时开放给玩家选择。
/// </summary>
public static class MapJumpVisibilityPolicy
{
    /// <summary>
    /// 将路径中的最后一个节点换算成游戏用于隐藏地图层的进度层。
    /// 尚未经过任何节点时，游戏使用 -1 表示第一层之前。
    /// </summary>
    public static int ResolveCurrentLayer(MapJumpCoordinate? latestVisitedNode) =>
        latestVisitedNode?.Y ?? -1;

    /// <summary>
    /// 判断指定地图层是否位于当前进度之后，可以由自由跳转临时显示。
    /// </summary>
    public static bool ShouldExposeForFreeJump(int candidateLayer, int currentLayer) =>
        candidateLayer >= 0 && candidateLayer > currentLayer;
}
