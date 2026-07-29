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
            return new AutomationAction("continueGame", null, AutomationStage.FrontEnd, "Continue the isolated test profile.");
        }

        if (options.Mode == AutomationGameMode.Random)
        {
            return new AutomationAction("enterRandomMode", null, AutomationStage.FrontEnd, "Enter random mode selection.");
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
            return new AutomationAction("prepareCommonMode", arguments, AutomationStage.FrontEnd, "Prepare unlocked common-mode choices.");
        }

        return new AutomationAction("submitCommonMode", arguments, AutomationStage.FrontEnd, "Start the common-mode run.");
    }

    public AutomationAction DecideInGame(JObject affordanceResult, JObject? rewardResult, JObject? eventResult)
    {
        JObject state = State(affordanceResult);
        if (state["gameOver"]?.Value<bool>() == true)
        {
            return AutomationAction.Wait(AutomationStage.Completed, "The game reported game-over.");
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
            return new AutomationAction("closeShop", null, AutomationStage.ManagingShop, "Close the shop without spending test resources.");
        }

        if (HasBlocker(blockers, "UI_PopPanel_Option"))
        {
            return new AutomationAction("submitPopOption", JObject.FromObject(new { action = "submit" }), AutomationStage.Recovery, "Confirm the blocking option dialog.");
        }

        if (HasBlocker(blockers, "disposablePreview"))
        {
            return new AutomationAction("cancelDisposable", null, AutomationStage.Recovery, "Cancel a stale disposable preview.");
        }

        if (state.SelectToken("map.canStartWave")?.Value<bool>() == true)
        {
            return new AutomationAction("startWave", null, AutomationStage.StartingWave, "Start the selected wave.");
        }

        if (state.SelectToken("map.canSelectNextNode")?.Value<bool>() == true)
        {
            JArray nodes = state.SelectToken("map.selectableNodes") as JArray ?? new JArray();
            int? index = SelectRoute(nodes);
            if (index.HasValue)
            {
                return new AutomationAction("selectMapNode", JObject.FromObject(new { readyIndex = index.Value }), AutomationStage.SelectingRoute, $"Select route option {index.Value}.");
            }

            return AutomationAction.Wait(
                AutomationStage.SelectingRoute,
                "The runtime reported route selection, but no selectable node had a valid ready index.");
        }

        return AutomationAction.Wait(AutomationStage.InitializingRun, "Wait for the scene or UI state to settle.");
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
            return new AutomationAction("collectRewardObject", arguments, AutomationStage.ManagingRewards, "Collect the first scene reward object.");
        }

        JArray options = state["options"] as JArray ?? new JArray();
        if (options.Count > 0)
        {
            int index = SelectReward(options);
            return new AutomationAction("chooseRewardOption", JObject.FromObject(new { index }), AutomationStage.ManagingRewards, $"Choose reward option {index}.");
        }

        return AutomationAction.Wait(AutomationStage.ManagingRewards, "Wait for the reward animation or queue.");
    }

    public AutomationAction DecideEvent(JObject? result, string panel)
    {
        JObject state = State(result);
        string path = panel == "RepairUI" ? "repairPanel.options" : "eventPanel.options";
        JArray options = state.SelectToken(path) as JArray ?? new JArray();
        JObject? candidate = options.OfType<JObject>()
            .FirstOrDefault(option => option["conditionPass"]?.Value<bool>() != false && option["buttonActive"]?.Value<bool>() != false);
        if (candidate == null)
        {
            return AutomationAction.Wait(AutomationStage.ManagingEvent, $"Wait for an enabled {panel} option.");
        }

        int index = candidate["index"]?.Value<int>() ?? 0;
        return new AutomationAction("chooseWaveFunctionOption", JObject.FromObject(new { panel, index }), AutomationStage.ManagingEvent, $"Choose enabled {panel} option {index}.");
    }

    private static AutomationAction DecideRandomSelection(JObject state, AutomationRunOptions options)
    {
        if (state["managerExists"]?.Value<bool>() != true)
        {
            return AutomationAction.Wait(AutomationStage.RandomSelection, "Wait for random-mode data.");
        }

        if (state["selectedVehicleSelectable"]?.Value<bool>() != true)
        {
            return new AutomationAction("selectRandomVehicle", JObject.FromObject(new { index = options.RandomVehicleIndex }), AutomationStage.RandomSelection, "Select an available random-mode vehicle.");
        }

        if (state["selectedFetterSelectable"]?.Value<bool>() != true)
        {
            return new AutomationAction("selectRandomFetter", JObject.FromObject(new { index = options.RandomFetterIndex }), AutomationStage.RandomSelection, "Select an available random-mode fetter.");
        }

        return new AutomationAction("submitRandomMode", JObject.FromObject(new { autoStop = true }), AutomationStage.RandomSelection, "Advance the random-mode spinner and enter the run.");
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
        string node = state.SelectToken("wave.nodeType")?.Value<string>() ?? "unknown";
        return remaining.HasValue ? $"Battle: {node}, {remaining.Value} enemies remain." : $"Battle: {node}.";
    }

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
