using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Reflection;
using Loopstructor.AutoPlayer.Core;
using Newtonsoft.Json.Linq;
using UnityEngine;

namespace Loopstructor.AutoPlayer.Plugin;

internal sealed class CheatRuntimeBridge
{
    private const double MaxAttributeMagnitude = 1_000_000_000d;
    private const float MaxCoordinateMagnitude = 10_000f;
    private const int MaxGrantCount = 20;
    private const int MaxSpawnCount = 10;

    private readonly List<string> _missingMembers = new();
    private readonly object _godModeSource = new();
    private GUIStyle? _enemyIdStyle;
    private object? _baseReceiver;

    private Type? _vehicleManagerType;
    private Type? _vehicleControllerType;
    private Type? _gameConfigManagerType;
    private Type? _gameSoConfigType;
    private Type? _mainRazorTypeSoType;
    private Type? _fetterInfoCfgType;
    private Type? _vehicleType;
    private Type? _fetterType;
    private Type? _disposableManagerType;
    private Type? _disposableType;
    private Type? _gameControllerType;
    private Type? _mainStationType;
    private Type? _waveControllerType;
    private Type? _agentCreatorType;
    private Type? _enemyConfigType;
    private Type? _aiIdType;
    private Type? _basicAiType;
    private Type? _basicAgentType;
    private Type? _battleSystemType;
    private Type? _damageReceiverType;
    private Type? _battleRuntimeHandleType;
    private Type? _blackboardMemoryType;
    private Type? _generalFloatParameterType;
    private Type? _generalIntParameterType;
    private Type? _battleGroupType;
    private Type? _agentRegisterType;
    private Type? _superModuleManagerType;
    private Type? _superModuleType;
    private Type? _rewardManagerType;
    private Type? _potionRewardType;
    private Type? _superModuleRewardType;
    private Type? _battleMemoryType;
    private Type? _healthMaxSyncModeType;

    public bool IsAvailable { get; private set; }
    public bool EnemyIdsVisible { get; set; }
    public bool BaseGodModeRequested { get; private set; }
    public IReadOnlyList<string> MissingMembers => _missingMembers;
    public IReadOnlyList<string> Capabilities => IsAvailable ? CheatCommands.All : Array.Empty<string>();

    public void Initialize()
    {
        _missingMembers.Clear();
        _vehicleManagerType = Require("MetroTD.VehicleSystem.VehicleManager");
        _vehicleControllerType = Require("MetroTD.VehicleSystem.VehicleController");
        _gameConfigManagerType = Require("MetroTD.GameConfigManager");
        _gameSoConfigType = Require("MetroTD.GameSOConfig");
        _mainRazorTypeSoType = Require("MetroTD.VehicleSystem.MainRazorTypeSO");
        _fetterInfoCfgType = Require("MetroTD.VehicleSystem.SO_FetterInfoCfg");
        _vehicleType = Require("MetroTD.VehicleSystem.VehicleType");
        _fetterType = Require("FetterEnum");
        _disposableManagerType = Require("MetroTD.DisposableSystem.DisposableManager");
        _disposableType = Require("MetroTD.DisposableSystem.DisposableEnum");
        _gameControllerType = Require("MetroTD.GameController");
        _mainStationType = Require("MetroTD.CatapultSystem.MainStation");
        _waveControllerType = Require("MetroTD.RoomSystem.WaveDurationController");
        _agentCreatorType = Require("MetroTD.AISystem.AgentCreator");
        _enemyConfigType = Require("MetroTD.AISystem.SO_EnemyCfg");
        _aiIdType = Require("MetroTD.AISystem.AI_ID");
        _basicAiType = Require("BasicAI");
        _basicAgentType = Require("MetroTD.AISystem.BasicAgent");
        _battleSystemType = Require("MetroTD.BattleSystem.BattleSystem");
        _damageReceiverType = Require("MetroTD.BattleSystem.DamageReceiver");
        _battleRuntimeHandleType = Require("MetroTD.BattleSystem.BattleSystemRuntimeHandle");
        _blackboardMemoryType = Require("ActFramework_ByHZR.BasicUtil.BlackboardMemory");
        _generalFloatParameterType = Require("ActFramework_ByHZR.IntelligentParameter.GeneralFloatParameter");
        _generalIntParameterType = Require("ActFramework_ByHZR.IntelligentParameter.GeneralIntParameter");
        _battleGroupType = Require("MetroTD.BattleSystem.BattleGroup");
        _agentRegisterType = Require("MetroTD.AISystem.AgentRegisterType");
        _superModuleManagerType = Require("MetroTD.SuperModuleSystem.SuperModuleManager");
        _superModuleType = Require("MetroTD.SuperModuleSystem.SuperModuleEnum");
        _rewardManagerType = Require("MetroTD.RewardSystem.RewardManager");
        _potionRewardType = Require("MetroTD.RewardSystem.PotionReward");
        _superModuleRewardType = Require("MetroTD.RewardSystem.SuperModuleReward");
        _battleMemoryType = Require("MetroTD.BattleSystem.BattleMemoryEnum");
        _healthMaxSyncModeType = Require("MetroTD.BattleSystem.HealthMaxSyncMode");
        ValidateRuntimeContract();
        IsAvailable = _missingMembers.Count == 0;
    }

    public JObject QueryCatalog()
    {
        EnsureAvailable();
        IReadOnlyList<object> vehicles = ConfiguredVehicleValues();
        IReadOnlyList<object> enchantments = ConfiguredEnchantmentValues();
        IReadOnlyList<object> disposables = ConfiguredRewardValues("AllDisposableRewards", "disposableEnum", _disposableType!);
        IReadOnlyList<object> relics = ConfiguredRewardValues("AllSuperModuleRewards", "superModuleEnum", _superModuleType!);
        return new JObject
        {
            ["vehicles"] = EnumItems(vehicles),
            ["enchantments"] = EnumItems(enchantments),
            ["disposables"] = EnumItems(disposables),
            ["relics"] = EnumItems(relics),
            ["enemies"] = SafeEnemyItems(),
            ["limits"] = new JObject
            {
                ["maxGrantCount"] = MaxGrantCount,
                ["maxSpawnCount"] = MaxSpawnCount,
                ["maxEnchantmentLevel"] = 7,
                ["maxEnemyLevel"] = 200,
                ["maxCoordinateMagnitude"] = MaxCoordinateMagnitude
            }
        };
    }

    public CheatExecutionResult GrantVehicle(JObject arguments)
    {
        EnsureAvailable();
        string vehicleId = RequiredText(arguments, "vehicleId", "必须选择战车类型。");
        int count = BoundedInt(arguments, "count", 1, MaxGrantCount, 1);
        bool enchanted = arguments.Value<bool?>("enchanted") == true;
        string enchantmentId = arguments.Value<string>("enchantmentId") ?? string.Empty;
        int enchantmentLevel = BoundedInt(arguments, "enchantmentLevel", 1, 7, 1);
        object vehicleEnum = ParseEnum(_vehicleType!, vehicleId, "战车类型");
        if (!ContainsEnumValue(ConfiguredVehicleValues(), vehicleEnum))
        {
            return CheatExecutionResult.Fail("当前游戏配置中没有可生成的战车：" + vehicleId + "。", "VEHICLE_NOT_CONFIGURED");
        }

        object? enchantmentEnum = null;
        if (enchanted)
        {
            if (string.IsNullOrWhiteSpace(enchantmentId) || enchantmentId == "None")
            {
                return CheatExecutionResult.Fail("已勾选附魔，但没有选择有效附魔。", "INVALID_ENCHANTMENT");
            }

            enchantmentEnum = ParseEnum(_fetterType!, enchantmentId, "附魔类型");
            if (!ContainsEnumValue(ConfiguredEnchantmentValues(), enchantmentEnum))
            {
                return CheatExecutionResult.Fail("当前游戏配置中没有可用的基础附魔：" + enchantmentId + "。", "ENCHANTMENT_NOT_CONFIGURED");
            }
        }

        object manager = GetRequiredSingleton(_vehicleManagerType!, "VehicleManager");
        MethodInfo? method = enchanted
            ? FindMethod(manager.GetType(), "GetCustomNewMainRazor", _vehicleType!, _fetterType!, typeof(int))
            : FindMethod(manager.GetType(), "GetNewMainRazor", _vehicleType!);
        if (method == null)
        {
            return CheatExecutionResult.Fail("当前游戏版本缺少战车获取入口。", "VEHICLE_API_MISSING");
        }

        JArray granted = new();
        for (int index = 0; index < count; index++)
        {
            object? created = method.Invoke(
                manager,
                enchanted
                    ? new[] { vehicleEnum, enchantmentEnum!, (object)enchantmentLevel }
                    : new[] { vehicleEnum });
            if (created == null) break;
            granted.Add(BuildVehicleReference(created));
        }

        JObject data = new()
        {
            ["requested"] = count,
            ["granted"] = granted.Count,
            ["vehicles"] = granted
        };
        string message = $"已获取 {granted.Count}/{count} 辆战车 {vehicleId}" +
                         (enchanted ? $"，附魔 {enchantmentId} 等级 {enchantmentLevel}。" : "。 ");
        if (granted.Count == count) return CheatExecutionResult.Changed(message.TrimEnd(), data);
        return granted.Count > 0
            ? CheatExecutionResult.Partial(message + " 部分战车未能生成，请检查配置或容量。", data)
            : CheatExecutionResult.Fail("未能获取战车，请确认当前位于已初始化的对局场景。", "VEHICLE_GRANT_FAILED");
    }

    public CheatExecutionResult GrantDisposable(JObject arguments)
    {
        EnsureAvailable();
        string disposableId = RequiredText(arguments, "disposableId", "必须选择消耗品类型。");
        int count = BoundedInt(arguments, "count", 1, MaxGrantCount, 1);
        object disposableEnum = ParseEnum(_disposableType!, disposableId, "消耗品类型");
        if (!ContainsEnumValue(
                ConfiguredRewardValues("AllDisposableRewards", "disposableEnum", _disposableType!),
                disposableEnum))
        {
            return CheatExecutionResult.Fail("当前奖励配置中没有该消耗品：" + disposableId + "。", "DISPOSABLE_NOT_CONFIGURED");
        }

        object manager = GetRequiredSingleton(_disposableManagerType!, "DisposableManager");
        MethodInfo? method = FindMethod(manager.GetType(), "TryGetDisposable", _disposableType!);
        if (method == null)
        {
            return CheatExecutionResult.Fail("当前游戏版本缺少消耗品获取入口。", "DISPOSABLE_API_MISSING");
        }

        int granted = 0;
        for (int index = 0; index < count; index++)
        {
            if (method.Invoke(manager, new[] { disposableEnum }) is not bool success || !success) break;
            granted++;
        }

        JObject data = new() { ["requested"] = count, ["granted"] = granted, ["disposableId"] = disposableId };
        string message = $"已获取 {granted}/{count} 个消耗品 {disposableId}。";
        if (granted == count) return CheatExecutionResult.Changed(message, data);
        return granted > 0
            ? CheatExecutionResult.Partial(message + " 背包容量或配置阻止了剩余获取。", data)
            : CheatExecutionResult.Fail("未能获取消耗品，可能已达到容量上限或当前场景未初始化。", "DISPOSABLE_GRANT_FAILED");
    }

    public CheatExecutionResult GrantRelic(JObject arguments)
    {
        EnsureAvailable();
        string relicId = RequiredText(arguments, "relicId", "必须选择遗物类型。");
        if (string.Equals(relicId, "None", StringComparison.OrdinalIgnoreCase))
        {
            return CheatExecutionResult.Fail("None 不是可获取的遗物。", "INVALID_RELIC");
        }

        object relicEnum = ParseEnum(_superModuleType!, relicId, "遗物类型");
        if (!ContainsEnumValue(
                ConfiguredRewardValues("AllSuperModuleRewards", "superModuleEnum", _superModuleType!),
                relicEnum))
        {
            return CheatExecutionResult.Fail("当前奖励配置中没有该遗物：" + relicId + "。", "RELIC_NOT_CONFIGURED");
        }

        object manager = GetRequiredSingleton(_superModuleManagerType!, "SuperModuleManager");
        MethodInfo? method = FindMethod(manager.GetType(), "GetSuperModule", _superModuleType!, typeof(bool));
        if (method == null)
        {
            return CheatExecutionResult.Fail("当前游戏版本缺少遗物获取入口。", "RELIC_API_MISSING");
        }

        int before = GetDictionaryListCount(GetMember(manager, "superModules"), relicEnum);
        method.Invoke(manager, new[] { relicEnum, (object)true });
        int after = GetDictionaryListCount(GetMember(manager, "superModules"), relicEnum);
        if (after <= before)
        {
            return CheatExecutionResult.Fail(
                "遗物获取请求未增加持有数量，请确认当前对局已初始化且遗物配置有效。",
                "RELIC_GRANT_FAILED");
        }

        return CheatExecutionResult.Changed(
            "已获取遗物 " + relicId + "。",
            new JObject { ["relicId"] = relicId, ["before"] = before, ["after"] = after });
    }

    public CheatExecutionResult SetBaseGodMode(JObject arguments)
    {
        EnsureAvailable();
        bool enabled = arguments.Value<bool?>("enabled") == true;
        object receiver = ResolveBaseReceiver();
        MethodInfo? method = receiver.GetType().GetMethod(
            enabled ? "AddGodModeSource" : "RemoveGodModeSource",
            BindingFlags.Public | BindingFlags.Instance,
            null,
            new[] { typeof(object) },
            null);
        if (method == null)
        {
            return CheatExecutionResult.Fail("当前游戏版本缺少按来源控制基地无敌的入口。", "GOD_MODE_API_MISSING");
        }

        method.Invoke(receiver, new[] { _godModeSource });
        _baseReceiver = enabled ? receiver : null;
        BaseGodModeRequested = enabled;
        bool actual = GetBool(receiver, "GodMode");
        JObject data = new()
        {
            ["requested"] = enabled,
            ["actual"] = actual,
            ["health"] = GetNumber(receiver, "Health"),
            ["healthMax"] = GetNumber(receiver, "HealthMax")
        };
        return CheatExecutionResult.Changed(enabled ? "基地无敌已开启。" : "基地无敌已关闭。", data);
    }

    public CheatExecutionResult EndWave()
    {
        EnsureAvailable();
        object controller = GetRequiredSingleton(_waveControllerType!, "WaveDurationController");
        if (GetBool(controller, "templateLock"))
        {
            return CheatExecutionResult.Fail("当前波次被模板锁定，不能强制结束。", "WAVE_LOCKED");
        }

        if (!GetBool(controller, "m_isInWave"))
        {
            return CheatExecutionResult.Fail("当前没有正在进行的波次。", "WAVE_NOT_ACTIVE");
        }

        if (GetBool(controller, "m_isBossWave"))
        {
            return CheatExecutionResult.Fail(
                "为避免破坏 Boss 结算账本，不能直接跳过 Boss 波；可使用“清除所有敌人”让游戏按 Boss 流程结算。",
                "BOSS_WAVE_NOT_SKIPPABLE");
        }

        MethodInfo? method = FindMethod(controller.GetType(), "WaveOver");
        if (method == null)
        {
            return CheatExecutionResult.Fail("当前游戏版本缺少结束波次入口。", "WAVE_OVER_API_MISSING");
        }

        object creator = GetRequiredSingleton(_agentCreatorType!, "AgentCreator");
        MethodInfo? clearDeferred = FindMethod(creator.GetType(), "ClearDeferredSpawnQueue");
        MethodInfo? clearEnemies = FindMethod(creator.GetType(), "ClearAllEnemy");
        if (clearDeferred == null || clearEnemies == null)
        {
            return CheatExecutionResult.Fail("当前游戏版本缺少安全结束波次所需的清场入口。", "WAVE_CLEANUP_API_MISSING");
        }

        List<GameObject> enemies = SnapshotEnemyGameObjects();
        foreach (GameObject enemy in enemies)
        {
            DisableEnemyDeathMessage(enemy);
        }

        clearDeferred.Invoke(creator, null);
        clearEnemies.Invoke(creator, null);
        method.Invoke(controller, null);
        if (GetBool(controller, "m_isInWave"))
        {
            return CheatExecutionResult.Partial(
                "已执行安全清场，但游戏仍报告处于波次中；请关闭游戏进程后重新测试。",
                new JObject { ["clearedEnemies"] = enemies.Count });
        }

        return CheatExecutionResult.Changed(
            $"已结束当前波次并安全清理 {enemies.Count} 个旧敌人及待生成队列。",
            new JObject { ["clearedEnemies"] = enemies.Count });
    }

    public CheatExecutionResult ClearEnemies()
    {
        EnsureAvailable();
        object creator = GetRequiredSingleton(_agentCreatorType!, "AgentCreator");
        int before = SnapshotEnemyTargets().Count;
        MethodInfo? clear = FindMethod(creator.GetType(), "ClearAllEnemy");
        if (clear == null)
        {
            return CheatExecutionResult.Fail("当前游戏版本缺少清除敌人入口。", "CLEAR_ENEMY_API_MISSING");
        }

        clear.Invoke(creator, null);
        int after = SnapshotEnemyTargets().Count;
        return CheatExecutionResult.Changed(
            $"已清除场上敌人，操作前 {before} 个，操作后 {after} 个；波次尚未生成的敌人仍按原计划生成。",
            new JObject { ["before"] = before, ["after"] = after });
    }

    public JObject QueryVehicles()
    {
        EnsureAvailable();
        JArray vehicles = new();
        foreach (object vehicle in SnapshotVehicles())
        {
            vehicles.Add(BuildVehicleState(vehicle));
        }

        return new JObject { ["vehicles"] = vehicles, ["count"] = vehicles.Count };
    }

    public CheatExecutionResult ModifyVehicle(JObject arguments)
    {
        EnsureAvailable();
        int vehicleId = arguments.Value<int?>("vehicleId")
                        ?? throw new InvalidOperationException("必须选择有效的战车 ID。");
        string attributeId = RequiredText(arguments, "attributeId", "必须选择战车属性。");
        double value = RequiredFiniteNumber(arguments, "value");
        object? vehicle = SnapshotVehicles().FirstOrDefault(item => GetInt(item, "ID") == vehicleId);
        if (vehicle == null)
        {
            return CheatExecutionResult.Fail("目标战车已不存在，请刷新战车列表后重试。", "VEHICLE_NOT_FOUND");
        }

        Component component = vehicle as Component
                              ?? throw new InvalidOperationException("目标战车不是有效的 Unity 组件。");
        JObject change = ModifyObjectAttribute(component, attributeId, value, isEnemy: false, receiver: null);
        return CheatExecutionResult.Changed(
            $"已修改战车 #{vehicleId} 的属性 {attributeId}。",
            new JObject { ["vehicleId"] = vehicleId, ["change"] = change });
    }

    public JObject QueryEnemies()
    {
        EnsureAvailable();
        JArray enemies = new();
        foreach (EnemyTarget target in SnapshotEnemyTargets())
        {
            enemies.Add(BuildEnemyState(target));
        }

        return new JObject { ["enemies"] = enemies, ["count"] = enemies.Count };
    }

    public CheatExecutionResult ModifyEnemy(JObject arguments)
    {
        EnsureAvailable();
        string runtimeId = RequiredText(arguments, "runtimeId", "必须选择有效的敌人运行时 ID。");
        string attributeId = RequiredText(arguments, "attributeId", "必须选择敌人属性。");
        double value = RequiredFiniteNumber(arguments, "value");
        EnemyTarget? target = SnapshotEnemyTargets()
            .FirstOrDefault(item => string.Equals(item.RuntimeId, runtimeId, StringComparison.Ordinal));
        if (target == null)
        {
            return CheatExecutionResult.Fail("目标敌人已死亡、回收或生命周期已变化，请刷新敌人列表后重试。", "ENEMY_NOT_FOUND");
        }

        JObject change = ModifyObjectAttribute(target.Ai, attributeId, value, isEnemy: true, target.Receiver);
        return CheatExecutionResult.Changed(
            $"已修改敌人 [{runtimeId}] 的属性 {attributeId}。",
            new JObject { ["runtimeId"] = runtimeId, ["change"] = change });
    }

    public CheatExecutionResult SpawnEnemy(JObject arguments)
    {
        EnsureAvailable();
        string enemyId = RequiredText(arguments, "enemyId", "必须选择怪物类型。");
        if (!IsSafeSpawnId(enemyId))
        {
            return CheatExecutionResult.Fail(
                "为避免破坏 Boss 波次和对象池账本，当前版本不允许生成 Boss、环境物、友军或特殊多节单位。",
                "UNSAFE_ENEMY_TYPE");
        }

        int count = BoundedInt(arguments, "count", 1, MaxSpawnCount, 1);
        int level = BoundedInt(arguments, "level", 1, 200, 1);
        float x = BoundedCoordinate(arguments, "x");
        float y = BoundedCoordinate(arguments, "y");
        float z = BoundedCoordinate(arguments, "z");
        object aiId = ParseEnum(_aiIdType!, enemyId, "怪物类型");
        if (!IsSafeConfiguredEnemy(aiId, out string configurationReason))
        {
            return CheatExecutionResult.Fail(configurationReason, "UNSAFE_ENEMY_CONFIGURATION");
        }

        object creator = GetRequiredSingleton(_agentCreatorType!, "AgentCreator");
        MethodInfo? create = FindMethod(
            creator.GetType(),
            "CreateAgent",
            _aiIdType!,
            typeof(Vector3),
            typeof(Quaternion),
            typeof(int),
            _battleGroupType!,
            typeof(bool),
            _basicAgentType!.MakeByRefType(),
            typeof(Action<GameObject>),
            _agentRegisterType!);
        if (create == null)
        {
            return CheatExecutionResult.Fail("当前游戏版本缺少指定位置生成怪物的入口。", "SPAWN_API_MISSING");
        }

        object battleGroup = ParseEnum(_battleGroupType!, "Enemy", "战斗阵营");
        object registerType = ParseEnum(_agentRegisterType!, "Enemy", "单位登记类型");
        object? wave = TryGetSingleton(_waveControllerType!);
        bool activeWave = wave != null && GetBool(wave, "m_isInWave");
        if (wave != null && GetBool(wave, "templateLock"))
        {
            return CheatExecutionResult.Fail("当前波次被模板锁定，不能额外生成怪物。", "WAVE_LOCKED");
        }

        if (activeWave && GetBool(wave!, "m_isBossWave"))
        {
            return CheatExecutionResult.Fail("Boss 波期间不允许额外生成怪物，以免破坏 Boss 结算账本。", "BOSS_WAVE_SPAWN_BLOCKED");
        }

        JArray created = new();

        for (int index = 0; index < count; index++)
        {
            object?[] invokeArgs =
            {
                aiId,
                new Vector3(x, y, z),
                Quaternion.identity,
                level,
                battleGroup,
                true,
                null,
                null,
                registerType
            };
            if (create.Invoke(creator, invokeArgs) is not bool success || !success || invokeArgs[6] is not Component spawned)
            {
                break;
            }

            Component? ai = _basicAiType == null ? null : spawned.GetComponent(_basicAiType);
            if (ai != null && !activeWave)
            {
                DisableEnemyDeathMessage(ai.gameObject);
            }
            else if (ai != null && wave != null && GetBool(ai, "SendsDeathMessage"))
            {
                FindMethod(wave.GetType(), "AddEnemyCount")?.Invoke(wave, null);
            }

            if (ai != null && TryBuildEnemyTarget(ai.gameObject, out EnemyTarget? target))
            {
                created.Add(new JObject
                {
                    ["runtimeId"] = target!.RuntimeId,
                    ["instanceId"] = ai.gameObject.GetInstanceID(),
                    ["enemyId"] = enemyId
                });
            }
        }

        JObject data = new()
        {
            ["requested"] = count,
            ["spawned"] = created.Count,
            ["enemyId"] = enemyId,
            ["position"] = VectorData(new Vector3(x, y, z)),
            ["enemies"] = created,
            ["countedInActiveWave"] = activeWave
        };
        string message = $"已在 ({x:0.##}, {y:0.##}, {z:0.##}) 生成 {created.Count}/{count} 个 {enemyId}。";
        if (created.Count == count) return CheatExecutionResult.Changed(message, data);
        return created.Count > 0
            ? CheatExecutionResult.Partial(message + " 其余生成请求被容量或配置拒绝。", data)
            : CheatExecutionResult.Fail("怪物生成失败，请确认当前对局、怪物配置和数量上限。", "SPAWN_FAILED");
    }

    public void DrawEnemyIds()
    {
        if (!EnemyIdsVisible) return;
        Camera camera = Camera.main;
        if (camera == null) return;

        _enemyIdStyle ??= new GUIStyle(GUI.skin.label)
        {
            alignment = TextAnchor.MiddleCenter,
            fontSize = 14,
            fontStyle = FontStyle.Bold
        };
        _enemyIdStyle.normal.textColor = Color.yellow;

        foreach (EnemyTarget target in SnapshotEnemyTargets())
        {
            Vector3 screen = camera.WorldToScreenPoint(target.GameObject.transform.position + (Vector3.up * 1.4f));
            if (screen.z <= 0f) continue;
            Rect rect = new(screen.x - 110f, Screen.height - screen.y - 14f, 220f, 28f);
            GUI.Label(rect, $"[{target.RuntimeId}] {target.TypeId}", _enemyIdStyle);
        }
    }

    public void ResetTransientFeatures()
    {
        EnemyIdsVisible = false;
        if (_baseReceiver != null)
        {
            try
            {
                FindMethod(_baseReceiver.GetType(), "RemoveGodModeSource", typeof(object))
                    ?.Invoke(_baseReceiver, new[] { _godModeSource });
            }
            catch
            {
                // The previous scene may already have destroyed its base object.
            }
        }

        _baseReceiver = null;
        BaseGodModeRequested = false;
    }

    private JObject BuildVehicleState(object vehicle)
    {
        JObject state = BuildVehicleReference(vehicle);
        Component component = vehicle as Component
                              ?? throw new InvalidOperationException("战车实例不是有效的 Unity 组件。");
        state["name"] = component.gameObject.name;
        state["active"] = component.gameObject.activeInHierarchy;
        state["position"] = VectorData(component.transform.position);
        state["attributes"] = BuildEditableAttributes(component, isEnemy: false, receiver: null);
        return state;
    }

    private JObject BuildVehicleReference(object vehicle)
    {
        Component? component = vehicle as Component;
        return new JObject
        {
            ["vehicleId"] = GetInt(vehicle, "ID"),
            ["instanceId"] = component?.GetInstanceID() ?? 0,
            ["typeId"] = GetMember(vehicle, "vehicleType")?.ToString() ?? string.Empty,
            ["level"] = GetInt(vehicle, "level")
        };
    }

    private JObject BuildEnemyState(EnemyTarget target)
    {
        Vector3 position = target.GameObject.transform.position;
        return new JObject
        {
            ["runtimeId"] = target.RuntimeId,
            ["instanceId"] = target.GameObject.GetInstanceID(),
            ["typeId"] = target.TypeId,
            ["typeValue"] = target.TypeValue,
            ["name"] = target.GameObject.name,
            ["health"] = target.Receiver == null ? null : GetNumber(target.Receiver, "Health"),
            ["healthMax"] = target.Receiver == null ? null : GetNumber(target.Receiver, "HealthMax"),
            ["isBoss"] = GetBool(target.Ai, "IsBoss"),
            ["position"] = VectorData(position),
            ["attributes"] = BuildEditableAttributes(target.Ai, isEnemy: true, target.Receiver)
        };
    }

    private JArray BuildEditableAttributes(Component owner, bool isEnemy, object? receiver)
    {
        JArray result = new();
        if (isEnemy && receiver != null)
        {
            result.Add(AttributeData("currentHealth", "当前生命值", "float", GetNumber(receiver, "Health"), GetNumber(receiver, "Health"), 0, MaxAttributeMagnitude));
        }

        object? battleSystem = owner.GetComponent(_battleSystemType!);
        if (battleSystem != null)
        {
            if (isEnemy && TryReadParameter(GetMember(battleSystem, "TimeScale"), out NumericParameter? speed))
            {
                result.Add(AttributeData("attackSpeed", "攻击速度", speed!.Kind, speed.Value, speed.BaseValue, 0, MaxAttributeMagnitude));
            }

            foreach (NumericParameter parameter in ReadBlackboardParameters(battleSystem))
            {
                double minimum = string.Equals(parameter.Key, "health", StringComparison.OrdinalIgnoreCase)
                    ? 1d
                    : -MaxAttributeMagnitude;
                result.Add(AttributeData(parameter.Key, parameter.Key, parameter.Kind, parameter.Value, parameter.BaseValue, minimum, MaxAttributeMagnitude));
            }
        }

        Vector3 position = owner.transform.position;
        result.Add(AttributeData("positionX", "位置 X", "float", position.x, position.x, -MaxCoordinateMagnitude, MaxCoordinateMagnitude));
        result.Add(AttributeData("positionY", "位置 Y", "float", position.y, position.y, -MaxCoordinateMagnitude, MaxCoordinateMagnitude));
        result.Add(AttributeData("positionZ", "位置 Z", "float", position.z, position.z, -MaxCoordinateMagnitude, MaxCoordinateMagnitude));
        return result;
    }

    private JObject ModifyObjectAttribute(Component owner, string attributeId, double value, bool isEnemy, object? receiver)
    {
        if (attributeId is "positionX" or "positionY" or "positionZ")
        {
            if (Math.Abs(value) > MaxCoordinateMagnitude)
            {
                throw new InvalidOperationException($"坐标必须在 {-MaxCoordinateMagnitude} 到 {MaxCoordinateMagnitude} 之间。");
            }

            Vector3 before = owner.transform.position;
            Vector3 after = before;
            if (attributeId == "positionX") after.x = (float)value;
            if (attributeId == "positionY") after.y = (float)value;
            if (attributeId == "positionZ") after.z = (float)value;
            owner.transform.position = after;
            return new JObject { ["attributeId"] = attributeId, ["before"] = VectorData(before), ["after"] = VectorData(after) };
        }

        if (isEnemy && attributeId == "currentHealth")
        {
            if (receiver == null) throw new InvalidOperationException("目标敌人没有生命组件。");
            double maximum = Math.Max(0d, GetNumber(receiver, "HealthMax"));
            double clamped = Math.Max(0d, Math.Min(value, maximum));
            double before = GetNumber(receiver, "Health");
            FindMethod(receiver.GetType(), "SetHealth", typeof(float))
                ?.Invoke(receiver, new object[] { (float)clamped });
            return ScalarChange(attributeId, before, GetNumber(receiver, "Health"));
        }

        object battleSystem = owner.GetComponent(_battleSystemType!)
                              ?? throw new InvalidOperationException("目标对象没有战斗属性黑板。");
        object? parameter;
        if (isEnemy && attributeId == "attackSpeed")
        {
            parameter = GetMember(battleSystem, "TimeScale");
        }
        else
        {
            if (!Enum.TryParse(_battleMemoryType!, attributeId, true, out _))
            {
                throw new InvalidOperationException("属性不在允许的 BattleMemoryEnum 白名单中。");
            }

            parameter = FindBlackboardParameter(battleSystem, attributeId);
        }

        if (!TryReadParameter(parameter, out NumericParameter? current))
        {
            throw new InvalidOperationException("目标对象当前没有可编辑的数值属性 " + attributeId + "。");
        }

        if (Math.Abs(value) > MaxAttributeMagnitude)
        {
            throw new InvalidOperationException("属性值超出允许范围。");
        }

        bool changesEnemyHealthMaximum = isEnemy
                                         && receiver != null
                                         && string.Equals(attributeId, "health", StringComparison.OrdinalIgnoreCase);
        if (changesEnemyHealthMaximum && value < 1d)
        {
            throw new InvalidOperationException("敌人生命上限必须大于或等于 1。");
        }

        double oldHealthMaxSetting = changesEnemyHealthMaximum
            ? GetNumber(receiver!, "HealthMaxSetting")
            : 0d;
        SetParameterBaseValue(parameter!, current!.Kind, value);
        if (!TryReadParameter(parameter, out NumericParameter? updated))
        {
            throw new InvalidOperationException("属性写入后无法回读验证。");
        }

        if (changesEnemyHealthMaximum)
        {
            double newHealthMaxSetting = GetNumber(receiver!, "HealthMaxSetting");
            ApplyEnemyHealthMax(receiver!, oldHealthMaxSetting, newHealthMaxSetting);
        }

        return new JObject
        {
            ["attributeId"] = attributeId,
            ["kind"] = updated!.Kind,
            ["before"] = current.Value,
            ["beforeBase"] = current.BaseValue,
            ["after"] = updated.Value,
            ["afterBase"] = updated.BaseValue
        };
    }

    private void ApplyEnemyHealthMax(object receiver, double oldMaximum, double newMaximum)
    {
        MethodInfo? method = receiver.GetType().GetMethod(
            "ApplyHealthMaxChanged",
            BindingFlags.Public | BindingFlags.Instance,
            null,
            new[] { typeof(float), typeof(float), _healthMaxSyncModeType! },
            null);
        if (method == null) return;
        object mode = ParseEnum(_healthMaxSyncModeType!, "KeepCurrentRatio", "生命上限同步模式");
        method.Invoke(receiver, new object[] { (float)oldMaximum, (float)Math.Max(0d, newMaximum), mode });
    }

    private List<NumericParameter> ReadBlackboardParameters(object battleSystem)
    {
        object? memory = GetMember(battleSystem, "memoryBlackboard");
        if (memory == null) return new List<NumericParameter>();
        MethodInfo? getAll = FindMethod(memory.GetType(), "GetAllValue");
        if (getAll?.Invoke(memory, null) is not IDictionary values) return new List<NumericParameter>();

        List<NumericParameter> result = new();
        foreach (DictionaryEntry entry in values)
        {
            string key = entry.Key?.ToString() ?? string.Empty;
            if (string.IsNullOrWhiteSpace(key) || !Enum.TryParse(_battleMemoryType!, key, true, out _)) continue;
            if (!TryReadParameter(entry.Value, out NumericParameter? parameter)) continue;
            parameter!.Key = key;
            result.Add(parameter);
        }

        result.Sort((left, right) => string.Compare(left.Key, right.Key, StringComparison.OrdinalIgnoreCase));
        return result;
    }

    private object? FindBlackboardParameter(object battleSystem, string key)
    {
        object? memory = GetMember(battleSystem, "memoryBlackboard");
        MethodInfo? getAll = memory == null ? null : FindMethod(memory.GetType(), "GetAllValue");
        if (getAll?.Invoke(memory, null) is not IDictionary values) return null;
        foreach (DictionaryEntry entry in values)
        {
            if (string.Equals(entry.Key?.ToString(), key, StringComparison.OrdinalIgnoreCase)) return entry.Value;
        }

        return null;
    }

    private static bool TryReadParameter(object? parameter, out NumericParameter? result)
    {
        result = null;
        if (parameter == null) return false;
        string typeName = parameter.GetType().Name;
        if (typeName is not ("GeneralFloatParameter" or "GeneralIntParameter")) return false;
        object? effective = GetMember(parameter, "Value");
        object? baseValue = FindMethod(parameter.GetType(), "GetRealValue")?.Invoke(parameter, null);
        if (effective == null || baseValue == null) return false;
        result = new NumericParameter
        {
            Kind = typeName == "GeneralIntParameter" ? "integer" : "float",
            Value = Convert.ToDouble(effective, CultureInfo.InvariantCulture),
            BaseValue = Convert.ToDouble(baseValue, CultureInfo.InvariantCulture)
        };
        return true;
    }

    private static void SetParameterBaseValue(object parameter, string kind, double value)
    {
        if (kind == "integer")
        {
            int integer = checked((int)Math.Round(value, MidpointRounding.AwayFromZero));
            FindMethod(parameter.GetType(), "SetValue", typeof(int))
                ?.Invoke(parameter, new object[] { integer });
            return;
        }

        FindMethod(parameter.GetType(), "SetValue", typeof(float))
            ?.Invoke(parameter, new object[] { (float)value });
    }

    private List<object> SnapshotVehicles()
    {
        object? manager = TryGetSingleton(_vehicleManagerType!);
        if (manager == null || GetMember(manager, "MainVehicles") is not IEnumerable source) return new List<object>();
        List<object> result = new();
        foreach (object? vehicle in source)
        {
            if (vehicle is Component component && component != null)
            {
                result.Add(vehicle);
            }
        }

        return result;
    }

    private List<EnemyTarget> SnapshotEnemyTargets()
    {
        List<EnemyTarget> result = new();
        foreach (GameObject gameObject in SnapshotEnemyGameObjects())
        {
            if (TryBuildEnemyTarget(gameObject, out EnemyTarget? target)) result.Add(target!);
        }

        return result;
    }

    private List<GameObject> SnapshotEnemyGameObjects()
    {
        object? creator = TryGetSingleton(_agentCreatorType!);
        if (creator == null || GetMember(creator, "enemyAgents") is not IEnumerable source)
        {
            return new List<GameObject>();
        }

        List<GameObject> result = new();
        foreach (object? item in source)
        {
            if (item is GameObject gameObject && gameObject != null) result.Add(gameObject);
        }

        return result;
    }

    private bool TryBuildEnemyTarget(GameObject gameObject, out EnemyTarget? target)
    {
        target = null;
        if (gameObject == null || !gameObject.activeInHierarchy || _basicAiType == null) return false;
        Component? ai = gameObject.GetComponent(_basicAiType);
        if (ai == null) return false;
        object? receiver = GetMember(ai, "DamageReceiver");
        if (receiver != null && GetBool(receiver, "IsDie")) return false;
        object? battleSystem = GetMember(ai, "BattleSystem") ?? ai.GetComponent(_battleSystemType!);
        object? handle = battleSystem == null ? null : GetMember(battleSystem, "RuntimeHandle");
        if (handle == null || GetBool(handle, "IsDisposed")) return false;
        int handleId = GetInt(handle, "Id");
        int lifetime = GetInt(handle, "LifetimeVersion");
        if (handleId <= 0 || lifetime <= 0) return false;
        object? type = GetMember(ai, "aiID") ?? GetMember(ai, "Id");
        target = new EnemyTarget
        {
            GameObject = gameObject,
            Ai = ai,
            Receiver = receiver,
            RuntimeId = handleId + ":" + lifetime,
            TypeId = type?.ToString() ?? string.Empty,
            TypeValue = type == null ? 0 : Convert.ToInt32(type, CultureInfo.InvariantCulture)
        };
        return true;
    }

    private void DisableEnemyDeathMessage(GameObject gameObject)
    {
        if (_basicAiType == null || gameObject == null) return;
        Component? ai = gameObject.GetComponent(_basicAiType);
        FindMethod(ai?.GetType(), "SetSendMessage", typeof(bool))?.Invoke(ai, new object[] { false });
    }

    private object ResolveBaseReceiver()
    {
        object controller = GetRequiredSingleton(_gameControllerType!, "GameController");
        object? mainBase = GetMember(controller, "MainBase") ?? GetMember(controller, "mainStation");
        object? receiver = mainBase == null
            ? null
            : GetMember(mainBase, "mainStationDamageReceiver") ?? GetMember(mainBase, "DamageReceiver");
        return receiver ?? throw new InvalidOperationException("当前场景的主基地尚未初始化。");
    }

    private Type? Require(string typeName)
    {
        Type? type = FindType(typeName);
        if (type == null) AddMissing(typeName);
        return type;
    }

    private void ValidateRuntimeContract()
    {
        RequireSingletonAccessor(_vehicleManagerType);
        RequireMember(_vehicleManagerType, "MainVehicles");
        RequireMethodContract(_vehicleManagerType, "GetNewMainRazor", _vehicleType);
        RequireMethodContract(_vehicleManagerType, "GetCustomNewMainRazor", _vehicleType, _fetterType, typeof(int));
        RequireMember(_vehicleControllerType, "ID");
        RequireMember(_vehicleControllerType, "vehicleType");
        RequireMember(_vehicleControllerType, "level");
        RequireSingletonAccessor(_gameConfigManagerType);
        RequireMember(_gameConfigManagerType, "gameConfig");
        RequireMember(_gameSoConfigType, "mainRazorTypeSo");
        RequireMember(_mainRazorTypeSoType, "mainRazorComponents");
        RequireSingletonAccessor(_fetterInfoCfgType);
        RequireMethodContract(_fetterInfoCfgType, "GetAllBasicFetter");

        RequireSingletonAccessor(_disposableManagerType);
        RequireMethodContract(_disposableManagerType, "TryGetDisposable", _disposableType);
        RequireSingletonAccessor(_superModuleManagerType);
        RequireMember(_superModuleManagerType, "superModules");
        RequireMethodContract(_superModuleManagerType, "GetSuperModule", _superModuleType, typeof(bool));
        RequireSingletonAccessor(_rewardManagerType);
        RequireMember(_rewardManagerType, "AllDisposableRewards");
        RequireMember(_rewardManagerType, "AllSuperModuleRewards");
        RequireMethodContract(_rewardManagerType, "UpdateAllRewards");
        RequireMember(_potionRewardType, "disposableEnum");
        RequireMember(_superModuleRewardType, "superModuleEnum");

        RequireSingletonAccessor(_gameControllerType);
        RequireMember(_gameControllerType, "MainBase");
        RequireMember(_mainStationType, "mainStationDamageReceiver");
        RequireMethodContract(_damageReceiverType, "AddGodModeSource", typeof(object));
        RequireMethodContract(_damageReceiverType, "RemoveGodModeSource", typeof(object));
        RequireMethodContract(_damageReceiverType, "SetHealth", typeof(float));
        RequireMethodContract(
            _damageReceiverType,
            "ApplyHealthMaxChanged",
            typeof(float),
            typeof(float),
            _healthMaxSyncModeType);
        RequireMember(_damageReceiverType, "GodMode");
        RequireMember(_damageReceiverType, "Health");
        RequireMember(_damageReceiverType, "HealthMax");
        RequireMember(_damageReceiverType, "HealthMaxSetting");
        RequireMember(_damageReceiverType, "IsDie");

        RequireSingletonAccessor(_waveControllerType);
        RequireMember(_waveControllerType, "templateLock");
        RequireMember(_waveControllerType, "m_isInWave");
        RequireMember(_waveControllerType, "m_isBossWave");
        RequireMethodContract(_waveControllerType, "WaveOver");
        RequireMethodContract(_waveControllerType, "AddEnemyCount");

        RequireSingletonAccessor(_agentCreatorType);
        RequireMember(_agentCreatorType, "enemyAgents");
        RequireMethodContract(_agentCreatorType, "ClearDeferredSpawnQueue");
        RequireMethodContract(_agentCreatorType, "ClearAllEnemy");
        RequireMethodContract(
            _agentCreatorType,
            "CreateAgent",
            _aiIdType,
            typeof(Vector3),
            typeof(Quaternion),
            typeof(int),
            _battleGroupType,
            typeof(bool),
            _basicAgentType?.MakeByRefType(),
            typeof(Action<GameObject>),
            _agentRegisterType);

        RequireSingletonAccessor(_enemyConfigType);
        RequireMember(_enemyConfigType, "datas");
        RequireMember(_basicAiType, "aiID");
        RequireMember(_basicAiType, "IsBoss");
        RequireMember(_basicAiType, "SendsDeathMessage");
        RequireMember(_basicAiType, "DamageReceiver");
        RequireMember(_basicAiType, "BattleSystem");
        RequireMethodContract(_basicAiType, "SetSendMessage", typeof(bool));

        RequireMember(_battleSystemType, "memoryBlackboard");
        RequireMember(_battleSystemType, "TimeScale");
        RequireMember(_battleSystemType, "RuntimeHandle");
        RequireMember(_battleRuntimeHandleType, "Id");
        RequireMember(_battleRuntimeHandleType, "LifetimeVersion");
        RequireMember(_battleRuntimeHandleType, "IsDisposed");
        RequireMethodContract(_blackboardMemoryType, "GetAllValue");
        RequireMember(_generalFloatParameterType, "Value");
        RequireMethodContract(_generalFloatParameterType, "GetRealValue");
        RequireMethodContract(_generalFloatParameterType, "SetValue", typeof(float));
        RequireMember(_generalIntParameterType, "Value");
        RequireMethodContract(_generalIntParameterType, "GetRealValue");
        RequireMethodContract(_generalIntParameterType, "SetValue", typeof(int));
    }

    private void RequireSingletonAccessor(Type? type)
    {
        if (type == null) return;
        const BindingFlags flags = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static | BindingFlags.FlattenHierarchy;
        if (type.GetProperty("Instance", flags) != null
            || type.GetField("Instance", flags) != null
            || type.GetField("instance", flags) != null)
        {
            return;
        }

        AddMissing((type.FullName ?? type.Name) + ".Instance");
    }

    private void RequireMember(Type? type, string name)
    {
        if (type == null) return;
        const BindingFlags flags = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.FlattenHierarchy;
        if (type.GetProperty(name, flags) != null || type.GetField(name, flags) != null) return;
        AddMissing((type.FullName ?? type.Name) + "." + name);
    }

    private void RequireMethodContract(Type? type, string name, params Type?[] parameterTypes)
    {
        if (type == null || parameterTypes.Any(parameter => parameter == null)) return;
        Type[] parameters = parameterTypes.Select(parameter => parameter!).ToArray();
        if (FindMethod(type, name, parameters) != null) return;
        string signature = string.Join(",", parameters.Select(parameter => parameter.Name));
        AddMissing($"{type.FullName ?? type.Name}.{name}({signature})");
    }

    private void AddMissing(string member)
    {
        if (!_missingMembers.Contains(member, StringComparer.Ordinal)) _missingMembers.Add(member);
    }

    private static Type? FindType(string fullName)
    {
        foreach (Assembly assembly in AppDomain.CurrentDomain.GetAssemblies())
        {
            Type? exact = assembly.GetType(fullName, false);
            if (exact != null) return exact;
        }

        if (fullName.Contains('.')) return null;
        foreach (Assembly assembly in AppDomain.CurrentDomain.GetAssemblies())
        {
            try
            {
                Type? match = assembly.GetTypes().FirstOrDefault(type => type.Name == fullName);
                if (match != null) return match;
            }
            catch (ReflectionTypeLoadException exception)
            {
                Type? match = exception.Types.FirstOrDefault(type => type?.Name == fullName);
                if (match != null) return match;
            }
        }

        return null;
    }

    private static object GetRequiredSingleton(Type type, string displayName) =>
        TryGetSingleton(type) ?? throw new InvalidOperationException(displayName + " 尚未初始化，请进入对局场景后重试。");

    private static object? TryGetSingleton(Type type)
    {
        PropertyInfo? property = type.GetProperty("Instance", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static | BindingFlags.FlattenHierarchy);
        if (property != null) return property.GetValue(null, null);
        FieldInfo? field = type.GetField("Instance", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static | BindingFlags.FlattenHierarchy)
                           ?? type.GetField("instance", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static | BindingFlags.FlattenHierarchy);
        return field?.GetValue(null);
    }

    private static object? GetMember(object? target, string name)
    {
        if (target == null) return null;
        Type type = target.GetType();
        PropertyInfo? property = type.GetProperty(name, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.FlattenHierarchy);
        if (property != null) return property.GetValue(target, null);
        FieldInfo? field = type.GetField(name, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.FlattenHierarchy);
        return field?.GetValue(target);
    }

    private static MethodInfo? FindMethod(Type? type, string name, params Type[] parameters) =>
        type?.GetMethod(
            name,
            BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static | BindingFlags.FlattenHierarchy,
            null,
            parameters,
            null);

    private static object ParseEnum(Type type, string value, string displayName)
    {
        try
        {
            return Enum.Parse(type, value, true);
        }
        catch
        {
            throw new InvalidOperationException($"无法识别{displayName}：{value}。");
        }
    }

    private IReadOnlyList<object> ConfiguredVehicleValues()
    {
        object manager = GetRequiredSingleton(_gameConfigManagerType!, "GameConfigManager");
        object? gameConfig = GetMember(manager, "gameConfig");
        object? vehicleConfig = GetMember(gameConfig, "mainRazorTypeSo");
        if (GetMember(vehicleConfig, "mainRazorComponents") is not IDictionary entries)
        {
            throw new InvalidOperationException("战车预制体配置尚未初始化。");
        }

        List<object> result = new();
        foreach (DictionaryEntry entry in entries)
        {
            if (entry.Key == null || entry.Key.GetType() != _vehicleType) continue;
            if (entry.Value == null) continue;
            if (entry.Value is UnityEngine.Object unityObject && unityObject == null) continue;
            string id = entry.Key.ToString() ?? string.Empty;
            if (id == "None" || id == "Train_Head") continue;
            result.Add(entry.Key);
        }

        return DistinctEnumValues(result);
    }

    private IReadOnlyList<object> ConfiguredEnchantmentValues()
    {
        object configuration = GetRequiredSingleton(_fetterInfoCfgType!, "SO_FetterInfoCfg");
        MethodInfo method = FindMethod(configuration.GetType(), "GetAllBasicFetter")
                            ?? throw new InvalidOperationException("基础附魔配置入口不可用。");
        if (method.Invoke(configuration, null) is not IEnumerable values)
        {
            throw new InvalidOperationException("基础附魔配置尚未初始化。");
        }

        List<object> result = new();
        foreach (object? value in values)
        {
            if (value == null || value.GetType() != _fetterType || string.Equals(value.ToString(), "None", StringComparison.Ordinal)) continue;
            result.Add(value);
        }

        return DistinctEnumValues(result);
    }

    private IReadOnlyList<object> ConfiguredRewardValues(string collectionName, string enumMember, Type enumType)
    {
        object manager = GetRequiredSingleton(_rewardManagerType!, "RewardManager");
        object? source = GetMember(manager, collectionName);
        if (source == null)
        {
            FindMethod(manager.GetType(), "UpdateAllRewards")?.Invoke(manager, null);
            source = GetMember(manager, collectionName);
        }

        if (source is not IEnumerable rewards)
        {
            throw new InvalidOperationException("奖励目录尚未初始化：" + collectionName + "。");
        }

        List<object> result = new();
        foreach (object? reward in rewards)
        {
            if (reward == null) continue;
            if (reward is UnityEngine.Object unityObject && unityObject == null) continue;
            object? value = GetMember(reward, enumMember);
            if (value == null || value.GetType() != enumType || string.Equals(value.ToString(), "None", StringComparison.Ordinal)) continue;
            result.Add(value);
        }

        return DistinctEnumValues(result);
    }

    private static IReadOnlyList<object> DistinctEnumValues(IEnumerable<object> values)
    {
        List<object> result = new();
        HashSet<long> seen = new();
        foreach (object value in values.OrderBy(value => Convert.ToInt64(value, CultureInfo.InvariantCulture)))
        {
            long numeric = Convert.ToInt64(value, CultureInfo.InvariantCulture);
            if (seen.Add(numeric)) result.Add(value);
        }

        return result;
    }

    private static bool ContainsEnumValue(IEnumerable<object> values, object expected) =>
        values.Any(value => Equals(value, expected));

    private static JArray EnumItems(IEnumerable<object> values)
    {
        JArray result = new();
        foreach (object value in values)
        {
            string id = value.ToString() ?? string.Empty;
            result.Add(new JObject
            {
                ["id"] = id,
                ["name"] = id,
                ["value"] = Convert.ToInt64(value, CultureInfo.InvariantCulture)
            });
        }

        return result;
    }

    private JArray SafeEnemyItems()
    {
        JArray result = new();
        foreach (object value in Enum.GetValues(_aiIdType!))
        {
            string id = value.ToString() ?? string.Empty;
            if (!IsSafeSpawnId(id) || !IsSafeConfiguredEnemy(value, out _)) continue;
            result.Add(new JObject
            {
                ["id"] = id,
                ["name"] = id,
                ["value"] = Convert.ToInt64(value, CultureInfo.InvariantCulture)
            });
        }

        return result;
    }

    private bool IsSafeConfiguredEnemy(object aiId, out string reason)
    {
        string id = aiId.ToString() ?? string.Empty;
        if (!IsSafeSpawnId(id))
        {
            reason = "为避免破坏特殊单位或波次账本，不能生成该怪物类型：" + id + "。";
            return false;
        }

        object? configuration;
        try
        {
            configuration = TryGetSingleton(_enemyConfigType!);
        }
        catch
        {
            configuration = null;
        }

        object? entries = configuration == null ? null : GetMember(GetMember(configuration, "datas"), "Dic");
        if (entries is not IDictionary dictionary || !dictionary.Contains(aiId))
        {
            reason = "当前游戏配置中没有该怪物的有效预制体：" + id + "。";
            return false;
        }

        object? entry = dictionary[aiId];
        GameObject? prefab = GetMember(entry, "prefab") as GameObject;
        Component? ai = prefab == null ? null : prefab.GetComponent(_basicAiType!);
        if (ai == null)
        {
            reason = "该配置不是可安全生成的 BasicAI 怪物：" + id + "。";
            return false;
        }

        if (GetBool(ai, "IsBoss"))
        {
            reason = "当前版本不允许生成 Boss，以免破坏 Boss 结算账本：" + id + "。";
            return false;
        }

        reason = string.Empty;
        return true;
    }

    private static bool IsSafeSpawnId(string id)
    {
        if (string.IsNullOrWhiteSpace(id)) return false;
        string value = id.ToLowerInvariant();
        return !value.Contains("boss")
               && !value.Contains("multisegmentworm")
               && !value.StartsWith("friendly_", StringComparison.Ordinal)
               && !value.StartsWith("environmentalobject_", StringComparison.Ordinal)
               && !value.StartsWith("deadenemy_", StringComparison.Ordinal);
    }

    private static string RequiredText(JObject arguments, string name, string error)
    {
        string value = arguments.Value<string>(name)?.Trim() ?? string.Empty;
        if (string.IsNullOrWhiteSpace(value)) throw new InvalidOperationException(error);
        return value;
    }

    private static int BoundedInt(JObject arguments, string name, int minimum, int maximum, int fallback)
    {
        int value = arguments.Value<int?>(name) ?? fallback;
        if (value < minimum || value > maximum)
        {
            throw new InvalidOperationException($"{name} 必须在 {minimum} 到 {maximum} 之间。");
        }

        return value;
    }

    private static double RequiredFiniteNumber(JObject arguments, string name)
    {
        double? value = arguments.Value<double?>(name);
        if (!value.HasValue || double.IsNaN(value.Value) || double.IsInfinity(value.Value))
        {
            throw new InvalidOperationException(name + " 必须是有限数字。");
        }

        return value.Value;
    }

    private static float BoundedCoordinate(JObject arguments, string name)
    {
        double value = RequiredFiniteNumber(arguments, name);
        if (Math.Abs(value) > MaxCoordinateMagnitude)
        {
            throw new InvalidOperationException($"坐标 {name} 必须在 {-MaxCoordinateMagnitude} 到 {MaxCoordinateMagnitude} 之间。");
        }

        return (float)value;
    }

    private static int GetInt(object target, string name)
    {
        object? value = GetMember(target, name);
        return value == null ? 0 : Convert.ToInt32(value, CultureInfo.InvariantCulture);
    }

    private static bool GetBool(object target, string name) => GetMember(target, name) is bool value && value;

    private static int GetDictionaryListCount(object? source, object key)
    {
        if (source is not IDictionary dictionary || !dictionary.Contains(key)) return 0;
        return dictionary[key] is ICollection collection ? collection.Count : 0;
    }

    private static double GetNumber(object target, string name)
    {
        object? value = GetMember(target, name);
        return value == null ? 0d : Convert.ToDouble(value, CultureInfo.InvariantCulture);
    }

    private static JObject VectorData(Vector3 value) => new()
    {
        ["x"] = value.x,
        ["y"] = value.y,
        ["z"] = value.z
    };

    private static JObject AttributeData(
        string id,
        string name,
        string kind,
        double value,
        double baseValue,
        double minimum,
        double maximum) => new()
    {
        ["id"] = id,
        ["name"] = name,
        ["kind"] = kind,
        ["value"] = value,
        ["baseValue"] = baseValue,
        ["minimum"] = minimum,
        ["maximum"] = maximum
    };

    private static JObject ScalarChange(string id, double before, double after) => new()
    {
        ["attributeId"] = id,
        ["before"] = before,
        ["after"] = after
    };

    private void EnsureAvailable()
    {
        if (!IsAvailable)
        {
            throw new InvalidOperationException("作弊运行时不可用：" + string.Join("、", _missingMembers));
        }
    }

    internal static Exception Unwrap(Exception exception) =>
        exception is TargetInvocationException target && target.InnerException != null
            ? target.InnerException
            : exception;

    private sealed class NumericParameter
    {
        public string Key { get; set; } = string.Empty;
        public string Kind { get; set; } = "float";
        public double Value { get; set; }
        public double BaseValue { get; set; }
    }

    private sealed class EnemyTarget
    {
        public GameObject GameObject { get; set; } = null!;
        public Component Ai { get; set; } = null!;
        public object? Receiver { get; set; }
        public string RuntimeId { get; set; } = string.Empty;
        public string TypeId { get; set; } = string.Empty;
        public int TypeValue { get; set; }
    }
}
