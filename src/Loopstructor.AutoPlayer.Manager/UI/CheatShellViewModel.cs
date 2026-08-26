using System.Windows.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using Loopstructor.AutoPlayer.Core;
using Loopstructor.AutoPlayer.Manager.Models;

namespace Loopstructor.AutoPlayer.Manager.UI;

internal sealed partial class CheatShellViewModel : ObservableObject
{
    private static readonly SolidColorBrush RedBrush = FrozenBrush(0xD7, 0x4B, 0x31);
    private static readonly SolidColorBrush BlueBrush = FrozenBrush(0x32, 0x8C, 0xC5);
    private static readonly SolidColorBrush AmberBrush = FrozenBrush(0xF0, 0xB2, 0x3D);
    private static readonly SolidColorBrush GreenBrush = FrozenBrush(0x78, 0xD1, 0x3E);
    private static readonly SolidColorBrush RedPanelBrush = FrozenBrush(0x30, 0x18, 0x14);
    private static readonly SolidColorBrush BluePanelBrush = FrozenBrush(0x18, 0x2B, 0x36);
    private static readonly SolidColorBrush AmberPanelBrush = FrozenBrush(0x2D, 0x23, 0x13);
    private static readonly SolidColorBrush GreenPanelBrush = FrozenBrush(0x18, 0x29, 0x17);

    [ObservableProperty]
    private string _runStateLabel = "未连接";

    [ObservableProperty]
    private string _runStateDetail = "请先启动已安装插件的游戏，并等待 Manager 完成安全握手。";

    [ObservableProperty]
    private Brush _runStateAccent = RedBrush;

    [ObservableProperty]
    private Brush _runStatePanel = RedPanelBrush;

    internal void UpdateRunState(
        bool writeOutcomeUnknown,
        bool trusted,
        bool available,
        bool busy,
        bool runConflict,
        AutoPlayerStatus? status,
        BridgeHello? hello)
    {
        if (writeOutcomeUnknown)
        {
            SetState("上次操作结果待确认", "上一条修改尚未确认结果；为避免重复修改，暂时不能继续写入。", RedBrush, RedPanelBrush);
            return;
        }

        if (!trusted)
        {
            SetState("等待游戏连接", "请先启动已安装插件的游戏，并等待 Manager 完成连接。", RedBrush, RedPanelBrush);
            return;
        }

        if (!available)
        {
            string reason = status?.CheatAvailabilityReason ?? hello?.CheatAvailabilityReason ?? string.Empty;
            SetState("当前游戏版本不可用", string.IsNullOrWhiteSpace(reason) ? "当前游戏版本不支持这组作弊功能。" : reason, RedBrush, RedPanelBrush);
            return;
        }

        if (busy)
        {
            SetState("正在执行", "正在等待 AutoPlayer 插件完成当前作弊命令。", BlueBrush, BluePanelBrush);
            return;
        }

        if (runConflict && status?.CheatModeEnabled == true)
        {
            string detail = "自动游玩期间可查询目录、战车和敌人，并切换敌人 ID 与 Buff 显示；其他作弊写操作已锁定。";
            if (status.CheatUsed) detail += " 当前配置已有作弊记录。";
            SetState("自动游玩中 · 仅可查看", detail, AmberBrush, AmberPanelBrush);
            return;
        }

        if (runConflict)
        {
            string detail = "可以启用作弊监视；启用后仍只开放查询以及敌人 ID、Buff 显示。";
            if (status?.CheatUsed == true) detail += " 当前配置已有作弊记录。";
            SetState("自动游玩中 · 仅可查看", detail, GreenBrush, GreenPanelBrush);
            return;
        }

        if (status?.CheatModeEnabled == true && status.CheatUsed)
        {
            SetState("作弊已开启 · 本局用过作弊", "作弊模式已启用，本局已执行过修改。", AmberBrush, AmberPanelBrush);
        }
        else if (status?.CheatModeEnabled == true)
        {
            SetState("作弊已开启", "可以执行资源、战斗、属性和怪物生成命令。", GreenBrush, GreenPanelBrush);
        }
        else if (status?.CheatUsed == true)
        {
            SetState("作弊已关闭 · 本局用过作弊", "作弊模式已关闭，但本局已经执行过修改。", AmberBrush, AmberPanelBrush);
        }
        else
        {
            SetState("作弊已关闭", "开启作弊模式后可以执行修改命令。", GreenBrush, GreenPanelBrush);
        }
    }

    private void SetState(string label, string detail, Brush accent, Brush panel)
    {
        RunStateLabel = label;
        RunStateDetail = detail;
        RunStateAccent = accent;
        RunStatePanel = panel;
    }

    private static SolidColorBrush FrozenBrush(byte red, byte green, byte blue)
    {
        var brush = new SolidColorBrush(Color.FromRgb(red, green, blue));
        brush.Freeze();
        return brush;
    }
}
