using Loopstructor.AutoPlayer.Core;
using Loopstructor.AutoPlayer.Manager.Models;
using Loopstructor.AutoPlayer.Manager.Services;
using Newtonsoft.Json.Linq;

namespace Loopstructor.AutoPlayer.Manager.UI;

internal static class DemoData
{
    [ThreadStatic] private static bool _enemyIdsVisible;
    [ThreadStatic] private static bool _enemyBuffsVisible;

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
        ProcessInstanceId = "cb866de72f7b45d4a2e35564bc19e515",
        PluginVersion = "0.5.9",
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
        hello.ProfileRoot = @"%LOCALAPPDATA%\LoopstructorAutoPlayer\profiles\b13f9f3421ae61aa\qa-default";
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
        _enemyIdsVisible = false;
        _enemyBuffsVisible = false;
        return BuildCheatStatus();
    }

    private static AutoPlayerStatus BuildCheatStatus()
    {
        AutoPlayerStatus status = Status();
        BridgeHello hello = CheatHello();
        status.RunState = AutoPlayerRunState.Standby;
        status.Stage = AutomationStage.FrontEnd;
        status.StageDetail = "作弊工具已开启，等待手动操作";
        status.IsolatedSaveRoot = hello.ProfileRoot;
        status.CheatSessionAuthorized = true;
        status.CheatAvailable = true;
        status.CheatModeEnabled = true;
        status.EnemyIdsVisible = _enemyIdsVisible;
        status.EnemyBuffsVisible = _enemyBuffsVisible;
        status.RunIntegrity = "clean";
        status.LastCommand = CheatCommands.QueryCatalog;
        status.LastMessage = "作弊资源目录已就绪";
        return status;
    }

    public static ControlResponse CheatResponse(string command, JObject? arguments)
    {
        AutoPlayerStatus status = BuildCheatStatus();
        JObject state = new()
        {
            ["enabled"] = true,
            ["baseGodMode"] = false,
            ["enemyIdsVisible"] = _enemyIdsVisible,
            ["enemyBuffsVisible"] = _enemyBuffsVisible,
            ["mapSkipEnabled"] = false,
            ["ownedRelics"] = new JArray(
                new JObject
                {
                    ["id"] = "Scope",
                    ["relicId"] = "Scope",
                    ["enumName"] = "Scope",
                    ["name"] = "瞄准镜",
                    ["count"] = 1
                }),
            ["ownedCatapultPoints"] = new JArray(
                new JObject
                {
                    ["id"] = "FreePoint",
                    ["catapultPointId"] = "point-normal-poison",
                    ["disposableId"] = "FreePoint",
                    ["enumName"] = "FreePoint",
                    ["name"] = "自由弹射点",
                    ["count"] = 2,
                    ["buffs"] = new JArray("Poison")
                },
                new JObject
                {
                    ["id"] = "FreePoint",
                    ["catapultPointId"] = "point-normal-energy",
                    ["disposableId"] = "FreePoint",
                    ["enumName"] = "FreePoint",
                    ["name"] = "自由弹射点",
                    ["count"] = 1,
                    ["buffs"] = new JArray("Energy")
                }),
            ["spawnPointCapture"] = new JObject
            {
                ["state"] = "idle",
                ["message"] = "未启动"
            }
        };

        if (string.Equals(command, CheatCommands.QueryCatalog, StringComparison.OrdinalIgnoreCase))
        {
            return Success("演示资源目录已加载。", CheatCatalog(), status);
        }

        if (string.Equals(command, CheatCommands.QueryVehicles, StringComparison.OrdinalIgnoreCase))
        {
            return Success("演示战车列表已加载。", DemoVehicles(), status);
        }

        if (string.Equals(command, CheatCommands.QueryEnemies, StringComparison.OrdinalIgnoreCase))
        {
            return Success("演示敌人列表已加载。", DemoEnemies(), status);
        }

        if (string.Equals(command, CheatCommands.SetEnabled, StringComparison.OrdinalIgnoreCase))
        {
            bool enabled = arguments?.Value<bool?>("enabled") ?? true;
            status.CheatModeEnabled = enabled;
            state["enabled"] = enabled;
        }

        if (string.Equals(command, CheatCommands.SetEnemyIdOverlay, StringComparison.OrdinalIgnoreCase))
        {
            bool visible = arguments?.Value<bool?>("visible") == true;
            _enemyIdsVisible = visible;
            status.EnemyIdsVisible = visible;
            state["visible"] = visible;
            state["enemyIdsVisible"] = visible;
        }

        if (string.Equals(command, CheatCommands.SetEnemyBuffOverlay, StringComparison.OrdinalIgnoreCase))
        {
            bool visible = arguments?.Value<bool?>("visible") == true;
            _enemyBuffsVisible = visible;
            status.EnemyBuffsVisible = visible;
            state["visible"] = visible;
            state["enemyBuffsVisible"] = visible;
        }

        return Success(
            string.Equals(command, CheatCommands.QueryState, StringComparison.OrdinalIgnoreCase)
                ? "演示作弊状态已读取。"
                : "演示命令已模拟；未连接或修改游戏。",
            state,
            status);
    }

    private static JObject CheatCatalog() => new()
    {
        ["catalogVersion"] = 2,
        ["locale"] = "zh",
        ["vehicles"] = new JArray(
            CatalogItem("Link_ElectricFork_L1", "雷叉", "战车", 1),
            CatalogItem("Link_ElectricFork_L2", "雷叉", "战车", 2),
            CatalogItem("Link_ElectricFork_L3", "雷叉", "战车", 3),
            CatalogItem("Shell_DoubleShell_L4", "双发重炮", "战车", 4),
            CatalogItem("Penetrate_WindPiercer_L2", "风矢", "战车", 2)),
        ["enchantments"] = new JArray(
            CatalogItem("Poison", "中毒", "附魔"),
            CatalogItem("Energy", "能量", "附魔"),
            CatalogItem("Slow", "减速", "附魔"),
            CatalogItem("Tornado", "龙卷风", "附魔"),
            CatalogItem("Tornado_Domain", "龙卷风场域", "附魔")),
        ["disposables"] = new JArray(
            CatalogItem("基地守护", "基地守护", "消耗品"),
            CatalogItem("极度冷冻", "极度冷冻", "消耗品"),
            CatalogItem("龙卷弹", "龙卷弹", "消耗品")),
        ["relics"] = new JArray(
            CatalogItem("瞄准镜", "瞄准镜", "遗物"),
            CatalogItem("黑洞", "黑洞", "遗物"),
            CatalogItem("充电宝", "充电宝", "遗物")),
        ["enemies"] = new JArray(
            CatalogItem("CommonMonster", "普通骷髅", "怪物"),
            CatalogItem("ShootingMonster", "射击骷髅", "怪物"),
            CatalogItem("SkullGiant", "巨型骷髅", "怪物"),
            CatalogItem("SpiderQueen_Explode", "爆炸蜘蛛女王", "怪物"),
            CatalogItem("BigBeetle", "巨型甲虫", "怪物")),
        ["catapultPoints"] = new JArray(
            CatalogItem("FreePoint", "自由弹射点", "弹射点"),
            CatalogItem("FreePoint_Attribute", "属性弹射点", "弹射点")),
        ["limits"] = new JObject
        {
            ["maxGrantCount"] = 99,
            ["maxEnchantmentLevel"] = 9,
            ["maxEnchantmentsPerVehicle"] = 5,
            ["maxEnemyLevel"] = 200,
            ["maxSpawnCount"] = 100,
            ["maxSpawnRadius"] = 50,
            ["maxCoordinateMagnitude"] = 10000
        }
    };

    private static JObject DemoVehicles() => new()
    {
        ["vehicles"] = new JArray(
            new JObject
            {
                ["vehicleId"] = 1042,
                ["typeId"] = "Link_ElectricFork_L2",
                ["name"] = "雷叉",
                ["level"] = 2,
                ["position"] = Position(12.5, -4.25, 0),
                ["attributes"] = new JArray(
                    Attribute("damage", "基础伤害", "float", 25, 0, 999999),
                    Attribute("range", "射程", "float", 6, 0, 100),
                    Attribute("targetCount", "目标个数", "integer", 4, 1, 100)),
                ["enchantments"] = new JArray(
                    new JObject { ["id"] = "Poison", ["name"] = "中毒", ["level"] = 2, ["effectiveLevel"] = 2 },
                    new JObject { ["id"] = "Energy", ["name"] = "能量", ["level"] = 1, ["effectiveLevel"] = 1 })
            })
    };

    private static JObject DemoEnemies() => new()
    {
        ["enemies"] = new JArray(
            new JObject
            {
                ["runtimeId"] = "enemy-0027",
                ["typeId"] = "SkullGiant",
                ["name"] = "巨型骷髅",
                ["health"] = 840,
                ["healthMax"] = 1200,
                ["position"] = Position(31.5, 8.75, 0),
                ["attributes"] = new JArray(
                    Attribute("health", "当前生命", "float", 840, 0, 99999999),
                    Attribute("moveSpeed", "移动速度", "float", 1.2, 0, 100),
                    Attribute("armor", "护甲", "float", 18, 0, 999999))
            })
    };

    private static JObject CatalogItem(string id, string name, string category, int? level = null)
    {
        JObject item = new()
        {
            ["id"] = id,
            ["name"] = name,
            ["fallbackName"] = id,
            ["tags"] = new JArray(category, id, name)
        };
        if (level.HasValue) item["level"] = level.Value;
        return item;
    }

    private static JObject Attribute(
        string id,
        string name,
        string kind,
        double value,
        double minimum,
        double maximum) => new()
    {
        ["id"] = id,
        ["name"] = name,
        ["kind"] = kind,
        ["value"] = value,
        ["baseValue"] = value,
        ["minimum"] = minimum,
        ["maximum"] = maximum
    };

    private static JObject Position(double x, double y, double z) => new()
    {
        ["x"] = x,
        ["y"] = y,
        ["z"] = z
    };

    private static ControlResponse Success(string message, JObject data, AutoPlayerStatus status) => new()
    {
        Success = true,
        Message = message,
        Hello = CheatHello(),
        Status = status,
        Data = data
    };

    public static IReadOnlyList<string> LogLines() => new[]
    {
            "14:28:10.044  信息  已验证 Skyspine 构建 1.237",
            $"14:28:10.106  信息  插件握手成功，协议 v{Protocol.CurrentVersion}",
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
