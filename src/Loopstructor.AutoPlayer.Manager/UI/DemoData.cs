using Loopstructor.AutoPlayer.Core;
using Loopstructor.AutoPlayer.Manager.Models;
using Loopstructor.AutoPlayer.Manager.Services;

namespace Loopstructor.AutoPlayer.Manager.UI;

internal static class DemoData
{
    public static GameInstallValidation Game()
    {
        return new GameInstallValidation
        {
            GameRoot = @"D:\QA Builds\Skyspine-1.237",
            ExecutablePath = @"D:\QA Builds\Skyspine-1.237\Loopstructor 2_ Skyspine.exe",
            DataDirectory = @"D:\QA Builds\Skyspine-1.237\Loopstructor 2_ Skyspine_Data",
            AssemblyPath = @"D:\QA Builds\Skyspine-1.237\Loopstructor 2_ Skyspine_Data\Managed\Assembly-CSharp.dll",
            AssemblySha256 = "a7ef5217bd9321585fd56e89d7b46d2ca45e32d9af886c065aff9dd2f52ace19",
            AssemblyMvid = "327a40b5-0fd2-455a-8261-9da078e85a36",
            ProductName = "Loopstructor 2: Skyspine",
            ProductVersion = "1.237",
            SteamAppId = GameInstallValidator.ExpectedSteamAppId
        };
    }

    public static BridgeHello Hello() => new()
    {
        ProtocolVersion = Protocol.CurrentVersion,
        GameProcessId = 18420,
        PluginVersion = "0.2.0",
        GameVersion = "1.237",
        UnityVersion = "2022.3.62f3c1",
        BuildGuid = "649c0d22d9f344e3909fe5f620040de4",
        AssemblySha256 = Game().AssemblySha256,
        AssemblyMvid = Game().AssemblyMvid,
        ProductIdentityValid = true,
        FingerprintAccepted = true,
        RuntimeContractAvailable = true,
        SaveIsolationApplied = true,
        SaveIsolationVerified = true,
        PlatformWritesBlocked = true,
        GameArtifactsRedirected = true,
        ProfileRoot = @"%LOCALAPPDATA%\LoopstructorAutoPlayer\profiles\b13f9f3421ae61aa\qa-default",
        ArtifactRoot = @"%LOCALAPPDATA%\LoopstructorAutoPlayer\artifacts\b13f9f3421ae61aa\20260729-142810-8fc17a2d",
        Commands = new[] { "hello", "status", "start", "pause", "resume", "stop" }
    };

    public static BridgeHello CheatHello()
    {
        BridgeHello hello = Hello();
        hello.CheatProtocolVersion = Protocol.CheatCurrentVersion;
        hello.CheatSessionAuthorized = true;
        hello.CheatAvailable = true;
        hello.CheatModeEnabled = true;
        hello.CheatAvailabilityReason = string.Empty;
        hello.CheatCapabilities = CheatCommands.All;
        hello.ProfileRoot = @"%LOCALAPPDATA%\LoopstructorAutoPlayer\profiles\b13f9f3421ae61aa\cheat\20260729-142810-8fc17a2d";
        return hello;
    }

    public static AutoPlayerStatus Status(bool needsProcessRestart = false)
    {
        DateTime now = DateTime.UtcNow;
        BridgeHello hello = Hello();
        return new AutoPlayerStatus
        {
            ProtocolVersion = Protocol.CurrentVersion,
            PluginVersion = hello.PluginVersion,
            RunState = needsProcessRestart ? AutoPlayerRunState.Completed : AutoPlayerRunState.Running,
            Stage = needsProcessRestart ? AutomationStage.Completed : AutomationStage.Battle,
            StageDetail = needsProcessRestart
                ? "本轮测试已结束；开始下一轮前必须彻底重启游戏进程"
                : "第 7 波进行中，剩余敌人 18",
            Scene = "Level_Common_03",
            ProductName = "Loopstructor 2: Skyspine",
            CompanyName = "PoneGames",
            GameVersion = hello.GameVersion,
            UnityVersion = hello.UnityVersion,
            BuildGuid = hello.BuildGuid,
            SteamBuildId = "19482136",
            AssemblySha256 = hello.AssemblySha256,
            AssemblyMvid = hello.AssemblyMvid,
            ProductIdentityValid = true,
            FingerprintAccepted = true,
            RuntimeContractAvailable = true,
            SaveIsolationApplied = true,
            SaveIsolationVerified = true,
            PlatformWritesBlocked = true,
            GameArtifactsRedirected = true,
            IsolatedSaveRoot = hello.ProfileRoot,
            ArtifactDirectory = hello.ArtifactRoot,
            NeedsProcessRestart = needsProcessRestart,
            WavesStarted = 7,
            WavesCompleted = needsProcessRestart ? 7 : 6,
            StartedAtUtc = now.AddMinutes(-18),
            LastActionAtUtc = now.AddSeconds(-3),
            LastCommand = needsProcessRestart ? "stop" : "queryAffordances",
            LastMessage = needsProcessRestart ? "测试结束，等待彻底重启游戏进程" : "战斗状态稳定",
            EvidenceDirectory = hello.ArtifactRoot,
            Timeline = new[]
            {
                Event(now.AddMinutes(-18), AutomationStage.FrontEnd, "action", "创建普通模式测试对局"),
                Event(now.AddMinutes(-17), AutomationStage.PreparingDefense, "action", "默认防线布置完成"),
                Event(now.AddMinutes(-14), AutomationStage.ManagingRewards, "action", "选择车辆奖励：重型平台"),
                Event(now.AddMinutes(-11), AutomationStage.SelectingRoute, "action", "进入资源节点 2"),
                Event(now.AddMinutes(-7), AutomationStage.ManagingEvent, "info", "事件选项验证通过"),
                Event(now.AddMinutes(-3), AutomationStage.StartingWave, "action", "启动第 7 波"),
                Event(now.AddSeconds(-3), AutomationStage.Battle, "info", "第 7 波进行中，剩余敌人 18")
            }
        };
    }

    public static AutoPlayerStatus CheatStatus()
    {
        AutoPlayerStatus status = Status();
        BridgeHello hello = CheatHello();
        status.RunState = AutoPlayerRunState.Standby;
        status.Stage = AutomationStage.FrontEnd;
        status.StageDetail = "作弊调试会话已授权，等待手动操作";
        status.IsolatedSaveRoot = hello.ProfileRoot;
        status.CheatSessionAuthorized = true;
        status.CheatAvailable = true;
        status.CheatModeEnabled = true;
        status.RunIntegrity = "cheat-session";
        status.LastCommand = CheatCommands.QueryCatalog;
        status.LastMessage = "作弊资源目录已就绪";
        return status;
    }

    public static IReadOnlyList<string> LogLines() => new[]
    {
            "14:28:10.044  信息  已验证 Skyspine 构建 1.237",
            "14:28:10.106  信息  插件握手成功，协议 v1",
            "14:28:10.108  安全  存档已隔离，平台写入已阻断",
            "14:31:42.810  操作  选择路线，就绪序号=2",
            "14:36:19.214  操作  收集奖励对象，序号=0",
            "14:43:01.705  操作  启动第 7 波",
            "14:46:07.391  信息  战斗中 / 剩余敌人=18"
    };

    private static TimelineEvent Event(DateTime time, AutomationStage stage, string kind, string message) => new()
    {
        TimestampUtc = time,
        Stage = stage,
        Kind = kind,
        Message = message
    };
}
