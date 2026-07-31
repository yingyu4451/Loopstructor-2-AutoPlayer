using System;
using System.Linq;
using Newtonsoft.Json.Linq;

namespace Loopstructor.AutoPlayer.Core;

public sealed class DecisionEngine
{
    public AutomationAction DecideFrontEnd(JObject result, AutomationRunOptions options)
    {
        JObject state = State(result);
        string scene = state["sceneName"]?.Value<string>() ?? string.Empty;
        if (string.Equals(scene, "RandomChooseScene", StringComparison.OrdinalIgnoreCase))
        {
            return DecideRandomSelection(state, options);
        }

        bool hasSave = state.SelectToken("startMenu.hasDefaultSave")?.Value<bool>() == true;
        if (options.ContinueExistingProfile && hasSave)
        {
            return new AutomationAction("continueGame", null, AutomationStage.FrontEnd, "继续使用隔离的测试存档。");
        }

        if (options.Mode == AutomationGameMode.Random)
        {
            return new AutomationAction("enterRandomMode", null, AutomationStage.FrontEnd, "进入随机模式选择界面。");
        }

        bool panelActive = state.SelectToken("commonMode.panelActive")?.Value<bool>() == true;
        bool ready = state.SelectToken("commonMode.readyForSubmit")?.Value<bool>() == true;
        JObject arguments = JObject.FromObject(new
        {
            characterIndex = options.CharacterIndex,
            difficultyIndex = options.DifficultyIndex,
            superModuleIndex = options.SuperModuleIndex
        });

        if (!panelActive || !ready)
        {
            return new AutomationAction("prepareCommonMode", arguments, AutomationStage.FrontEnd, "配置已解锁的普通模式选项。");
        }

        return new AutomationAction("submitCommonMode", arguments, AutomationStage.FrontEnd, "开始普通模式测试。");
    }

    public AutomationAction DecideInGame(JObject affordanceResult, JObject? rewardResult, JObject? eventResult)
    {
        JObject state = State(affordanceResult);
        if (state["gameOver"]?.Value<bool>() == true)
        {
            return AutomationAction.Wait(AutomationStage.Completed, "游戏报告本局已结束。");
        }

        if (state.SelectToken("wave.isInWaving")?.Value<bool>() == true)
        {
            return AutomationAction.Wait(AutomationStage.Battle, BuildBattleDetail(state));
        }

        JArray blockers = state["blockers"] as JArray ?? new JArray();
        if (HasBlocker(blockers, "reward"))
        {
            return DecideReward(rewardResult);
        }

        if (HasBlocker(blockers, "EventUI"))
        {
            return DecideEvent(eventResult, "EventUI");
        }

        if (HasBlocker(blockers, "RepairUI"))
        {
            return DecideEvent(eventResult, "RepairUI");
        }

        if (HasBlocker(blockers, "shop"))
        {
            return new AutomationAction("closeShop", null, AutomationStage.ManagingShop, "关闭商店，不消耗测试资源。");
        }

        if (HasBlocker(blockers, "UI_PopPanel_Option"))
        {
            return new AutomationAction("submitPopOption", JObject.FromObject(new { action = "submit" }), AutomationStage.Recovery, "确认阻塞操作的选项对话框。");
        }

        if (HasBlocker(blockers, "disposablePreview"))
        {
            return new AutomationAction("cancelDisposable", null, AutomationStage.Recovery, "取消残留的一次性物品预览。");
        }

        if (state.SelectToken("map.canStartWave")?.Value<bool>() == true)
        {
            return new AutomationAction("startWave", null, AutomationStage.StartingWave, "开始选定的波次。");
        }

        if (state.SelectToken("map.canSelectNextNode")?.Value<bool>() == true)
        {
            JArray nodes = state.SelectToken("map.selectableNodes") as JArray ?? new JArray();
            int? index = SelectRoute(nodes);
            if (index.HasValue)
            {
                return new AutomationAction("selectMapNode", JObject.FromObject(new { readyIndex = index.Value }), AutomationStage.SelectingRoute, $"选择路线选项 {index.Value}。");
            }

            return AutomationAction.Wait(
                AutomationStage.SelectingRoute,
                "运行时报告可以选择路线，但没有可选节点包含有效的就绪索引。");
        }

        return AutomationAction.Wait(AutomationStage.InitializingRun, "等待场景或界面状态稳定。");
    }

    public AutomationAction DecideReward(JObject? result)
    {
        JObject state = State(result);
        int rewardObjectCount = state["activeRewardObjectCount"]?.Value<int>() ?? 0;
        if (rewardObjectCount > 0)
        {
            JObject? rewardObject = (state["rewardObjects"] as JArray)?.OfType<JObject>()
                .FirstOrDefault(item => item["active"]?.Value<bool>() != false);
            int? instanceId = rewardObject?["instanceId"]?.Value<int?>();
            JObject arguments = instanceId.HasValue && instanceId.Value != 0
                ? JObject.FromObject(new { instanceId = instanceId.Value })
                : JObject.FromObject(new { index = 0 });
            return new AutomationAction("collectRewardObject", arguments, AutomationStage.ManagingRewards, "收取场景中的第一个奖励物品。");
        }

        JArray options = state["options"] as JArray ?? new JArray();
        if (options.Count > 0)
        {
            int index = SelectReward(options);
            return new AutomationAction("chooseRewardOption", JObject.FromObject(new { index }), AutomationStage.ManagingRewards, $"选择奖励选项 {index}。");
        }

        return AutomationAction.Wait(AutomationStage.ManagingRewards, "等待奖励动画或队列处理完成。");
    }

    public AutomationAction DecideEvent(JObject? result, string panel)
    {
        JObject state = State(result);
        string path = panel == "RepairUI" ? "repairPanel.options" : "eventPanel.options";
        string panelName = PanelDisplayName(panel);
        JArray options = state.SelectToken(path) as JArray ?? new JArray();
        JObject? candidate = options.OfType<JObject>()
            .FirstOrDefault(option => option["conditionPass"]?.Value<bool>() != false && option["buttonActive"]?.Value<bool>() != false);
        if (candidate == null)
        {
            return AutomationAction.Wait(AutomationStage.ManagingEvent, $"等待{panelName}中出现可用选项。");
        }

        int index = candidate["index"]?.Value<int>() ?? 0;
        return new AutomationAction("chooseWaveFunctionOption", JObject.FromObject(new { panel, index }), AutomationStage.ManagingEvent, $"选择{panelName}中可用的选项 {index}。");
    }

    private static AutomationAction DecideRandomSelection(JObject state, AutomationRunOptions options)
    {
        if (state["managerExists"]?.Value<bool>() != true)
        {
            return AutomationAction.Wait(AutomationStage.RandomSelection, "等待随机模式数据。");
        }

        if (state["selectedVehicleSelectable"]?.Value<bool>() != true)
        {
            return new AutomationAction("selectRandomVehicle", JObject.FromObject(new { index = options.RandomVehicleIndex }), AutomationStage.RandomSelection, "选择一个可用的随机模式载具。");
        }

        if (state["selectedFetterSelectable"]?.Value<bool>() != true)
        {
            return new AutomationAction("selectRandomFetter", JObject.FromObject(new { index = options.RandomFetterIndex }), AutomationStage.RandomSelection, "选择一个可用的随机模式羁绊。");
        }

        return new AutomationAction("submitRandomMode", JObject.FromObject(new { autoStop = true }), AutomationStage.RandomSelection, "推进随机模式转盘并进入本局。");
    }

    private static JObject State(JObject? result)
    {
        if (result == null)
        {
            return new JObject();
        }

        return result.SelectToken("data.state") as JObject
               ?? result["state"] as JObject
               ?? result;
    }

    private static bool HasBlocker(JArray blockers, string key) =>
        blockers.OfType<JObject>().Any(blocker => string.Equals(blocker["key"]?.Value<string>(), key, StringComparison.OrdinalIgnoreCase));

    private static string BuildBattleDetail(JObject state)
    {
        int? remaining = state.SelectToken("wave.enemy.remaining")?.Value<int?>();
        string node = NodeDisplayName(state.SelectToken("wave.nodeType")?.Value<string>());
        return remaining.HasValue ? $"战斗中：{node}，剩余 {remaining.Value} 个敌人。" : $"战斗中：{node}。";
    }

    private static string PanelDisplayName(string panel) => panel switch
    {
        "EventUI" => "事件界面",
        "RepairUI" => "修理界面",
        _ => "未知事件界面"
    };

    private static string NodeDisplayName(string? node) => node switch
    {
        "common" => "普通节点",
        "ferocityCommon" => "狂暴节点",
        "elite" => "精英节点",
        "boss" => "首领节点",
        _ => "未知节点"
    };

    private static int SelectReward(JArray options)
    {
        static int Score(JObject option)
        {
            string kind = option["rewardKind"]?.Value<string>() ?? "unknown";
            int baseScore = kind switch
            {
                "vehicle" => 400,
                "superModule" => 300,
                "disposable" => 200,
                "money" => 100,
                _ => 0
            };
            return baseScore + (option["rewardRare"]?.Value<string>() switch
            {
                "legend" => 30,
                "rare" => 20,
                "normal" => 10,
                _ => 0
            });
        }

        return options.OfType<JObject>()
            .OrderByDescending(Score)
            .ThenBy(option => option["index"]?.Value<int>() ?? int.MaxValue)
            .Select(option => option["index"]?.Value<int>() ?? 0)
            .FirstOrDefault();
    }

    private static int? SelectRoute(JArray nodes)
    {
        static int Score(JObject node)
        {
            int score = node["rewardEnum"]?.Value<string>() switch
            {
                "vehicle" => 500,
                "superModule" => 400,
                "potion" => 300,
                "money" => 200,
                _ => 100
            };
            if (node["isBoss"]?.Value<bool>() == true) score -= 20;
            if (node["needFight"]?.Value<bool>() != true) score += 10;
            return score;
        }

        return nodes.OfType<JObject>()
            .Where(node => node["canPlayerSelect"]?.Value<bool>() == true)
            .Where(node => node["readyIndex"]?.Type == JTokenType.Integer && node["readyIndex"]!.Value<int>() >= 0)
            .OrderByDescending(Score)
            .ThenBy(node => node["readyIndex"]!.Value<int>())
            .Select(node => (int?)node["readyIndex"]!.Value<int>())
            .FirstOrDefault();
    }
}
