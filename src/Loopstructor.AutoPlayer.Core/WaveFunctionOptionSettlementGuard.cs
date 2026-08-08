using System;
using System.Collections.Generic;
using System.Linq;
using Newtonsoft.Json.Linq;

namespace Loopstructor.AutoPlayer.Core;

public enum WaveFunctionOptionSettlementStatus
{
    None,
    Waiting,
    Settled,
    TimedOut
}

/// <summary>
/// Retains one EventUI or RepairUI option click until later read-only state proves
/// that the original option can no longer be clicked. A timeout never releases the lock.
/// </summary>
public sealed class WaveFunctionOptionSettlementGuard
{
    public bool IsArmed { get; private set; }
    public string Panel { get; private set; } = string.Empty;
    public int Index { get; private set; } = -1;
    public int PanelInstanceId { get; private set; }
    public int ItemInstanceId { get; private set; }
    public string OptionIdentity { get; private set; } = string.Empty;
    public bool OutcomeUnknown { get; private set; }
    public float StartedAt { get; private set; } = -1f;
    public WaveFunctionOptionSettlementStatus Status { get; private set; } =
        WaveFunctionOptionSettlementStatus.None;

    public bool TryArm(
        AutomationAction? action,
        JObject? identitySource,
        bool outcomeUnknown,
        float now)
    {
        if (IsArmed || action == null ||
            !string.Equals(
                action.Command,
                "chooseWaveFunctionOption",
                StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        string panel = ReadText(action.Arguments["panel"]) ?? string.Empty;
        int index = ReadNonNegativeInt(action.Arguments["index"]) ?? -1;
        bool repairPanel = string.Equals(panel, "RepairUI", StringComparison.OrdinalIgnoreCase);
        bool eventPanel = string.Equals(panel, "EventUI", StringComparison.OrdinalIgnoreCase);
        if ((!repairPanel && !eventPanel) || index < 0)
        {
            return false;
        }

        JObject? state = TryReadState(identitySource);
        Panel = repairPanel ? "RepairUI" : "EventUI";
        Index = index;
        PanelInstanceId = ReadNonZeroInt(action.Arguments["panelInstanceId"])
                          ?? ReadNonZeroInt(state?["panelInstanceId"])
                          ?? 0;
        ItemInstanceId = ReadNonZeroInt(action.Arguments["instanceId"])
                         ?? ReadNonZeroInt(state?["instanceId"])
                         ?? 0;
        OptionIdentity = ReadText(action.Arguments["optionIdentity"])
                         ?? BuildOptionIdentity(state)
                         ?? string.Empty;
        if (ItemInstanceId == 0 && OptionIdentity.Length == 0)
        {
            Reset();
            return false;
        }

        OutcomeUnknown = outcomeUnknown || RuntimeResultInspector.IsUnsafe(identitySource);
        StartedAt = now;
        IsArmed = true;
        Status = WaveFunctionOptionSettlementStatus.Waiting;
        return true;
    }

    /// <summary>
    /// Reconciles only from an explicitly complete snapshot for the retained panel.
    /// A native or partial empty list is not evidence that the option disappeared.
    /// </summary>
    public WaveFunctionOptionSettlementStatus ObserveOptions(
        JObject? readOnlyResult,
        float now,
        float timeoutSeconds)
    {
        if (!IsArmed)
        {
            return WaveFunctionOptionSettlementStatus.None;
        }

        JObject? state = TryReadState(readOnlyResult);
        string panelProperty = string.Equals(Panel, "RepairUI", StringComparison.Ordinal)
            ? "repairPanel"
            : "eventPanel";
        JObject? panel = state?[panelProperty] as JObject;
        if (panel?["snapshotComplete"]?.Value<bool>() == true)
        {
            bool? panelOpen = ReadBoolean(panel["panelOpen"]);
            if (panelOpen == false)
            {
                Status = WaveFunctionOptionSettlementStatus.Settled;
                return Status;
            }

            if (panelOpen == true)
            {
                int observedPanelId = ReadNonZeroInt(panel["panelInstanceId"]) ?? 0;
                if (PanelInstanceId != 0 && observedPanelId != 0 &&
                    PanelInstanceId != observedPanelId)
                {
                    Status = WaveFunctionOptionSettlementStatus.Settled;
                    return Status;
                }

                if (panel["options"] is JArray options && TargetMissing(options))
                {
                    Status = WaveFunctionOptionSettlementStatus.Settled;
                    return Status;
                }
            }
        }

        return UpdateTimeout(now, timeoutSeconds);
    }

    /// <summary>
    /// A successful wave query is authoritative about blockers. The retained panel
    /// blocker disappearing proves that the original panel has closed or advanced.
    /// </summary>
    public WaveFunctionOptionSettlementStatus ObservePanelVisibility(
        bool snapshotComplete,
        bool repairPanelOpen,
        float now,
        float timeoutSeconds)
    {
        if (!IsArmed)
        {
            return WaveFunctionOptionSettlementStatus.None;
        }

        if (snapshotComplete && !repairPanelOpen)
        {
            Status = WaveFunctionOptionSettlementStatus.Settled;
            return Status;
        }

        return UpdateTimeout(now, timeoutSeconds);
    }

    public void Reset()
    {
        IsArmed = false;
        Panel = string.Empty;
        Index = -1;
        PanelInstanceId = 0;
        ItemInstanceId = 0;
        OptionIdentity = string.Empty;
        OutcomeUnknown = false;
        StartedAt = -1f;
        Status = WaveFunctionOptionSettlementStatus.None;
    }

    public static string? BuildOptionIdentity(JObject? option)
    {
        if (option == null)
        {
            return null;
        }

        List<string> parts = new();
        AddText(parts, "name", option["optionName"]);
        AddText(parts, "item", option["currentItemType"]);
        AddText(parts, "extra", option["extraDataType"]);
        AddText(parts, "text", option["displayText"]);
        AddArray(parts, "behaviours", option["behaviourTypeIds"] ?? option["behaviourTypes"]);
        return parts.Count == 0 ? null : string.Join("|", parts);
    }

    private bool TargetMissing(JArray options)
    {
        JObject[] visible = options.OfType<JObject>().ToArray();
        if (ItemInstanceId != 0)
        {
            return visible.All(option =>
                (ReadNonZeroInt(option["instanceId"]) ?? 0) != ItemInstanceId);
        }

        if (OptionIdentity.Length > 0)
        {
            return visible.All(option =>
                !string.Equals(
                    BuildOptionIdentity(option),
                    OptionIdentity,
                    StringComparison.Ordinal));
        }

        // Panel + index is enough to retain the lock, but not enough to prove that
        // an option at the same position is still the original clickable object.
        return false;
    }

    private WaveFunctionOptionSettlementStatus UpdateTimeout(float now, float timeoutSeconds)
    {
        if (Status == WaveFunctionOptionSettlementStatus.Settled)
        {
            return Status;
        }

        if (Status == WaveFunctionOptionSettlementStatus.TimedOut)
        {
            return Status;
        }

        float timeout = Math.Max(0.1f, timeoutSeconds);
        if (now - StartedAt >= timeout)
        {
            Status = WaveFunctionOptionSettlementStatus.TimedOut;
        }

        return Status;
    }

    private static JObject? TryReadState(JObject? result)
    {
        if (result == null)
        {
            return null;
        }

        return result.SelectToken("data.state") as JObject
               ?? result["state"] as JObject
               ?? (result.Property("panel") != null || result.Property("repairPanel") != null
                   ? result
                   : null);
    }

    private static bool? ReadBoolean(JToken? token) =>
        token?.Type == JTokenType.Boolean ? token.Value<bool>() : null;

    private static int? ReadNonZeroInt(JToken? token) =>
        token?.Type == JTokenType.Integer && token.Value<int>() != 0
            ? token.Value<int>()
            : null;

    private static int? ReadNonNegativeInt(JToken? token) =>
        token?.Type == JTokenType.Integer && token.Value<int>() >= 0
            ? token.Value<int>()
            : null;

    private static string? ReadText(JToken? token)
    {
        if (token?.Type != JTokenType.String)
        {
            return null;
        }

        string value = token.Value<string>()?.Trim() ?? string.Empty;
        return value.Length == 0 ? null : value;
    }

    private static void AddText(ICollection<string> parts, string name, JToken? token)
    {
        string? value = ReadText(token);
        if (value != null)
        {
            parts.Add(name + ":" + value);
        }
    }

    private static void AddArray(ICollection<string> parts, string name, JToken? token)
    {
        if (token is not JArray array || array.Count == 0)
        {
            return;
        }

        string[] values = array
            .Select(item => item.Type == JTokenType.String ? item.Value<string>()?.Trim() : null)
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .Cast<string>()
            .ToArray();
        if (values.Length > 0)
        {
            parts.Add(name + ":" + string.Join(",", values));
        }
    }
}
