using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Threading;
using BepInEx.Logging;
using Loopstructor.AutoPlayer.Core;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using UnityEngine.SceneManagement;

namespace Loopstructor.AutoPlayer.Plugin;

internal sealed class CheatController : IDisposable
{
    private static readonly TimeSpan ManagerLeaseTimeout = TimeSpan.FromSeconds(10);
    private readonly AutoPlayController _autoPlay;
    private readonly ActivationContext _activation;
    private readonly ManualLogSource _log;
    private readonly CheatRuntimeBridge _runtime = new();
    private readonly bool _baseContractAccepted;
    private int _sceneHandle;
    private long _lastManagerHeartbeatUtcTicks;

    public CheatController(
        AutoPlayController autoPlay,
        ActivationContext activation,
        ManualLogSource log,
        bool baseContractAccepted)
    {
        _autoPlay = autoPlay;
        _activation = activation;
        _log = log;
        _baseContractAccepted = baseContractAccepted;
        _runtime.Initialize(_activation.ArtifactRoot, message => _log.LogWarning(message));
        _sceneHandle = SceneManager.GetActiveScene().handle;
        _lastManagerHeartbeatUtcTicks = DateTime.UtcNow.Ticks;

        string reason = BuildAvailabilityReason();
        _autoPlay.ConfigureCheat(IsAvailable, reason, _runtime.Capabilities);
    }

    public bool IsAvailable => _activation.CheatModeAllowed && _baseContractAccepted && _runtime.IsAvailable;
    public bool Enabled { get; private set; }

    public void NotifyManagerHeartbeat() =>
        Interlocked.Exchange(ref _lastManagerHeartbeatUtcTicks, DateTime.UtcNow.Ticks);

    public void NotifyManagerCommandCompleted() => NotifyManagerHeartbeat();

    public void OnRuntimeHostLost()
    {
        DisableAndReset("AutoPlayer 运行时宿主已中断，作弊模式和全部瞬态效果已立即关闭。");
    }

    public void Tick()
    {
        if (Enabled)
        {
            long lastHeartbeat = Interlocked.Read(ref _lastManagerHeartbeatUtcTicks);
            if (DateTime.UtcNow - new DateTime(lastHeartbeat, DateTimeKind.Utc) > ManagerLeaseTimeout)
            {
                DisableAndReset("Manager 心跳已中断，作弊模式、基地无敌、敌人信息显示、位置捕获和地图跳关已自动关闭。");
                return;
            }
        }

        int currentSceneHandle = SceneManager.GetActiveScene().handle;
        if (currentSceneHandle != _sceneHandle)
        {
            _sceneHandle = currentSceneHandle;
            _runtime.CancelGrantAllRelics("游戏场景已切换，一键获取所有遗物任务已取消。");
            _runtime.CancelRemoveAllRelics("游戏场景已切换，删除所有遗物任务已取消。");
            ResetTransientFeatures();
            _log.LogInfo("作弊工具已在场景切换后关闭基地无敌、敌人信息显示、位置捕获与地图跳关。");
        }

        if (Enabled)
        {
            if (_autoPlay.IsAutoPlayActive)
            {
                _runtime.CancelGrantAllRelics("自动游玩已开始，一键获取所有遗物任务已取消。");
                _runtime.CancelRemoveAllRelics("自动游玩已开始，删除所有遗物任务已取消。");
                _runtime.SetFieldCatapultDeleteMode(false);
            }
            else
            {
                _runtime.TickGrantAllRelics();
                _runtime.TickRemoveAllRelics();
            }

            MapSkipPatch.Tick();
            _runtime.TickEnemyOverlays();
        }
    }

    public void DrawEnemyOverlays()
    {
        if (Enabled)
        {
            _runtime.DrawEnemyOverlays();
        }
    }

    public CheatExecutionResult Execute(string requestId, string command, JObject? arguments)
    {
        JObject args = arguments ?? new JObject();
        if (_autoPlay.IsAutoPlayActive && !CheatCommands.IsAutoPlayObservationCommand(command))
        {
            CheatExecutionResult blocked = CheatExecutionResult.Fail(
                "自动游玩运行或暂停期间只允许作弊监视：可启用或关闭作弊模式、查询目录/战车/敌人，以及切换敌人 ID 和 Buff 显示。其他作弊写命令已拒绝。",
                "AUTO_PLAY_WRITE_CONFLICT");
            _log.LogWarning(blocked.Message);
            return blocked;
        }

        bool mutationAttempt = Enabled && CheatCommands.IsMutationCommand(command);
        if (mutationAttempt && !_activation.TryMarkCheatProfileTainted(requestId, command, out string markerError))
        {
            CheatExecutionResult blocked = CheatExecutionResult.Fail(
                "无法在当前自动游玩配置中持久化作弊标记；为避免产生未标记的修改，已拒绝执行该写命令。" +
                (string.IsNullOrWhiteSpace(markerError) ? string.Empty : " 原因：" + markerError),
                "CHEAT_PROFILE_TAINT_MARKER_FAILED");
            _log.LogError(blocked.Message);
            AppendAudit(requestId, command, args, blocked);
            return blocked;
        }

        CheatExecutionResult result;
        try
        {
            result = ExecuteCore(command, args);
        }
        catch (Exception exception)
        {
            Exception error = CheatRuntimeBridge.Unwrap(exception);
            result = CheatExecutionResult.Fail("执行作弊命令时发生异常：" + error.Message);
            _log.LogError("作弊命令执行异常：" + error);
        }

        // A multi-step reflection call can change the game before a later step
        // fails. Treat every authorized write attempt as tainted, even when its
        // final response reports failure, so it can never count as a clean run.
        if (mutationAttempt)
        {
            _autoPlay.RecordCheatAction(command, result.Message);
            AppendAudit(requestId, command, args, result);
        }

        return result;
    }

    public void Dispose()
    {
        DisableAndReset(null);
    }

    private CheatExecutionResult ExecuteCore(string command, JObject arguments)
    {
        if (string.Equals(command, CheatCommands.SetEnabled, StringComparison.OrdinalIgnoreCase))
        {
            return SetEnabled(arguments.Value<bool?>("enabled") == true);
        }

        if (string.Equals(command, CheatCommands.QueryCatalog, StringComparison.OrdinalIgnoreCase))
        {
            return IsAvailable
                ? CheatExecutionResult.Ok("已读取作弊项目目录。", _runtime.QueryCatalog())
                : CheatExecutionResult.Fail(BuildAvailabilityReason());
        }

        if (string.Equals(command, CheatCommands.QueryState, StringComparison.OrdinalIgnoreCase))
        {
            return CheatExecutionResult.Ok("已读取作弊模式状态。", BuildStateData());
        }

        if (!Enabled)
        {
            return CheatExecutionResult.Fail("作弊模式尚未启用。请先在作弊工具顶部显式开启。", "CHEAT_MODE_DISABLED");
        }

        return command.ToLowerInvariant() switch
        {
            "cheat.grantvehicle" => _runtime.GrantVehicle(arguments),
            "cheat.removevehicle" => _runtime.RemoveVehicle(arguments),
            "cheat.grantdisposable" => _runtime.GrantDisposable(arguments),
            "cheat.clearconsumables" => _runtime.ClearConsumables(),
            "cheat.grantcatapultpoint" => _runtime.GrantCatapultPoint(arguments),
            "cheat.removecatapultpoint" => _runtime.RemoveCatapultPoint(arguments),
            "cheat.clearbackpackcatapultpoints" => _runtime.ClearBackpackCatapultPoints(),
            "cheat.removefieldcatapultpoint" => _runtime.RemoveFieldCatapultPoint(arguments),
            "cheat.clearfieldcatapultpoints" => _runtime.ClearFieldCatapultPoints(),
            "cheat.setfieldcatapultdeletemode" => _runtime.SetFieldCatapultDeleteMode(arguments.Value<bool?>("enabled") == true),
            "cheat.setbasegodmode" => SetBaseGodMode(arguments),
            "cheat.endwave" => _runtime.EndWave(),
            "cheat.clearenemies" => _runtime.ClearEnemies(),
            "cheat.queryvehicles" => CheatExecutionResult.Ok("已读取当前战车。", _runtime.QueryVehicles()),
            "cheat.modifyvehicle" => _runtime.ModifyVehicle(arguments),
            "cheat.setvehicleenchantment" => _runtime.SetVehicleEnchantment(arguments),
            "cheat.queryenemies" => CheatExecutionResult.Ok("已读取当前敌人。", _runtime.QueryEnemies()),
            "cheat.modifyenemy" => _runtime.ModifyEnemy(arguments),
            "cheat.setenemyidoverlay" => SetEnemyIdOverlay(arguments),
            "cheat.setenemybuffoverlay" => SetEnemyBuffOverlay(arguments),
            "cheat.grantrelic" => _runtime.GrantRelic(arguments),
            "cheat.grantallrelics" => _runtime.StartGrantAllRelics(),
            "cheat.removerelic" => _runtime.RemoveRelic(arguments),
            "cheat.removeallrelics" => _runtime.StartRemoveAllRelics(),
            "cheat.spawnenemy" => _runtime.SpawnEnemy(arguments),
            "cheat.setspawnpointcapture" => _runtime.SetSpawnPointCapture(arguments),
            "cheat.removespawnpoint" => _runtime.RemoveSpawnPoint(arguments),
            "cheat.clearspawnpoints" => _runtime.ClearSpawnPoints(),
            "cheat.setmapskipenabled" => SetMapSkipEnabled(arguments),
            _ => CheatExecutionResult.Fail("未知的作弊命令：" + command, "UNKNOWN_CHEAT_COMMAND")
        };
    }

    private CheatExecutionResult SetEnabled(bool enabled)
    {
        if (enabled)
        {
            if (!IsAvailable)
            {
                return CheatExecutionResult.Fail(BuildAvailabilityReason(), "CHEAT_UNAVAILABLE");
            }

            if (!AutoPlayerSafetyGate.IsReady(
                    _activation.ActivationMode,
                    SaveIsolationPatch.Applied,
                    SaveIsolationPatch.Verified,
                    PlatformWriteIsolationPatch.Applied,
                    GameArtifactIsolationPatch.Applied))
            {
                string gateMessage = _activation.IsPlayerMode
                    ? "玩家模式检测到不应启用的 QA 隔离补丁，已拒绝开启作弊模式。"
                    : "隔离 QA 模式只允许在存档隔离、平台写入阻断和产物重定向均已验证后启用。";
                return CheatExecutionResult.Fail(gateMessage, "SAFETY_GATE_NOT_READY");
            }
        }

        if (!_autoPlay.TrySetCheatMode(enabled, out string message))
        {
            return CheatExecutionResult.Fail(message, "AUTO_PLAY_CONFLICT");
        }

        Enabled = enabled;
        VehicleEnchantmentDisplayPatch.SetEnabled(enabled);
        if (!enabled)
        {
            _runtime.CancelGrantAllRelics("作弊模式已关闭，一键获取所有遗物任务已取消。");
            _runtime.CancelRemoveAllRelics("作弊模式已关闭，删除所有遗物任务已取消。");
            ResetTransientFeatures();
        }

        _log.LogInfo(message);
        return CheatExecutionResult.Ok(message, BuildStateData());
    }

    private void DisableAndReset(string? logMessage)
    {
        _runtime.CancelGrantAllRelics("作弊控制已中断，一键获取所有遗物任务已取消。");
        _runtime.CancelRemoveAllRelics("作弊控制已中断，删除所有遗物任务已取消。");
        ResetTransientFeatures();
        Enabled = false;
        _autoPlay.TrySetCheatMode(false, out _);
        if (!string.IsNullOrWhiteSpace(logMessage)) _log.LogWarning(logMessage);
    }

    private void ResetTransientFeatures()
    {
        ResetStep("运行时作弊效果", _runtime.ResetTransientFeatures);
        ResetStep("战车附魔图标布局", VehicleEnchantmentDisplayPatch.Reset);
        ResetStep("地图跳关", MapSkipPatch.Reset);
        ResetStep("敌人 ID 显示", () => _autoPlay.SetEnemyIdsVisible(false));
        ResetStep("敌人 Buff 显示", () => _autoPlay.SetEnemyBuffsVisible(false));
        ResetStep("基地无敌", () => _autoPlay.SetBaseGodModeEnabled(false));
    }

    private void ResetStep(string feature, Action action)
    {
        try
        {
            action();
        }
        catch (Exception exception)
        {
            try { _log.LogError($"关闭瞬态作弊功能失败（{feature}）：{exception}"); } catch { }
        }
    }

    private CheatExecutionResult SetEnemyIdOverlay(JObject arguments)
    {
        bool visible = arguments.Value<bool?>("visible") == true;
        _runtime.EnemyIdsVisible = visible;
        _runtime.InvalidateEnemyOverlayCache();
        _autoPlay.SetEnemyIdsVisible(visible);
        string message = visible ? "已在游戏画面中显示敌人 ID。" : "已关闭游戏画面中的敌人 ID。";
        return CheatExecutionResult.Changed(
            message,
            new JObject { ["visible"] = visible, ["enemyIdsVisible"] = visible });
    }

    private CheatExecutionResult SetEnemyBuffOverlay(JObject arguments)
    {
        bool visible = arguments.Value<bool?>("visible") == true;
        _runtime.EnemyBuffsVisible = visible;
        _runtime.InvalidateEnemyOverlayCache();
        _autoPlay.SetEnemyBuffsVisible(visible);
        string message = visible
            ? "已在游戏画面中显示怪物 Buff 图标与持续时间。"
            : "已关闭游戏画面中的怪物 Buff 显示。";
        return CheatExecutionResult.Changed(
            message,
            new JObject { ["visible"] = visible, ["enemyBuffsVisible"] = visible });
    }

    private CheatExecutionResult SetBaseGodMode(JObject arguments)
    {
        CheatExecutionResult result = _runtime.SetBaseGodMode(arguments);
        if (result.Success)
        {
            _autoPlay.SetBaseGodModeEnabled(result.Data.Value<bool?>("requested") == true);
        }

        return result;
    }

    private CheatExecutionResult SetMapSkipEnabled(JObject arguments)
    {
        bool enabled = arguments.Value<bool?>("enabled") == true;
        if (!MapSkipPatch.SetEnabled(enabled))
        {
            return CheatExecutionResult.Fail(
                "当前游戏版本未能安装地图跳关输入补丁。",
                "MAP_SKIP_PATCH_UNAVAILABLE");
        }

        bool actualEnabled = MapSkipPatch.Enabled;
        string message = actualEnabled
            ? "地图跳关已开启；可在当前地图界面点击进度之后的任意节点。"
            : "地图跳关已关闭。";
        return CheatExecutionResult.Changed(
            message,
            new JObject { ["mapSkipEnabled"] = actualEnabled });
    }

    private JObject BuildStateData()
    {
        JObject state = new()
        {
            ["available"] = IsAvailable,
            ["enabled"] = Enabled,
            ["enemyIdsVisible"] = _runtime.EnemyIdsVisible,
            ["enemyBuffsVisible"] = _runtime.EnemyBuffsVisible,
            ["baseGodMode"] = _runtime.BaseGodModeRequested,
            ["mapSkipEnabled"] = MapSkipPatch.Enabled,
            ["spawnPointCapture"] = _runtime.SpawnPointCaptureData(),
            ["fieldCatapultDeleteMode"] = _runtime.FieldCatapultDeleteMode,
            ["protocolVersion"] = Protocol.CheatCurrentVersion,
            ["availabilityReason"] = BuildAvailabilityReason(),
            ["capabilities"] = JArray.FromObject(_runtime.Capabilities),
            ["ownedVehicles"] = new JArray(),
            ["ownedRelics"] = new JArray(),
            ["ownedCatapultPoints"] = new JArray(),
            ["fieldCatapultPoints"] = new JArray(),
            ["grantAllRelics"] = new JObject
            {
                ["state"] = "idle",
                ["totalCount"] = 0,
                ["processedCount"] = 0,
                ["grantedCount"] = 0,
                ["skippedCount"] = 0,
                ["failedCount"] = 0,
                ["remainingCount"] = 0,
                ["failedRelics"] = new JArray(),
                ["message"] = "尚未启动一键获取所有遗物任务。"
            },
            ["removeAllRelics"] = new JObject
            {
                ["state"] = "idle",
                ["totalCount"] = 0,
                ["processedCount"] = 0,
                ["removedTypes"] = 0,
                ["removedInstances"] = 0,
                ["failedCount"] = 0,
                ["remainingCount"] = 0,
                ["failedRelics"] = new JArray(),
                ["message"] = "尚未启动删除所有遗物任务。"
            }
        };
        if (!IsAvailable) return state;

        try
        {
            state.Merge(_runtime.QueryOwnedState(), new JsonMergeSettings
            {
                MergeArrayHandling = MergeArrayHandling.Replace
            });
        }
        catch (Exception exception)
        {
            state["inventoryError"] = CheatRuntimeBridge.Unwrap(exception).Message;
        }
        return state;
    }

    private string BuildAvailabilityReason()
    {
        if (!_activation.CheatModeAllowed)
        {
            return "本次游戏进程未由可信 Manager 会话授权作弊控制，请由 Manager 重新启动游戏。";
        }

        if (!_baseContractAccepted)
        {
            return "游戏构建指纹或正常自动化运行时合同未通过，作弊工具保持关闭。";
        }

        if (!_runtime.IsAvailable)
        {
            return "作弊运行时缺少游戏成员：" + string.Join("、", _runtime.MissingMembers);
        }

        return string.Empty;
    }

    private void AppendAudit(string requestId, string command, JObject arguments, CheatExecutionResult result)
    {
        try
        {
            Directory.CreateDirectory(_activation.ArtifactRoot);
            JObject entry = new()
            {
                ["timestampUtc"] = DateTime.UtcNow,
                ["requestId"] = requestId,
                ["command"] = command,
                ["success"] = result.Success,
                ["reportedMutation"] = result.Mutated,
                ["message"] = result.Message,
                ["arguments"] = arguments.DeepClone(),
                ["data"] = result.Data.DeepClone()
            };
            string path = Path.Combine(_activation.ArtifactRoot, "cheat-actions.jsonl");
            File.AppendAllText(path, entry.ToString(Formatting.None) + Environment.NewLine, new UTF8Encoding(false));
        }
        catch (Exception exception)
        {
            _log.LogWarning("无法写入作弊操作审计：" + exception.Message);
        }
    }
}

internal sealed class CheatExecutionResult
{
    public bool Success { get; private set; }
    public bool Mutated { get; private set; }
    public string Message { get; private set; } = string.Empty;
    public string ErrorCode { get; private set; } = string.Empty;
    public JObject Data { get; private set; } = new();

    public static CheatExecutionResult Ok(string message, JObject? data = null) => new()
    {
        Success = true,
        Message = message,
        Data = data ?? new JObject()
    };

    public static CheatExecutionResult Changed(string message, JObject? data = null) => new()
    {
        Success = true,
        Mutated = true,
        Message = message,
        Data = data ?? new JObject()
    };

    public static CheatExecutionResult Partial(string message, JObject? data = null) => new()
    {
        Mutated = true,
        Message = message,
        ErrorCode = "PARTIAL_CHANGE",
        Data = data ?? new JObject()
    };

    public static CheatExecutionResult Fail(string message, string errorCode = "CHEAT_COMMAND_FAILED") => new()
    {
        Message = message,
        ErrorCode = errorCode,
        Data = new JObject { ["errorCode"] = errorCode }
    };
}
