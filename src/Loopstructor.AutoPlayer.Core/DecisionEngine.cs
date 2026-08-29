using System;
using System.Linq;
using Newtonsoft.Json.Linq;

namespace Loopstructor.AutoPlayer.Core;

public sealed class DecisionEngine
{
    private const int RailExpansionRewardBonus = 2400;

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
        return DecideInGameCore(affordanceResult, rewardResult, eventResult, null);
    }

    public AutomationAction DecideInGame(
        JObject affordanceResult,
        JObject? rewardResult,
        JObject? eventResult,
        AutomationDecisionPriority priority)
    {
        return DecideInGameCore(affordanceResult, rewardResult, eventResult, priority);
    }

    private AutomationAction DecideInGameCore(
        JObject affordanceResult,
        JObject? rewardResult,
        JObject? eventResult,
        AutomationDecisionPriority? priority)
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
            return DecideRewardCore(rewardResult, null, priority);
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
            JObject? route = SelectRoute(nodes, priority);
            int? index = route?["readyIndex"]?.Value<int?>();
            if (route != null && index.HasValue)
            {
                string reward = route["rewardEnum"]?.Value<string>() ?? "未知奖励";
                bool needFight = route["needFight"]?.Value<bool>() == true;
                int enemies = RouteCount(route["totalEnemyAmount"], 250);
                string risk = needFight ? $"，预计 {enemies} 个敌人" : "，非战斗节点";
                string candidates = BuildRouteCandidateSummary(nodes, priority);
                return new AutomationAction(
                    "selectMapNode",
                    JObject.FromObject(new
                    {
                        readyIndex = index.Value,
                        instanceId = route["instanceId"]?.Value<int>() ?? 0,
                        path = route["path"]?.Value<string>() ?? string.Empty,
                        x = route.SelectToken("pos.x")?.Value<int>() ?? 0,
                        y = route.SelectToken("pos.y")?.Value<int>() ?? 0
                    }),
                    AutomationStage.SelectingRoute,
                    $"选择路线选项 {index.Value}（{reward}{risk}，策略评分 {RouteScore(route, priority)}）。候选：{candidates}");
            }

            return AutomationAction.Wait(
                AutomationStage.SelectingRoute,
                "运行时报告可以选择路线，但没有可选节点包含有效的就绪索引。");
        }

        return AutomationAction.Wait(AutomationStage.InitializingRun, "等待场景或界面状态稳定。");
    }

    public AutomationAction DecideReward(JObject? result, JObject? vehicleResult = null)
    {
        return DecideRewardCore(result, vehicleResult, null);
    }

    public AutomationAction DecideReward(
        JObject? result,
        JObject? vehicleResult,
        AutomationDecisionPriority priority)
    {
        return DecideRewardCore(result, vehicleResult, priority);
    }

    private static AutomationAction DecideRewardCore(
        JObject? result,
        JObject? vehicleResult,
        AutomationDecisionPriority? priority)
    {
        JObject state = State(result);
        int rewardObjectCount = state["activeRewardObjectCount"]?.Value<int>() ?? 0;
        if (rewardObjectCount > 0)
        {
            JObject? rewardObject = (state["rewardObjects"] as JArray)?.OfType<JObject>()
                .FirstOrDefault(item => item["active"]?.Value<bool>() != false);
            int? instanceId = rewardObject?["instanceId"]?.Value<int?>();
            JObject? arguments = instanceId.HasValue && instanceId.Value != 0
                ? JObject.FromObject(new { instanceId = instanceId.Value })
                : null;
            if (arguments == null)
            {
                return AutomationAction.Wait(AutomationStage.ManagingRewards, "奖励物品仍在显示，但尚未提供可安全定位的实例或索引。");
            }

            return new AutomationAction("collectRewardObject", arguments, AutomationStage.ManagingRewards, "收取场景中的第一个奖励物品。");
        }

        JArray options = state["options"] as JArray ?? new JArray();
        if (options.Count > 0)
        {
            int? index = SelectReward(options, vehicleResult, priority);
            if (index.HasValue)
            {
                return new AutomationAction("chooseRewardOption", JObject.FromObject(new { index = index.Value }), AutomationStage.ManagingRewards, $"选择奖励选项 {index.Value}。");
            }

            bool hasAcquirableOption = options.OfType<JObject>().Any(option =>
                option["buttonActive"]?.Value<bool>() != false &&
                option["canAcquire"]?.Value<bool>() != false &&
                option["index"]?.Type == JTokenType.Integer &&
                option["index"]!.Value<int>() >= 0);
            string phaseToken = state["phaseToken"]?.Value<string>() ?? string.Empty;
            if (!hasAcquirableOption &&
                state["canSkip"]?.Value<bool>() == true &&
                !string.IsNullOrWhiteSpace(phaseToken))
            {
                return new AutomationAction(
                    "skipReward",
                    JObject.FromObject(new { phaseToken }),
                    AutomationStage.ManagingRewards,
                    "当前奖励均不可领取，跳过本次可选奖励。");
            }

            return AutomationAction.Wait(
                AutomationStage.ManagingRewards,
                hasAcquirableOption
                    ? "奖励选项仍在显示，但当前没有可安全定位的有效选项。"
                    : state["currentQueueMandatory"]?.Value<bool>() == true
                        ? "当前奖励均不可领取，但该奖励机会是强制选择，正在等待容量或状态变化。"
                        : "当前奖励均不可领取，且游戏当前不允许跳过，正在等待状态变化。");
        }

        return AutomationAction.Wait(AutomationStage.ManagingRewards, "等待奖励动画或队列处理完成。");
    }

    public AutomationAction DecideEvent(JObject? result, string panel)
    {
        JObject state = State(result);
        string panelPath = panel == "RepairUI" ? "repairPanel" : "eventPanel";
        JObject panelState = state.SelectToken(panelPath) as JObject ?? new JObject();
        string path = panelPath + ".options";
        string panelName = PanelDisplayName(panel);
        JArray options = state.SelectToken(path) as JArray ?? new JArray();
        IEnumerable<JObject> enabledOptions = options.OfType<JObject>()
            .Where(option => option["conditionPass"]?.Value<bool>() != false
                             && option["buttonActive"]?.Value<bool>() != false);
        JObject? candidate = string.Equals(panel, "RepairUI", StringComparison.OrdinalIgnoreCase)
            ? enabledOptions
                .OrderByDescending(RepairOptionPriority)
                .ThenBy(option => option["index"]?.Value<int>() ?? int.MaxValue)
                .FirstOrDefault()
            : enabledOptions
                .OrderByDescending(EventOptionPriority)
                .ThenBy(option => option["index"]?.Value<int>() ?? int.MaxValue)
                .FirstOrDefault();
        if (candidate == null)
        {
            return AutomationAction.Wait(AutomationStage.ManagingEvent, $"等待{panelName}中出现可用选项。");
        }

        int index = candidate["index"]?.Value<int>() ?? 0;
        return new AutomationAction(
            "chooseWaveFunctionOption",
            JObject.FromObject(new
            {
                panel,
                index,
                panelInstanceId = panelState["panelInstanceId"]?.Value<int>() ?? 0,
                instanceId = candidate["instanceId"]?.Value<int>() ?? 0,
                optionIdentity = WaveFunctionOptionSettlementGuard.BuildOptionIdentity(candidate) ?? string.Empty,
                directUpgrade = IsDirectUpgradeOption(candidate)
            }),
            AutomationStage.ManagingEvent,
            $"选择{panelName}中可用的选项 {index}。");
    }

    private static int RepairOptionPriority(JObject option)
    {
        if (IsDirectUpgradeOption(option)) return 2000;

        bool opensSecondaryPanel = HasBehaviourType(option, "OpenUIPanelBehaviour")
                                   || HasBehaviourType(option, "DisposableInvokeBehaviour");
        bool closesRepairPanel = HasBehaviourType(option, "WaveFunctionBehaviour");
        bool endsRepairWave = HasBehaviourType(option, "OverWaveBehaviour");
        bool restoresFort = HasBehaviourType(option, "ResourcesControl_MainHp");

        if (restoresFort && closesRepairPanel && endsRepairWave && !opensSecondaryPanel)
        {
            return 1000;
        }

        if (closesRepairPanel && endsRepairWave && !opensSecondaryPanel)
        {
            return 500;
        }

        return opensSecondaryPanel ? -1000 : 0;
    }

    private static bool IsDirectUpgradeOption(JObject option)
    {
        IEnumerable<string> identities = new[]
            {
                option["currentItemType"]?.Value<string>(),
                option["extraDataType"]?.Value<string>(),
                option["optionName"]?.Value<string>(),
                option["displayText"]?.Value<string>()
            }
            .Concat((option["behaviourTypes"] as JArray)?.Values<string>() ?? Enumerable.Empty<string>())
            .Concat((option["behaviourTypeIds"] as JArray)?.Values<string>() ?? Enumerable.Empty<string>())
            .Concat((option["behaviourNames"] as JArray)?.Values<string>() ?? Enumerable.Empty<string>())
            .Where(value => !string.IsNullOrWhiteSpace(value))!;
        return identities.Any(value =>
            value.IndexOf("DirectUpgrade", StringComparison.OrdinalIgnoreCase) >= 0 ||
            value.IndexOf("直接升级", StringComparison.Ordinal) >= 0);
    }

    private static int EventOptionPriority(JObject option)
    {
        bool opensSecondaryFlow = HasBehaviourType(option, "WaveFunctionOptionFlowBehaviour")
                                  || HasBehaviourType(option, "OpenUIPanelBehaviour")
                                  || HasBehaviourType(option, "DisposableInvokeBehaviour");
        bool closesEventPanel = HasBehaviourType(option, "WaveFunctionBehaviour");
        bool endsEventWave = HasBehaviourType(option, "OverWaveBehaviour");

        if (opensSecondaryFlow)
        {
            return -1000;
        }

        if (closesEventPanel && endsEventWave)
        {
            return 1000;
        }

        if (endsEventWave)
        {
            return 750;
        }

        return closesEventPanel ? 500 : 100;
    }

    private static bool HasBehaviourType(JObject option, string typeName)
    {
        foreach (string propertyName in new[] { "behaviourTypeIds", "behaviourTypes" })
        {
            if (option[propertyName] is not JArray identifiers)
            {
                continue;
            }

            if (identifiers.Values<string>().Any(identifier =>
                    string.Equals(identifier, typeName, StringComparison.Ordinal)
                    || (identifier?.EndsWith("." + typeName, StringComparison.Ordinal) ?? false)))
            {
                return true;
            }
        }

        return false;
    }

    private static AutomationAction DecideRandomSelection(JObject state, AutomationRunOptions options)
    {
        if (state["managerExists"]?.Value<bool>() != true)
        {
            return AutomationAction.Wait(AutomationStage.RandomSelection, "等待随机模式数据。");
        }

        if (state["selectedVehicleSelectable"]?.Value<bool>() != true)
        {
            JObject? selected = (state["availableVehicles"] as JArray)?.OfType<JObject>()
                .FirstOrDefault(item => item["index"]?.Value<int>() == options.RandomVehicleIndex);
            return new AutomationAction(
                "selectRandomVehicle",
                JObject.FromObject(new
                {
                    index = options.RandomVehicleIndex,
                    vehicleType = selected?["vehicleType"]?.Value<string>() ?? string.Empty
                }),
                AutomationStage.RandomSelection,
                "选择一个可用的随机模式载具。");
        }

        if (state["selectedFetterSelectable"]?.Value<bool>() != true)
        {
            string requiredFetter = state["requiredFetterEnum"]?.Value<string>() ?? string.Empty;
            JObject? selected = (state["availableFetters"] as JArray)?.OfType<JObject>()
                .FirstOrDefault(item => !string.IsNullOrWhiteSpace(requiredFetter) && string.Equals(
                    item["fetterEnum"]?.Value<string>(),
                    requiredFetter,
                    StringComparison.Ordinal))
                ?? (state["availableFetters"] as JArray)?.OfType<JObject>()
                    .FirstOrDefault(item => item["index"]?.Value<int>() == options.RandomFetterIndex)
                ?? (state["availableFetters"] as JArray)?.OfType<JObject>().FirstOrDefault();
            return new AutomationAction(
                "selectRandomFetter",
                JObject.FromObject(new
                {
                    fetterEnum = selected?["fetterEnum"]?.Value<string>() ?? string.Empty,
                    targetInstanceId = selected?["instanceId"]?.Value<int>() ?? 0,
                    targetPath = selected?["path"]?.Value<string>() ?? string.Empty
                }),
                AutomationStage.RandomSelection,
                "选择一个可用的随机模式羁绊。");
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

    private static int? SelectReward(
        JArray options,
        JObject? vehicleResult,
        AutomationDecisionPriority? priority)
    {
        JObject[] candidates = options.OfType<JObject>()
            .Where(option => option["buttonActive"]?.Value<bool>() != false)
            .Where(option => option["canAcquire"]?.Value<bool>() != false)
            .Where(option => option["index"]?.Type == JTokenType.Integer && option["index"]!.Value<int>() >= 0)
            .ToArray();
        if (candidates.Length == 0)
        {
            return null;
        }

        JArray? vehicles = vehicleResult == null
            ? null
            : State(vehicleResult)["vehicles"] as JArray;
        string mainFetter = vehicles == null ? string.Empty : ResolveMainFetter(vehicles);
        return candidates
            .OrderByDescending(option => RewardStrategicScore(option, vehicles, mainFetter, priority))
            .ThenByDescending(RewardRarityPriority)
            .ThenBy(option => option["index"]?.Value<int>() ?? int.MaxValue)
            .Select(option => (int?)option["index"]!.Value<int>())
            .FirstOrDefault();
    }

    private static int RewardKindPriority(JObject option) =>
        (option["rewardKind"]?.Value<string>() ?? string.Empty).ToLowerInvariant() switch
        {
            "vehicle" => 4,
            "supermodule" => 3,
            "disposable" => 2,
            "money" => 1,
            _ => 0
        };

    private static int RewardRarityPriority(JObject option) =>
        (option["rewardRare"]?.Value<string>() ?? string.Empty).ToLowerInvariant() switch
        {
            "boss4" => 7,
            "boss3" => 6,
            "boss2" => 5,
            "boss1" => 4,
            "epic" => 3,
            // Retained for packages that used the pre-runtime placeholder value.
            "legend" => 3,
            "rare" => 2,
            "normal" => 1,
            _ => 0
        };

    private static int RewardStrategicScore(
        JObject option,
        JArray? vehicles,
        string mainFetter,
        AutomationDecisionPriority? priority)
    {
        int kindPriority = RewardKindPriority(option);
        int score = kindPriority switch
        {
            4 => 300,
            3 => 420,
            2 => 220,
            1 => 50,
            _ => 0
        };
        if (priority == AutomationDecisionPriority.Relics && kindPriority == 3)
        {
            score += 3200;
        }
        score += RewardRarityPriority(option) * 40;
        if (kindPriority == 2)
        {
            bool legacyAttributeExpansion = IsRailExpansionReward(option);
            bool catapultPointReward = IsCatapultPointReward(option);
            score += priority switch
            {
                AutomationDecisionPriority.VehicleRewards when catapultPointReward => 400,
                AutomationDecisionPriority.CatapultPoints when catapultPointReward => RailExpansionRewardBonus + 1400,
                null when legacyAttributeExpansion => RailExpansionRewardBonus,
                _ => 0
            };
        }

        if (kindPriority != 4)
        {
            return score;
        }

        int vehicleLevel = RewardVehicleLevel(option);
        score += vehicleLevel * 100;
        if (priority == AutomationDecisionPriority.VehicleRewards)
        {
            score += 600;
            if (vehicleLevel >= 3)
            {
                score += 2800;
            }
        }

        if (vehicles != null) score += RewardMatchesFetter(option, mainFetter) * 120;

        return score;
    }

    private static bool IsRailExpansionReward(JObject option)
    {
        return IsRailExpansionDisposableEnum(option["disposableEnum"]?.Value<string>())
               || IsRailExpansionDisposableEnum(option["assignDisposableEnum"]?.Value<string>());
    }

    private static bool IsCatapultPointReward(JObject option)
    {
        return IsCatapultPointDisposableEnum(option["disposableEnum"]?.Value<string>())
               || IsCatapultPointDisposableEnum(option["assignDisposableEnum"]?.Value<string>());
    }

    private static bool IsRailExpansionDisposableEnum(string? value)
    {
        string candidate = value?.Trim() ?? string.Empty;
        return string.Equals(candidate, "FreePoint_Attribute", StringComparison.OrdinalIgnoreCase)
               || string.Equals(candidate, "AddNewPoint_Attribute", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsCatapultPointDisposableEnum(string? value)
    {
        string candidate = value?.Trim() ?? string.Empty;
        return IsRailExpansionDisposableEnum(candidate)
               || string.Equals(candidate, "FreePoint", StringComparison.OrdinalIgnoreCase)
               || string.Equals(candidate, "AddNewPoint", StringComparison.OrdinalIgnoreCase)
               || string.Equals(candidate, "EnergyPoint", StringComparison.OrdinalIgnoreCase)
               || string.Equals(candidate, "CreateFreeEnergyExpansion", StringComparison.OrdinalIgnoreCase);
    }

    private static int RewardVehicleLevel(JObject option)
    {
        if (RewardKindPriority(option) != 4) return 0;

        int? explicitLevel = option["level"]?.Value<int?>();
        if (explicitLevel.HasValue) return Math.Max(explicitLevel.Value, 0);

        string vehicleType = option["vehicleType"]?.Value<string>() ?? string.Empty;
        int marker = vehicleType.LastIndexOf("_L", StringComparison.OrdinalIgnoreCase);
        return marker >= 0 && int.TryParse(vehicleType.Substring(marker + 2), out int parsedLevel)
            ? Math.Max(parsedLevel, 0)
            : 0;
    }

    private static string ResolveMainFetter(JArray vehicles)
    {
        JObject[] allVehicles = vehicles.OfType<JObject>()
            .Where(vehicle => vehicle["isVirtual"]?.Value<bool>() != true && vehicle["isFixedHead"]?.Value<bool>() != true)
            .ToArray();
        JObject[] deployedVehicles = allVehicles
            .Where(vehicle => vehicle["inBag"]?.Value<bool>() != true && vehicle["active"]?.Value<bool>() != false)
            .ToArray();

        string deployedMain = ResolveMainFetter(deployedVehicles);
        return !string.IsNullOrWhiteSpace(deployedMain)
            ? deployedMain
            : ResolveMainFetter(allVehicles);
    }

    private static string ResolveMainFetter(IEnumerable<JObject> vehicles)
    {
        return vehicles
            .SelectMany(vehicle => (vehicle["fetters"] as JArray)?.OfType<JObject>() ?? Enumerable.Empty<JObject>())
            .Where(fetter => fetter["count"]?.Value<int>() > 0)
            .Select(fetter => new
            {
                Name = fetter["fetterEnum"]?.Value<string>() ?? string.Empty,
                Count = fetter["count"]!.Value<int>()
            })
            .Where(fetter => !string.IsNullOrWhiteSpace(fetter.Name) &&
                             !string.Equals(fetter.Name, "None", StringComparison.OrdinalIgnoreCase) &&
                             !fetter.Name.EndsWith("_Train", StringComparison.OrdinalIgnoreCase))
            .GroupBy(fetter => fetter.Name, StringComparer.OrdinalIgnoreCase)
            .Select(group => new { Name = group.Key, Count = group.Sum(fetter => fetter.Count) })
            .OrderByDescending(fetter => fetter.Count)
            .ThenBy(fetter => fetter.Name, StringComparer.Ordinal)
            .Select(fetter => fetter.Name)
            .FirstOrDefault() ?? string.Empty;
    }

    private static int RewardMatchesFetter(JObject option, string mainFetter)
    {
        if (string.IsNullOrWhiteSpace(mainFetter) || option["effectiveFetters"] is not JArray fetters) return 0;

        return fetters.OfType<JObject>().Any(fetter =>
            fetter["isActual"]?.Value<bool>() != false &&
            (fetter["count"]?.Value<int>() ?? 0) > 0 &&
            string.Equals(fetter["fetterEnum"]?.Value<string>(), mainFetter, StringComparison.OrdinalIgnoreCase))
            ? 1
            : 0;
    }

    private static JObject? SelectRoute(JArray nodes, AutomationDecisionPriority? priority)
    {
        return nodes.OfType<JObject>()
            .Where(node => node["canPlayerSelect"]?.Value<bool>() == true)
            .Where(node => node["readyIndex"]?.Type == JTokenType.Integer && node["readyIndex"]!.Value<int>() >= 0)
            .OrderByDescending(node => RouteScore(node, priority))
            .ThenBy(node => node["needFight"]?.Value<bool>() == true ? 1 : 0)
            .ThenBy(node => RouteCount(node["totalEnemyAmount"], 250))
            .ThenByDescending(RouteRewardDiversity)
            .ThenBy(node => node["readyIndex"]!.Value<int>())
            .FirstOrDefault();
    }

    private static string BuildRouteCandidateSummary(JArray nodes, AutomationDecisionPriority? priority)
    {
        string[] candidates = nodes.OfType<JObject>()
            .Where(node => node["canPlayerSelect"]?.Value<bool>() == true)
            .Where(node => node["readyIndex"]?.Type == JTokenType.Integer && node["readyIndex"]!.Value<int>() >= 0)
            .OrderBy(node => node["readyIndex"]!.Value<int>())
            .Take(8)
            .Select(node =>
            {
                int readyIndex = node["readyIndex"]!.Value<int>();
                string reward = node["rewardEnum"]?.Value<string>() ?? "未知奖励";
                string risk = node["needFight"]?.Value<bool>() == true
                    ? $"{RouteCount(node["totalEnemyAmount"], 250)} 敌人"
                    : "非战斗";
                return $"{readyIndex}:{reward}/{risk}/{RouteScore(node, priority)} 分";
            })
            .ToArray();
        return candidates.Length == 0 ? "无" : string.Join("；", candidates);
    }

    private static int RouteScore(JObject node, AutomationDecisionPriority? priority)
    {
        JObject? drops = node["dropCounts"] as JObject;
        int vehicleCount = RouteCount(drops?["vehicle"]);
        int catapultCount = RouteCount(drops?["catapult"]);
        int superModuleCount = RouteCount(drops?["superModule"]);
        int disposableCount = RouteCount(drops?["disposable"]);
        int moneyCount = RouteCount(drops?["money"]);

        int vehicleWeight = priority switch
        {
            AutomationDecisionPriority.VehicleRewards => 1100,
            AutomationDecisionPriority.CatapultPoints => 350,
            AutomationDecisionPriority.Relics => 300,
            _ => 700
        };
        int catapultWeight = priority switch
        {
            AutomationDecisionPriority.VehicleRewards => 250,
            AutomationDecisionPriority.CatapultPoints => 1000,
            AutomationDecisionPriority.Relics => 250,
            _ => 350
        };
        int disposableWeight = priority switch
        {
            AutomationDecisionPriority.VehicleRewards => 180,
            AutomationDecisionPriority.CatapultPoints => 650,
            AutomationDecisionPriority.Relics => 180,
            _ => 250
        };
        int rewardScore = (vehicleCount * vehicleWeight)
                          + (catapultCount * catapultWeight)
                          + (superModuleCount * (priority == AutomationDecisionPriority.Relics ? 1800 : 600))
                          + (disposableCount * disposableWeight)
                          + (moneyCount * 60);

        string rewardEnum = node["rewardEnum"]?.Value<string>() ?? string.Empty;
        int score = rewardScore + RouteRewardTypeScore(rewardEnum, priority);

        bool needFight = node["needFight"]?.Value<bool>() == true;
        if (needFight)
        {
            int enemyCount = RouteCount(node["totalEnemyAmount"], 250);
            score -= RouteCombatRiskPenalty(enemyCount);
        }
        else
        {
            score += 100;
        }

        if (node["isBoss"]?.Value<bool>() == true)
        {
            score -= 1200;
        }

        return score;
    }

    private static int RouteCombatRiskPenalty(int enemyCount)
    {
        int normalizedEnemyCount = Math.Min(Math.Max(enemyCount, 0), 250);
        int crowdingCount = Math.Max(normalizedEnemyCount - 40, 0);
        return 40 + (normalizedEnemyCount * 3) + (crowdingCount * 18);
    }

    private static int RouteRewardTypeScore(string rewardEnum, AutomationDecisionPriority? priority)
    {
        if (priority == AutomationDecisionPriority.Relics &&
            string.Equals(rewardEnum, "superModule", StringComparison.OrdinalIgnoreCase))
        {
            return 1800;
        }

        if (priority == AutomationDecisionPriority.VehicleRewards &&
            string.Equals(rewardEnum, "vehicle", StringComparison.OrdinalIgnoreCase))
        {
            return 1100;
        }

        if (priority == AutomationDecisionPriority.CatapultPoints)
        {
            switch (rewardEnum.ToLowerInvariant())
            {
                case "potion":
                case "disposable": return 650;
                case "build": return 1000;
                case "elitecatapult": return 800;
                case "ferocitycommoncatapult": return 760;
                case "commoncatapult": return 720;
            }
        }

        switch (rewardEnum.ToLowerInvariant())
        {
            // Legacy protocol values are retained for compatibility with older game bridges.
            case "vehicle": return 700;
            case "supermodule": return 600;
            case "potion":
            case "disposable": return 250;
            case "money": return 60;

            // Current GuiGameMcpMapRuntime WaveRewardEnum values.
            case "build": return 450;
            case "elitecatapult": return 200;
            case "ferocitycommoncatapult": return 180;
            case "commoncatapult": return 160;
            case "treasurechest": return 200;
            case "elite":
            case "elite1": return 120;
            case "boss": return 80;
            case "ferocitycommon":
            case "ferocitycommon1": return 80;
            case "randomevent": return 100;
            // The current automation intentionally closes shops without buying. Avoid
            // spending a route on one while any productive legal alternative exists.
            case "shop": return -1000;
            case "common":
            case "common1": return 60;
            default: return 0;
        }
    }

    private static int RouteRewardDiversity(JObject node)
    {
        JObject? drops = node["dropCounts"] as JObject;
        return new[] { "vehicle", "catapult", "superModule", "disposable", "money" }
            .Count(name => RouteCount(drops?[name]) > 0);
    }

    private static int RouteCount(JToken? token, int maximum = 20)
    {
        int value = token?.Type == JTokenType.Integer ? token.Value<int>() : 0;
        return Math.Min(Math.Max(value, 0), maximum);
    }
}
