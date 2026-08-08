using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using Newtonsoft.Json.Linq;
using UnityEngine;

namespace Loopstructor.AutoPlayer.Plugin;

/// <summary>
/// Adds a lightweight live-enemy snapshot to the native wave-threat query.
/// Reflection metadata is resolved once; snapshots only read AgentCreator.enemyAgents.
/// </summary>
internal sealed class LiveEnemyThreatReader
{
    private readonly HashSet<ulong> _seenRuntimeIdentities = new();

    private PropertyInfo? _agentCreatorInstance;
    private FieldInfo? _enemyAgents;
    private Type? _basicAiType;
    private PropertyInfo? _aiIsRunning;
    private PropertyInfo? _aiDamageReceiver;
    private PropertyInfo? _aiBattleSystem;
    private PropertyInfo? _aiIsBoss;
    private PropertyInfo? _aiSendsDeathMessage;
    private FieldInfo? _aiId;
    private PropertyInfo? _receiverIsDie;
    private PropertyInfo? _receiverHealth;
    private PropertyInfo? _receiverHealthMax;
    private PropertyInfo? _battleSystemRuntimeHandle;
    private PropertyInfo? _runtimeHandleId;
    private PropertyInfo? _runtimeHandleLifetimeVersion;
    private PropertyInfo? _runtimeHandleIsDisposed;

    public bool IsAvailable { get; private set; }

    public void Initialize()
    {
        IsAvailable = false;
        Type? agentCreatorType = FindType("MetroTD.AISystem.AgentCreator");
        _basicAiType = FindType("BasicAI");
        Type? damageReceiverType = FindType("MetroTD.BattleSystem.DamageReceiver");
        Type? battleSystemType = FindType("MetroTD.BattleSystem.BattleSystem");
        Type? runtimeHandleType = FindType("MetroTD.BattleSystem.BattleSystemRuntimeHandle");
        if (agentCreatorType == null ||
            _basicAiType == null ||
            damageReceiverType == null ||
            battleSystemType == null ||
            runtimeHandleType == null)
        {
            return;
        }

        const BindingFlags staticFlags = BindingFlags.Public | BindingFlags.Static | BindingFlags.FlattenHierarchy;
        const BindingFlags instanceFlags = BindingFlags.Public | BindingFlags.Instance | BindingFlags.FlattenHierarchy;
        _agentCreatorInstance = agentCreatorType.GetProperty("Instance", staticFlags);
        _enemyAgents = agentCreatorType.GetField("enemyAgents", instanceFlags);
        _aiIsRunning = _basicAiType.GetProperty("AIIsRunning", instanceFlags);
        _aiDamageReceiver = _basicAiType.GetProperty("DamageReceiver", instanceFlags);
        _aiBattleSystem = _basicAiType.GetProperty("BattleSystem", instanceFlags);
        _aiIsBoss = _basicAiType.GetProperty("IsBoss", instanceFlags);
        _aiSendsDeathMessage = _basicAiType.GetProperty("SendsDeathMessage", instanceFlags);
        _aiId = _basicAiType.GetField("aiID", instanceFlags);
        _receiverIsDie = damageReceiverType.GetProperty("IsDie", instanceFlags);
        _receiverHealth = damageReceiverType.GetProperty("Health", instanceFlags);
        _receiverHealthMax = damageReceiverType.GetProperty("HealthMax", instanceFlags);
        _battleSystemRuntimeHandle = battleSystemType.GetProperty("RuntimeHandle", instanceFlags);
        _runtimeHandleId = runtimeHandleType.GetProperty("Id", instanceFlags);
        _runtimeHandleLifetimeVersion = runtimeHandleType.GetProperty("LifetimeVersion", instanceFlags);
        _runtimeHandleIsDisposed = runtimeHandleType.GetProperty("IsDisposed", instanceFlags);

        IsAvailable = _agentCreatorInstance != null &&
                      _enemyAgents != null &&
                      _aiIsRunning != null &&
                      _aiDamageReceiver != null &&
                      _aiBattleSystem != null &&
                      _aiIsBoss != null &&
                      _aiSendsDeathMessage != null &&
                      _aiId != null &&
                      _receiverIsDie != null &&
                      _receiverHealth != null &&
                      _receiverHealthMax != null &&
                      _battleSystemRuntimeHandle != null &&
                      _runtimeHandleId != null &&
                      _runtimeHandleLifetimeVersion != null &&
                      _runtimeHandleIsDisposed != null;
    }

    public bool TryEnrich(
        JObject result,
        bool wavePulseAvailable,
        bool inWave,
        int remainingEnemies)
    {
        JObject? state = result.SelectToken("data.state") as JObject
                         ?? result["state"] as JObject;
        if (state == null)
        {
            return false;
        }

        if (!IsAvailable || !TryReadVector(state.SelectToken("mainBase.world"), out Vector3 mainBaseWorld))
        {
            state["liveThreatsAvailable"] = false;
            return false;
        }

        try
        {
            JArray liveThreats = new();
            int accountedLiveCount = CollectLiveThreats(liveThreats, mainBaseWorld);
            bool remainingReliable = wavePulseAvailable &&
                                     inWave &&
                                     remainingEnemies >= 0 &&
                                     remainingEnemies != int.MaxValue;
            int estimatedFutureCount = remainingReliable
                ? Math.Max(remainingEnemies - accountedLiveCount, 0)
                : -1;

            state["liveThreatsAvailable"] = true;
            state["capturedFrame"] = Time.frameCount;
            state["liveThreatCount"] = liveThreats.Count;
            state["accountedLiveCount"] = accountedLiveCount;
            state["liveThreats"] = liveThreats;
            state["enemyAccounting"] = new JObject
            {
                ["globalRemaining"] = wavePulseAvailable ? remainingEnemies : null,
                ["remainingReliable"] = remainingReliable,
                ["estimatedFutureCount"] = remainingReliable ? estimatedFutureCount : null,
                ["consistent"] = remainingReliable ? remainingEnemies >= accountedLiveCount : null
            };
            return true;
        }
        catch
        {
            state["liveThreatsAvailable"] = false;
            return false;
        }
    }

    private int CollectLiveThreats(JArray output, Vector3 mainBaseWorld)
    {
        output.Clear();
        _seenRuntimeIdentities.Clear();
        object? creator = _agentCreatorInstance!.GetValue(null, null);
        if (creator == null || _enemyAgents!.GetValue(creator) is not IList enemyAgents)
        {
            return 0;
        }

        int accountedLiveCount = 0;
        for (int index = 0; index < enemyAgents.Count; index++)
        {
            try
            {
                if (enemyAgents[index] is not GameObject gameObject ||
                    gameObject == null ||
                    !gameObject.activeInHierarchy)
                {
                    continue;
                }

                Component? ai = gameObject.GetComponent(_basicAiType!);
                if (ai == null)
                {
                    continue;
                }

                object? receiver = _aiDamageReceiver!.GetValue(ai, null);
                if (receiver == null || ReadBoolean(_receiverIsDie!, receiver))
                {
                    continue;
                }

                object? battleSystem = _aiBattleSystem!.GetValue(ai, null);
                object? runtimeHandle = battleSystem == null
                    ? null
                    : _battleSystemRuntimeHandle!.GetValue(battleSystem, null);
                if (runtimeHandle == null || ReadBoolean(_runtimeHandleIsDisposed!, runtimeHandle))
                {
                    continue;
                }

                int handleId = ReadInt32(_runtimeHandleId!, runtimeHandle);
                int lifetimeVersion = ReadInt32(_runtimeHandleLifetimeVersion!, runtimeHandle);
                if (handleId <= 0 || lifetimeVersion <= 0)
                {
                    continue;
                }

                ulong identity = ((ulong)(uint)handleId << 32) | (uint)lifetimeVersion;
                if (!_seenRuntimeIdentities.Add(identity))
                {
                    continue;
                }

                Vector3 world = gameObject.transform.position;
                Vector3 relative = world - mainBaseWorld;
                if (!IsFinite(world) || !IsFinite(relative))
                {
                    continue;
                }

                bool countsTowardWave = ReadBoolean(_aiSendsDeathMessage!, ai);
                if (countsTowardWave)
                {
                    accountedLiveCount++;
                }

                output.Add(new JObject
                {
                    ["instanceId"] = gameObject.GetInstanceID(),
                    ["runtimeHandleId"] = handleId,
                    ["lifetimeVersion"] = lifetimeVersion,
                    ["typeValue"] = ReadInt32(_aiId!, ai),
                    ["isBoss"] = ReadBoolean(_aiIsBoss!, ai),
                    ["aiRunning"] = ReadBoolean(_aiIsRunning!, ai),
                    ["countsTowardWave"] = countsTowardWave,
                    ["world"] = VectorData(world),
                    ["relativeToMainBase"] = new JObject
                    {
                        ["vector"] = VectorData(relative)
                    },
                    ["health"] = FiniteNumber(_receiverHealth!, receiver),
                    ["healthMax"] = FiniteNumber(_receiverHealthMax!, receiver)
                });
            }
            catch
            {
                // A pooled object can become invalid while it is being inspected; skip only that object.
            }
        }

        return accountedLiveCount;
    }

    private static bool ReadBoolean(PropertyInfo property, object target) =>
        property.GetValue(target, null) is bool value && value;

    private static int ReadInt32(MemberInfo member, object target)
    {
        object? value = member switch
        {
            PropertyInfo property => property.GetValue(target, null),
            FieldInfo field => field.GetValue(target),
            _ => null
        };
        return value == null ? 0 : Convert.ToInt32(value);
    }

    private static JToken FiniteNumber(PropertyInfo property, object target)
    {
        object? value = property.GetValue(target, null);
        if (value == null)
        {
            return JValue.CreateNull();
        }

        double number = Convert.ToDouble(value);
        return double.IsNaN(number) || double.IsInfinity(number)
            ? JValue.CreateNull()
            : new JValue(number);
    }

    private static bool TryReadVector(JToken? token, out Vector3 vector)
    {
        vector = default;
        if (token is not JObject value ||
            !TryReadSingle(value["x"], out float x) ||
            !TryReadSingle(value["y"], out float y) ||
            !TryReadSingle(value["z"], out float z))
        {
            return false;
        }

        vector = new Vector3(x, y, z);
        return IsFinite(vector);
    }

    private static bool TryReadSingle(JToken? token, out float value)
    {
        value = 0f;
        if (token?.Type is not (JTokenType.Integer or JTokenType.Float))
        {
            return false;
        }

        value = token.Value<float>();
        return !float.IsNaN(value) && !float.IsInfinity(value);
    }

    private static bool IsFinite(Vector3 value) =>
        !float.IsNaN(value.x) && !float.IsInfinity(value.x) &&
        !float.IsNaN(value.y) && !float.IsInfinity(value.y) &&
        !float.IsNaN(value.z) && !float.IsInfinity(value.z);

    private static JObject VectorData(Vector3 value) => new()
    {
        ["x"] = value.x,
        ["y"] = value.y,
        ["z"] = value.z
    };

    private static Type? FindType(string fullName)
    {
        foreach (Assembly assembly in AppDomain.CurrentDomain.GetAssemblies())
        {
            Type? type = assembly.GetType(fullName, false);
            if (type != null)
            {
                return type;
            }
        }

        return null;
    }
}
