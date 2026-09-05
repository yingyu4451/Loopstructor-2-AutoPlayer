using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Security.Cryptography;
using Loopstructor.AutoPlayer.Core;
using Newtonsoft.Json.Linq;
using UnityEngine;

namespace Loopstructor.AutoPlayer.Plugin;

internal sealed class CheatRuntimeBridge
{
    private const double MaxAttributeMagnitude = 1_000_000_000d;
    private const float MaxCoordinateMagnitude = 10_000f;
    private const int MaxGrantCount = 20;
    private const int MaxGrantAllRelicsPerFrame = 1;
    private const int MaxRemoveAllRelicsPerFrame = 1;
    private const int MaxSpawnCount = 10;
    private const int MaxSpawnPointCount = 12;
    private const int MaxTotalSpawnCount = 50;
    private const float DefaultSpawnRadius = 6f;
    private const float MaxSpawnRadius = 50f;
    private const float MinimumSpawnSpacing = 1.5f;
    private const int SpawnPositionAttemptCount = 24;
    private const int CatalogIconSize = 48;
    private const float EnemyOverlayRefreshInterval = 0.5f;
    private const float EnemyBuffIconSize = 30f;
    private const float EnemyBuffCellWidth = 40f;
    private const float EnemyBuffCellHeight = 58f;
    private const int MaxEnemyBuffColumns = 8;
    private static readonly TimeSpan SpawnPointCaptureTimeout = TimeSpan.FromMinutes(2);
    private static readonly IReadOnlyDictionary<string, string> VehicleTypeChineseNames =
        new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["Shell"] = "炮弹",
            ["Link"] = "连锁",
            ["Penetrate"] = "穿透",
            ["Missile"] = "导弹"
        };
    private static readonly IReadOnlyDictionary<string, string> BattleAttributeFallbackNames =
        new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["transform"] = "变换组件",
            ["razorCoreTransform"] = "战车核心变换组件",
            ["damageAmplify"] = "伤害增幅",
            ["vehicleRadius"] = "战车碰撞范围",
            ["vehicleCollisionDamage"] = "战车碰撞伤害",
            ["vehicleCuttingInterval"] = "战车切割间隔",
            ["hitLinePointPosition"] = "命中线点位置",
            ["attackRange"] = "攻击范围",
            ["shootingInterval"] = "攻击频率",
            ["attackCount"] = "攻击次数",
            ["barrageHitDamage"] = "弹幕命中伤害",
            ["barrageMovementDistance"] = "弹幕飞行距离",
            ["barragePenetrationDamage"] = "穿透弹丸基础伤害",
            ["bulletTouchDamageInterval"] = "弹丸接触伤害间隔",
            ["shootingBulletTrackSpeed"] = "射击弹丸追踪速度",
            ["explosionDamage"] = "爆炸伤害",
            ["explodeRadius"] = "爆炸范围",
            ["poisonDamage"] = "中毒伤害",
            ["poisonInterval"] = "腐化伤害间隔",
            ["freezeDuration"] = "冻结持续时间",
            ["freezeRadius"] = "冰冻范围",
            ["poisonContinueTime"] = "腐化持续时间",
            ["pursuitsCount"] = "追击次数",
            ["pursuitsDamageRatio"] = "追击伤害倍率",
            ["vehicleMaximumSpeed"] = "战车最大速度",
            ["vehicleMinSpeed"] = "炮塔最小速度",
            ["vehicleMaxSpeedTime"] = "炮塔最大速度持续时间",
            ["vehicleSpeedFade"] = "炮塔速度衰减",
            ["linePoint"] = "线路点",
            ["uavBehaviourInterval"] = "旋翼机行为间隔",
            ["agentContinueTime"] = "旋翼机持续时间",
            ["uavLevel"] = "旋翼机召唤等级",
            ["agentCount"] = "旋翼机召唤数量",
            ["generalTarget"] = "通用目标",
            ["generalAttackDamage"] = "通用攻击伤害",
            ["generalBuffContinueTime"] = "通用增益持续时间",
            ["chooseTargetCount"] = "目标个数",
            ["criticalRate"] = "暴击率",
            ["criticalDamageAddRate"] = "暴击伤害加成",
            ["linkDamage"] = "基础伤害",
            ["linkCount"] = "弹射次数",
            ["linkRadius"] = "弹射范围",
            ["projectileDamage"] = "投射物伤害",
            ["projectileRadius"] = "投射物半径",
            ["shockWaveDamage"] = "冲击波伤害",
            ["shockWaveRadius"] = "冲击波范围",
            ["siteContinueTime"] = "余震持续时间",
            ["siteDamageInterval"] = "余震伤害间隔",
            ["siteDamage"] = "余震伤害",
            ["siteRadius"] = "余震半径",
            ["slowContinueTime"] = "减速时间",
            ["slowRate"] = "减速比例",
            ["hitCount"] = "命中次数",
            ["damageElementType"] = "伤害元素类型",
            ["damageEffectVFX"] = "伤害特效",
            ["extraDamageAddRate"] = "额外伤害加成",
            ["generalRadius"] = "通用范围",
            ["superModuleBanned"] = "禁用超级模块",
            ["driverCount"] = "驱动器数量",
            ["meleeDamage"] = "近战伤害",
            ["remoteDamage"] = "远程伤害",
            ["damageInterval"] = "伤害间隔",
            ["physicalIntensity"] = "物理强度",
            ["behaviourInterval"] = "行为间隔",
            ["health"] = "生命值",
            ["moveSpeed"] = "移动速度",
            ["behaviourSpeed"] = "行为速度",
            ["objectSize"] = "物体尺寸",
            ["cureAmount"] = "治疗量",
            ["changedTime"] = "变化时间",
            ["bulletCount"] = "弹丸数量",
            ["generalTargetList"] = "通用目标列表",
            ["supportContinueTime"] = "支援持续时间",
            ["generalContinueTime"] = "通用持续时间",
            ["killExp"] = "击杀经验",
            ["fusionLevel"] = "融合等级",
            ["spawnMax"] = "最大生成数量",
            ["splitRate"] = "分裂概率",
            ["extraAttackCount"] = "额外攻击次数",
            ["TornadoDamage"] = "空间坍缩伤害",
            ["TornadoRadius"] = "空间坍缩范围",
            ["TornadoMoveRate"] = "空间坍缩移动速率",
            ["TornadoDuration"] = "空间坍缩持续时间",
            ["SiteDamgeRate"] = "持续攻击区域伤害倍率",
            ["PoisonStackAdd"] = "腐化层数",
            ["BurnStackAdd"] = "灼烧层数",
            ["BurnContinueTime"] = "灼烧持续时间",
            ["BurnInterval"] = "灼烧伤害间隔",
            ["damageExtraAddRate"] = "非基础伤害暴击率",
            ["damageExtraAddAmountRate"] = "非基础伤害暴击伤害加成",
            ["freezeDamage"] = "冰冻伤害",
            ["freezeVulnerabilityRate"] = "冰冻易伤比例",
            ["slowDizzRate"] = "减速晕眩概率",
            ["slowDizzContinueTime"] = "减速晕眩持续时间",
            ["PassPoisonStackAdd"] = "传染腐化层数",
            ["agentTenacity"] = "AI 韧性",
            ["fixedTargetRadius"] = "固定目标半径",
            ["TornadoInterval"] = "空间坍缩触发间隔",
            ["KillMoney_AddMoneyNum"] = "击杀金币额外金币数",
            ["KillMoney_BoxMoneyNum"] = "击杀金币宝箱金币数",
            ["knifeDamageInterval"] = "刀片伤害间隔",
            ["knifeDamage"] = "刀片伤害",
            ["knifeRadius"] = "刀片范围",
            ["conductiveInterval"] = "导电触发间隔",
            ["conductiveStackCost"] = "导电消耗层数",
            ["BuffCountExplode_StackLimit"] = "不稳定化合物层数上限",
            ["BuffCountFreeze_StackLimit"] = "急冻层数上限",
            ["KillCountSpeed_KillLimit"] = "追击击杀触发阈值",
            ["KillCountBlood_KillLimit"] = "血池击杀触发阈值",
            ["conductiveContinueTime"] = "导电持续时间",
            ["BuffCountExplode_unstableContinueTime"] = "不稳定化合物持续时间",
            ["BuffCountFreeze_ContinueTime"] = "急冻持续时间",
            ["KillCountSpeed_ContinueTime"] = "追击持续时间",
            ["HitBlood_ContinueTime"] = "血契持续时间",
            ["bloodPoolContinueTime"] = "血池持续时间",
            ["CollisionCountLinkCount_HitLimit"] = "弹簧碰撞触发阈值",
            ["CollisionCountSplit_HitLimit"] = "蓄能碰撞触发阈值",
            ["HitCountSplit_KillLimit"] = "裂变击杀触发阈值",
            ["HitCountLinkCount_KillLimit"] = "跳弹击杀触发阈值",
            ["HitCountFreeze_HitLimit"] = "液氮命中触发阈值",
            ["HitCountRange_HitLimit"] = "制导命中触发阈值",
            ["HitCountRange_ContinueTime"] = "制导持续时间",
            ["Electricity_linkDamage"] = "导电连锁伤害",
            ["Electricity_linkRadius"] = "导电连锁范围",
            ["Electricity_linkCount"] = "导电连锁次数",
            ["damageAddPlus_HitBlood"] = "血契伤害加成",
            ["HitBlood_HitLimit"] = "血契命中触发阈值",
            ["BuffCountExplode_ExplosionDamage"] = "不稳定化合物爆炸伤害",
            ["BuffCountExplode_ExplodeRadius"] = "不稳定化合物爆炸范围",
            ["KillCountBlood_damageAddPlus"] = "血池伤害加成",
            ["HitCountFreeze_FreezeContinueTime"] = "液氮冰冻持续时间",
            ["BuffCountFreeze_FreezeDuration"] = "急冻冰冻时间",
            ["CollisionCountLinkCount_LinkCount"] = "弹簧连锁次数",
            ["CollisionCountSplit_SplitCount"] = "蓄能分裂数量",
            ["HitCountLinkCount_LinkCount"] = "跳弹连锁次数",
            ["HitBlood_AddStackCount"] = "血契增加层数",
            ["AddPoisonInterval"] = "周期施加腐化间隔",
            ["AddPoisonIntervalStackAdd"] = "周期施加腐化层数",
            ["AddPoisonIntervalContinueTime"] = "周期施加腐化持续时间",
            ["verticalOffers"] = "垂直偏移",
            ["TargetCount"] = "目标数量",
            ["multiShootInterval"] = "多重射击间隔",
            ["fanAngleParam"] = "扇形角度",
            ["bothSidesBulletCount"] = "两侧弹丸数量",
            ["burstHitCount"] = "散射触发命中数",
            ["burstbothSidesAddCount"] = "散射两侧增加弹丸数",
            ["KillCountPoint_TriggerCountNum"] = "考古击杀触发数",
            ["KillCountPoint_SpawnRectMInLength"] = "考古生成区域最小边长",
            ["KillCountPoint_SpawnRectMaxLength"] = "考古生成区域最大边长",
            ["KillMoney_Possibility"] = "铸币触发概率",
            ["KillCountBlood_AddStackCount"] = "血池每次增加层数",
            ["crackArmorVulnerabilityContinueTime"] = "裂甲易伤持续时间",
            ["crackArmorVulnerabilityRate"] = "裂甲易伤比例",
            ["crackArmorLevel3ExtraFixedDamage"] = "裂甲 3 级额外固定伤害",
            ["crackArmorLevel7ExtraFixedDamage"] = "裂甲 7 级额外固定伤害",
            ["vulnerabilityExtraFixedDamage"] = "易伤额外固定伤害",
            ["AddBuffCountFreezeInterval"] = "急冻叠层间隔",
            ["BuffCountFreeze_StackCostRate"] = "急冻层数消耗比例",
            ["BuffCountFreeze_AddStackCount"] = "急冻增加层数",
            ["BuffCountFreeze_AddStackCount_PeriodicOnly"] = "急冻周期攻击增加层数",
            ["PeriodicAttackBehaviourBuffContinueTime_AttackFlashFreezeBehaviour"] = "闪冻周期攻击增益持续时间",
            ["KillCountMoney_AddMoneyNum"] = "盗墓额外金币数",
            ["KillCountMoney_TriggerCountNum"] = "盗墓击杀触发数",
            ["CrossbowHitCount"] = "弩箭命中次数",
            ["CrossbowAttackCountAddCount"] = "弩箭增加攻击次数",
            ["WindPiercerHitCount"] = "穿风命中次数",
            ["WindPiercerBarrageMovementDistanceAddCount"] = "穿风增加弹丸飞行距离",
            ["ReturnToOriginHitCount"] = "归去来兮命中次数",
            ["BeetleShield"] = "甲虫护盾值",
            ["CthulhuTreeDamageCounter"] = "克苏鲁树受伤计数",
            ["TeleportDamageCounter"] = "传送受伤计数",
            ["ShotgunBullets"] = "霰弹弹丸数量",
            ["ProjectileRadius"] = "投射物范围",
            ["TrailFreezeWidth"] = "冰冻拖尾宽度",
            ["TrailFreezeLength"] = "冰冻拖尾长度",
            ["TrailFreezeContinueTime"] = "冰冻拖尾持续时间",
            ["explosionDamage_Doamin"] = "爆炸场域伤害",
            ["extraDamageAddRate_Doamin"] = "爆炸场域额外伤害增幅率",
            ["criticalRate_Doamin"] = "爆炸场域暴击率",
            ["explodeRadius_Doamin"] = "爆炸场域范围",
            ["explosionDamage_Railway"] = "爆炸轨道伤害",
            ["extraDamageAddRate_Railway"] = "爆炸轨道额外伤害增幅率",
            ["criticalRate_Railway"] = "爆炸轨道暴击率",
            ["explodeRadius_Railway"] = "爆炸轨道范围",
            ["FreezeDuration_Doamin"] = "冰冻场域持续时间",
            ["FreezeRadius_Doamin"] = "冰冻场域范围",
            ["FreezeDuration_Railway"] = "冰冻轨道持续时间",
            ["FreezeRadius_Railway"] = "冰冻轨道范围",
            ["slowRate_Doamin"] = "减速场域减速比例",
            ["slowContinueTime_Doamin"] = "减速场域持续时间",
            ["TrainFetterThresholdApplyBuffBuff_剧毒车列附魔"] = "剧毒车列附魔参数",
            ["ExplosionDamage_FactorVehicleTotal"] = "爆炸伤害战车总值系数",
            ["ExplodeRadius_FactorVehicleTotal"] = "爆炸范围战车总值系数",
            ["FreezeDuration_FactorVehicleTotal"] = "冰冻时间战车总值系数",
            ["FreezeRadius_FactorVehicleTotal"] = "冰冻范围战车总值系数",
            ["LinkCount_FactorVehicleTotal"] = "弹射次数战车总值系数",
            ["LinkRadius_FactorVehicleTotal"] = "弹射范围战车总值系数",
            ["LinkDamage_FactorVehicleTotal"] = "弹射伤害战车总值系数",
            ["barrageHitDamage_FactorVehicleTotal"] = "基础伤害战车总值系数",
            ["PoisonStackAdd_FactorVehicleTotal"] = "腐化层数战车总值系数",
            ["PoisonContinueTime_FactorVehicleTotal"] = "腐化持续时间战车总值系数",
            ["chooseTargetCount_FactorVehicleTotal"] = "目标数量战车总值系数",
            ["criticalDamageAddRate_FactorVehicleTotal"] = "暴击伤害加成战车总值系数",
            ["vehicleCollisionSourceCount"] = "战车碰撞资格来源数",
            ["minimumAttackRange"] = "最小攻击范围",
            ["PenetrateSplitArrowSplitCount"] = "穿透分裂箭分裂数量",
            ["PenetrateSplitArrowFanAngle"] = "穿透分裂箭扇形角度",
            ["TornadoRadius_FactorVehicleTotal"] = "空间坍缩范围战车总值系数",
            ["TornadoDuration_FactorVehicleTotal"] = "空间坍缩持续时间战车总值系数",
            ["BattleObjectFlightDistanceAttributeFactor"] = "战斗对象飞行距离属性系数"
        };

    private readonly List<string> _missingMembers = new();
    private readonly object _godModeSource = new();
    private readonly Dictionary<int, CatalogIcon> _catalogIcons = new();
    private readonly Dictionary<string, EnemyBuffIconSource> _enemyBuffIconSources =
        new(StringComparer.Ordinal);
    private List<EnemyOverlaySnapshot> _enemyOverlaySnapshots = new();
    private List<EnemyOverlaySnapshot> _enemyOverlayRefreshBuffer = new();
    private readonly List<EnemyTarget> _enemyTargetRefreshBuffer = new();
    private string _artifactRoot = string.Empty;
    private GUIStyle? _enemyIdStyle;
    private GUIStyle? _spawnPointStyle;
    private GUIStyle? _enemyBuffFrameStyle;
    private GUIStyle? _enemyBuffDurationStyle;
    private GUIStyle? _enemyBuffStackStyle;
    private GUIStyle? _enemyBuffDetailStyle;
    private GUIStyle? _enemyBuffFallbackStyle;
    private GUIStyle? _enemyBuffTooltipStyle;
    private float _nextEnemyOverlayRefreshAt;
    private object? _baseReceiver;
    private SpawnPointCapture _spawnPointCapture = SpawnPointCapture.Idle();
    private readonly List<SavedSpawnPoint> _spawnPoints = new();
    private string _lastCapturedPointId = string.Empty;
    private GrantAllRelicsJob _grantAllRelicsJob = GrantAllRelicsJob.Idle();
    private RelicRemovalJob _removeAllRelicsJob = RelicRemovalJob.Idle();
    private bool _fieldCatapultDeleteMode;
    private Action<string>? _warningLogger;
    private IReadOnlyList<object>? _availableVehicleValues;
    private IReadOnlyList<object> _unavailableVehicleValues = Array.Empty<object>();
    private readonly Dictionary<string, int> _vehicleTypeOrders = new(StringComparer.Ordinal);
    private readonly Dictionary<string, int> _vehicleFamilyOrders = new(StringComparer.Ordinal);
    private IReadOnlyList<object>? _availableEnchantmentValues;
    private IReadOnlyList<object> _unavailableEnchantmentValues = Array.Empty<object>();

    private Type? _cheatManagerType;
    private Type? _cheatVehiclePanelCfgType;
    private Type? _vehicleManagerType;
    private Type? _vehicleDataManagerType;
    private Type? _vehicleControllerType;
    private Type? _vehicleInterfaceType;
    private Type? _basicVehicleComponentType;
    private Type? _fetterInfoCfgType;
    private Type? _fetterDetailDataType;
    private Type? _fetterModuleDataType;
    private Type? _vehicleType;
    private Type? _fetterType;
    private Type? _disposableManagerType;
    private Type? _disposableDataType;
    private Type? _disposableObjectType;
    private Type? _disposableType;
    private Type? _infoManagerType;
    private Type? _razorDescriptionType;
    private Type? _gameControllerType;
    private Type? _mainStationType;
    private Type? _waveControllerType;
    private Type? _waveProgressControllerType;
    private Type? _agentCreatorType;
    private Type? _enemyConfigType;
    private Type? _aiInformationDataSoType;
    private Type? _aiInformationDataType;
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
    private Type? _superModuleDataType;
    private Type? _battleMemoryType;
    private Type? _battleAttributeCfgType;
    private Type? _attributeShowInfoType;
    private Type? _healthMaxSyncModeType;
    private Type? _inputManagerType;
    private Type? _inputKeyType;
    private Type? _mouseKeyType;
    private Type? _pressStateRoType;
    private Type? _defaultUiInteractionType;
    private Type? _mapPosManagerType;
    private Type? _pointDataUiType;
    private Type? _disposablePointDataType;
    private Type? _catapultCreatorType;
    private Type? _catapultManagerType;
    private Type? _catapultBaseType;
    private Type? _linePointType;
    private Type? _guiSaveHandlerType;
    private Type? _updateVehicleStateEventHandlerType;
    private Type? _buffAcceptorType;
    private Type? _buffManagerType;
    private Type? _buffType;
    private Type? _buffDataPathSoType;
    private Type? _buffFlagType;
    private Type? _buffDisplayDataType;
    private MethodInfo? _getBuffsMethod;

    public bool IsAvailable { get; private set; }
    public bool EnemyIdsVisible { get; set; }
    public bool EnemyBuffsVisible { get; set; }
    public bool BaseGodModeRequested { get; private set; }
    public bool FieldCatapultDeleteMode => _fieldCatapultDeleteMode;
    public IReadOnlyList<string> MissingMembers => _missingMembers;
    public IReadOnlyList<string> Capabilities => IsAvailable ? CheatCommands.All : Array.Empty<string>();

    public void Initialize(string artifactRoot, Action<string>? warningLogger = null)
    {
        _artifactRoot = Path.GetFullPath(artifactRoot);
        _warningLogger = warningLogger;
        _missingMembers.Clear();
        _catalogIcons.Clear();
        _grantAllRelicsJob = GrantAllRelicsJob.Idle();
        _removeAllRelicsJob = RelicRemovalJob.Idle();
        _fieldCatapultDeleteMode = false;
        InvalidateRuntimeCatalogCache();
        _cheatManagerType = Require("MetroTD.CheatSystem.CheatManager");
        _cheatVehiclePanelCfgType = Require("MetroTD.CheatSystem.UI.CheatVehiclePanelCfg");
        _vehicleManagerType = Require("MetroTD.VehicleSystem.VehicleManager");
        _vehicleDataManagerType = Require("MetroTD.VehicleSystem.VehicleDataManager");
        _vehicleControllerType = Require("MetroTD.VehicleSystem.VehicleController");
        _vehicleInterfaceType = Require("MetroTD.VehicleSystem.IVehicle");
        _basicVehicleComponentType = Require("MetroTD.VehicleSystem.BasicVehicleComponent");
        _fetterInfoCfgType = Require("MetroTD.VehicleSystem.SO_FetterInfoCfg");
        _fetterDetailDataType = Require("MetroTD.VehicleSystem.FetterDetailData");
        _fetterModuleDataType = Require("MetroTD.BuffSystem.FetterModuleData");
        _vehicleType = Require("MetroTD.VehicleSystem.VehicleType");
        _fetterType = Require("FetterEnum");
        _disposableManagerType = Require("MetroTD.DisposableSystem.DisposableManager");
        _disposableDataType = Require("MetroTD.DisposableSystem.DisposableData");
        _disposableObjectType = Require("MetroTD.DisposableSystem.DisposableObject");
        _disposableType = Require("MetroTD.DisposableSystem.DisposableEnum");
        _infoManagerType = Require("MetroTD.InfoSystem.InfoManager");
        _razorDescriptionType = Require("MetroTD.InfoSystem.RazorDescription");
        _gameControllerType = Require("MetroTD.GameController");
        _mainStationType = Require("MetroTD.CatapultSystem.MainStation");
        _waveControllerType = Require("MetroTD.RoomSystem.WaveDurationController");
        _waveProgressControllerType = Require("MetroTD.RoomSystem.WaveProgressController");
        _agentCreatorType = Require("MetroTD.AISystem.AgentCreator");
        _enemyConfigType = Require("MetroTD.AISystem.SO_EnemyCfg");
        _aiInformationDataSoType = Require("MetroTD.AISystem.AIInformationDataSO");
        _aiInformationDataType = Require("MetroTD.AISystem.AIInformationData");
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
        _superModuleDataType = Require("MetroTD.SuperModuleSystem.SuperModuleData");
        _battleMemoryType = Require("MetroTD.BattleSystem.BattleMemoryEnum");
        _battleAttributeCfgType = Require("MetroTD.BattleSystem.BattleAttributeCfg");
        _attributeShowInfoType = Require("MetroTD.BattleSystem.AttributeShowInfo");
        _healthMaxSyncModeType = Require("MetroTD.BattleSystem.HealthMaxSyncMode");
        _inputManagerType = Require("ActFramework_ByHZR.InputManager");
        _inputKeyType = Require("UnityEngine.InputSystem.Key");
        _mouseKeyType = Require("ActFramework_ByHZR.MouseKey");
        _pressStateRoType = Require("ActFramework_ByHZR.PressStateRO");
        _defaultUiInteractionType = Require("ActFramework_ByHZR.UI.DefaultUIInteraction");
        _mapPosManagerType = Require("MapPosManager");
        _pointDataUiType = Require("MetroTD.UISystem.DisposableInfo_Extension_PointDataUI");
        _disposablePointDataType = Require("MetroTD.UISystem.DisposablePointData");
        _catapultCreatorType = Require("CatapultCreator");
        _catapultManagerType = Require("CatapultManager");
        _catapultBaseType = Require("MetroTD.CatapultSystem.CatapultBase");
        _linePointType = Require("MetroTD.LineSystem.LinePoint");
        _guiSaveHandlerType = Require("GuiSaveHandler");
        _updateVehicleStateEventHandlerType = Require("MetroTD.LineSystem.UpdateVehicleStateEventHandler");
        _buffAcceptorType = Require("MetroTD.BuffSystem.IBuffAcceptor");
        _buffManagerType = Require("ActFramework_ByHZR.StatusEffect.BuffManagerOnAgentMono");
        _buffType = Require("ActFramework_ByHZR.StatusEffect.Buff");
        _buffDataPathSoType = Require("MetroTD.BuffSystem.BuffDataPathSO");
        _buffFlagType = Require("BuffFlag");
        _buffDisplayDataType = Require("MetroTD.BuffSystem.BuffDisplayData");
        _getBuffsMethod = _buffManagerType?
            .GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.FlattenHierarchy)
            .FirstOrDefault(method =>
                string.Equals(method.Name, "GetBuffs", StringComparison.Ordinal)
                && method.GetParameters().Length == 1);
        ValidateRuntimeContract();
        IsAvailable = _missingMembers.Count == 0;
        if (IsAvailable)
        {
            SpawnPointCaptureInputPatch.Register(() =>
            {
                TickFieldCatapultDeleteInput();
                TickSpawnPointCapture();
            });
        }
    }

    public JObject QueryCatalog()
    {
        EnsureAvailable();
        InvalidateRuntimeCatalogCache();
        IReadOnlyList<object> vehicles = AllVehicleValues(reportUnavailable: true);
        IReadOnlyList<object> enchantments = AllEnchantmentValues(reportUnavailable: true)
            .Where(value => !(value.ToString() ?? string.Empty).EndsWith("_Train", StringComparison.OrdinalIgnoreCase))
            .ToArray();
        IReadOnlyList<object> allDisposables = AllEnumValues(_disposableType!);
        IReadOnlyList<object> catapultPoints = allDisposables.Where(IsCatapultPoint).ToList();
        IReadOnlyList<object> disposables = allDisposables.Where(value => !IsCatapultPoint(value)).ToList();
        IReadOnlyList<object> relics = AllEnumValues(_superModuleType!);
        return new JObject
        {
            ["catalogVersion"] = 5,
            ["locale"] = "zh",
            ["vehicles"] = CatalogItems(vehicles, BuildVehicleCatalogItem),
            ["enchantments"] = CatalogItems(enchantments, BuildEnchantmentCatalogItem),
            ["disposables"] = CatalogItems(disposables, BuildDisposableCatalogItem),
            ["relics"] = CatalogItems(relics, BuildRelicCatalogItem),
            ["enemies"] = SafeEnemyCatalogItems(),
            ["catapultPoints"] = CatalogItems(catapultPoints, BuildCatapultPointCatalogItem),
            ["limits"] = new JObject
            {
                ["maxGrantCount"] = MaxGrantCount,
                ["maxSpawnCount"] = MaxSpawnCount,
                ["maxSpawnPointCount"] = MaxSpawnPointCount,
                ["maxTotalSpawnCount"] = MaxTotalSpawnCount,
                ["defaultSpawnRadius"] = DefaultSpawnRadius,
                ["maxSpawnRadius"] = MaxSpawnRadius,
                ["minimumSpawnSpacing"] = MinimumSpawnSpacing,
                ["maxEnemyLevel"] = 200,
                ["maxCoordinateMagnitude"] = MaxCoordinateMagnitude
            }
        };
    }

    public JObject QueryOwnedState()
    {
        EnsureAvailable();
        JObject state = new()
        {
            ["ownedVehicles"] = new JArray(),
            ["ownedRelics"] = new JArray(),
            ["ownedConsumables"] = new JArray(),
            ["ownedCatapultPoints"] = new JArray(),
            ["fieldCatapultPoints"] = new JArray(),
            ["grantAllRelics"] = BuildGrantAllRelicsState(),
            ["removeAllRelics"] = BuildRemoveAllRelicsState(),
            ["fieldCatapultDeleteMode"] = _fieldCatapultDeleteMode
        };
        List<string> errors = new();

        try
        {
            JArray vehicles = new();
            foreach (object vehicle in SnapshotVehicles()) vehicles.Add(BuildVehicleState(vehicle));
            state["ownedVehicles"] = vehicles;
        }
        catch (Exception exception)
        {
            errors.Add("战车：" + Unwrap(exception).Message);
        }

        TryPopulateInventoryState(state, errors, "ownedRelics", "遗物", BuildOwnedRelics);
        TryPopulateInventoryState(state, errors, "ownedConsumables", "消耗品", BuildOwnedConsumables);
        TryPopulateInventoryState(state, errors, "ownedCatapultPoints", "背包弹射点", BuildOwnedCatapultPoints);
        TryPopulateInventoryState(state, errors, "fieldCatapultPoints", "场上弹射点", BuildFieldCatapultPoints);
        if (errors.Count > 0) state["inventoryError"] = string.Join("；", errors.ToArray());
        return state;
    }

    private static void TryPopulateInventoryState(
        JObject state,
        ICollection<string> errors,
        string propertyName,
        string displayName,
        Func<JArray> read)
    {
        try
        {
            state[propertyName] = read();
        }
        catch (Exception exception)
        {
            errors.Add(displayName + "：" + Unwrap(exception).Message);
        }
    }

    public CheatExecutionResult GrantVehicle(JObject arguments)
    {
        EnsureAvailable();
        string vehicleId = RequiredText(arguments, "vehicleId", "必须选择战车类型。");
        int count = BoundedInt(arguments, "count", 1, MaxGrantCount, 1);
        object vehicleEnum = ParseEnum(_vehicleType!, vehicleId, "战车类型");
        if (!ContainsEnumValue(AllVehicleValues(), vehicleEnum))
        {
            return CheatExecutionResult.Fail(
                "当前游戏作弊面板未配置可获取战车：" + vehicleId + "。",
                "VEHICLE_NOT_CONFIGURED");
        }

        JToken? enchantmentsToken = arguments["enchantments"];
        if (enchantmentsToken != null
            && enchantmentsToken.Type != JTokenType.Null
            && enchantmentsToken is not JArray)
        {
            return CheatExecutionResult.Fail("附魔列表格式无效。", "INVALID_ENCHANTMENT");
        }

        JArray requestedEnchantments = enchantmentsToken as JArray ?? new JArray();
        IList runtimeEnchantments = CreateRuntimeList(_fetterModuleDataType!);
        HashSet<long> selectedEnchantments = new();
        JArray appliedEnchantments = new();
        foreach (JToken token in requestedEnchantments)
        {
            if (token is not JObject enchantment)
            {
                return CheatExecutionResult.Fail("附魔列表格式无效。", "INVALID_ENCHANTMENT");
            }

            string enchantmentId = RequiredText(enchantment, "enchantmentId", "必须选择附魔类型。");
            if (string.Equals(enchantmentId, "None", StringComparison.OrdinalIgnoreCase))
            {
                return CheatExecutionResult.Fail("None 不是可添加的附魔。", "INVALID_ENCHANTMENT");
            }

            int enchantmentLevel = PositiveInt(enchantment, "level", 1);
            object enchantmentEnum = ParseEnum(_fetterType!, enchantmentId, "附魔类型");
            if (!ContainsEnumValue(AllEnchantmentValues(), enchantmentEnum))
            {
                return CheatExecutionResult.Fail(
                    "当前游戏枚举中没有可用附魔：" + enchantmentId + "。",
                    "ENCHANTMENT_NOT_CONFIGURED");
            }

            long numeric = Convert.ToInt64(enchantmentEnum, CultureInfo.InvariantCulture);
            if (!selectedEnchantments.Add(numeric))
            {
                return CheatExecutionResult.Fail("同一种附魔不能重复添加：" + enchantmentId + "。", "DUPLICATE_ENCHANTMENT");
            }

            object moduleData = Activator.CreateInstance(_fetterModuleDataType!)
                                ?? throw new InvalidOperationException("无法创建附魔数据。");
            SetMemberValue(moduleData, "fetterEnum", enchantmentEnum);
            SetMemberValue(moduleData, "level", enchantmentLevel);
            SetMemberValue(moduleData, "count", 1);
            runtimeEnchantments.Add(moduleData);
            appliedEnchantments.Add(new JObject
            {
                ["enchantmentId"] = enchantmentId,
                ["level"] = enchantmentLevel
            });
        }

        object manager = GetRequiredSingleton(_vehicleManagerType!, "VehicleManager");
        bool hasEnchantments = runtimeEnchantments.Count > 0;
        Type runtimeListType = runtimeEnchantments.GetType();
        MethodInfo? method = hasEnchantments
            ? FindMethod(manager.GetType(), "GetCustomNewMainRazor", _vehicleType!, runtimeListType)
            : FindMethod(manager.GetType(), "GetNewMainRazor", _vehicleType!);
        if (method == null)
        {
            return CheatExecutionResult.Fail("当前游戏版本缺少战车获取入口。", "VEHICLE_API_MISSING");
        }

        JArray granted = new();
        for (int index = 0; index < count; index++)
        {
            object? created;
            try
            {
                created = method.Invoke(
                    manager,
                    hasEnchantments
                        ? new object[] { vehicleEnum, runtimeEnchantments }
                        : new[] { vehicleEnum });
            }
            finally
            {
                if (hasEnchantments)
                {
                    try
                    {
                        FindMethod(manager.GetType(), "EndVehicleGetMode")?.Invoke(manager, null);
                    }
                    catch
                    {
                        // Never let cleanup hide the original spawn failure.
                    }
                }
            }

            if (created == null) break;
            granted.Add(BuildVehicleReference(created));
        }

        JObject data = new()
        {
            ["requested"] = count,
            ["granted"] = granted.Count,
            ["vehicles"] = granted,
            ["enchantments"] = appliedEnchantments
        };
        string enchantmentSummary = appliedEnchantments.Count == 0
            ? string.Empty
            : "，附魔 " + string.Join(
                " + ",
                appliedEnchantments
                    .OfType<JObject>()
                    .Select(item => $"{item.Value<string>("enchantmentId")} {item.Value<int>("level")}级"));
        string message = $"已获取 {granted.Count}/{count} 辆战车 {vehicleId}{enchantmentSummary}。";
        if (granted.Count == count) return CheatExecutionResult.Changed(message.TrimEnd(), data);
        return granted.Count > 0
            ? CheatExecutionResult.Partial(message + " 部分战车未能生成，请检查配置或容量。", data)
            : CheatExecutionResult.Fail("未能获取战车，请确认当前位于已初始化的对局场景。", "VEHICLE_GRANT_FAILED");
    }

    public CheatExecutionResult RemoveVehicle(JObject arguments)
    {
        EnsureAvailable();
        int? vehicleId = arguments.Value<int?>("vehicleId");
        string typeId = arguments.Value<string>("typeId")?.Trim() ?? string.Empty;
        int count = BoundedInt(arguments, "count", 1, MaxGrantCount, 1);
        List<object> candidates = SnapshotVehicles();
        List<object> targets = vehicleId.HasValue
            ? candidates.Where(item => GetInt(item, "ID") == vehicleId.Value).Take(1).ToList()
            : candidates
                .Where(item => !string.IsNullOrWhiteSpace(typeId)
                               && string.Equals(GetMember(item, "vehicleType")?.ToString(), typeId, StringComparison.Ordinal))
                .Take(count)
                .ToList();
        if (targets.Count == 0)
        {
            return CheatExecutionResult.Fail("没有找到要删除的已有战车，请刷新战车列表后重试。", "VEHICLE_NOT_FOUND");
        }

        object manager = GetRequiredSingleton(_vehicleManagerType!, "VehicleManager");
        MethodInfo remove = FindMethod(manager.GetType(), "DeleteVehicle", _vehicleInterfaceType!)
                            ?? throw new MissingMethodException(manager.GetType().FullName, "DeleteVehicle");
        JArray removed = new();
        foreach (object target in targets)
        {
            JObject reference = BuildVehicleState(target);
            remove.Invoke(manager, new[] { target });
            removed.Add(reference);
        }

        return CheatExecutionResult.Changed(
            $"已删除 {removed.Count} 辆已有战车。",
            new JObject { ["removed"] = removed.Count, ["vehicles"] = removed });
    }

    public CheatExecutionResult GrantDisposable(JObject arguments)
    {
        EnsureAvailable();
        string disposableId = RequiredText(arguments, "disposableId", "必须选择消耗品类型。");
        int count = BoundedInt(arguments, "count", 1, MaxGrantCount, 1);
        object disposableEnum = ParseEnum(_disposableType!, disposableId, "消耗品类型");
        if (IsCatapultPoint(disposableEnum))
        {
            return CheatExecutionResult.Fail(
                "该道具会直接创建弹射点，请改用“获取弹射点”。",
                "DISPOSABLE_IS_CATAPULT_POINT");
        }
        object manager = GetRequiredSingleton(_disposableManagerType!, "DisposableManager");
        MethodInfo? method = FindMethod(manager.GetType(), "TryGetDisposable", _disposableType!);
        if (method == null)
        {
            return CheatExecutionResult.Fail("当前游戏版本缺少消耗品获取入口。", "DISPOSABLE_API_MISSING");
        }

        MethodInfo? isStackableMethod = FindMethod(manager.GetType(), "IsStackable", _disposableType!);
        MethodInfo? slotCountMethod = FindMethod(manager.GetType(), "GetNormalDisposableSlotCount");
        bool stackable = isStackableMethod?.Invoke(manager, new[] { disposableEnum }) is true;
        object? template = TryGetDisposableTemplate(disposableEnum);
        bool autoUse = template != null && GetBool(template, "isAutoUse");
        int beforeSlots = Convert.ToInt32(slotCountMethod?.Invoke(manager, null) ?? 0, CultureInfo.InvariantCulture);
        int capacity = ReadDisposableCapacity(manager);
        int allowed = stackable || autoUse ? count : Math.Min(count, Math.Max(0, capacity - beforeSlots));

        int granted = 0;
        for (int index = 0; index < allowed; index++)
        {
            if (method.Invoke(manager, new[] { disposableEnum }) is not bool success || !success) break;
            granted++;
        }

        JObject data = new()
        {
            ["requested"] = count,
            ["allowed"] = allowed,
            ["granted"] = granted,
            ["disposableId"] = disposableId,
            ["capacity"] = capacity,
            ["occupiedSlotsBefore"] = beforeSlots,
            ["ownedConsumables"] = BuildOwnedConsumables()
        };
        string message = $"已获取 {granted}/{count} 个消耗品 {disposableId}。";
        if (granted == count) return CheatExecutionResult.Changed(message, data);
        return granted > 0
            ? CheatExecutionResult.Partial(message + " 背包容量或配置阻止了剩余获取。", data)
            : CheatExecutionResult.Fail("未能获取消耗品，可能已达到容量上限或当前场景未初始化。", "DISPOSABLE_GRANT_FAILED");
    }

    public CheatExecutionResult ClearConsumables() => ClearBackpackItems(
        value => !IsCatapultPoint(value),
        "消耗品");

    public CheatExecutionResult ClearBackpackCatapultPoints() => ClearBackpackItems(
        IsCatapultPoint,
        "背包弹射点");

    private CheatExecutionResult ClearBackpackItems(Func<object, bool> predicate, string displayName)
    {
        EnsureAvailable();
        object manager = GetRequiredSingleton(_disposableManagerType!, "DisposableManager");
        MethodInfo consume = FindMethod(manager.GetType(), "TryConsumeDisposable", _disposableType!)
                             ?? throw new MissingMethodException(manager.GetType().FullName, "TryConsumeDisposable");
        List<(object Value, int Count)> targets = SnapshotDisposableCounts()
            .Where(item => predicate(item.Value))
            .ToList();
        int requested = targets.Sum(item => item.Count);
        int removed = 0;
        JArray failed = new();
        foreach ((object value, int count) in targets)
        {
            int itemRemoved = 0;
            while (itemRemoved < count && consume.Invoke(manager, new[] { value }) is true)
            {
                itemRemoved++;
                removed++;
            }
            if (itemRemoved < count)
            {
                failed.Add(new JObject
                {
                    ["disposableId"] = value.ToString(),
                    ["requested"] = count,
                    ["removed"] = itemRemoved
                });
            }
        }

        if (string.Equals(displayName, "背包弹射点", StringComparison.Ordinal))
        {
            ClearLegacyPointLedger();
        }

        JObject data = new()
        {
            ["requested"] = requested,
            ["removed"] = removed,
            ["failed"] = failed,
            ["ownedConsumables"] = string.Equals(displayName, "消耗品", StringComparison.Ordinal)
                ? BuildOwnedConsumables()
                : new JArray(),
            ["ownedCatapultPoints"] = BuildOwnedCatapultPoints()
        };
        string message = $"已删除 {removed}/{requested} 个{displayName}。";
        return failed.Count == 0
            ? CheatExecutionResult.Changed(message, data)
            : removed > 0
                ? CheatExecutionResult.Partial(message + " 部分道具未能从背包移除。", data)
                : CheatExecutionResult.Fail(message, "BACKPACK_CLEAR_FAILED");
    }

    private void ClearLegacyPointLedger()
    {
        object? pointDataUi = TryGetSingleton(_pointDataUiType!);
        if (pointDataUi == null || GetMember(pointDataUi, "PointDatas") is not IList pointDatas) return;
        pointDatas.Clear();
        RefreshPointDataUi(pointDataUi, pointDatas);
    }

    public CheatExecutionResult GrantCatapultPoint(JObject arguments)
    {
        EnsureAvailable();
        string disposableId = RequiredText(arguments, "disposableId", "必须选择弹射点类型。");
        int count = BoundedInt(arguments, "count", 1, MaxGrantCount, 1);
        if (!IsCatapultPointId(disposableId))
        {
            return CheatExecutionResult.Fail(
                "该枚举不是可直接放置的弹射点道具。",
                "INVALID_CATAPULT_POINT");
        }

        object disposableEnum = ParseEnum(_disposableType!, disposableId, "弹射点类型");
        object manager = GetRequiredSingleton(_disposableManagerType!, "DisposableManager");
        MethodInfo? grantMethod = FindMethod(manager.GetType(), "TryGetDisposable", _disposableType!);
        if (grantMethod == null)
        {
            return CheatExecutionResult.Fail("当前游戏版本缺少弹射点获取入口。", "CATAPULT_POINT_API_MISSING");
        }

        bool isLegacy = IsLegacyCatapultPointId(disposableId);
        bool isAttribute = string.Equals(disposableId, "FreePoint_Attribute", StringComparison.OrdinalIgnoreCase);
        object? pointDataUi = isLegacy ? GetRequiredSingleton(_pointDataUiType!, "弹射点数据界面") : null;
        MethodInfo? addPointMethod = pointDataUi == null ? null : FindMethod(pointDataUi.GetType(), "AddPointData", typeof(bool));
        if (isLegacy && addPointMethod == null)
        {
            return CheatExecutionResult.Fail("当前游戏版本缺少普通弹射点背包账本入口。", "CATAPULT_POINT_API_MISSING");
        }
        int granted = 0;
        for (int index = 0; index < count; index++)
        {
            if (grantMethod.Invoke(manager, new[] { disposableEnum }) is not bool success || !success) break;
            addPointMethod?.Invoke(pointDataUi, new object[] { isAttribute });
            granted++;
        }

        JObject data = new()
        {
            ["requested"] = count,
            ["granted"] = granted,
            ["disposableId"] = disposableId,
            ["isAttribute"] = isAttribute,
            ["ownedCatapultPoints"] = BuildOwnedCatapultPoints()
        };
        string message = $"已获取 {granted}/{count} 个弹射点 {disposableId}。";
        if (granted == count) return CheatExecutionResult.Changed(message, data);
        return granted > 0
            ? CheatExecutionResult.Partial(message + " 容量限制阻止了剩余获取。", data)
            : CheatExecutionResult.Fail("未能获取弹射点，请确认已进入对局且弹射点界面完成初始化。", "CATAPULT_POINT_GRANT_FAILED");
    }

    public CheatExecutionResult RemoveCatapultPoint(JObject arguments)
    {
        EnsureAvailable();
        string requestedId = arguments.Value<string>("catapultPointId")?.Trim()
                             ?? arguments.Value<string>("disposableId")?.Trim()
                             ?? string.Empty;
        string requestedDisposableId = arguments.Value<string>("disposableId")?.Trim() ?? requestedId;
        if (string.IsNullOrWhiteSpace(requestedId))
        {
            throw new InvalidOperationException("必须选择要删除的已有弹射点。");
        }

        int requestedCount = BoundedInt(arguments, "count", 1, MaxGrantCount, 1);
        if (IsCatapultPointId(requestedDisposableId) && !IsLegacyCatapultPointId(requestedDisposableId))
        {
            object specialEnum = ParseEnum(_disposableType!, requestedDisposableId, "弹射点类型");
            return RemoveRuntimeBackpackCatapult(specialEnum, requestedCount, requestedId);
        }
        object pointDataUi = GetRequiredSingleton(_pointDataUiType!, "弹射点数据界面");
        if (GetMember(pointDataUi, "PointDatas") is not IList pointDatas)
        {
            return CheatExecutionResult.Fail("弹射点背包尚未初始化。", "CATAPULT_POINT_BAG_UNAVAILABLE");
        }

        List<object> rows = pointDatas.Cast<object>()
            .Where(row => string.Equals(GetMember(row, "key")?.ToString(), requestedId, StringComparison.Ordinal)
                          || (IsCatapultPointId(requestedId)
                              && GetBool(row, "isAttribute") == string.Equals(
                                  requestedId,
                                  "FreePoint_Attribute",
                                  StringComparison.OrdinalIgnoreCase)))
            .ToList();
        if (rows.Count == 0)
        {
            return CheatExecutionResult.Fail("当前没有找到该背包弹射点。", "CATAPULT_POINT_NOT_OWNED");
        }

        bool isAttribute = GetBool(rows[0], "isAttribute");
        string disposableId = isAttribute ? "FreePoint_Attribute" : "FreePoint";
        object disposableEnum = ParseEnum(_disposableType!, disposableId, "弹射点类型");
        object manager = GetRequiredSingleton(_disposableManagerType!, "DisposableManager");
        MethodInfo consume = FindMethod(manager.GetType(), "TryConsumeDisposable", _disposableType!)
                             ?? throw new MissingMethodException(manager.GetType().FullName, "TryConsumeDisposable");

        int removed = 0;
        foreach (object row in rows)
        {
            while (removed < requestedCount && GetInt(row, "count") > 0)
            {
                if (consume.Invoke(manager, new[] { disposableEnum }) is not true) break;
                SetMemberValue(row, "count", GetInt(row, "count") - 1);
                removed++;
            }

            if (GetInt(row, "count") <= 0) pointDatas.Remove(row);
            if (removed >= requestedCount) break;
        }

        RefreshPointDataUi(pointDataUi, pointDatas);
        if (removed == 0)
        {
            return CheatExecutionResult.Fail("弹射点背包与道具库存不一致，未执行删除。", "CATAPULT_POINT_REMOVE_FAILED");
        }

        JObject data = new()
        {
            ["catapultPointId"] = requestedId,
            ["disposableId"] = disposableId,
            ["requested"] = requestedCount,
            ["removed"] = removed,
            ["ownedCatapultPoints"] = BuildOwnedCatapultPoints()
        };
        string message = $"已删除 {removed}/{requestedCount} 个已有{(isAttribute ? "能量" : "普通")}弹射点。";
        return removed == requestedCount
            ? CheatExecutionResult.Changed(message, data)
            : CheatExecutionResult.Partial(message, data);
    }

    private CheatExecutionResult RemoveRuntimeBackpackCatapult(object disposableEnum, int requestedCount, string pointId)
    {
        object manager = GetRequiredSingleton(_disposableManagerType!, "DisposableManager");
        MethodInfo consume = FindMethod(manager.GetType(), "TryConsumeDisposable", _disposableType!)
                             ?? throw new MissingMethodException(manager.GetType().FullName, "TryConsumeDisposable");
        int available = SnapshotDisposableCounts()
            .Where(item => Equals(item.Value, disposableEnum))
            .Sum(item => item.Count);
        if (available <= 0)
        {
            return CheatExecutionResult.Fail("当前背包没有该特殊弹射点。", "CATAPULT_POINT_NOT_OWNED");
        }

        int target = Math.Min(requestedCount, available);
        int removed = 0;
        while (removed < target && consume.Invoke(manager, new[] { disposableEnum }) is true) removed++;
        JObject data = new()
        {
            ["catapultPointId"] = pointId,
            ["disposableId"] = disposableEnum.ToString(),
            ["requested"] = requestedCount,
            ["removed"] = removed,
            ["ownedCatapultPoints"] = BuildOwnedCatapultPoints()
        };
        if (removed == 0) return CheatExecutionResult.Fail("特殊弹射点删除失败。", "CATAPULT_POINT_REMOVE_FAILED");
        string message = $"已删除 {removed}/{requestedCount} 个背包特殊弹射点。";
        return removed == requestedCount
            ? CheatExecutionResult.Changed(message, data)
            : CheatExecutionResult.Partial(message, data);
    }

    public CheatExecutionResult RemoveFieldCatapultPoint(JObject arguments)
    {
        EnsureAvailable();
        string runtimeId = RequiredText(arguments, "runtimeId", "必须选择场上的弹射点。");
        object? target = SnapshotFieldCatapultPoints()
            .FirstOrDefault(item => string.Equals(FieldCatapultRuntimeId(item), runtimeId, StringComparison.Ordinal));
        if (target == null)
        {
            return CheatExecutionResult.Fail("目标场上弹射点已经不存在，请刷新后重试。", "FIELD_CATAPULT_NOT_FOUND");
        }

        JObject before = BuildFieldCatapultPoint(target);
        DeleteFieldCatapult(target);
        return CheatExecutionResult.Changed(
            "已删除场上弹射点。",
            new JObject { ["removed"] = before, ["fieldCatapultPoints"] = BuildFieldCatapultPoints() });
    }

    public CheatExecutionResult ClearFieldCatapultPoints()
    {
        EnsureAvailable();
        List<object> targets = SnapshotFieldCatapultPoints();
        JArray removed = new();
        foreach (object target in targets)
        {
            removed.Add(BuildFieldCatapultPoint(target));
            DeleteFieldCatapult(target);
        }

        return CheatExecutionResult.Changed(
            $"已删除场上全部弹射点，共 {removed.Count} 个。",
            new JObject
            {
                ["removedCount"] = removed.Count,
                ["removed"] = removed,
                ["fieldCatapultPoints"] = BuildFieldCatapultPoints()
            });
    }

    public CheatExecutionResult SetFieldCatapultDeleteMode(bool enabled)
    {
        EnsureAvailable();
        if (enabled && !SpawnPointCaptureInputPatch.IsInstalled)
        {
            return CheatExecutionResult.Fail("场上弹射点点击删除未接入游戏输入流水线。", "INPUT_CAPTURE_UNAVAILABLE");
        }
        _fieldCatapultDeleteMode = enabled;
        string message = enabled
            ? "点击删除模式已开启：在游戏内左键点击场上弹射点即可直接删除，按 Esc 退出。"
            : "点击删除模式已关闭。";
        return CheatExecutionResult.Changed(message, new JObject { ["fieldCatapultDeleteMode"] = enabled });
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
        object manager = GetRequiredSingleton(_superModuleManagerType!, "SuperModuleManager");
        MethodInfo? method = FindMethod(manager.GetType(), "GetSuperModule", _superModuleType!, typeof(bool));
        if (method == null)
        {
            return CheatExecutionResult.Fail("当前游戏版本缺少遗物获取入口。", "RELIC_API_MISSING");
        }

        int before = GetDictionaryListCount(GetMember(manager, "superModules"), relicEnum);
        if (before > 0)
        {
            return CheatExecutionResult.Ok(
                "该遗物已经启用：" + relicId + "。",
                new JObject { ["relicId"] = relicId, ["before"] = before, ["after"] = before });
        }

        method.Invoke(manager, new[] { relicEnum, (object)false });
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

    public CheatExecutionResult StartGrantAllRelics()
    {
        EnsureAvailable();
        if (_removeAllRelicsJob.IsRunning)
        {
            return CheatExecutionResult.Fail(
                "删除所有遗物任务正在进行，不能同时获取全部遗物。",
                "RELIC_JOB_CONFLICT");
        }
        if (_grantAllRelicsJob.IsRunning)
        {
            return CheatExecutionResult.Ok(
                "一键获取所有遗物任务正在进行中。",
                BuildGrantAllRelicsResponse());
        }

        IReadOnlyList<object> configured = AllEnumValues(_superModuleType!);
        if (configured.Count == 0)
        {
            return CheatExecutionResult.Fail(
                "当前游戏的遗物奖励目录为空，未启动一键获取任务。",
                "RELIC_CATALOG_EMPTY");
        }

        object manager = GetRequiredSingleton(_superModuleManagerType!, "SuperModuleManager");
        if (FindMethod(manager.GetType(), "GetSuperModule", _superModuleType!, typeof(bool)) == null)
        {
            return CheatExecutionResult.Fail("当前游戏版本缺少遗物获取入口。", "RELIC_API_MISSING");
        }

        object? owned = GetMember(manager, "superModules");
        List<object> pending = new();
        int skippedCount = 0;
        foreach (object relic in configured)
        {
            if (GetDictionaryListCount(owned, relic) > 0)
            {
                skippedCount++;
            }
            else
            {
                pending.Add(relic);
            }
        }

        _grantAllRelicsJob = GrantAllRelicsJob.Start(configured.Count, skippedCount, pending);
        if (pending.Count == 0)
        {
            _grantAllRelicsJob.Complete();
        }

        JObject response = BuildGrantAllRelicsResponse();
        return pending.Count == 0
            ? CheatExecutionResult.Ok(_grantAllRelicsJob.Message, response)
            : CheatExecutionResult.Changed(_grantAllRelicsJob.Message, response);
    }

    public void TickGrantAllRelics()
    {
        if (!_grantAllRelicsJob.IsRunning) return;
        int remainingBudget = MaxGrantAllRelicsPerFrame;
        while (remainingBudget-- > 0 && _grantAllRelicsJob.IsRunning)
        {
            if (!_grantAllRelicsJob.TryTakeNext(out object? relic) || relic == null)
            {
                _grantAllRelicsJob.Complete();
                return;
            }

            string relicId = relic.ToString() ?? Convert.ToInt64(relic, CultureInfo.InvariantCulture).ToString(CultureInfo.InvariantCulture);
            try
            {
                object manager = GetRequiredSingleton(_superModuleManagerType!, "SuperModuleManager");
                object? owned = GetMember(manager, "superModules");
                int before = GetDictionaryListCount(owned, relic);
                if (before > 0)
                {
                    _grantAllRelicsJob.RecordSkipped(relicId);
                }
                else
                {
                    MethodInfo method = FindMethod(manager.GetType(), "GetSuperModule", _superModuleType!, typeof(bool))
                                        ?? throw new MissingMethodException(manager.GetType().FullName, "GetSuperModule");
                    method.Invoke(manager, new[] { relic, (object)false });
                    int after = GetDictionaryListCount(GetMember(manager, "superModules"), relic);
                    if (after > before)
                    {
                        _grantAllRelicsJob.RecordGranted(relicId);
                    }
                    else
                    {
                        _grantAllRelicsJob.RecordFailed(relicId, "遗物获取后持有数量未增加。");
                    }
                }
            }
            catch (Exception exception)
            {
                _grantAllRelicsJob.RecordFailed(relicId, Unwrap(exception).Message);
            }
        }

        if (!_grantAllRelicsJob.HasRemaining)
        {
            _grantAllRelicsJob.Complete();
        }
    }

    public void CancelGrantAllRelics(string message)
    {
        _grantAllRelicsJob.Cancel(message);
    }

    public CheatExecutionResult StartRemoveAllRelics()
    {
        EnsureAvailable();
        if (_grantAllRelicsJob.IsRunning)
        {
            return CheatExecutionResult.Fail("一键获取所有遗物正在进行，不能同时删除全部遗物。", "RELIC_JOB_CONFLICT");
        }
        if (_removeAllRelicsJob.IsRunning)
        {
            return CheatExecutionResult.Ok("删除所有遗物任务正在进行。", BuildRemoveAllRelicsResponse());
        }

        object manager = GetRequiredSingleton(_superModuleManagerType!, "SuperModuleManager");
        MethodInfo? remove = FindMethod(manager.GetType(), "TryRemoveSuperModule", _superModuleType!);
        if (remove == null)
        {
            return CheatExecutionResult.Fail("当前游戏版本缺少遗物安全删除入口。", "RELIC_REMOVE_API_MISSING");
        }
        List<object> pending = new();
        if (GetMember(manager, "superModules") is IDictionary owned)
        {
            foreach (DictionaryEntry entry in owned)
            {
                if (entry.Key != null && GetDictionaryListCount(owned, entry.Key) > 0) pending.Add(entry.Key);
            }
        }
        pending = DistinctEnumValues(pending).ToList();
        _removeAllRelicsJob = RelicRemovalJob.Start(pending);
        if (pending.Count == 0) _removeAllRelicsJob.Complete();
        return pending.Count == 0
            ? CheatExecutionResult.Ok(_removeAllRelicsJob.Message, BuildRemoveAllRelicsResponse())
            : CheatExecutionResult.Changed(_removeAllRelicsJob.Message, BuildRemoveAllRelicsResponse());
    }

    public void TickRemoveAllRelics()
    {
        if (!_removeAllRelicsJob.IsRunning) return;
        int budget = MaxRemoveAllRelicsPerFrame;
        while (budget-- > 0 && _removeAllRelicsJob.TryTakeNext(out object? relic) && relic != null)
        {
            string id = relic.ToString() ?? string.Empty;
            try
            {
                object manager = GetRequiredSingleton(_superModuleManagerType!, "SuperModuleManager");
                int before = GetDictionaryListCount(GetMember(manager, "superModules"), relic);
                MethodInfo remove = FindMethod(manager.GetType(), "TryRemoveSuperModule", _superModuleType!)
                                    ?? throw new MissingMethodException(manager.GetType().FullName, "TryRemoveSuperModule");
                bool changed = remove.Invoke(manager, new[] { relic }) is true;
                int after = GetDictionaryListCount(GetMember(manager, "superModules"), relic);
                if (changed && after < before) _removeAllRelicsJob.RecordRemoved(id, before - after);
                else _removeAllRelicsJob.RecordFailed(id, "删除后持有数量没有减少。");
            }
            catch (Exception exception)
            {
                _removeAllRelicsJob.RecordFailed(id, Unwrap(exception).Message);
            }
        }
        if (!_removeAllRelicsJob.HasRemaining) _removeAllRelicsJob.Complete();
    }

    public void CancelRemoveAllRelics(string message) => _removeAllRelicsJob.Cancel(message);

    private JObject BuildRemoveAllRelicsResponse() => new()
    {
        ["removeAllRelics"] = BuildRemoveAllRelicsState(),
        ["ownedRelics"] = BuildOwnedRelics()
    };

    private JObject BuildRemoveAllRelicsState() => _removeAllRelicsJob.ToData();

    private JObject BuildGrantAllRelicsResponse()
    {
        JObject response = new()
        {
            ["grantAllRelics"] = BuildGrantAllRelicsState(),
            ["ownedRelics"] = new JArray()
        };
        try
        {
            response["ownedRelics"] = BuildOwnedRelics();
        }
        catch (Exception exception)
        {
            response["inventoryError"] = "遗物：" + Unwrap(exception).Message;
        }
        return response;
    }

    private JObject BuildGrantAllRelicsState() => _grantAllRelicsJob.ToData();

    public CheatExecutionResult RemoveRelic(JObject arguments)
    {
        EnsureAvailable();
        string relicId = RequiredText(arguments, "relicId", "必须选择要删除的已有遗物。");
        object relicEnum = ParseEnum(_superModuleType!, relicId, "遗物类型");
        object manager = GetRequiredSingleton(_superModuleManagerType!, "SuperModuleManager");
        int before = GetDictionaryListCount(GetMember(manager, "superModules"), relicEnum);
        if (before <= 0)
        {
            return CheatExecutionResult.Fail("当前没有持有该遗物：" + relicId, "RELIC_NOT_OWNED");
        }

        MethodInfo remove = FindMethod(manager.GetType(), "TryRemoveSuperModule", _superModuleType!)
                            ?? throw new MissingMethodException(manager.GetType().FullName, "TryRemoveSuperModule");
        bool changed = remove.Invoke(manager, new[] { relicEnum }) is true;
        int after = GetDictionaryListCount(GetMember(manager, "superModules"), relicEnum);
        if (!changed || after >= before)
        {
            return CheatExecutionResult.Fail("遗物删除链路没有移除目标遗物：" + relicId, "RELIC_REMOVE_FAILED");
        }

        return CheatExecutionResult.Changed(
            $"已删除已有遗物 {relicId}，共移除 {before - after} 个实例。",
            new JObject
            {
                ["relicId"] = relicId,
                ["before"] = before,
                ["after"] = after,
                ["removed"] = before - after
            });
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

    public CheatExecutionResult SkipRewardPopup()
    {
        EnsureAvailable();
        Type? rewardPanelType = FindType("MetroTD.RewardSystem.RewardUIPanel");
        if (rewardPanelType == null)
        {
            return CheatExecutionResult.Fail(
                "当前游戏版本没有可识别的奖励面板。",
                "REWARD_PANEL_API_MISSING");
        }

        object? panel = TryGetSingleton(rewardPanelType);
        object? currentItem = GetMember(panel, "m_currentQueueItem");
        bool panelActive = panel != null
                           && (GetMember(panel, "IsActive") is bool active ? active : GetBool(panel, "m_enter"));
        if (panel == null || !panelActive || currentItem == null)
        {
            return CheatExecutionResult.Fail(
                "当前没有正在显示且可以跳过的奖励弹窗。",
                "REWARD_POPUP_NOT_OPEN");
        }

        object? itemType = GetMember(currentItem, "itemType");
        string itemTypeName = itemType?.ToString() ?? "Unknown";
        bool mandatory = GetBool(currentItem, "isMandatory");
        int remainingSelections = Math.Max(1, GetInt(currentItem, "remainingSelectionCount"));
        int pendingBefore = GetCollectionCount(GetMember(panel, "m_currentRewardQueneItems"));
        bool mutationStarted = false;

        try
        {
            if (!mandatory)
            {
                MethodInfo? skip = FindMethod(rewardPanelType, "SkipHandle");
                if (skip == null)
                {
                    return CheatExecutionResult.Fail(
                        "当前游戏版本缺少奖励弹窗的原生跳过入口。",
                        "REWARD_SKIP_API_MISSING");
                }

                mutationStarted = true;
                skip.Invoke(panel, null);
            }
            else
            {
                MethodInfo? useCurrent = FindMethodByParameterCount(rewardPanelType, "UseCurrent", 1);
                if (useCurrent == null)
                {
                    return CheatExecutionResult.Fail(
                        "当前游戏版本缺少奖励队列的安全推进入口。",
                        "REWARD_ADVANCE_API_MISSING");
                }

                mutationStarted = true;
                if (remainingSelections > 1)
                {
                    SetMemberValue(currentItem, "remainingSelectionCount", 1);
                }
                useCurrent.Invoke(panel, new object?[] { null });

                DispatchRewardJump(itemType);
                RequestGameSave(nameof(SkipRewardPopup));
            }

            MethodInfo? updateImmediately = FindMethod(rewardPanelType, "UpdateImmediately");
            if (updateImmediately == null)
            {
                return CheatExecutionResult.Partial(
                    "当前奖励已标记为跳过，但游戏缺少立即推进队列的入口；请等待下一帧确认弹窗是否关闭。",
                    new JObject
                    {
                        ["itemType"] = itemTypeName,
                        ["mandatory"] = mandatory,
                        ["remainingSelections"] = remainingSelections
                    });
            }

            updateImmediately.Invoke(panel, null);
            object? currentAfter = GetMember(panel, "m_currentQueueItem");
            bool activeAfter = GetMember(panel, "IsActive") is bool afterActive
                ? afterActive
                : GetBool(panel, "m_enter");
            int pendingAfter = GetCollectionCount(GetMember(panel, "m_currentRewardQueneItems"));
            bool advanced = !activeAfter
                            || !ReferenceEquals(currentItem, currentAfter)
                            || pendingAfter < pendingBefore;
            JObject data = new()
            {
                ["itemType"] = itemTypeName,
                ["mandatory"] = mandatory,
                ["remainingSelections"] = remainingSelections,
                ["pendingBefore"] = pendingBefore,
                ["pendingAfter"] = pendingAfter,
                ["panelClosed"] = !activeAfter,
                ["queueAdvanced"] = advanced
            };
            if (!advanced)
            {
                return CheatExecutionResult.Partial(
                    "游戏没有确认奖励队列已经前进；为避免重复操作，未继续发送跳过命令。",
                    data);
            }

            return CheatExecutionResult.Changed(
                activeAfter ? "已放弃当前奖励，并推进到下一项奖励。" : "已放弃当前奖励并关闭奖励弹窗。",
                data);
        }
        catch (Exception exception)
        {
            Exception error = Unwrap(exception);
            return mutationStarted
                ? CheatExecutionResult.Partial(
                    "奖励跳过流程已经开始，但后续确认失败：" + error.Message,
                    new JObject
                    {
                        ["itemType"] = itemTypeName,
                        ["mandatory"] = mandatory,
                        ["remainingSelections"] = remainingSelections
                    })
                : CheatExecutionResult.Fail("跳过奖励弹窗失败：" + error.Message, "REWARD_SKIP_FAILED");
        }
    }

    private static void DispatchRewardJump(object? itemType)
    {
        if (itemType == null) return;
        Type? eventType = FindType("MetroTD.RewardSystem.RewardJumpEventHandler");
        FindMethod(eventType, "Throw", itemType.GetType())?.Invoke(null, new[] { itemType });
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

    public CheatExecutionResult SetVehicleEnchantment(JObject arguments)
    {
        EnsureAvailable();
        int vehicleId = arguments.Value<int?>("vehicleId")
                        ?? throw new InvalidOperationException("必须选择有效的战车 ID。");
        string enchantmentId = RequiredText(arguments, "enchantmentId", "必须选择附魔。");
        int level = NonNegativeInt(arguments, "level", 1);
        object enchantment = ParseEnum(_fetterType!, enchantmentId, "附魔");
        IReadOnlyList<object> availableEnchantments = AllEnchantmentValues();
        if (!ContainsEnumValue(availableEnchantments, enchantment))
        {
            return CheatExecutionResult.Fail(
                "当前游戏枚举中没有可编辑的附魔：" + enchantmentId + "。",
                "ENCHANTMENT_NOT_CONFIGURED");
        }

        object? vehicle = SnapshotVehicles().FirstOrDefault(item => GetInt(item, "ID") == vehicleId);
        if (vehicle == null)
        {
            return CheatExecutionResult.Fail("目标战车已不存在，请刷新战车列表后重试。", "VEHICLE_NOT_FOUND");
        }

        IList originalModules = GetVehicleEvolutionData(vehicle);
        IList updatedModules = CloneFetterModules(originalModules);
        int moduleIndex = FindFetterModuleIndex(updatedModules, enchantment);
        int beforeLevel = moduleIndex < 0 ? 0 : GetInt(updatedModules[moduleIndex], "level");
        if (moduleIndex < 0 && level > 0)
        {
            object module = Activator.CreateInstance(_fetterModuleDataType!)
                            ?? throw new InvalidOperationException("无法创建附魔模块数据。");
            SetMemberValue(module, "fetterEnum", enchantment);
            SetMemberValue(module, "level", level);
            SetMemberValue(module, "count", 1);
            updatedModules.Add(module);
        }
        else if (moduleIndex >= 0 && level == 0)
        {
            updatedModules.RemoveAt(moduleIndex);
        }
        else if (moduleIndex >= 0)
        {
            SetMemberValue(updatedModules[moduleIndex]!, "level", level);
            SetMemberValue(updatedModules[moduleIndex]!, "count", 1);
        }

        if (beforeLevel == level)
        {
            return CheatExecutionResult.Ok(
                $"战车 #{vehicleId} 的{ResolveEnchantmentDisplayName(enchantment)}已经是 {level} 级。",
                new JObject { ["vehicle"] = BuildVehicleState(vehicle) });
        }

        ApplyVehicleFetterModules(vehicle, updatedModules, originalModules);
        string displayName = ResolveEnchantmentDisplayName(enchantment);
        string message = level == 0
            ? $"已移除战车 #{vehicleId} 的{displayName}。"
            : $"已将战车 #{vehicleId} 的{displayName}设为 {level} 级。";
        return CheatExecutionResult.Changed(
            message,
            new JObject
            {
                ["vehicleId"] = vehicleId,
                ["enchantmentId"] = enchantmentId,
                ["beforeLevel"] = beforeLevel,
                ["afterLevel"] = level,
                ["vehicle"] = BuildVehicleState(vehicle)
            });
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

        int countPerPoint = BoundedInt(arguments, "count", 1, MaxSpawnCount, 1);
        (int level, string levelSource, int? requestedLevel) = ResolveEnemySpawnLevel(arguments);
        float spawnRadius = BoundedFloat(arguments, "spawnRadius", 0f, MaxSpawnRadius, DefaultSpawnRadius);
        List<SpawnCenter> spawnCenters = ResolveSpawnCenters(arguments);
        if (spawnCenters.Count == 0)
        {
            return CheatExecutionResult.Fail("至少需要一个有效的怪物生成点。", "SPAWN_POINT_REQUIRED");
        }
        if (spawnCenters.Count > MaxSpawnPointCount)
        {
            return CheatExecutionResult.Fail($"一次最多使用 {MaxSpawnPointCount} 个怪物生成点。", "TOO_MANY_SPAWN_POINTS");
        }
        int count = checked(countPerPoint * spawnCenters.Count);
        if (count > MaxTotalSpawnCount)
        {
            return CheatExecutionResult.Fail($"一次最多生成 {MaxTotalSpawnCount} 个怪物，请减少生成点或每点数量。", "TOO_MANY_SPAWNED_ENEMIES");
        }

        List<Vector3> spawnPositions = new(count);
        string positionReason = string.Empty;
        foreach (SpawnCenter center in spawnCenters)
        {
            if (!TryValidateSpawnPosition(center.Position, out positionReason))
            {
                return CheatExecutionResult.Fail(positionReason, "INVALID_SPAWN_POSITION");
            }
            if (!TryCreateDistributedSpawnPositions(
                    center.Position,
                    countPerPoint,
                    spawnRadius,
                    out List<Vector3> positions,
                    out positionReason))
            {
                return CheatExecutionResult.Fail(positionReason, "SPAWN_AREA_TOO_SMALL");
            }
            spawnPositions.AddRange(positions);
        }

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
        int enemyLayer = LayerMask.NameToLayer("Enemy");
        if (enemyLayer < 0)
        {
            return CheatExecutionResult.Fail("当前游戏没有 Enemy 层级，无法保证生成的怪物能被战车锁定。", "ENEMY_LAYER_MISSING");
        }

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
        string failureReason = string.Empty;

        for (int index = 0; index < count; index++)
        {
            Vector3 spawnPosition = spawnPositions[index];
            Action<GameObject> prepareEnemy = gameObject => NormalizeEnemyLayer(gameObject, enemyLayer);
            object?[] invokeArgs =
            {
                aiId,
                spawnPosition,
                Quaternion.identity,
                level,
                battleGroup,
                true,
                null,
                prepareEnemy,
                registerType
            };
            if (create.Invoke(creator, invokeArgs) is not bool success || !success || invokeArgs[6] is not Component spawned)
            {
                failureReason = "游戏对象池拒绝了后续怪物生成请求。";
                break;
            }

            if (!TryValidateSpawnedEnemy(
                    spawned.gameObject,
                    enemyLayer,
                    battleGroup,
                    registerType,
                    out EnemyTarget? target,
                    out failureReason))
            {
                RecycleInvalidSpawnedEnemy(creator, spawned.gameObject);
                break;
            }

            EnemyTarget validatedTarget = target!;
            Component ai = validatedTarget.Ai;
            if (!activeWave)
            {
                DisableEnemyDeathMessage(ai.gameObject);
            }
            else if (wave != null && GetBool(ai, "SendsDeathMessage"))
            {
                FindMethod(wave.GetType(), "AddEnemyCount")?.Invoke(wave, null);
            }

            created.Add(new JObject
            {
                ["runtimeId"] = validatedTarget.RuntimeId,
                ["instanceId"] = ai.gameObject.GetInstanceID(),
                ["enemyId"] = enemyId,
                ["position"] = VectorData(spawnPosition)
            });
        }

        JObject data = new()
        {
            ["requested"] = count,
            ["requestedPerPoint"] = countPerPoint,
            ["pointCount"] = spawnCenters.Count,
            ["spawned"] = created.Count,
            ["enemyId"] = enemyId,
            ["position"] = VectorData(spawnCenters[0].Position),
            ["points"] = new JArray(spawnCenters.Select(center => center.ToData())),
            ["resolvedLevel"] = level,
            ["displayLevel"] = level + 1,
            ["requestedLevel"] = requestedLevel,
            ["levelSource"] = levelSource,
            ["spawnRadius"] = spawnRadius,
            ["minimumSpacing"] = MinimumSpawnSpacing,
            ["enemies"] = created,
            ["countedInActiveWave"] = activeWave,
            ["failureReason"] = failureReason
        };
        string message = $"已在 {spawnCenters.Count} 个位置按关卡属性等级 {level + 1} 分散生成 {created.Count}/{count} 个 {enemyId}。";
        if (created.Count == count) return CheatExecutionResult.Changed(message, data);
        return created.Count > 0
            ? CheatExecutionResult.Partial(message + " " + failureReason, data)
            : CheatExecutionResult.Fail(
                string.IsNullOrWhiteSpace(failureReason)
                    ? "怪物生成失败，请确认当前对局、怪物配置和数量上限。"
                    : failureReason,
                "SPAWN_FAILED");
    }

    public CheatExecutionResult SetSpawnPointCapture(JObject arguments)
    {
        EnsureAvailable();
        bool enabled = arguments.Value<bool?>("enabled") == true;
        if (enabled && !SpawnPointCaptureInputPatch.IsInstalled)
        {
            return CheatExecutionResult.Fail(
                "怪物生成位置捕获未接入游戏输入流水线，无法安全拦截点击。",
                "INPUT_CAPTURE_UNAVAILABLE");
        }

        _spawnPointCapture = enabled
            ? SpawnPointCapture.Armed(
                DateTime.UtcNow.Add(SpawnPointCaptureTimeout),
                "已等待定位：请在两分钟内切换到游戏，按住左 Alt 并点击鼠标左键。")
            : SpawnPointCapture.Idle("已取消怪物生成位置定位。");
        return CheatExecutionResult.Ok(_spawnPointCapture.Message, SpawnPointCaptureData());
    }

    public CheatExecutionResult RemoveSpawnPoint(JObject arguments)
    {
        EnsureAvailable();
        string pointId = RequiredText(arguments, "pointId", "必须选择要删除的怪物生成点。");
        int index = _spawnPoints.FindIndex(point => string.Equals(point.PointId, pointId, StringComparison.Ordinal));
        if (index < 0)
        {
            return CheatExecutionResult.Fail("指定的怪物生成点已经不存在。", "SPAWN_POINT_NOT_FOUND");
        }

        SavedSpawnPoint removed = _spawnPoints[index];
        _spawnPoints.RemoveAt(index);
        if (string.Equals(_lastCapturedPointId, pointId, StringComparison.Ordinal)) _lastCapturedPointId = string.Empty;
        return CheatExecutionResult.Ok(
            "已删除怪物生成点 " + pointId + "。",
            new JObject { ["removed"] = removed.ToData(), ["spawnPointCapture"] = SpawnPointCaptureData() });
    }

    public CheatExecutionResult ClearSpawnPoints()
    {
        EnsureAvailable();
        int count = _spawnPoints.Count;
        _spawnPoints.Clear();
        _lastCapturedPointId = string.Empty;
        return CheatExecutionResult.Ok(
            $"已清空 {count} 个怪物生成点。",
            new JObject { ["removed"] = count, ["spawnPointCapture"] = SpawnPointCaptureData() });
    }

    public void TickSpawnPointCapture()
    {
        // CheatController.Tick is hosted by an unrelated MonoBehaviour whose Update
        // order is not defined relative to the game. Only accept calls dispatched by
        // the Harmony hook inside DefaultInputHandler.Update.
        if (!SpawnPointCaptureInputPatch.IsDispatching) return;
        ExpireSpawnPointCaptureIfNeeded();
        if (!_spawnPointCapture.IsArmed) return;

        try
        {
            object? input = TryGetSingleton(_inputManagerType!);
            if (input == null) return;

            object leftAlt = ParseEnum(_inputKeyType!, "LeftAlt", "定位快捷键");
            object leftMouse = ParseEnum(_mouseKeyType!, "left", "鼠标按键");
            object? altState = FindMethod(input.GetType(), "GetKeyPressStateRO", _inputKeyType!)
                ?.Invoke(input, new[] { leftAlt });
            if (altState == null || !GetBool(altState, "isPressed")) return;

            object? mouseState = FindMethod(input.GetType(), "GetMousePressStateRO", _mouseKeyType!)
                ?.Invoke(input, new[] { leftMouse });
            if (mouseState == null || !GetBool(mouseState, "wasPressedThisFrame")) return;

            if (GetBool(input, "hasUsed")) return;
            object? uiInteraction = GetStaticMember(_defaultUiInteractionType!, "Current");
            if (uiInteraction != null && GetBool(uiInteraction, "isInUI")) return;

            if (GetMember(input, "currentWorldMousePosition") is not Vector3 position)
            {
                _spawnPointCapture = SpawnPointCapture.Failed("无法读取鼠标世界坐标，请重新定位。");
                return;
            }

            position.z = 0f;
            if (!TryValidateSpawnPosition(position, out string reason))
            {
                _spawnPointCapture = SpawnPointCapture.Failed(reason);
                return;
            }

            FindMethod(input.GetType(), "UseInputOnly")?.Invoke(input, null);
            if (_spawnPoints.Count >= MaxSpawnPointCount)
            {
                _spawnPointCapture = SpawnPointCapture.Failed(
                    $"最多保留 {MaxSpawnPointCount} 个怪物生成点，请先在作弊工具中删除不需要的点。");
                return;
            }
            SavedSpawnPoint saved = new(Guid.NewGuid().ToString("N"), position, DateTime.UtcNow);
            _spawnPoints.Add(saved);
            _lastCapturedPointId = saved.PointId;
            _spawnPointCapture = SpawnPointCapture.Captured(
                position,
                $"已添加生成点 #{_spawnPoints.Count}：({position.x:0.##}, {position.y:0.##})。");
        }
        catch (Exception exception)
        {
            _spawnPointCapture = SpawnPointCapture.Failed(
                "定位失败：" + Unwrap(exception).Message);
        }
    }

    public JObject SpawnPointCaptureData()
    {
        ExpireSpawnPointCaptureIfNeeded();
        JObject data = _spawnPointCapture.ToData();
        data["lastPointId"] = _lastCapturedPointId;
        data["points"] = new JArray(_spawnPoints.Select(point => point.ToData()));
        data["count"] = _spawnPoints.Count;
        data["maximum"] = MaxSpawnPointCount;
        return data;
    }

    private void ExpireSpawnPointCaptureIfNeeded()
    {
        if (_spawnPointCapture.IsArmed
            && _spawnPointCapture.ExpiresAtUtc.HasValue
            && DateTime.UtcNow >= _spawnPointCapture.ExpiresAtUtc.Value)
        {
            _spawnPointCapture = SpawnPointCapture.Expired("定位请求已超过两分钟，已自动取消。");
        }
    }

    public void TickEnemyOverlays()
    {
        if (!EnemyIdsVisible && !EnemyBuffsVisible)
        {
            InvalidateEnemyOverlayCache();
            return;
        }

        float now = Time.realtimeSinceStartup;
        if (now < _nextEnemyOverlayRefreshAt) return;
        _nextEnemyOverlayRefreshAt = now + EnemyOverlayRefreshInterval;

        try
        {
            RefreshEnemyOverlayCache();
        }
        catch
        {
            _enemyOverlaySnapshots.Clear();
        }
    }

    public void TickFieldCatapultDeleteInput()
    {
        if (!_fieldCatapultDeleteMode || !SpawnPointCaptureInputPatch.IsDispatching) return;
        try
        {
            object? input = TryGetSingleton(_inputManagerType!);
            if (input == null) return;
            object escape = ParseEnum(_inputKeyType!, "Escape", "退出快捷键");
            object? escapeState = FindMethod(input.GetType(), "GetKeyPressStateRO", _inputKeyType!)?.Invoke(input, new[] { escape });
            if (escapeState != null && GetBool(escapeState, "wasPressedThisFrame"))
            {
                FindMethod(input.GetType(), "UseInputOnly")?.Invoke(input, null);
                _fieldCatapultDeleteMode = false;
                return;
            }
            object leftMouse = ParseEnum(_mouseKeyType!, "left", "鼠标按键");
            object? mouseState = FindMethod(input.GetType(), "GetMousePressStateRO", _mouseKeyType!)?.Invoke(input, new[] { leftMouse });
            if (mouseState == null || !GetBool(mouseState, "wasPressedThisFrame") || GetBool(input, "hasUsed")) return;
            object? uiInteraction = GetStaticMember(_defaultUiInteractionType!, "Current");
            if (uiInteraction != null && GetBool(uiInteraction, "isInUI")) return;
            FindMethod(input.GetType(), "UseInputOnly")?.Invoke(input, null);
            object? target = FindHoveredFieldCatapult(input);
            if (target != null)
            {
                string runtimeId = FieldCatapultRuntimeId(target);
                try
                {
                    DeleteFieldCatapult(target);
                    _warningLogger?.Invoke($"作弊点击删除场上弹射点成功：runtimeId={runtimeId}");
                }
                catch (Exception exception)
                {
                    _warningLogger?.Invoke(
                        $"作弊点击删除场上弹射点失败：runtimeId={runtimeId}；{Unwrap(exception).Message}");
                    throw;
                }
            }
        }
        catch
        {
            _fieldCatapultDeleteMode = false;
        }
    }

    private object? FindHoveredFieldCatapult(object input)
    {
        if (GetMember(input, "currentWorldMousePosition") is not Vector3 pointer) return null;
        object? best = null;
        float bestDistance = float.PositiveInfinity;
        foreach (object candidate in SnapshotFieldCatapultPoints())
        {
            if (candidate is not Component component || component == null) continue;
            Collider2D[] colliders = component.GetComponentsInChildren<Collider2D>(true);
            if (!colliders.Any(collider => collider != null && collider.enabled && collider.OverlapPoint(pointer))) continue;
            float distance = (component.transform.position - pointer).sqrMagnitude;
            if (distance > bestDistance) continue;
            bestDistance = distance;
            best = candidate;
        }
        return best;
    }

    public void InvalidateEnemyOverlayCache()
    {
        _enemyOverlaySnapshots.Clear();
        _enemyOverlayRefreshBuffer.Clear();
        _enemyTargetRefreshBuffer.Clear();
        _nextEnemyOverlayRefreshAt = 0f;
    }

    public void DrawEnemyOverlays()
    {
        if (Event.current == null || Event.current.type != EventType.Repaint) return;
        Camera camera = Camera.main;
        if (camera == null) return;

        EnsureEnemyOverlayStyles();
        DrawSpawnPointMarkers(camera);
        DrawFieldCatapultDeleteOverlay(camera);

        string tooltip = string.Empty;
        for (int index = 0; index < _enemyOverlaySnapshots.Count; index++)
        {
            EnemyOverlaySnapshot overlay = _enemyOverlaySnapshots[index];
            if (overlay.GameObject == null || !overlay.GameObject.activeInHierarchy || overlay.IdAnchor == null) continue;

            Vector3 screen = camera.WorldToScreenPoint(overlay.IdAnchor.position + (Vector3.up * overlay.IdWorldYOffset));
            Rect viewport = camera.pixelRect;
            if (screen.z <= 0f
                || screen.x < viewport.xMin
                || screen.x > viewport.xMax
                || screen.y < viewport.yMin
                || screen.y > viewport.yMax)
            {
                continue;
            }
            float guiY = Screen.height - screen.y;

            if (EnemyBuffsVisible && overlay.Buffs.Count > 0)
            {
                Transform buffAnchor = overlay.BuffAnchor == null ? overlay.GameObject.transform : overlay.BuffAnchor;
                Vector3 buffScreen = camera.WorldToScreenPoint(
                    buffAnchor.position + (Vector3.up * overlay.BuffWorldYOffset));
                if (buffScreen.z > 0f)
                    DrawEnemyBuffIcons(overlay.Buffs, buffScreen.x, Screen.height - buffScreen.y, ref tooltip);
            }

            if (EnemyIdsVisible)
            {
                Rect rect = new(screen.x - 110f, guiY - 14f, 220f, 28f);
                GUI.Label(rect, overlay.IdText, _enemyIdStyle);
            }
        }

        if (!string.IsNullOrWhiteSpace(tooltip)) DrawEnemyBuffTooltip(tooltip);
    }

    private void RefreshEnemyOverlayCache()
    {
        List<EnemyOverlaySnapshot> refreshed = _enemyOverlayRefreshBuffer;
        refreshed.Clear();
        CollectEnemyTargets(_enemyTargetRefreshBuffer);
        foreach (EnemyTarget target in _enemyTargetRefreshBuffer)
        {
            if (!GetBool(target.Ai, "AIIsRunning")) continue;
            IReadOnlyList<EnemyBuffIconSnapshot> buffs = EnemyBuffsVisible
                ? SnapshotEnemyBuffIcons(target)
                : Array.Empty<EnemyBuffIconSnapshot>();
            if (!EnemyIdsVisible && buffs.Count == 0) continue;

            Transform? idAnchor = GetMember(target.Ai, "HpSliderTransform") as Transform;
            float idWorldYOffset = 0.45f;
            if (idAnchor == null)
            {
                idAnchor = target.GameObject.transform;
                idWorldYOffset = 1.4f;
            }

            refreshed.Add(new EnemyOverlaySnapshot
            {
                GameObject = target.GameObject,
                IdAnchor = idAnchor,
                IdWorldYOffset = idWorldYOffset,
                BuffAnchor = target.GameObject.transform,
                BuffWorldYOffset = ResolveEnemyBuffWorldYOffset(target.GameObject),
                IdText = $"[{target.RuntimeId}] {target.TypeId}",
                Buffs = buffs
            });
        }

        (_enemyOverlaySnapshots, _enemyOverlayRefreshBuffer) =
            (_enemyOverlayRefreshBuffer, _enemyOverlaySnapshots);
    }

    private IReadOnlyList<EnemyBuffIconSnapshot> SnapshotEnemyBuffIcons(EnemyTarget target)
    {
        if (_buffAcceptorType == null || _getBuffsMethod == null) return Array.Empty<EnemyBuffIconSnapshot>();

        object? acceptor = target.GameObject.GetComponent(_buffAcceptorType)
                           ?? GetMember(target.Ai, "m_buffAcceptor");
        object? manager = GetMember(acceptor, "buffMr");
        if (manager == null) return Array.Empty<EnemyBuffIconSnapshot>();

        object? value = _getBuffsMethod.Invoke(manager, new object?[] { null });
        if (value is not IEnumerable buffs) return Array.Empty<EnemyBuffIconSnapshot>();

        Dictionary<string, EnemyBuffIconSnapshot> grouped = new(StringComparer.Ordinal);
        foreach (object? buff in buffs)
        {
            if (buff == null || GetBool(buff, "IsEnd")) continue;
            string key = GetMember(buff, "Key")?.ToString()?.Trim() ?? string.Empty;
            if (string.IsNullOrWhiteSpace(key)
                || !TryResolveEnemyBuffIcon(key, out EnemyBuffIconSource icon))
            {
                continue;
            }

            int stackCount = ResolveEnemyBuffStackCount(buff, out bool explicitStackCount);
            string detailText = ResolveEnemyBuffDetail(buff, key);
            if (grouped.TryGetValue(key, out EnemyBuffIconSnapshot? existing))
            {
                if (explicitStackCount)
                {
                    existing.StackCount = Math.Max(existing.StackCount, stackCount);
                    existing.HasExplicitStackCount = true;
                }
                else if (!existing.HasExplicitStackCount)
                {
                    existing.StackCount = Math.Min(9999, existing.StackCount + 1);
                }
                existing.ShowStackCount = true;
                if (string.IsNullOrWhiteSpace(existing.DetailText)) existing.DetailText = detailText;
                continue;
            }

            grouped[key] = new EnemyBuffIconSnapshot
            {
                Key = key,
                DisplayName = icon.DisplayName,
                Texture = icon.Texture,
                Uv = icon.Uv,
                FallbackColor = icon.FallbackColor,
                DurationText = ResolveEnemyBuffDuration(GetMember(buff, "LifeRule")),
                StackCount = stackCount,
                HasExplicitStackCount = explicitStackCount,
                ShowStackCount = explicitStackCount
                                 || key.IndexOf("poison", StringComparison.OrdinalIgnoreCase) >= 0
                                 || key.IndexOf("腐化", StringComparison.OrdinalIgnoreCase) >= 0,
                DetailText = detailText
            };
        }

        return grouped.Values.ToArray();
    }

    private static int ResolveEnemyBuffStackCount(object buff, out bool explicitStackCount)
    {
        explicitStackCount = false;
        string[] members =
        {
            "StackCount", "stackCount", "CurrentStack", "currentStack", "Stack", "stack", "Layer", "layer"
        };
        foreach (string member in members)
        {
            object? value = GetMember(buff, member);
            if (value == null) continue;
            try
            {
                int count = Convert.ToInt32(value, CultureInfo.InvariantCulture);
                if (count > 0)
                {
                    explicitStackCount = true;
                    return Math.Min(9999, count);
                }
            }
            catch (Exception exception) when (exception is FormatException or InvalidCastException or OverflowException)
            {
                // Continue with the next known runtime member.
            }
        }
        return 1;
    }

    private static string ResolveEnemyBuffDetail(object buff, string key)
    {
        bool slow = key.IndexOf("slow", StringComparison.OrdinalIgnoreCase) >= 0
                    || key.IndexOf("减速", StringComparison.OrdinalIgnoreCase) >= 0;
        (string Member, string Label, bool Percentage, bool Inverted)[] candidates = slow
            ? new[]
            {
                ("SlowRate", "减速", true, false), ("slowRate", "减速", true, false),
                ("MoveSpeedRate", "移速倍率", true, false), ("moveSpeedRate", "移速倍率", true, false),
                ("SpeedRate", "移速倍率", true, false), ("speedRate", "移速倍率", true, false),
                ("Ratio", "效果比例", true, false), ("ratio", "效果比例", true, false)
            }
            : new[]
            {
                ("Damage", "每层伤害", false, false), ("damage", "每层伤害", false, false),
                ("Value", "效果值", false, false), ("value", "效果值", false, false),
                ("Amount", "效果值", false, false), ("amount", "效果值", false, false),
                ("Rate", "效果比例", true, false), ("rate", "效果比例", true, false)
            };
        object?[] sources =
        {
            buff,
            GetMember(buff, "Data"), GetMember(buff, "data"),
            GetMember(buff, "BuffData"), GetMember(buff, "buffData"),
            GetMember(buff, "Config"), GetMember(buff, "config"),
            GetMember(buff, "Effect"), GetMember(buff, "effect")
        };
        foreach (object? source in sources)
        {
            if (source == null) continue;
            foreach ((string member, string label, bool percentage, bool inverted) in candidates)
            {
                if (!TryGetFiniteFloat(GetMember(source, member), out float value)) continue;
                if (percentage)
                {
                    float percent = Mathf.Abs(value) <= 2f ? value * 100f : value;
                    if (inverted) percent = 100f - percent;
                    if (string.Equals(label, "减速", StringComparison.Ordinal)) percent = Mathf.Abs(percent);
                    return label + " " + percent.ToString("0.#", CultureInfo.InvariantCulture) + "%";
                }
                return label + " " + value.ToString("0.##", CultureInfo.InvariantCulture);
            }
        }
        return string.Empty;
    }

    private static float ResolveEnemyBuffWorldYOffset(GameObject gameObject)
    {
        float rootY = gameObject.transform.position.y;
        Collider2D collider = gameObject.GetComponentInChildren<Collider2D>();
        if (collider != null && collider.enabled)
            return Mathf.Clamp(collider.bounds.min.y - rootY - 0.22f, -3f, -0.45f);
        Collider collider3D = gameObject.GetComponentInChildren<Collider>();
        if (collider3D != null && collider3D.enabled)
            return Mathf.Clamp(collider3D.bounds.min.y - rootY - 0.22f, -3f, -0.45f);
        Renderer renderer = gameObject.GetComponentInChildren<Renderer>();
        float bottom = renderer != null && renderer.enabled ? renderer.bounds.min.y : rootY - 0.65f;
        return Mathf.Clamp(bottom - rootY - 0.22f, -3f, -0.45f);
    }

    private bool TryResolveEnemyBuffIcon(string key, out EnemyBuffIconSource source)
    {
        source = null!;
        if (_enemyBuffIconSources.TryGetValue(key, out EnemyBuffIconSource? cached))
        {
            source = cached;
            return true;
        }

        if (_buffDataPathSoType == null || _buffFlagType == null)
        {
            source = CreateEnemyBuffFallbackIcon(key);
            _enemyBuffIconSources[key] = source;
            return true;
        }
        try
        {
            object? configuration = TryGetSingleton(_buffDataPathSoType);
            object? displayConfiguration = GetMember(configuration, "buffDisplayData");
            if (GetMember(displayConfiguration, "Dic") is not IDictionary dictionary)
            {
                source = CreateEnemyBuffFallbackIcon(key);
                _enemyBuffIconSources[key] = source;
                return true;
            }
            if (!Enum.TryParse(_buffFlagType, key, false, out object? flag)
                || flag == null
                || !dictionary.Contains(flag))
            {
                source = CreateEnemyBuffFallbackIcon(key);
                _enemyBuffIconSources[key] = source;
                return true;
            }

            object? displayData = dictionary[flag];
            if (GetMember(displayData, "sprite") is not Sprite sprite || sprite == null)
            {
                source = CreateEnemyBuffFallbackIcon(key);
                _enemyBuffIconSources[key] = source;
                return true;
            }
            Texture2D texture = sprite.texture;
            Rect sourceRect = sprite.textureRect;
            if (texture == null || texture.width <= 0 || texture.height <= 0
                || sourceRect.width <= 0f || sourceRect.height <= 0f)
            {
                source = CreateEnemyBuffFallbackIcon(key);
                _enemyBuffIconSources[key] = source;
                return true;
            }

            string displayName = ResolveChineseLocalizedString(GetMember(displayData, "title"));
            source = new EnemyBuffIconSource
            {
                DisplayName = string.IsNullOrWhiteSpace(displayName) ? key : displayName,
                Texture = texture,
                Uv = new Rect(
                    sourceRect.x / texture.width,
                    sourceRect.y / texture.height,
                    sourceRect.width / texture.width,
                    sourceRect.height / texture.height)
            };
            _enemyBuffIconSources[key] = source;
            return true;
        }
        catch
        {
            source = CreateEnemyBuffFallbackIcon(key);
            _enemyBuffIconSources[key] = source;
            return true;
        }
    }

    private static EnemyBuffIconSource CreateEnemyBuffFallbackIcon(string key)
    {
        uint hash = 2166136261;
        for (int index = 0; index < key.Length; index++)
        {
            hash ^= key[index];
            hash *= 16777619;
        }

        float red = 0.28f + (((hash >> 16) & 0xff) / 255f * 0.42f);
        float green = 0.28f + (((hash >> 8) & 0xff) / 255f * 0.42f);
        float blue = 0.28f + ((hash & 0xff) / 255f * 0.42f);
        return new EnemyBuffIconSource
        {
            DisplayName = key,
            Texture = null,
            Uv = default,
            FallbackColor = new Color(red, green, blue, 1f)
        };
    }

    private static string ResolveEnemyBuffDuration(object? lifeRule)
    {
        if (lifeRule == null) return "--";
        try
        {
            string typeName = lifeRule.GetType().Name;
            if (typeName.IndexOf("NeverEnd", StringComparison.OrdinalIgnoreCase) >= 0) return "∞";

            if (TryGetFiniteFloat(GetMember(lifeRule, "RemainingDuration"), out float remaining))
            {
                return FormatEnemyBuffDuration(Mathf.Max(0f, remaining));
            }

            bool hasDuration = TryGetFiniteFloat(GetMember(lifeRule, "Duration"), out float configuredDuration);
            if (hasDuration && configuredDuration < 0f) return "∞";

            object? timer = GetMember(lifeRule, "Timer");
            if (timer != null
                && TryGetFiniteFloat(GetMember(timer, "duration"), out float timerDuration)
                && TryGetFiniteFloat(GetMember(timer, "time"), out float elapsed))
            {
                if (timerDuration < 0f) return "∞";
                return FormatEnemyBuffDuration(Mathf.Max(0f, timerDuration - elapsed));
            }

            return "--";
        }
        catch
        {
            return "--";
        }
    }

    private static bool TryGetFiniteFloat(object? value, out float result)
    {
        result = 0f;
        if (value == null) return false;
        try
        {
            result = Convert.ToSingle(value, CultureInfo.InvariantCulture);
            return !float.IsNaN(result) && !float.IsInfinity(result);
        }
        catch
        {
            return false;
        }
    }

    private static string FormatEnemyBuffDuration(float seconds) =>
        seconds < 10f
            ? seconds.ToString("0.0", CultureInfo.InvariantCulture) + "s"
            : seconds.ToString("0", CultureInfo.InvariantCulture) + "s";

    private void EnsureEnemyOverlayStyles()
    {
        _spawnPointStyle ??= new GUIStyle(GUI.skin.label)
        {
            alignment = TextAnchor.MiddleCenter,
            fontSize = 15,
            fontStyle = FontStyle.Bold
        };
        _spawnPointStyle.normal.textColor = new Color(0.35f, 0.95f, 1f, 1f);

        _enemyIdStyle ??= new GUIStyle(GUI.skin.label)
        {
            alignment = TextAnchor.MiddleCenter,
            fontSize = 14,
            fontStyle = FontStyle.Bold
        };
        _enemyIdStyle.normal.textColor = Color.yellow;

        _enemyBuffFrameStyle ??= new GUIStyle(GUI.skin.box)
        {
            alignment = TextAnchor.MiddleCenter,
            padding = new RectOffset(1, 1, 1, 1)
        };
        _enemyBuffDurationStyle ??= new GUIStyle(GUI.skin.label)
        {
            alignment = TextAnchor.MiddleCenter,
            fontSize = 11,
            fontStyle = FontStyle.Bold,
            clipping = TextClipping.Clip
        };
        _enemyBuffDurationStyle.normal.textColor = Color.white;
        _enemyBuffStackStyle ??= new GUIStyle(GUI.skin.label)
        {
            alignment = TextAnchor.MiddleCenter,
            fontSize = 10,
            fontStyle = FontStyle.Bold,
            clipping = TextClipping.Clip
        };
        _enemyBuffStackStyle.normal.textColor = new Color(1f, 0.94f, 0.58f, 1f);
        _enemyBuffDetailStyle ??= new GUIStyle(GUI.skin.label)
        {
            alignment = TextAnchor.MiddleCenter,
            fontSize = 9,
            fontStyle = FontStyle.Bold,
            clipping = TextClipping.Clip
        };
        _enemyBuffDetailStyle.normal.textColor = new Color(0.57f, 0.9f, 1f, 1f);
        _enemyBuffFallbackStyle ??= new GUIStyle(GUI.skin.label)
        {
            alignment = TextAnchor.MiddleCenter,
            fontSize = 20,
            fontStyle = FontStyle.Bold
        };
        _enemyBuffFallbackStyle.normal.textColor = Color.white;
        _enemyBuffTooltipStyle ??= new GUIStyle(GUI.skin.box)
        {
            alignment = TextAnchor.MiddleLeft,
            fontSize = 12,
            fontStyle = FontStyle.Bold,
            wordWrap = true,
            padding = new RectOffset(8, 8, 6, 6)
        };
        _enemyBuffTooltipStyle.normal.textColor = Color.white;
    }

    private void DrawSpawnPointMarkers(Camera camera)
    {
        for (int index = 0; index < _spawnPoints.Count; index++)
        {
            SavedSpawnPoint point = _spawnPoints[index];
            Vector3 screen = camera.WorldToScreenPoint(point.Position);
            if (screen.z <= 0f) continue;
            Rect marker = new(screen.x - 90f, Screen.height - screen.y - 30f, 180f, 60f);
            GUI.Label(marker, $"＋\n#{index + 1} ({point.Position.x:0.##}, {point.Position.y:0.##})", _spawnPointStyle);
        }
    }

    private void DrawFieldCatapultDeleteOverlay(Camera camera)
    {
        if (!_fieldCatapultDeleteMode) return;
        GUI.Label(new Rect(12f, 92f, 460f, 32f), "删除模式：左键删除弹射点，Esc 退出", _enemyIdStyle);
        object? input = TryGetSingleton(_inputManagerType!);
        object? hovered = input == null ? null : FindHoveredFieldCatapult(input);
        if (hovered is not Component component || component == null) return;
        Vector3 screen = camera.WorldToScreenPoint(component.transform.position);
        if (screen.z <= 0f) return;
        Rect marker = new(screen.x - 30f, Screen.height - screen.y - 30f, 60f, 60f);
        Color previous = GUI.color;
        GUI.color = new Color(1f, 0.12f, 0.08f, 0.96f);
        const float border = 3f;
        GUI.DrawTexture(new Rect(marker.x, marker.y, marker.width, border), Texture2D.whiteTexture);
        GUI.DrawTexture(new Rect(marker.x, marker.yMax - border, marker.width, border), Texture2D.whiteTexture);
        GUI.DrawTexture(new Rect(marker.x, marker.y, border, marker.height), Texture2D.whiteTexture);
        GUI.DrawTexture(new Rect(marker.xMax - border, marker.y, border, marker.height), Texture2D.whiteTexture);
        GUI.color = previous;
        GUI.Label(marker, "×", _enemyIdStyle);
    }

    private void DrawEnemyBuffIcons(
        IReadOnlyList<EnemyBuffIconSnapshot> buffs,
        float screenX,
        float screenY,
        ref string tooltip)
    {
        int screenColumns = Mathf.Max(1, Mathf.FloorToInt((Screen.width - 16f) / EnemyBuffCellWidth));
        int columns = Mathf.Min(MaxEnemyBuffColumns, Mathf.Min(screenColumns, buffs.Count));
        int rows = Mathf.CeilToInt(buffs.Count / (float)columns);
        float totalHeight = rows * EnemyBuffCellHeight;
        float top = screenY + 6f;
        top = Mathf.Clamp(top, 8f, Mathf.Max(8f, Screen.height - totalHeight - 8f));

        Vector2 mouse = Event.current.mousePosition;
        for (int index = 0; index < buffs.Count; index++)
        {
            int row = index / columns;
            int column = index % columns;
            int rowCount = Mathf.Min(columns, buffs.Count - (row * columns));
            float rowWidth = rowCount * EnemyBuffCellWidth;
            float left = Mathf.Clamp(
                screenX - (rowWidth * 0.5f),
                8f,
                Mathf.Max(8f, Screen.width - rowWidth - 8f));
            float x = left + (column * EnemyBuffCellWidth) + ((EnemyBuffCellWidth - EnemyBuffIconSize) * 0.5f);
            float y = top + (row * EnemyBuffCellHeight);
            Rect iconFrame = new(x, y, EnemyBuffIconSize, EnemyBuffIconSize);
            Rect textureRect = new(x + 2f, y + 2f, EnemyBuffIconSize - 4f, EnemyBuffIconSize - 4f);
            Rect durationRect = new(x - 4f, y + EnemyBuffIconSize + 1f, EnemyBuffIconSize + 8f, 12f);
            Rect detailRect = new(x - 5f, y + EnemyBuffIconSize + 13f, EnemyBuffIconSize + 10f, 12f);
            EnemyBuffIconSnapshot buff = buffs[index];

            GUI.Box(iconFrame, GUIContent.none, _enemyBuffFrameStyle);
            if (buff.Texture != null)
            {
                GUI.DrawTextureWithTexCoords(textureRect, buff.Texture, buff.Uv, true);
            }
            else
            {
                Color previousColor = GUI.color;
                GUI.color = buff.FallbackColor;
                GUI.DrawTexture(textureRect, Texture2D.whiteTexture);
                GUI.color = previousColor;
                GUI.Label(textureRect, "?", _enemyBuffFallbackStyle);
            }
            if (buff.ShowStackCount)
            {
                string stackText = buff.StackCount > 999 ? "999+" : "×" + buff.StackCount;
                Rect stackRect = new(iconFrame.xMax - 19f, iconFrame.y - 3f, 22f, 14f);
                GUI.Box(stackRect, GUIContent.none, _enemyBuffFrameStyle);
                GUI.Label(stackRect, stackText, _enemyBuffStackStyle);
            }
            GUI.Box(durationRect, GUIContent.none, _enemyBuffFrameStyle);
            GUI.Label(durationRect, buff.DurationText, _enemyBuffDurationStyle);
            if (!string.IsNullOrWhiteSpace(buff.DetailText))
                GUI.Label(detailRect, buff.DetailText, _enemyBuffDetailStyle);

            if (iconFrame.Contains(mouse) || durationRect.Contains(mouse) || detailRect.Contains(mouse))
            {
                string stacks = buff.ShowStackCount ? $"\n层数：{buff.StackCount}" : string.Empty;
                string details = string.IsNullOrWhiteSpace(buff.DetailText) ? string.Empty : "\n" + buff.DetailText;
                tooltip = $"{buff.DisplayName} ({buff.Key}){stacks}\n持续时间：{buff.DurationText}{details}";
            }
        }
    }

    private void DrawEnemyBuffTooltip(string tooltip)
    {
        const float width = 260f;
        GUIStyle tooltipStyle = _enemyBuffTooltipStyle ?? GUI.skin.box;
        float height = Mathf.Clamp(tooltipStyle.CalcHeight(new GUIContent(tooltip), width - 16f) + 12f, 52f, 132f);
        Vector2 mouse = Event.current.mousePosition;
        float x = Mathf.Clamp(mouse.x + 14f, 8f, Mathf.Max(8f, Screen.width - width - 8f));
        float y = Mathf.Clamp(mouse.y + 14f, 8f, Mathf.Max(8f, Screen.height - height - 8f));
        GUI.Box(new Rect(x, y, width, height), tooltip, tooltipStyle);
    }

    public void ResetTransientFeatures()
    {
        EnemyIdsVisible = false;
        EnemyBuffsVisible = false;
        InvalidateEnemyOverlayCache();
        _enemyBuffIconSources.Clear();
        _spawnPointCapture = SpawnPointCapture.Idle();
        _spawnPoints.Clear();
        _lastCapturedPointId = string.Empty;
        _fieldCatapultDeleteMode = false;
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
        object? vehicleType = GetMember(vehicle, "vehicleType");
        if (vehicleType != null)
        {
            CopyCatalogPresentation(BuildVehicleCatalogItem(vehicleType), state);
        }
        state["runtimeName"] = component.gameObject.name;
        state["active"] = component.gameObject.activeInHierarchy;
        state["position"] = VectorData(component.transform.position);
        state["attributes"] = BuildEditableAttributes(component, isEnemy: false, receiver: null);
        state["enchantments"] = BuildVehicleEnchantments(vehicle);
        return state;
    }

    private JArray BuildVehicleEnchantments(object vehicle)
    {
        IReadOnlyList<object> availableEnchantments = AllEnchantmentValues();
        JArray result = new();
        foreach (object? module in GetVehicleEvolutionData(vehicle))
        {
            object? enchantment = GetMember(module, "fetterEnum");
            if (enchantment == null || !ContainsEnumValue(availableEnchantments, enchantment)) continue;
            JObject item = new()
            {
                ["id"] = enchantment.ToString() ?? string.Empty,
                ["enumName"] = enchantment.ToString() ?? string.Empty,
                ["level"] = GetInt(module, "level"),
                ["count"] = GetInt(module, "count")
            };
            CopyCatalogPresentation(BuildEnchantmentCatalogItem(enchantment), item);
            result.Add(item);
        }

        return result;
    }

    private JObject BuildVehicleReference(object vehicle)
    {
        Component? component = vehicle as Component;
        return new JObject
        {
            ["vehicleId"] = GetInt(vehicle, "ID"),
            ["instanceId"] = component?.GetInstanceID() ?? 0,
            ["typeId"] = GetMember(vehicle, "vehicleType")?.ToString() ?? string.Empty,
            ["enumName"] = GetMember(vehicle, "vehicleType")?.ToString() ?? string.Empty,
            ["level"] = GetInt(vehicle, "level")
        };
    }

    private JObject BuildEnemyState(EnemyTarget target)
    {
        Vector3 position = target.GameObject.transform.position;
        JObject state = new()
        {
            ["runtimeId"] = target.RuntimeId,
            ["instanceId"] = target.GameObject.GetInstanceID(),
            ["typeId"] = target.TypeId,
            ["enumName"] = target.TypeId,
            ["typeValue"] = target.TypeValue,
            ["runtimeName"] = target.GameObject.name,
            ["health"] = target.Receiver == null ? null : GetNumber(target.Receiver, "Health"),
            ["healthMax"] = target.Receiver == null ? null : GetNumber(target.Receiver, "HealthMax"),
            ["isBoss"] = GetBool(target.Ai, "IsBoss"),
            ["position"] = VectorData(position),
            ["attributes"] = BuildEditableAttributes(target.Ai, isEnemy: true, target.Receiver)
        };
        object enemyType = Enum.ToObject(_aiIdType!, target.TypeValue);
        CopyCatalogPresentation(BuildEnemyCatalogItem(enemyType), state);
        return state;
    }

    private static void CopyCatalogPresentation(JObject catalogItem, JObject destination)
    {
        foreach (string name in new[] { "name", "enumName", "iconBase64", "iconFile", "iconSha256" })
        {
            destination[name] = catalogItem[name]?.DeepClone() ?? JValue.CreateNull();
        }
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
                double minimum = string.Equals(parameter.Key, "health", StringComparison.Ordinal)
                    ? 1d
                    : -MaxAttributeMagnitude;
                result.Add(AttributeData(
                    parameter.Key,
                    ResolveAttributeDisplayName(parameter.Key),
                    parameter.Kind,
                    parameter.Value,
                    parameter.BaseValue,
                    minimum,
                    MaxAttributeMagnitude));
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
            if (!TryParseBattleMemoryId(attributeId, out _))
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
                                         && string.Equals(attributeId, "health", StringComparison.Ordinal);
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
            if (string.IsNullOrWhiteSpace(key) || !TryParseBattleMemoryId(key, out _)) continue;
            if (!TryReadParameter(entry.Value, out NumericParameter? parameter)) continue;
            parameter!.Key = key;
            result.Add(parameter);
        }

        result.Sort((left, right) => string.Compare(left.Key, right.Key, StringComparison.Ordinal));
        return result;
    }

    private object? FindBlackboardParameter(object battleSystem, string key)
    {
        object? memory = GetMember(battleSystem, "memoryBlackboard");
        MethodInfo? getAll = memory == null ? null : FindMethod(memory.GetType(), "GetAllValue");
        if (getAll?.Invoke(memory, null) is not IDictionary values) return null;
        foreach (DictionaryEntry entry in values)
        {
            if (string.Equals(entry.Key?.ToString(), key, StringComparison.Ordinal)) return entry.Value;
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

    private IList GetVehicleEvolutionData(object vehicle)
    {
        MethodInfo? method = FindMethod(_vehicleManagerType, "GetEvolutionData", _vehicleInterfaceType!);
        return method?.Invoke(null, new[] { vehicle }) as IList
               ?? throw new InvalidOperationException("无法读取战车当前附魔。");
    }

    private IList CloneFetterModules(IEnumerable source)
    {
        IList result = CreateRuntimeList(_fetterModuleDataType!);
        foreach (object? module in source)
        {
            if (module == null) continue;
            object clone = Activator.CreateInstance(_fetterModuleDataType!)
                           ?? throw new InvalidOperationException("无法复制附魔模块数据。");
            SetMemberValue(clone, "fetterEnum", GetMember(module, "fetterEnum")!);
            SetMemberValue(clone, "level", GetInt(module, "level"));
            SetMemberValue(clone, "count", GetInt(module, "count"));
            result.Add(clone);
        }

        return result;
    }

    private static int FindFetterModuleIndex(IList modules, object enchantment)
    {
        for (int index = 0; index < modules.Count; index++)
        {
            if (Equals(GetMember(modules[index], "fetterEnum"), enchantment)) return index;
        }

        return -1;
    }

    private static int CountConfiguredEnchantments(IList modules, IReadOnlyList<object> configuredEnchantments)
    {
        int count = 0;
        for (int index = 0; index < modules.Count; index++)
        {
            object? value = GetMember(modules[index], "fetterEnum");
            if (value != null && ContainsEnumValue(configuredEnchantments, value)) count++;
        }

        return count;
    }

    private void ApplyVehicleFetterModules(object vehicle, IList updatedModules, IList originalModules)
    {
        Type runtimeListType = typeof(List<>).MakeGenericType(_fetterModuleDataType!);
        MethodInfo? clear = FindMethod(_vehicleManagerType, "ClearVehicleFetterModuleBuffs", _vehicleInterfaceType!);
        MethodInfo? apply = FindMethod(
            _vehicleManagerType,
            "ApplyEvolutionBuffToNewVehicle",
            _vehicleInterfaceType!,
            runtimeListType);
        if (clear == null || apply == null)
        {
            throw new InvalidOperationException("当前游戏版本缺少重建战车附魔的入口。");
        }

        clear.Invoke(null, new[] { vehicle });
        try
        {
            apply.Invoke(null, new object[] { vehicle, updatedModules });
        }
        catch (Exception applyException)
        {
            try
            {
                clear.Invoke(null, new[] { vehicle });
                apply.Invoke(null, new object[] { vehicle, originalModules });
                RefreshVehicleAfterEnchantmentChange(vehicle);
            }
            catch (Exception rollbackException)
            {
                throw new InvalidOperationException(
                    "附魔写入失败，且恢复原附魔时也发生异常：" + Unwrap(rollbackException).Message,
                    Unwrap(applyException));
            }

            throw new InvalidOperationException(
                "附魔写入失败，已恢复原附魔：" + Unwrap(applyException).Message,
                Unwrap(applyException));
        }

        RefreshVehicleAfterEnchantmentChange(vehicle);
        RequestGameSave();
    }

    private void RefreshVehicleAfterEnchantmentChange(object vehicle)
    {
        object? motionSubDriver = GetMember(vehicle, "motionSubDriver");
        if (motionSubDriver != null && GetBool(motionSubDriver, "IsInTrain"))
        {
            object? motionDriver = GetMember(vehicle, "motionDriver");
            object? trainController = GetMember(motionDriver, "SubDriverController");
            FindMethod(trainController?.GetType(), "UpdateTrainInfo")?.Invoke(trainController, null);
        }

        MethodInfo updateState = FindMethod(
                                     _updateVehicleStateEventHandlerType,
                                     "Throw",
                                     _vehicleControllerType!)
                                 ?? throw new InvalidOperationException("当前游戏版本缺少战车状态刷新入口。");
        updateState.Invoke(null, new[] { vehicle });
    }

    private void RequestGameSave(string source = nameof(SetVehicleEnchantment))
    {
        object saveHandler = GetRequiredSingleton(_guiSaveHandlerType!, "GuiSaveHandler");
        FindMethod(saveHandler.GetType(), "SaveDurationInValidGameTick", typeof(string), typeof(string), typeof(int))
            ?.Invoke(saveHandler, new object[] { string.Empty, source, 0 });
    }

    private JArray BuildOwnedRelics()
    {
        JArray result = new();
        object? manager = TryGetSingleton(_superModuleManagerType!);
        if (manager == null || GetMember(manager, "superModules") is not IDictionary relics) return result;
        foreach (DictionaryEntry entry in relics)
        {
            if (entry.Key == null) continue;
            JObject item = BuildRelicCatalogItem(entry.Key);
            item["relicId"] = entry.Key.ToString() ?? string.Empty;
            item["count"] = entry.Value is ICollection collection ? collection.Count : 0;
            result.Add(item);
        }

        return result;
    }

    private JArray BuildOwnedConsumables()
    {
        JArray result = new();
        foreach ((object value, int count) in SnapshotDisposableCounts())
        {
            if (IsCatapultPoint(value)) continue;
            JObject item = BuildDisposableCatalogItem(value);
            item["disposableId"] = value.ToString() ?? string.Empty;
            item["count"] = count;
            result.Add(item);
        }
        return result;
    }

    private static int ReadDisposableCapacity(object manager)
    {
        object? handler = GetMember(manager, "m_intHandler");
        int value = handler == null ? 0 : GetInt(handler, "Value");
        object? parameter = handler == null ? null : GetMember(handler, "m_parameter");
        if (value <= 0 && parameter != null) value = GetInt(parameter, "Value");
        return value > 0 ? value : 5;
    }

    private JArray BuildOwnedCatapultPoints()
    {
        JArray result = new();
        Dictionary<string, int> legacyLedgerCounts = new(StringComparer.Ordinal);
        object? pointDataUi = TryGetSingleton(_pointDataUiType!);
        if (pointDataUi != null && GetMember(pointDataUi, "PointDatas") is IList pointDatas)
        {
            for (int index = 0; index < pointDatas.Count; index++)
            {
                object? row = pointDatas[index];
                if (row == null) continue;
                bool isAttribute = GetBool(row, "isAttribute");
                string disposableId = isAttribute ? "FreePoint_Attribute" : "FreePoint";
                object disposableEnum = ParseEnum(_disposableType!, disposableId, "弹射点类型");
                JObject item = BuildCatalogItem(
                    disposableEnum,
                    TryGetDisposableData(disposableEnum, out object? data) ? data : null,
                    "name",
                    "icon",
                    "弹射点");
                string key = GetMember(row, "key")?.ToString() ?? string.Empty;
                int count = GetInt(row, "count");
                legacyLedgerCounts[disposableId] = legacyLedgerCounts.TryGetValue(disposableId, out int old) ? old + count : count;
                item["catapultPointId"] = string.IsNullOrWhiteSpace(key) ? $"point-{index}" : key;
                item["disposableId"] = disposableId;
                item["isAttribute"] = isAttribute;
                item["count"] = count;
                JArray enchantments = new();
                if (GetMember(row, "hasBuffFlag") is IEnumerable flags)
                {
                    foreach (object? flag in flags)
                    {
                        if (flag != null) enchantments.Add(flag.ToString());
                    }
                }
                item["buffs"] = enchantments;
                result.Add(item);
            }
        }

        foreach ((object value, int count) in SnapshotDisposableCounts())
        {
            if (!IsCatapultPoint(value)) continue;
            string id = value.ToString() ?? string.Empty;
            if (IsLegacyCatapultPointId(id) && legacyLedgerCounts.ContainsKey(id)) continue;
            JObject item = BuildCatapultPointCatalogItem(value);
            item["catapultPointId"] = "bag:" + id;
            item["disposableId"] = id;
            item["isAttribute"] = string.Equals(id, "FreePoint_Attribute", StringComparison.OrdinalIgnoreCase);
            item["count"] = count;
            item["buffs"] = new JArray();
            result.Add(item);
        }

        return result;
    }

    private List<(object Value, int Count)> SnapshotDisposableCounts()
    {
        List<(object Value, int Count)> result = new();
        object? manager = TryGetSingleton(_disposableManagerType!);
        if (manager == null || GetMember(manager, "disposableObjects") is not IEnumerable source) return result;
        MethodInfo? isStackable = FindMethod(manager.GetType(), "IsStackable", _disposableType!);
        MethodInfo? getStackCount = FindMethod(manager.GetType(), "GetStackCount", _disposableType!);
        HashSet<long> stackedSeen = new();
        foreach (object? item in source)
        {
            if (item == null) continue;
            object? value = GetMember(item, "disposableEnum");
            if (value == null) continue;
            long numeric = Convert.ToInt64(value, CultureInfo.InvariantCulture);
            bool stacked = isStackable?.Invoke(manager, new[] { value }) is true;
            if (stacked && !stackedSeen.Add(numeric)) continue;
            int count = stacked
                ? Convert.ToInt32(getStackCount?.Invoke(manager, new[] { value }) ?? 0, CultureInfo.InvariantCulture)
                : 1;
            if (count > 0) result.Add((value, count));
        }
        return result;
    }

    private List<object> SnapshotFieldCatapultPoints()
    {
        List<object> result = new();
        object? manager = TryGetSingleton(_catapultManagerType!);
        if (manager == null || GetMember(manager, "Catapults") is not IEnumerable source) return result;
        foreach (object? item in source)
        {
            if (item is Component component && component != null) result.Add(item);
        }
        return result;
    }

    private JArray BuildFieldCatapultPoints()
    {
        JArray result = new();
        foreach (object item in SnapshotFieldCatapultPoints()) result.Add(BuildFieldCatapultPoint(item));
        return result;
    }

    private JObject BuildFieldCatapultPoint(object catapult)
    {
        Component component = catapult as Component
                              ?? throw new InvalidOperationException("场上弹射点不是有效的 Unity 组件。");
        Component? linePoint = component.GetComponent(_linePointType!);
        object? disposableEnum = GetMember(catapult, "RecycleDisposableEnum");
        string disposableId = disposableEnum?.ToString() ?? string.Empty;
        JObject state = disposableEnum == null
            ? new JObject()
            : BuildCatalogItem(
                disposableEnum,
                TryGetDisposableData(disposableEnum, out object? data) ? data : null,
                "name",
                "icon",
                "场上弹射点");
        state["runtimeId"] = FieldCatapultRuntimeId(catapult);
        state["pointId"] = linePoint == null ? -1 : GetInt(linePoint, "ID");
        state["disposableId"] = disposableId;
        state["position"] = VectorData(component.transform.position);
        return state;
    }

    private static string FieldCatapultRuntimeId(object catapult) =>
        catapult is Component component ? component.GetInstanceID().ToString(CultureInfo.InvariantCulture) : string.Empty;

    private void DeleteFieldCatapult(object catapult)
    {
        object pointDataUi = GetRequiredSingleton(_pointDataUiType!, "弹射点数据界面");
        IList? pointDatas = GetMember(pointDataUi, "PointDatas") as IList;
        Dictionary<string, int> before = pointDatas == null ? new Dictionary<string, int>() : SnapshotPointDataCounts(pointDatas);
        object creator = GetRequiredSingleton(_catapultCreatorType!, "CatapultCreator");
        MethodInfo destroy = FindMethod(creator.GetType(), "DestroyStation", typeof(GameObject))
                             ?? throw new MissingMethodException(creator.GetType().FullName, "DestroyStation");
        GameObject gameObject = ((Component)catapult).gameObject;
        destroy.Invoke(creator, new object[] { gameObject });

        // DestroyStation broadcasts the normal recycle event, which adds one point
        // to the backpack. Remove that event-produced delta so "delete" is not a recycle.
        if (pointDatas != null)
        {
            RollBackAddedPointData(pointDatas, before);
            RefreshPointDataUi(pointDataUi, pointDatas);
        }
    }

    private static Dictionary<string, int> SnapshotPointDataCounts(IList pointDatas)
    {
        Dictionary<string, int> result = new(StringComparer.Ordinal);
        foreach (object? row in pointDatas)
        {
            if (row == null) continue;
            string key = GetMember(row, "key")?.ToString() ?? string.Empty;
            if (!string.IsNullOrWhiteSpace(key)) result[key] = GetInt(row, "count");
        }
        return result;
    }

    private static void RollBackAddedPointData(IList pointDatas, IReadOnlyDictionary<string, int> before)
    {
        for (int index = pointDatas.Count - 1; index >= 0; index--)
        {
            object? row = pointDatas[index];
            if (row == null) continue;
            string key = GetMember(row, "key")?.ToString() ?? string.Empty;
            int oldCount = before.TryGetValue(key, out int value) ? value : 0;
            int current = GetInt(row, "count");
            if (current <= oldCount) continue;
            SetMemberValue(row, "count", current - 1);
            if (current - 1 <= 0) pointDatas.RemoveAt(index);
            return;
        }
    }

    private static void RefreshPointDataUi(object pointDataUi, IList pointDatas)
    {
        FindMethod(pointDataUi.GetType(), "Load", pointDatas.GetType())?.Invoke(pointDataUi, new object[] { pointDatas });
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
        CollectEnemyTargets(result);
        return result;
    }

    private void CollectEnemyTargets(List<EnemyTarget> result)
    {
        result.Clear();
        object? creator = TryGetSingleton(_agentCreatorType!);
        if (creator == null || GetMember(creator, "enemyAgents") is not IEnumerable source) return;
        foreach (object? item in source)
        {
            if (item is not GameObject gameObject || gameObject == null) continue;
            if (TryBuildEnemyTarget(gameObject, out EnemyTarget? target)) result.Add(target!);
        }
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
        RequireMember(_cheatManagerType, "cheatVehiclePanelCfg");
        RequireMember(_cheatVehiclePanelCfgType, "vehicleTypes");
        RequireSingletonAccessor(_vehicleManagerType);
        RequireMember(_vehicleManagerType, "MainVehicles");
        RequireMethodContract(_vehicleManagerType, "GetNewMainRazor", _vehicleType);
        RequireMethodContract(_vehicleManagerType, "DeleteVehicle", _vehicleInterfaceType);
        Type? fetterListType = _fetterModuleDataType == null
            ? null
            : typeof(List<>).MakeGenericType(_fetterModuleDataType);
        RequireMethodContract(_vehicleManagerType, "GetCustomNewMainRazor", _vehicleType, fetterListType);
        RequireMethodContract(_vehicleManagerType, "GetEvolutionData", _vehicleInterfaceType);
        RequireMethodContract(_vehicleManagerType, "ClearVehicleFetterModuleBuffs", _vehicleInterfaceType);
        RequireMethodContract(
            _vehicleManagerType,
            "ApplyEvolutionBuffToNewVehicle",
            _vehicleInterfaceType,
            fetterListType);
        RequireMethodContract(_vehicleManagerType, "EndVehicleGetMode");
        RequireSingletonAccessor(_vehicleDataManagerType);
        RequireMethodContract(_vehicleDataManagerType, "GetAllMainRazorComponent");
        RequireMember(_basicVehicleComponentType, "vehicleType");
        RequireMethodContract(_updateVehicleStateEventHandlerType, "Throw", _vehicleControllerType);
        RequireMember(_vehicleControllerType, "ID");
        RequireMember(_vehicleControllerType, "vehicleType");
        RequireMember(_vehicleControllerType, "level");
        RequireSingletonAccessor(_fetterInfoCfgType);
        RequireMember(_fetterInfoCfgType, "fetterTypes");
        RequireMethodContract(
            _fetterInfoCfgType,
            "TryGetDetailData",
            _fetterType,
            _fetterDetailDataType?.MakeByRefType());
        RequireMember(_fetterDetailDataType, "enchantmentWordTextName");
        RequireMember(_fetterDetailDataType, "enchantmentWordText");
        RequireMember(_fetterDetailDataType, "icon");
        RequireMember(_fetterModuleDataType, "fetterEnum");
        RequireMember(_fetterModuleDataType, "level");
        RequireMember(_fetterModuleDataType, "count");

        RequireSingletonAccessor(_disposableManagerType);
        RequireMethodContract(_disposableManagerType, "TryGetDisposable", _disposableType);
        RequireMethodContract(_disposableManagerType, "TryConsumeDisposable", _disposableType);
        RequireMember(_disposableManagerType, "disposableObjects");
        RequireMember(_disposableObjectType, "disposableEnum");
        RequireSingletonAccessor(_infoManagerType);
        RequireMethodContract(_infoManagerType, "GetVehicleDescription", _vehicleType);
        RequireMember(_razorDescriptionType, "name");
        RequireMember(_razorDescriptionType, "sprite");
        RequireMember(_disposableDataType, "name");
        RequireMember(_disposableDataType, "icon");
        RequireMember(_disposableDataType, "description");
        RequireMember(_superModuleDataType, "name");
        RequireMember(_superModuleDataType, "icon");
        RequireMember(_superModuleDataType, "description");
        RequireSingletonAccessor(_superModuleManagerType);
        RequireMember(_superModuleManagerType, "superModules");
        RequireMethodContract(_superModuleManagerType, "GetSuperModule", _superModuleType, typeof(bool));
        RequireMethodContract(_superModuleManagerType, "TryRemoveSuperModule", _superModuleType);

        RequireSingletonAccessor(_gameControllerType);
        RequireMember(_gameControllerType, "MainBase");
        RequireMember(_gameControllerType, "GameIsOver");
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
        RequireMember(_damageReceiverType, "CanNotBeAttack");

        RequireSingletonAccessor(_waveControllerType);
        RequireMember(_waveControllerType, "templateLock");
        RequireMember(_waveControllerType, "m_isInWave");
        RequireMember(_waveControllerType, "m_isBossWave");
        RequireMethodContract(_waveControllerType, "WaveOver");
        RequireMethodContract(_waveControllerType, "AddEnemyCount");
        RequireSingletonAccessor(_waveProgressControllerType);
        RequireMember(_waveProgressControllerType, "CurrentAILevel");

        RequireSingletonAccessor(_agentCreatorType);
        RequireMember(_agentCreatorType, "enemyAgents");
        RequireMethodContract(_agentCreatorType, "ClearDeferredSpawnQueue");
        RequireMethodContract(_agentCreatorType, "ClearAllEnemy");
        RequireMethodContract(_agentCreatorType, "RecycleAI", typeof(GameObject));
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
        RequireSingletonAccessor(_aiInformationDataSoType);
        RequireMethodContract(_aiInformationDataSoType, "GetAIInformationData", _aiIdType);
        RequireMember(_aiInformationDataType, "name");
        RequireMember(_aiInformationDataType, "icon");
        RequireMember(_basicAiType, "aiID");
        RequireMember(_basicAiType, "IsBoss");
        RequireMember(_basicAiType, "SendsDeathMessage");
        RequireMember(_basicAiType, "DamageReceiver");
        RequireMember(_basicAiType, "BattleSystem");
        RequireMember(_basicAiType, "colliderOn");
        RequireMember(_basicAiType, "AIIsRunning");
        RequireMember(_basicAiType, "NeedToBattle");
        RequireMember(_basicAiType, "HpSliderTransform");
        RequireMethodContract(_basicAiType, "SetSendMessage", typeof(bool));
        RequireMember(_basicAgentType, "AgentRegisterType");

        RequireMember(_buffAcceptorType, "buffMr");
        if (_buffManagerType != null && _getBuffsMethod == null)
        {
            AddMissing((_buffManagerType.FullName ?? _buffManagerType.Name) + ".GetBuffs(Func<Buff,bool>)");
        }
        RequireMember(_buffType, "Key");
        RequireMember(_buffType, "IsEnd");
        RequireMember(_buffType, "LifeRule");
        RequireSingletonAccessor(_buffDataPathSoType);
        RequireMember(_buffDataPathSoType, "buffDisplayData");
        RequireMember(_buffDisplayDataType, "sprite");
        RequireMember(_buffDisplayDataType, "title");

        RequireMember(_battleSystemType, "memoryBlackboard");
        RequireMember(_battleSystemType, "TimeScale");
        RequireMember(_battleSystemType, "RuntimeHandle");
        RequireMember(_battleSystemType, "battleGroup");
        RequireMember(_battleRuntimeHandleType, "Id");
        RequireMember(_battleRuntimeHandleType, "LifetimeVersion");
        RequireMember(_battleRuntimeHandleType, "IsDisposed");
        RequireSingletonAccessor(_battleAttributeCfgType);
        RequireMember(_battleAttributeCfgType, "attributeInfos");
        RequireMember(_attributeShowInfoType, "attributeName");
        RequireMethodContract(_blackboardMemoryType, "GetAllValue");
        RequireMember(_generalFloatParameterType, "Value");
        RequireMethodContract(_generalFloatParameterType, "GetRealValue");
        RequireMethodContract(_generalFloatParameterType, "SetValue", typeof(float));
        RequireMember(_generalIntParameterType, "Value");
        RequireMethodContract(_generalIntParameterType, "GetRealValue");
        RequireMethodContract(_generalIntParameterType, "SetValue", typeof(int));

        RequireSingletonAccessor(_inputManagerType);
        RequireMember(_inputManagerType, "hasUsed");
        RequireMember(_inputManagerType, "currentWorldMousePosition");
        RequireMethodContract(_inputManagerType, "GetKeyPressStateRO", _inputKeyType);
        RequireMethodContract(_inputManagerType, "GetMousePressStateRO", _mouseKeyType);
        RequireMethodContract(_inputManagerType, "UseInputOnly");
        RequireMember(_pressStateRoType, "isPressed");
        RequireMember(_pressStateRoType, "wasPressedThisFrame");
        RequireStaticMember(_defaultUiInteractionType, "Current");
        RequireMember(_defaultUiInteractionType, "isInUI");
        RequireSingletonAccessor(_mapPosManagerType);
        RequireMember(_mapPosManagerType, "rect");
        RequireStaticMember(_pointDataUiType, "Instance");
        RequireMember(_pointDataUiType, "PointDatas");
        RequireMethodContract(_pointDataUiType, "AddPointData", typeof(bool));
        Type? pointDataListType = _disposablePointDataType == null
            ? null
            : typeof(List<>).MakeGenericType(_disposablePointDataType);
        RequireMethodContract(_pointDataUiType, "Load", pointDataListType);
        RequireMember(_disposablePointDataType, "key");
        RequireMember(_disposablePointDataType, "count");
        RequireMember(_disposablePointDataType, "isAttribute");
        RequireMember(_disposablePointDataType, "hasBuffFlag");
        RequireSingletonAccessor(_catapultCreatorType);
        RequireMethodContract(_catapultCreatorType, "RecycleStation", typeof(GameObject), typeof(bool));
        RequireMethodContract(_catapultCreatorType, "DestroyStation", typeof(GameObject));
        RequireSingletonAccessor(_catapultManagerType);
        RequireMember(_catapultManagerType, "Catapults");
        RequireMember(_catapultBaseType, "RecycleDisposableEnum");
        RequireMember(_linePointType, "ID");
        RequireSingletonAccessor(_guiSaveHandlerType);
        RequireMethodContract(
            _guiSaveHandlerType,
            "SaveDurationInValidGameTick",
            typeof(string),
            typeof(string),
            typeof(int));
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

    private void RequireStaticMember(Type? type, string name)
    {
        if (type == null) return;
        const BindingFlags flags = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static | BindingFlags.FlattenHierarchy;
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
        try
        {
            PropertyInfo? property = type.GetProperty("Instance", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static | BindingFlags.FlattenHierarchy);
            if (property != null) return property.GetValue(null, null);
            FieldInfo? field = type.GetField("Instance", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static | BindingFlags.FlattenHierarchy)
                               ?? type.GetField("instance", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static | BindingFlags.FlattenHierarchy);
            return field?.GetValue(null);
        }
        catch (TargetInvocationException exception) when (exception.InnerException is NullReferenceException)
        {
            // Several game singleton getters dereference the scene module before
            // it exists. A Try method treats that transition as "not ready".
            return null;
        }
    }

    private static object? GetMember(object? target, string name)
    {
        if (target == null) return null;
        Type type = target.GetType();
        PropertyInfo? property = type.GetProperty(name, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.FlattenHierarchy);
        if (property != null) return property.GetValue(target, null);
        const BindingFlags flags = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.DeclaredOnly;
        for (Type? current = type; current != null; current = current.BaseType)
        {
            FieldInfo? field = current.GetField(name, flags);
            if (field != null) return field.GetValue(target);
        }
        return null;
    }

    private static object? GetStaticMember(Type type, string name)
    {
        const BindingFlags flags = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static | BindingFlags.FlattenHierarchy;
        PropertyInfo? property = type.GetProperty(name, flags);
        if (property != null) return property.GetValue(null, null);
        return type.GetField(name, flags)?.GetValue(null);
    }

    private static void SetMemberValue(object target, string name, object value)
    {
        Type type = target.GetType();
        const BindingFlags flags = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.FlattenHierarchy;
        PropertyInfo? property = type.GetProperty(name, flags);
        if (property?.CanWrite == true)
        {
            property.SetValue(target, value, null);
            return;
        }

        FieldInfo? field = type.GetField(name, flags);
        if (field == null) throw new MissingMemberException(type.FullName, name);
        field.SetValue(target, value);
    }

    private static IList CreateRuntimeList(Type itemType)
    {
        Type listType = typeof(List<>).MakeGenericType(itemType);
        return (IList)(Activator.CreateInstance(listType)
                       ?? throw new InvalidOperationException("无法创建运行时列表：" + listType.FullName));
    }

    private static MethodInfo? FindMethod(Type? type, string name, params Type[] parameters) =>
        type?.GetMethod(
            name,
            BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static | BindingFlags.FlattenHierarchy,
            null,
            parameters,
            null);

    private static MethodInfo? FindMethodByParameterCount(Type? type, string name, int parameterCount) =>
        type?.GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static | BindingFlags.FlattenHierarchy)
            .FirstOrDefault(method =>
                string.Equals(method.Name, name, StringComparison.Ordinal)
                && method.GetParameters().Length == parameterCount);

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

    private IReadOnlyList<object> AllVehicleValues(bool reportUnavailable = false)
    {
        if (_availableVehicleValues != null)
        {
            if (reportUnavailable && _unavailableVehicleValues.Count > 0)
            {
                LogCatalogWarning("战车", "缺少可生成的 BasicVehicleComponent", _unavailableVehicleValues);
            }
            return _availableVehicleValues;
        }

        Type vehicleType = _vehicleType!;
        object configuration = GetRequiredCheatVehicleConfiguration();
        if (GetMember(configuration, "vehicleTypes") is not IEnumerable configuredVehicleTypes)
        {
            throw new InvalidOperationException("当前游戏作弊面板的战车名单尚未初始化，请稍后点击“刷新目录”重试。");
        }

        object manager = GetRequiredSingleton(_vehicleDataManagerType!, "VehicleDataManager");
        MethodInfo getAllComponents = FindMethod(manager.GetType(), "GetAllMainRazorComponent")
                                      ?? throw new MissingMethodException(
                                          manager.GetType().FullName,
                                          "GetAllMainRazorComponent");
        object? configuredComponents = getAllComponents.Invoke(manager, null);
        if (configuredComponents is not IEnumerable components)
        {
            throw new InvalidOperationException("当前游戏的可生成战车组件目录尚未初始化。");
        }

        _availableVehicleValues = FilterRuntimeVehicleValues(
            configuredVehicleTypes.Cast<object>(),
            components,
            vehicleType,
            out _unavailableVehicleValues);
        IndexVehicleCatalogOrder(_availableVehicleValues);
        if (reportUnavailable && _unavailableVehicleValues.Count > 0)
        {
            LogCatalogWarning("战车", "缺少可生成的 BasicVehicleComponent", _unavailableVehicleValues);
        }

        if (_availableVehicleValues.Count == 0)
        {
            _availableVehicleValues = null;
            throw new InvalidOperationException("当前游戏作弊面板没有配置能够由原生战车系统创建的战车。");
        }

        return _availableVehicleValues;
    }

    private IReadOnlyList<object> AllEnchantmentValues(bool reportUnavailable = false)
    {
        if (_availableEnchantmentValues != null)
        {
            if (reportUnavailable && _unavailableEnchantmentValues.Count > 0)
            {
                LogCatalogWarning("附魔", "缺少有效详情或 fetterTypes 类型映射", _unavailableEnchantmentValues);
            }
            return _availableEnchantmentValues;
        }

        object configuration = GetRequiredSingleton(_fetterInfoCfgType!, "SO_FetterInfoCfg");
        if (GetMember(configuration, "fetterTypes") is not IDictionary configuredTypes)
        {
            throw new InvalidOperationException("当前游戏的附魔类型映射尚未初始化。");
        }

        MethodInfo tryGetDetailData = FindMethod(
                                               configuration.GetType(),
                                               "TryGetDetailData",
                                               _fetterType!,
                                               _fetterDetailDataType!.MakeByRefType())
                                           ?? throw new MissingMethodException(
                                               configuration.GetType().FullName,
                                               "TryGetDetailData");
        _availableEnchantmentValues = FilterRuntimeEnchantmentValues(
            AllEnumValues(_fetterType!),
            configuredTypes,
            value => TryGetEnchantmentDetailData(configuration, tryGetDetailData, value, out _),
            out _unavailableEnchantmentValues);
        if (reportUnavailable && _unavailableEnchantmentValues.Count > 0)
        {
            LogCatalogWarning("附魔", "缺少有效详情或 fetterTypes 类型映射", _unavailableEnchantmentValues);
        }

        if (_availableEnchantmentValues.Count == 0)
        {
            _availableEnchantmentValues = null;
            throw new InvalidOperationException("当前游戏没有配置完整且能够安全生效的附魔。");
        }

        return _availableEnchantmentValues;
    }

    private void InvalidateRuntimeCatalogCache()
    {
        _availableVehicleValues = null;
        _unavailableVehicleValues = Array.Empty<object>();
        _vehicleTypeOrders.Clear();
        _vehicleFamilyOrders.Clear();
        _availableEnchantmentValues = null;
        _unavailableEnchantmentValues = Array.Empty<object>();
    }

    private static IReadOnlyList<object> FilterRuntimeVehicleValues(
        IEnumerable<object> configuredValues,
        IEnumerable configuredComponents,
        Type enumType,
        out IReadOnlyList<object> unavailable)
    {
        HashSet<long> configured = new();
        foreach (object? component in configuredComponents)
        {
            if (component == null) continue;
            object? value = GetMember(component, "vehicleType");
            if (value == null || value.GetType() != enumType) continue;
            configured.Add(Convert.ToInt64(value, CultureInfo.InvariantCulture));
        }

        List<object> availableValues = new();
        List<object> unavailableValues = new();
        HashSet<long> seen = new();
        foreach (object value in configuredValues)
        {
            if (value == null || value.GetType() != enumType) continue;
            string id = value.ToString() ?? string.Empty;
            long numeric = Convert.ToInt64(value, CultureInfo.InvariantCulture);
            if (!seen.Add(numeric)) continue;
            if (string.Equals(id, "None", StringComparison.OrdinalIgnoreCase)
                || string.Equals(id, "Train_Head", StringComparison.OrdinalIgnoreCase))
            {
                unavailableValues.Add(value);
                continue;
            }
            if (configured.Contains(numeric)) availableValues.Add(value);
            else unavailableValues.Add(value);
        }

        unavailable = unavailableValues;
        return availableValues;
    }

    private object GetRequiredCheatVehicleConfiguration()
    {
        foreach (UnityEngine.Object manager in Resources.FindObjectsOfTypeAll(_cheatManagerType!))
        {
            object? configuration = GetMember(manager, "cheatVehiclePanelCfg");
            if (configuration is UnityEngine.Object unityConfiguration && unityConfiguration != null)
            {
                return configuration;
            }
        }

        UnityEngine.Object? directConfiguration = Resources.FindObjectsOfTypeAll(_cheatVehiclePanelCfgType!)
            .FirstOrDefault(candidate => candidate != null);
        return directConfiguration
               ?? throw new InvalidOperationException(
                   "当前游戏作弊面板战车配置尚未加载，请稍后点击“刷新目录”重试。");
    }

    private void IndexVehicleCatalogOrder(IEnumerable<object> values)
    {
        _vehicleTypeOrders.Clear();
        _vehicleFamilyOrders.Clear();
        foreach (object value in values)
        {
            string enumName = value.ToString() ?? string.Empty;
            string type = VehicleTypeFamily(enumName);
            string family = VehicleFamily(enumName, out _);
            if (!_vehicleTypeOrders.ContainsKey(type)) _vehicleTypeOrders[type] = _vehicleTypeOrders.Count;
            if (!_vehicleFamilyOrders.ContainsKey(family)) _vehicleFamilyOrders[family] = _vehicleFamilyOrders.Count;
        }
    }

    private static IReadOnlyList<object> FilterRuntimeEnchantmentValues(
        IEnumerable<object> enumValues,
        IDictionary configuredTypes,
        Func<object, bool> hasDetail,
        out IReadOnlyList<object> unavailable)
    {
        List<object> availableValues = new();
        List<object> unavailableValues = new();
        foreach (object value in DistinctEnumValues(enumValues))
        {
            if (configuredTypes.Contains(value) && hasDetail(value)) availableValues.Add(value);
            else unavailableValues.Add(value);
        }

        unavailable = unavailableValues;
        return availableValues;
    }

    private static bool TryGetEnchantmentDetailData(
        object configuration,
        MethodInfo tryGetDetailData,
        object value,
        out object? detail)
    {
        object?[] invokeArguments = { value, null };
        bool succeeded = tryGetDetailData.Invoke(configuration, invokeArguments) is true;
        detail = invokeArguments[1];
        return succeeded && detail != null;
    }

    private void LogCatalogWarning(string category, string reason, IReadOnlyCollection<object> values)
    {
        const int previewLimit = 12;
        string preview = string.Join("、", values.Take(previewLimit).Select(value => value.ToString()));
        string remainder = values.Count > previewLimit ? $"，另有 {values.Count - previewLimit} 项" : string.Empty;
        _warningLogger?.Invoke($"作弊目录已跳过 {values.Count} 个不可用{category}（{reason}）：{preview}{remainder}。");
    }

    private static IReadOnlyList<object> AllEnumValues(Type enumType) =>
        DistinctEnumValues(
            Enum.GetValues(enumType)
                .Cast<object>()
                .Where(value => !string.Equals(value.ToString(), "None", StringComparison.OrdinalIgnoreCase)));

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

    private static JArray CatalogItems(IEnumerable<object> values, Func<object, JObject> builder)
    {
        JArray result = new();
        foreach (object value in values)
        {
            result.Add(builder(value));
        }

        return result;
    }

    private JObject BuildVehicleCatalogItem(object value)
    {
        object? data = InvokeInfoManager("GetVehicleDescription", _vehicleType!, value);
        JObject item = BuildCatalogItem(value, data, "name", "sprite", "战车");
        string enumName = value.ToString() ?? string.Empty;
        string family = VehicleFamily(enumName, out int level);
        string type = VehicleTypeFamily(enumName);
        int typeOrder = _vehicleTypeOrders.TryGetValue(type, out int configuredTypeOrder)
            ? configuredTypeOrder
            : VehicleTypeOrder(_vehicleType!, type);
        int familyOrder = _vehicleFamilyOrders.TryGetValue(family, out int configuredFamilyOrder)
            ? configuredFamilyOrder
            : EnumGroupOrder(_vehicleType!, family);
        ApplyGrouping(item, "vehicle:" + family, family, familyOrder, level);
        item["typeKey"] = type;
        item["typeName"] = VehicleTypeChineseNames.TryGetValue(type, out string? typeName)
            ? typeName
            : type;
        item["typeOrder"] = typeOrder;
        item["familyKey"] = family;
        item["familyOrder"] = familyOrder;
        item["level"] = level;
        return item;
    }

    private JObject BuildEnchantmentCatalogItem(object value)
    {
        object? data = GetEnchantmentDetailData(value);
        JObject item = BuildCatalogItem(value, data, "enchantmentWordTextName", "icon", "附魔");
        if (string.Equals((string?)item["name"], (string?)item["id"], StringComparison.Ordinal))
        {
            string fallbackName = ResolveChineseLocalizedString(GetMember(data, "fetterWordTextName"));
            if (!string.IsNullOrWhiteSpace(fallbackName)) item["name"] = fallbackName;
        }
        string enumName = value.ToString() ?? string.Empty;
        string family = enumName.Split(new[] { '_' }, 2)[0];
        int variantOrder = enumName.IndexOf('_') < 0 ? 0 : EnchantmentVariantOrder(enumName);
        ApplyGrouping(item, "enchantment:" + family, family, EnumGroupOrder(_fetterType!, family), variantOrder);
        item["description"] = ResolveCatalogDescription(data, "enchantmentWordText");
        return item;
    }

    private object? GetEnchantmentDetailData(object value)
    {
        object configuration = GetRequiredSingleton(_fetterInfoCfgType!, "SO_FetterInfoCfg");
        MethodInfo method = FindMethod(
                                configuration.GetType(),
                                "TryGetDetailData",
                                _fetterType!,
                                _fetterDetailDataType!.MakeByRefType())
                            ?? throw new MissingMethodException(
                                configuration.GetType().FullName,
                                "TryGetDetailData");
        return TryGetEnchantmentDetailData(configuration, method, value, out object? detail)
            ? detail
            : null;
    }

    private string ResolveEnchantmentDisplayName(object value)
    {
        object? data = GetEnchantmentDetailData(value);
        string name = ResolveChineseLocalizedString(GetMember(data, "enchantmentWordTextName"));
        if (string.IsNullOrWhiteSpace(name))
        {
            name = ResolveChineseLocalizedString(GetMember(data, "fetterWordTextName"));
        }
        return string.IsNullOrWhiteSpace(name) ? "未命名附魔" : name;
    }

    private JObject BuildDisposableCatalogItem(object value)
    {
        object? data = TryGetDisposableData(value, out object? configuredData)
            ? configuredData
            : null;
        JObject item = BuildCatalogItem(value, data, "name", "icon", "消耗品");
        item["description"] = ResolveCatalogDescription(data, "description");
        return item;
    }

    private JObject BuildCatapultPointCatalogItem(object value)
    {
        object? data = TryGetDisposableData(value, out object? configuredData)
            ? configuredData
            : null;
        JObject item = BuildCatalogItem(value, data, "name", "icon", "弹射点");
        item["description"] = ResolveCatalogDescription(data, "description");
        return item;
    }

    private JObject BuildRelicCatalogItem(object value)
    {
        TryGetSuperModuleData(value, out object? data);
        JObject item = BuildCatalogItem(value, data, "name", "icon", "遗物");
        item["description"] = ResolveCatalogDescription(data, "description");
        return item;
    }

    private JObject BuildEnemyCatalogItem(object value)
    {
        object? configuration = TryGetSingleton(_aiInformationDataSoType!);
        object? data = configuration == null
            ? null
            : FindMethod(configuration.GetType(), "GetAIInformationData", _aiIdType!)?.Invoke(configuration, new[] { value });
        return BuildCatalogItem(value, data, "name", "icon", "怪物");
    }

    private JObject BuildCatalogItem(
        object value,
        object? data,
        string nameMember,
        string iconMember,
        string category)
    {
        string id = value.ToString() ?? string.Empty;
        string name = ResolveChineseLocalizedString(GetMember(data, nameMember));
        CatalogIcon icon = ExportCatalogIcon(GetMember(data, iconMember) as Sprite);
        long numeric = Convert.ToInt64(value, CultureInfo.InvariantCulture);
        return new JObject
        {
            ["id"] = id,
            ["enumName"] = id,
            ["name"] = string.IsNullOrWhiteSpace(name) ? id : name,
            ["fallbackName"] = id,
            ["iconBase64"] = string.Empty,
            ["iconFile"] = icon.RelativeFile,
            ["iconSha256"] = icon.Sha256,
            ["tags"] = new JArray(category, id),
            ["value"] = numeric,
            ["groupKey"] = category,
            ["groupName"] = category,
            ["groupOrder"] = 0,
            ["itemOrder"] = numeric is < int.MinValue or > int.MaxValue ? int.MaxValue : (int)numeric
        };
    }

    private object? InvokeInfoManager(string methodName, Type argumentType, object value)
    {
        object? manager = TryGetSingleton(_infoManagerType!);
        return manager == null
            ? null
            : FindMethod(manager.GetType(), methodName, argumentType)?.Invoke(manager, new[] { value });
    }

    private bool TryGetDisposableData(object disposableEnum, out object? data)
    {
        object? manager = TryGetSingleton(_disposableManagerType!);
        object? configuration = GetMember(manager, "m_disposableSo")
                                ?? GetMember(manager, "disposableSo")
                                ?? GetMember(manager, "DisposableSo");
        return TryGetDictionaryValue(configuration, "disposableData", disposableEnum, out data);
    }

    private bool TryGetSuperModuleData(object relicEnum, out object? data)
    {
        object? manager = TryGetSingleton(_superModuleManagerType!);
        object? configuration = GetMember(manager, "m_superModuleSo")
                                ?? GetMember(manager, "superModuleSo")
                                ?? GetMember(manager, "SuperModuleSo");
        return TryGetDictionaryValue(configuration, "superModuleDatas", relicEnum, out data);
    }

    private static bool TryGetDictionaryValue(
        object? owner,
        string memberName,
        object key,
        out object? value)
    {
        value = null;
        if (GetMember(owner, memberName) is not IDictionary dictionary || !dictionary.Contains(key)) return false;
        value = dictionary[key];
        return value != null && (!(value is UnityEngine.Object unityObject) || unityObject != null);
    }

    private JArray SafeEnemyCatalogItems()
    {
        JArray result = new();
        foreach (object value in Enum.GetValues(_aiIdType!))
        {
            string id = value.ToString() ?? string.Empty;
            if (!IsSafeSpawnId(id) || !IsSafeConfiguredEnemy(value, out _)) continue;
            result.Add(BuildEnemyCatalogItem(value));
        }

        return result;
    }

    private bool IsCatapultPoint(object value)
    {
        string id = value.ToString() ?? string.Empty;
        if (IsLegacyCatapultPointId(id)) return true;
        return id.EndsWith("弹射点", StringComparison.Ordinal)
               || id.EndsWith("站点", StringComparison.Ordinal)
               || id.EndsWith("始发站", StringComparison.Ordinal);
    }

    private object? TryGetDisposableTemplate(object value)
    {
        object? manager = TryGetSingleton(_disposableManagerType!);
        object? configuration = GetMember(manager, "m_disposableSo")
                                ?? GetMember(manager, "disposableSo")
                                ?? GetMember(manager, "DisposableSo");
        if (GetMember(configuration, "disposableClassName") is not IDictionary templates) return null;
        return templates.Contains(value) ? templates[value] : null;
    }

    private static bool IsLegacyCatapultPointId(string id) =>
        string.Equals(id, "FreePoint", StringComparison.OrdinalIgnoreCase)
        || string.Equals(id, "FreePoint_Attribute", StringComparison.OrdinalIgnoreCase);

    private bool IsCatapultPointId(string id)
    {
        try
        {
            object value = ParseEnum(_disposableType!, id, "弹射点类型");
            return IsCatapultPoint(value);
        }
        catch
        {
            return false;
        }
    }

    private static string VehicleFamily(string enumName, out int level)
    {
        level = 0;
        int marker = enumName.LastIndexOf("_L", StringComparison.Ordinal);
        if (marker >= 0 && int.TryParse(enumName.Substring(marker + 2), NumberStyles.Integer, CultureInfo.InvariantCulture, out int parsed))
        {
            level = parsed;
            return enumName.Substring(0, marker);
        }
        return enumName;
    }

    private static string VehicleTypeFamily(string enumName)
    {
        int separator = enumName.IndexOf('_');
        return separator > 0 ? enumName.Substring(0, separator) : enumName;
    }

    private static int VehicleTypeOrder(Type? enumType, string type)
    {
        if (enumType == null) return int.MaxValue;
        int order = 0;
        HashSet<string> seen = new(StringComparer.Ordinal);
        foreach (object value in Enum.GetValues(enumType).Cast<object>()
                     .OrderBy(value => Convert.ToInt64(value, CultureInfo.InvariantCulture)))
        {
            string family = VehicleTypeFamily(value.ToString() ?? string.Empty);
            if (!seen.Add(family)) continue;
            if (string.Equals(family, type, StringComparison.Ordinal)) return order;
            order++;
        }
        return int.MaxValue;
    }

    private string ResolveCatalogDescription(object? data, string memberName)
    {
        string description = ResolveChineseLocalizedString(GetMember(data, memberName));
        return string.IsNullOrWhiteSpace(description) ? "游戏未提供描述" : description;
    }

    private static int EnchantmentVariantOrder(string enumName)
    {
        if (enumName.EndsWith("_Train", StringComparison.OrdinalIgnoreCase)) return 1;
        if (enumName.EndsWith("_Railway", StringComparison.OrdinalIgnoreCase)) return 2;
        if (enumName.EndsWith("_Domain", StringComparison.OrdinalIgnoreCase)
            || enumName.EndsWith("_Doamin", StringComparison.OrdinalIgnoreCase)) return 3;
        return 4;
    }

    private static int EnumGroupOrder(Type? enumType, string family)
    {
        if (enumType == null) return int.MaxValue;
        long best = long.MaxValue;
        foreach (object value in Enum.GetValues(enumType))
        {
            string name = value.ToString() ?? string.Empty;
            if (!string.Equals(name, family, StringComparison.Ordinal)
                && !name.StartsWith(family + "_", StringComparison.Ordinal)) continue;
            best = Math.Min(best, Convert.ToInt64(value, CultureInfo.InvariantCulture));
        }
        return best > int.MaxValue ? int.MaxValue : (int)best;
    }

    private static void ApplyGrouping(JObject item, string key, string name, int groupOrder, int itemOrder)
    {
        item["groupKey"] = key;
        item["groupName"] = name;
        item["groupOrder"] = groupOrder;
        item["itemOrder"] = itemOrder;
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

        Component? battleSystem = prefab!.GetComponent(_battleSystemType!);
        Component? receiver = prefab.GetComponent(_damageReceiverType!);
        Collider2D? collider = prefab.GetComponent<Collider2D>();
        if (battleSystem == null || receiver == null || collider == null)
        {
            reason = "该怪物预制体缺少战斗系统、受击组件或碰撞器，无法保证可被战车攻击：" + id + "。";
            return false;
        }

        if (!GetBool(ai, "colliderOn"))
        {
            reason = "该怪物配置关闭了战斗碰撞器，不能作为可攻击怪物生成：" + id + "。";
            return false;
        }

        if (!GetBool(ai, "NeedToBattle"))
        {
            reason = "该怪物配置未启用交战，战车不会将其作为目标：" + id + "。";
            return false;
        }

        if (GetBool(receiver, "CanNotBeAttack") || GetBool(receiver, "GodMode"))
        {
            reason = "该怪物配置为不可攻击或无敌，不能通过作弊面板生成：" + id + "。";
            return false;
        }

        reason = string.Empty;
        return true;
    }

    private (int Level, string Source, int? RequestedLevel) ResolveEnemySpawnLevel(JObject arguments)
    {
        string mode = arguments.Value<string>("levelMode")?.Trim() ?? "current";
        bool custom = string.Equals(mode, "custom", StringComparison.OrdinalIgnoreCase)
                      || arguments.Value<bool?>("useCurrentLevel") == false;
        if (custom)
        {
            int displayLevel = BoundedInt(arguments, "level", 1, 200, 1);
            return (displayLevel - 1, "custom", displayLevel);
        }

        object? waveProgress = TryGetSingleton(_waveProgressControllerType!);
        if (waveProgress == null)
        {
            throw new InvalidOperationException("当前关卡进度尚未初始化，无法解析怪物属性等级。");
        }
        int level = GetInt(waveProgress, "CurrentAILevel");
        if (level < 0)
        {
            throw new InvalidOperationException("当前关卡返回了无效的怪物属性等级。");
        }
        return (level, "current-wave", null);
    }

    private List<SpawnCenter> ResolveSpawnCenters(JObject arguments)
    {
        List<SpawnCenter> result = new();

        void AddCenter(SpawnCenter center)
        {
            bool duplicate = result.Any(existing =>
                (!string.IsNullOrWhiteSpace(center.PointId)
                 && string.Equals(existing.PointId, center.PointId, StringComparison.Ordinal))
                || (Math.Abs(existing.Position.x - center.Position.x) < 0.01f
                    && Math.Abs(existing.Position.y - center.Position.y) < 0.01f
                    && Math.Abs(existing.Position.z - center.Position.z) < 0.01f));
            if (!duplicate) result.Add(center);
        }

        bool hasExplicitPoints = arguments["points"] is JArray explicitPoints && explicitPoints.Count > 0;
        if (!hasExplicitPoints && arguments["pointIds"] is JArray pointIds)
        {
            foreach (JToken token in pointIds)
            {
                string pointId = token.Value<string>()?.Trim() ?? string.Empty;
                SavedSpawnPoint? saved = _spawnPoints.FirstOrDefault(point =>
                    string.Equals(point.PointId, pointId, StringComparison.Ordinal));
                if (saved == null)
                {
                    throw new InvalidOperationException("找不到已设置的怪物生成点：" + pointId);
                }
                AddCenter(new SpawnCenter(saved.PointId, saved.Position));
            }
        }

        if (arguments["points"] is JArray points)
        {
            foreach (JToken token in points)
            {
                if (token is not JObject point) throw new InvalidOperationException("生成点列表格式无效。");
                JObject coordinates = point["position"] as JObject ?? point;
                Vector3 position = new(
                    BoundedCoordinate(coordinates, "x"),
                    BoundedCoordinate(coordinates, "y"),
                    coordinates["z"] == null ? 0f : BoundedCoordinate(coordinates, "z"));
                AddCenter(new SpawnCenter(point.Value<string>("pointId")?.Trim() ?? string.Empty, position));
            }
        }

        if (result.Count == 0 && arguments["position"] is JObject positionObject)
        {
            AddCenter(new SpawnCenter(
                string.Empty,
                new Vector3(
                    BoundedCoordinate(positionObject, "x"),
                    BoundedCoordinate(positionObject, "y"),
                    positionObject["z"] == null ? 0f : BoundedCoordinate(positionObject, "z"))));
        }

        if (result.Count == 0 && (arguments["x"] != null || arguments["y"] != null))
        {
            AddCenter(new SpawnCenter(
                string.Empty,
                new Vector3(
                    BoundedCoordinate(arguments, "x"),
                    BoundedCoordinate(arguments, "y"),
                    arguments["z"] == null ? 0f : BoundedCoordinate(arguments, "z"))));
        }

        return result;
    }

    private bool TryCreateDistributedSpawnPositions(
        Vector3 center,
        int count,
        float radius,
        out List<Vector3> positions,
        out string reason)
    {
        positions = new List<Vector3>(count);
        if (count == 1)
        {
            positions.Add(new Vector3(center.x, center.y, 0f));
            reason = string.Empty;
            return true;
        }

        if (radius <= 0f || radius * 2f < MinimumSpawnSpacing)
        {
            reason = $"生成 {count} 个怪物时，分散半径必须足以保持至少 {MinimumSpawnSpacing:0.##} 的间距。";
            return false;
        }

        int seed = unchecked(Environment.TickCount
                             ^ (count * 397)
                             ^ center.x.GetHashCode()
                             ^ (center.y.GetHashCode() * 31));
        System.Random random = new(seed);
        float minimumDistanceSquared = MinimumSpawnSpacing * MinimumSpawnSpacing;
        for (int index = 0; index < count; index++)
        {
            bool found = false;
            for (int attempt = 0; attempt < SpawnPositionAttemptCount; attempt++)
            {
                double angle = random.NextDouble() * Math.PI * 2d;
                double distance = Math.Sqrt(random.NextDouble()) * radius;
                Vector3 candidate = new(
                    center.x + (float)(Math.Cos(angle) * distance),
                    center.y + (float)(Math.Sin(angle) * distance),
                    0f);
                if (!TryValidateSpawnPosition(candidate, out _)) continue;

                bool overlaps = positions.Any(position =>
                {
                    float deltaX = position.x - candidate.x;
                    float deltaY = position.y - candidate.y;
                    return deltaX * deltaX + deltaY * deltaY < minimumDistanceSquared;
                });
                if (overlaps) continue;

                positions.Add(candidate);
                found = true;
                break;
            }

            if (found) continue;
            positions.Clear();
            reason = $"无法在所选区域内分散放置 {count} 个怪物。请增大生成半径，或将生成中心移到离地图边缘更远的位置。";
            return false;
        }

        reason = string.Empty;
        return true;
    }

    private static void NormalizeEnemyLayer(GameObject gameObject, int enemyLayer)
    {
        Vector3 position = gameObject.transform.position;
        position.z = 0f;
        gameObject.transform.position = position;
        foreach (Transform child in gameObject.GetComponentsInChildren<Transform>(true))
        {
            child.gameObject.layer = enemyLayer;
        }
    }

    private bool TryValidateSpawnedEnemy(
        GameObject gameObject,
        int enemyLayer,
        object expectedBattleGroup,
        object expectedRegisterType,
        out EnemyTarget? target,
        out string reason)
    {
        target = null;
        if (gameObject == null || !gameObject.activeInHierarchy)
        {
            reason = "生成的怪物没有进入活动状态，已取消本次生成。";
            return false;
        }

        Component? ai = gameObject.GetComponent(_basicAiType!);
        if (ai == null || !GetBool(ai, "AIIsRunning"))
        {
            reason = "生成的怪物 AI 未正常启动，已回收该对象。";
            return false;
        }

        if (!GetBool(ai, "NeedToBattle"))
        {
            reason = "生成的怪物未启用交战，战车无法将其选为目标，已回收该对象。";
            return false;
        }

        Collider2D? collider = gameObject.GetComponent<Collider2D>();
        if (collider == null || !collider.enabled)
        {
            reason = "生成的怪物没有启用战斗碰撞器，已回收该对象。";
            return false;
        }

        if (gameObject.layer != enemyLayer
            || gameObject.GetComponentsInChildren<Collider2D>(true)
                .Any(childCollider => childCollider.enabled && childCollider.gameObject.layer != enemyLayer))
        {
            reason = "生成的怪物未正确设置为敌方层级，战车无法可靠锁定，已回收该对象。";
            return false;
        }

        object? battleSystem = GetMember(ai, "BattleSystem") ?? gameObject.GetComponent(_battleSystemType!);
        if (battleSystem == null || !Equals(GetMember(battleSystem, "battleGroup"), expectedBattleGroup))
        {
            reason = "生成的怪物战斗阵营不是敌方，已回收该对象。";
            return false;
        }

        object? receiver = GetMember(ai, "DamageReceiver") ?? gameObject.GetComponent(_damageReceiverType!);
        if (receiver == null
            || GetBool(receiver, "IsDie")
            || GetBool(receiver, "CanNotBeAttack")
            || GetBool(receiver, "GodMode"))
        {
            reason = "生成的怪物处于死亡、不可攻击或无敌状态，已回收该对象。";
            return false;
        }

        Component? agent = gameObject.GetComponent(_basicAgentType!);
        if (agent == null || !Equals(GetMember(agent, "AgentRegisterType"), expectedRegisterType))
        {
            reason = "生成的怪物未登记到敌人列表，已回收该对象。";
            return false;
        }

        if (!TryBuildEnemyTarget(gameObject, out target))
        {
            reason = "生成的怪物没有可用的战斗运行时句柄，已回收该对象。";
            return false;
        }

        reason = string.Empty;
        return true;
    }

    private void RecycleInvalidSpawnedEnemy(object creator, GameObject gameObject)
    {
        DisableEnemyDeathMessage(gameObject);
        MethodInfo recycle = FindMethod(creator.GetType(), "RecycleAI", typeof(GameObject))
                             ?? throw new MissingMethodException(creator.GetType().FullName, "RecycleAI");
        recycle.Invoke(creator, new object[] { gameObject });
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

    private bool TryValidateSpawnPosition(Vector3 position, out string reason)
    {
        if (float.IsNaN(position.x) || float.IsInfinity(position.x)
            || float.IsNaN(position.y) || float.IsInfinity(position.y)
            || float.IsNaN(position.z) || float.IsInfinity(position.z))
        {
            reason = "怪物生成坐标必须是有限数字。";
            return false;
        }

        object? gameController = TryGetSingleton(_gameControllerType!);
        if (gameController == null)
        {
            reason = "当前对局尚未初始化，无法固定怪物生成位置。";
            return false;
        }

        if (GetBool(gameController, "GameIsOver"))
        {
            reason = "对局已经结束，不能再生成怪物。";
            return false;
        }

        object? map = TryGetSingleton(_mapPosManagerType!);
        if (map == null || GetMember(map, "rect") is not Rect bounds)
        {
            reason = "地图边界尚未初始化，无法固定怪物生成位置。";
            return false;
        }

        if (!bounds.Contains(new Vector2(position.x, position.y)))
        {
            reason = "所选位置超出当前地图边界，请在地图内重新定位。";
            return false;
        }

        reason = string.Empty;
        return true;
    }

    private string ResolveAttributeDisplayName(string attributeId)
    {
        string requestedId = attributeId?.Trim() ?? string.Empty;
        try
        {
            if (!TryParseBattleMemoryId(requestedId, out object? attribute) || attribute == null)
            {
                return FormatAttributeDisplayName("战斗属性", requestedId);
            }

            string technicalId = Enum.GetName(_battleMemoryType!, attribute) ?? requestedId;
            object? configuration = TryGetSingleton(_battleAttributeCfgType!);
            if (configuration != null && GetMember(configuration, "attributeInfos") is IDictionary attributes)
            {
                foreach (DictionaryEntry entry in attributes)
                {
                    if (!Equals(entry.Key, attribute)) continue;
                    string name = ResolveChineseLocalizedString(GetMember(entry.Value, "attributeName"));
                    if (!string.IsNullOrWhiteSpace(name)) return FormatAttributeDisplayName(name, technicalId);
                    break;
                }
            }

            string fallback = BattleAttributeFallbackNames.TryGetValue(technicalId, out string? mapped)
                ? mapped
                : "战斗属性";
            return FormatAttributeDisplayName(fallback, technicalId);
        }
        catch
        {
            return FormatAttributeDisplayName("战斗属性", requestedId);
        }
    }

    private bool TryParseBattleMemoryId(string attributeId, out object? attribute)
    {
        attribute = null;
        if (_battleMemoryType == null
            || string.IsNullOrWhiteSpace(attributeId)
            || !Enum.TryParse(_battleMemoryType, attributeId, false, out attribute)
            || attribute == null)
        {
            return false;
        }

        return string.Equals(Enum.GetName(_battleMemoryType, attribute), attributeId, StringComparison.Ordinal);
    }

    private static string FormatAttributeDisplayName(string chineseName, string technicalId)
    {
        string name = string.IsNullOrWhiteSpace(chineseName) ? "战斗属性" : chineseName.Trim();
        string id = string.IsNullOrWhiteSpace(technicalId) ? "unknown" : technicalId.Trim();
        return $"{name}（{id}）";
    }

    private string ResolveChineseLocalizedString(object? localizedString)
    {
        if (localizedString == null) return string.Empty;
        try
        {
            Type? settingsType = FindType("UnityEngine.Localization.Settings.LocalizationSettings");
            if (settingsType != null)
            {
                object? initialization = GetStaticMember(settingsType, "InitializationOperation");
                if (initialization == null || GetBool(initialization, "IsDone"))
                {
                    string forcedChinese = ResolveLocalizedStringForLocale(localizedString, settingsType, "zh");
                    if (!string.IsNullOrWhiteSpace(forcedChinese)) return forcedChinese;
                }
            }
        }
        catch
        {
            // Fall through to the game's native LocalizedString path below.
        }

        try
        {
            MethodInfo? nativeMethod = localizedString.GetType().GetMethod(
                "GetLocalizedString",
                BindingFlags.Public | BindingFlags.Instance,
                null,
                Type.EmptyTypes,
                null);
            return (nativeMethod?.Invoke(localizedString, Array.Empty<object>()) as string)?.Trim() ?? string.Empty;
        }
        catch
        {
            return string.Empty;
        }
    }

    private static string ResolveLocalizedStringForLocale(object localizedString, Type settingsType, string localeCode)
    {
        object? availableLocales = GetStaticMember(settingsType, "AvailableLocales");
        object? locale = availableLocales == null
            ? null
            : FindMethod(availableLocales.GetType(), "GetLocale", typeof(string))
                ?.Invoke(availableLocales, new object[] { localeCode });
        object? database = GetStaticMember(settingsType, "StringDatabase");
        object? tableReference = GetMember(localizedString, "TableReference");
        object? entryReference = GetMember(localizedString, "TableEntryReference");
        if (locale == null || database == null || tableReference == null || entryReference == null)
        {
            return string.Empty;
        }

        MethodInfo? method = database.GetType()
            .GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.FlattenHierarchy)
            .FirstOrDefault(candidate =>
            {
                if (!string.Equals(candidate.Name, "GetLocalizedString", StringComparison.Ordinal)) return false;
                ParameterInfo[] parameters = candidate.GetParameters();
                return parameters.Length == 5
                       && parameters[0].ParameterType.Name == "TableReference"
                       && parameters[1].ParameterType.Name == "TableEntryReference"
                       && parameters[4].ParameterType == typeof(object[]);
            });
        if (method == null) return string.Empty;

        ParameterInfo[] methodParameters = method.GetParameters();
        object fallbackBehavior = Enum.ToObject(methodParameters[3].ParameterType, 0);
        string? value = method.Invoke(
            database,
            new[] { tableReference, entryReference, locale, fallbackBehavior, Array.Empty<object>() }) as string;
        return value?.Trim() ?? string.Empty;
    }

    private CatalogIcon ExportCatalogIcon(Sprite? sprite)
    {
        if (sprite == null || string.IsNullOrWhiteSpace(_artifactRoot)) return CatalogIcon.None;
        int instanceId = sprite.GetInstanceID();
        if (_catalogIcons.TryGetValue(instanceId, out CatalogIcon? cached)) return cached;

        RenderTexture? renderTexture = null;
        RenderTexture? previousActive = RenderTexture.active;
        Texture2D? readableTexture = null;
        try
        {
            Texture2D source = sprite.texture;
            Rect sourceRect = sprite.textureRect;
            if (source == null || source.width <= 0 || source.height <= 0
                || sourceRect.width <= 0f || sourceRect.height <= 0f)
            {
                return CatalogIcon.None;
            }

            renderTexture = RenderTexture.GetTemporary(
                CatalogIconSize,
                CatalogIconSize,
                0,
                RenderTextureFormat.ARGB32,
                RenderTextureReadWrite.sRGB);
            renderTexture.filterMode = FilterMode.Bilinear;
            RenderTexture.active = renderTexture;
            GL.Clear(true, true, Color.clear);
            Vector2 scale = new(sourceRect.width / source.width, sourceRect.height / source.height);
            Vector2 offset = new(sourceRect.x / source.width, sourceRect.y / source.height);
            Graphics.Blit(source, renderTexture, scale, offset);

            readableTexture = new Texture2D(CatalogIconSize, CatalogIconSize, TextureFormat.RGBA32, false);
            readableTexture.ReadPixels(new Rect(0f, 0f, CatalogIconSize, CatalogIconSize), 0, 0, false);
            readableTexture.Apply(false, false);
            byte[] png = readableTexture.EncodeToPNG();
            if (png == null || png.Length == 0) return CatalogIcon.None;

            string hash;
            using (SHA256 sha256 = SHA256.Create())
            {
                hash = BitConverter.ToString(sha256.ComputeHash(png))
                    .Replace("-", string.Empty)
                    .ToLowerInvariant();
            }

            string relativeFile = Path.Combine("cheat-icons", "2", hash + ".png");
            string fullPath = Path.Combine(_artifactRoot, relativeFile);
            Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);
            File.WriteAllBytes(fullPath, png);
            CatalogIcon icon = new(relativeFile.Replace('\\', '/'), hash);
            _catalogIcons[instanceId] = icon;
            return icon;
        }
        catch
        {
            return CatalogIcon.None;
        }
        finally
        {
            RenderTexture.active = previousActive;
            if (renderTexture != null) RenderTexture.ReleaseTemporary(renderTexture);
            if (readableTexture != null) UnityEngine.Object.Destroy(readableTexture);
        }
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

    private static int PositiveInt(JObject arguments, string name, int fallback)
    {
        int value = arguments.Value<int?>(name) ?? fallback;
        if (value < 1)
        {
            throw new InvalidOperationException(name + " 必须大于 0。");
        }
        return value;
    }

    private static int NonNegativeInt(JObject arguments, string name, int fallback)
    {
        int value = arguments.Value<int?>(name) ?? fallback;
        if (value < 0)
        {
            throw new InvalidOperationException(name + " 不能小于 0。");
        }
        return value;
    }

    private static float BoundedFloat(JObject arguments, string name, float minimum, float maximum, float fallback)
    {
        double value = arguments.Value<double?>(name) ?? fallback;
        if (double.IsNaN(value) || double.IsInfinity(value) || value < minimum || value > maximum)
        {
            throw new InvalidOperationException($"{name} 必须是 {minimum:0.##} 到 {maximum:0.##} 之间的有限数字。");
        }

        return (float)value;
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

    private static int GetCollectionCount(object? source) => source is ICollection collection ? collection.Count : 0;

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

    private sealed class GrantAllRelicsJob
    {
        private readonly List<object> _pending;
        private readonly List<JObject> _failedRelics = new();
        private int _nextIndex;

        private GrantAllRelicsJob(string state, int totalCount, int skippedCount, List<object>? pending, string message)
        {
            State = state;
            TotalCount = totalCount;
            SkippedCount = skippedCount;
            _pending = pending ?? new List<object>();
            Message = message;
        }

        public string State { get; private set; }
        public int TotalCount { get; }
        public int GrantedCount { get; private set; }
        public int SkippedCount { get; private set; }
        public int FailedCount { get; private set; }
        public string Message { get; private set; }
        public bool IsRunning => string.Equals(State, "running", StringComparison.Ordinal);
        public bool HasRemaining => _nextIndex < _pending.Count;
        public int RemainingCount => Math.Max(0, _pending.Count - _nextIndex);
        public int ProcessedCount => Math.Min(TotalCount, SkippedCount + GrantedCount + FailedCount);

        public static GrantAllRelicsJob Idle() =>
            new("idle", 0, 0, null, "尚未启动一键获取所有遗物任务。");

        public static GrantAllRelicsJob Start(int totalCount, int skippedCount, List<object> pending) =>
            new(
                "running",
                totalCount,
                skippedCount,
                pending,
                pending.Count == 0
                    ? "全部已配置遗物均已持有。"
                    : $"正在逐帧获取遗物，共需处理 {pending.Count} 个未持有遗物。");

        public bool TryTakeNext(out object? relic)
        {
            if (!IsRunning || !HasRemaining)
            {
                relic = null;
                return false;
            }

            relic = _pending[_nextIndex++];
            return true;
        }

        public void RecordGranted(string relicId)
        {
            GrantedCount++;
            Message = "已获取遗物 " + relicId + "。";
        }

        public void RecordSkipped(string relicId)
        {
            SkippedCount++;
            Message = "遗物 " + relicId + " 已持有，已跳过。";
        }

        public void RecordFailed(string relicId, string error)
        {
            FailedCount++;
            _failedRelics.Add(new JObject
            {
                ["relicId"] = relicId,
                ["error"] = error
            });
            Message = "获取遗物 " + relicId + " 失败：" + error;
        }

        public void Complete()
        {
            if (!IsRunning) return;
            if (FailedCount == 0)
            {
                State = "completed";
                Message = $"一键获取所有遗物已完成：新增 {GrantedCount} 个，跳过 {SkippedCount} 个已有遗物。";
            }
            else if (GrantedCount > 0 || SkippedCount > 0)
            {
                State = "partial";
                Message = $"一键获取所有遗物已部分完成：新增 {GrantedCount} 个，跳过 {SkippedCount} 个，失败 {FailedCount} 个。";
            }
            else
            {
                State = "failed";
                Message = $"一键获取所有遗物失败，共 {FailedCount} 个遗物未能获取。";
            }
        }

        public void Cancel(string message)
        {
            if (!IsRunning) return;
            State = "cancelled";
            Message = string.IsNullOrWhiteSpace(message) ? "一键获取所有遗物任务已取消。" : message;
        }

        public JObject ToData() => new()
        {
            ["state"] = State,
            ["totalCount"] = TotalCount,
            ["processedCount"] = ProcessedCount,
            ["grantedCount"] = GrantedCount,
            ["skippedCount"] = SkippedCount,
            ["failedCount"] = FailedCount,
            ["remainingCount"] = RemainingCount,
            ["failedRelics"] = new JArray(_failedRelics.Select(item => item.DeepClone())),
            ["message"] = Message
        };
    }

    private sealed class RelicRemovalJob
    {
        private readonly List<object> _pending;
        private readonly List<JObject> _failed = new();
        private int _nextIndex;

        private RelicRemovalJob(string state, List<object>? pending, string message)
        {
            State = state;
            _pending = pending ?? new List<object>();
            Message = message;
        }

        public string State { get; private set; }
        public string Message { get; private set; }
        public int RemovedTypes { get; private set; }
        public int RemovedInstances { get; private set; }
        public int FailedCount { get; private set; }
        public int TotalCount => _pending.Count;
        public int ProcessedCount => Math.Min(TotalCount, RemovedTypes + FailedCount);
        public int RemainingCount => Math.Max(0, TotalCount - _nextIndex);
        public bool IsRunning => string.Equals(State, "running", StringComparison.Ordinal);
        public bool HasRemaining => _nextIndex < _pending.Count;

        public static RelicRemovalJob Idle() => new("idle", null, "尚未启动删除所有遗物任务。");
        public static RelicRemovalJob Start(List<object> pending) =>
            new("running", pending, pending.Count == 0 ? "当前没有遗物。" : $"正在逐帧删除 {pending.Count} 种遗物。");

        public bool TryTakeNext(out object? relic)
        {
            if (!IsRunning || !HasRemaining)
            {
                relic = null;
                return false;
            }
            relic = _pending[_nextIndex++];
            return true;
        }

        public void RecordRemoved(string relicId, int instances)
        {
            RemovedTypes++;
            RemovedInstances += instances;
            Message = $"已删除遗物 {relicId}，移除 {instances} 个实例。";
        }

        public void RecordFailed(string relicId, string error)
        {
            FailedCount++;
            _failed.Add(new JObject { ["relicId"] = relicId, ["error"] = error });
            Message = $"删除遗物 {relicId} 失败：{error}";
        }

        public void Complete()
        {
            if (!IsRunning) return;
            State = FailedCount == 0 ? "completed" : RemovedTypes > 0 ? "partial" : "failed";
            Message = FailedCount == 0
                ? $"删除所有遗物已完成：移除 {RemovedTypes} 种、{RemovedInstances} 个实例。"
                : $"删除所有遗物部分完成：移除 {RemovedTypes} 种，失败 {FailedCount} 种。";
        }

        public void Cancel(string message)
        {
            if (!IsRunning) return;
            State = "cancelled";
            Message = string.IsNullOrWhiteSpace(message) ? "删除所有遗物任务已取消。" : message;
        }

        public JObject ToData() => new()
        {
            ["state"] = State,
            ["totalCount"] = TotalCount,
            ["processedCount"] = ProcessedCount,
            ["removedTypes"] = RemovedTypes,
            ["removedInstances"] = RemovedInstances,
            ["failedCount"] = FailedCount,
            ["remainingCount"] = RemainingCount,
            ["failedRelics"] = new JArray(_failed.Select(item => item.DeepClone())),
            ["message"] = Message
        };
    }

    private sealed class CatalogIcon
    {
        public static readonly CatalogIcon None = new(string.Empty, string.Empty);

        public CatalogIcon(string relativeFile, string sha256)
        {
            RelativeFile = relativeFile;
            Sha256 = sha256;
        }

        public string RelativeFile { get; }
        public string Sha256 { get; }
    }

    private sealed class SavedSpawnPoint
    {
        public SavedSpawnPoint(string pointId, Vector3 position, DateTime createdAtUtc)
        {
            PointId = pointId;
            Position = position;
            CreatedAtUtc = createdAtUtc;
        }

        public string PointId { get; }
        public Vector3 Position { get; }
        public DateTime CreatedAtUtc { get; }

        public JObject ToData() => new()
        {
            ["pointId"] = PointId,
            ["x"] = Position.x,
            ["y"] = Position.y,
            ["z"] = Position.z,
            ["position"] = VectorData(Position),
            ["createdAtUtc"] = CreatedAtUtc
        };
    }

    private sealed class SpawnCenter
    {
        public SpawnCenter(string pointId, Vector3 position)
        {
            PointId = pointId;
            Position = position;
        }

        public string PointId { get; }
        public Vector3 Position { get; }

        public JObject ToData() => new()
        {
            ["pointId"] = PointId,
            ["x"] = Position.x,
            ["y"] = Position.y,
            ["z"] = Position.z
        };
    }

    private sealed class SpawnPointCapture
    {
        private SpawnPointCapture(
            string state,
            bool isArmed,
            Vector3? position,
            string message,
            DateTime? expiresAtUtc = null)
        {
            State = state;
            IsArmed = isArmed;
            Position = position;
            Message = message;
            ExpiresAtUtc = expiresAtUtc;
        }

        public string State { get; }
        public bool IsArmed { get; }
        public Vector3? Position { get; }
        public string Message { get; }
        public DateTime? ExpiresAtUtc { get; }

        public static SpawnPointCapture Idle(string message = "未启用怪物生成位置定位。") =>
            new("idle", false, null, message);

        public static SpawnPointCapture Armed(DateTime expiresAtUtc, string message) =>
            new("armed", true, null, message, expiresAtUtc);

        public static SpawnPointCapture Captured(Vector3 position, string message) =>
            new("captured", false, position, message);

        public static SpawnPointCapture Failed(string message) =>
            new("failed", false, null, message);

        public static SpawnPointCapture Expired(string message) =>
            new("expired", false, null, message);

        public JObject ToData()
        {
            JObject data = new()
            {
                ["state"] = State,
                ["message"] = Message,
                ["expiresAtUtc"] = ExpiresAtUtc,
                ["x"] = Position?.x,
                ["y"] = Position?.y,
                ["z"] = Position?.z
            };
            return data;
        }
    }

    private sealed class NumericParameter
    {
        public string Key { get; set; } = string.Empty;
        public string Kind { get; set; } = "float";
        public double Value { get; set; }
        public double BaseValue { get; set; }
    }

    private sealed class EnemyOverlaySnapshot
    {
        public GameObject GameObject { get; set; } = null!;
        public Transform IdAnchor { get; set; } = null!;
        public float IdWorldYOffset { get; set; }
        public Transform BuffAnchor { get; set; } = null!;
        public float BuffWorldYOffset { get; set; }
        public string IdText { get; set; } = string.Empty;
        public IReadOnlyList<EnemyBuffIconSnapshot> Buffs { get; set; } =
            Array.Empty<EnemyBuffIconSnapshot>();
    }

    private sealed class EnemyBuffIconSource
    {
        public string DisplayName { get; set; } = string.Empty;
        public Texture2D? Texture { get; set; }
        public Rect Uv { get; set; }
        public Color FallbackColor { get; set; } = Color.gray;
    }

    private sealed class EnemyBuffIconSnapshot
    {
        public string Key { get; set; } = string.Empty;
        public string DisplayName { get; set; } = string.Empty;
        public Texture2D? Texture { get; set; }
        public Rect Uv { get; set; }
        public Color FallbackColor { get; set; } = Color.gray;
        public string DurationText { get; set; } = "--";
        public int StackCount { get; set; } = 1;
        public bool HasExplicitStackCount { get; set; }
        public bool ShowStackCount { get; set; }
        public string DetailText { get; set; } = string.Empty;
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
