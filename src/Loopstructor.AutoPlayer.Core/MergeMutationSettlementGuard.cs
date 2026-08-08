using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Newtonsoft.Json.Linq;

namespace Loopstructor.AutoPlayer.Core;

public enum MergeMutationSettlementStatus
{
    None,
    Waiting,
    Settled,
    TimedOut
}

/// <summary>
/// Retains one merge mutation until a later read-only panel observation proves that
/// the original write can no longer be replayed against the same panel state.
/// </summary>
public sealed class MergeMutationSettlementGuard
{
    private const string OpenPanelCommand = "openMergePanel";
    private const string SelectVehicleCommand = "selectMergeVehicle";
    private const string SubmitSelectionCommand = "submitMergeSelection";
    private const string ChooseFetterCommand = "chooseMergeFetter";
    private const string ConfirmSettlementCommand = "confirmMergeSettlement";
    private const string ClosePanelCommand = "closeMergePanel";

    private static readonly HashSet<string> SupportedCommands = new(StringComparer.Ordinal)
    {
        OpenPanelCommand,
        SelectVehicleCommand,
        SubmitSelectionCommand,
        ChooseFetterCommand,
        ConfirmSettlementCommand,
        ClosePanelCommand
    };

    private int _targetItemInstanceId;
    private int _targetVehicleInstanceId;
    private int _initialSelectedVehicleCount = -1;
    private string _initialPhase = string.Empty;

    public bool IsArmed { get; private set; }
    public string Command { get; private set; } = string.Empty;
    public int PanelIdentity { get; private set; }
    public string RosterIdentity { get; private set; } = string.Empty;
    public string VehicleIdentity { get; private set; } = string.Empty;
    public string GroupIdentity { get; private set; } = string.Empty;
    public string MutationIdentity { get; private set; } = string.Empty;
    public bool OutcomeUnknown { get; private set; }
    public float StartedAt { get; private set; } = -1f;
    public MergeMutationSettlementStatus Status { get; private set; } =
        MergeMutationSettlementStatus.None;

    /// <summary>
    /// Arms one merge mutation. While armed, every subsequent call is rejected so
    /// the caller cannot interleave or replay another write before reconciliation.
    /// </summary>
    public bool TryArm(
        AutomationAction? action,
        JObject? identitySource,
        bool outcomeUnknown,
        float now)
    {
        if (IsArmed || action == null)
        {
            return false;
        }

        string command = NormalizeCommand(action.Command);
        if (!SupportedCommands.Contains(command))
        {
            return false;
        }

        JObject? state = TryReadState(identitySource);
        PanelIdentity = ReadPositiveInt(action.Arguments["panelInstanceId"])
                        ?? ReadPositiveInt(state?["panelInstanceId"])
                        ?? 0;
        RosterIdentity = ReadText(action.Arguments["rosterFingerprint"])
                         ?? ReadText(state?["rosterFingerprint"])
                         ?? string.Empty;
        _targetItemInstanceId = ReadNonZeroInt(action.Arguments["itemInstanceId"]) ?? 0;
        _targetVehicleInstanceId = ReadNonZeroInt(action.Arguments["vehicleInstanceId"]) ?? 0;
        VehicleIdentity = BuildVehicleIdentity(
            _targetItemInstanceId,
            _targetVehicleInstanceId,
            ReadNonNegativeInt(action.Arguments["index"]));
        GroupIdentity = BuildGroupIdentity(action.Arguments, state);
        _initialPhase = ReadText(state?["phase"])?.ToLowerInvariant() ?? string.Empty;
        _initialSelectedVehicleCount = ReadNonNegativeInt(state?["selectedVehicleCount"])
                                       ?? ReadNonNegativeInt(state?["mergeSelectedCount"])
                                       ?? -1;

        Command = command;
        OutcomeUnknown = outcomeUnknown || HasUnknownOutcome(identitySource);
        StartedAt = now;
        MutationIdentity = BuildMutationIdentity(
            Command,
            PanelIdentity,
            RosterIdentity,
            VehicleIdentity,
            GroupIdentity,
            action.Arguments);
        IsArmed = true;
        Status = MergeMutationSettlementStatus.Waiting;
        return true;
    }

    /// <summary>
    /// Returns true when an action resolves to the currently retained write identity.
    /// An armed guard must still block other merge writes as well; this method exists
    /// to make an accidental direct replay explicit in diagnostics and tests.
    /// </summary>
    public bool IsReplay(AutomationAction? action, JObject? identitySource)
    {
        if (!IsArmed || action == null)
        {
            return false;
        }

        string command = NormalizeCommand(action.Command);
        if (!string.Equals(command, Command, StringComparison.Ordinal))
        {
            return false;
        }

        JObject? state = TryReadState(identitySource);
        int panelIdentity = ReadPositiveInt(action.Arguments["panelInstanceId"])
                            ?? ReadPositiveInt(state?["panelInstanceId"])
                            ?? 0;
        string rosterIdentity = ReadText(action.Arguments["rosterFingerprint"])
                                ?? ReadText(state?["rosterFingerprint"])
                                ?? string.Empty;
        string vehicleIdentity = BuildVehicleIdentity(
            ReadNonZeroInt(action.Arguments["itemInstanceId"]) ?? 0,
            ReadNonZeroInt(action.Arguments["vehicleInstanceId"]) ?? 0,
            ReadNonNegativeInt(action.Arguments["index"]));
        string groupIdentity = BuildGroupIdentity(action.Arguments, state);
        string mutationIdentity = BuildMutationIdentity(
            command,
            panelIdentity,
            rosterIdentity,
            vehicleIdentity,
            groupIdentity,
            action.Arguments);
        return string.Equals(MutationIdentity, mutationIdentity, StringComparison.Ordinal);
    }

    /// <summary>
    /// Reconciles the retained write only from a read-only merge panel snapshot.
    /// Missing or partial snapshots never prove settlement.
    /// </summary>
    public MergeMutationSettlementStatus Observe(
        JObject? readOnlyResult,
        float now,
        float timeoutSeconds)
    {
        if (!IsArmed)
        {
            return MergeMutationSettlementStatus.None;
        }

        if (Status == MergeMutationSettlementStatus.Settled)
        {
            return Status;
        }

        JObject? state = TryReadState(readOnlyResult);
        if (state != null && IsSettledByObservation(state))
        {
            Status = MergeMutationSettlementStatus.Settled;
            return Status;
        }

        // A timeout keeps the mutation locked, but it must not permanently hide a
        // later read-only observation that proves the panel has advanced. This is
        // what lets a paused run recover after the player finishes or closes the UI.
        if (Status == MergeMutationSettlementStatus.TimedOut)
        {
            return Status;
        }

        float timeout = Math.Max(0.1f, timeoutSeconds);
        if (now - StartedAt >= timeout)
        {
            Status = MergeMutationSettlementStatus.TimedOut;
        }

        return Status;
    }

    public void Reset()
    {
        IsArmed = false;
        Command = string.Empty;
        PanelIdentity = 0;
        RosterIdentity = string.Empty;
        VehicleIdentity = string.Empty;
        GroupIdentity = string.Empty;
        MutationIdentity = string.Empty;
        OutcomeUnknown = false;
        StartedAt = -1f;
        Status = MergeMutationSettlementStatus.None;
        _targetItemInstanceId = 0;
        _targetVehicleInstanceId = 0;
        _initialSelectedVehicleCount = -1;
        _initialPhase = string.Empty;
    }

    private bool IsSettledByObservation(JObject state)
    {
        bool? mergeOpen = ReadBoolean(state["mergeOpen"]);
        if (mergeOpen == false)
        {
            return !string.Equals(Command, OpenPanelCommand, StringComparison.Ordinal);
        }

        if (mergeOpen != true)
        {
            return false;
        }

        int observedPanelIdentity = ReadPositiveInt(state["panelInstanceId"]) ?? 0;
        if (PanelIdentity != 0 && observedPanelIdentity != 0 && observedPanelIdentity != PanelIdentity)
        {
            return true;
        }

        string observedRosterIdentity = ReadText(state["rosterFingerprint"]) ?? string.Empty;
        if (RosterIdentity.Length > 0 && observedRosterIdentity.Length > 0 &&
            !string.Equals(RosterIdentity, observedRosterIdentity, StringComparison.Ordinal))
        {
            return true;
        }

        string phase = ReadText(state["phase"])?.ToLowerInvariant() ?? string.Empty;
        switch (Command)
        {
            case OpenPanelCommand:
                return true;

            case SelectVehicleCommand:
                return IsTargetSelected(state) || HasAdvancedFromSelection(phase);

            case SubmitSelectionCommand:
                return HasAdvancedFromSelection(phase) || SelectedVehicleStateChanged(state);

            case ChooseFetterCommand:
                return phase is "settlement" or "transition" or "selection";

            case ConfirmSettlementCommand:
                return phase.Length > 0 && !string.Equals(phase, "settlement", StringComparison.Ordinal);

            case ClosePanelCommand:
                return false;

            default:
                return false;
        }
    }

    private bool IsTargetSelected(JObject state)
    {
        if (_targetItemInstanceId == 0 && _targetVehicleInstanceId == 0)
        {
            return false;
        }

        if (state["mergeVehicles"] is not JArray vehicles)
        {
            return false;
        }

        foreach (JObject item in vehicles.OfType<JObject>())
        {
            int itemInstanceId = ReadNonZeroInt(item["instanceId"]) ?? 0;
            int vehicleInstanceId = ReadNonZeroInt(item.SelectToken("vehicle.instanceId")) ?? 0;
            bool identityMatches = _targetItemInstanceId != 0 && itemInstanceId == _targetItemInstanceId;
            identityMatches |= _targetVehicleInstanceId != 0 && vehicleInstanceId == _targetVehicleInstanceId;
            if (identityMatches && ReadBoolean(item["selected"]) == true)
            {
                return true;
            }
        }

        return false;
    }

    private bool SelectedVehicleStateChanged(JObject state)
    {
        if (_initialSelectedVehicleCount < 0)
        {
            return false;
        }

        int observedCount = ReadNonNegativeInt(state["selectedVehicleCount"])
                            ?? ReadNonNegativeInt(state["mergeSelectedCount"])
                            ?? -1;
        return observedCount >= 0 && observedCount != _initialSelectedVehicleCount;
    }

    private bool HasAdvancedFromSelection(string phase)
    {
        if (phase.Length == 0)
        {
            return false;
        }

        string baseline = _initialPhase.Length > 0 ? _initialPhase : "selection";
        return string.Equals(baseline, "selection", StringComparison.Ordinal) &&
               !string.Equals(phase, "selection", StringComparison.Ordinal);
    }

    private static string BuildMutationIdentity(
        string command,
        int panelIdentity,
        string rosterIdentity,
        string vehicleIdentity,
        string groupIdentity,
        JObject arguments)
    {
        List<string> parts = new()
        {
            command,
            panelIdentity == 0
                ? "panel:?"
                : "panel:" + panelIdentity.ToString(CultureInfo.InvariantCulture),
            rosterIdentity.Length == 0 ? "roster:?" : "roster:" + rosterIdentity,
            vehicleIdentity.Length == 0 ? "vehicle:?" : vehicleIdentity,
            groupIdentity.Length == 0 ? "group:?" : groupIdentity
        };

        if (ReadNonNegativeInt(arguments["index"]) is int index)
        {
            parts.Add("index:" + index.ToString(CultureInfo.InvariantCulture));
        }

        return string.Join("|", parts);
    }

    private static string BuildVehicleIdentity(int itemInstanceId, int vehicleInstanceId, int? index)
    {
        List<string> parts = new();
        if (itemInstanceId != 0)
        {
            parts.Add("item:" + itemInstanceId.ToString(CultureInfo.InvariantCulture));
        }

        if (vehicleInstanceId != 0)
        {
            parts.Add("vehicle:" + vehicleInstanceId.ToString(CultureInfo.InvariantCulture));
        }

        if (parts.Count == 0 && index.HasValue)
        {
            parts.Add("index:" + index.Value.ToString(CultureInfo.InvariantCulture));
        }

        return string.Join(",", parts);
    }

    private static string BuildGroupIdentity(JObject arguments, JObject? state)
    {
        string fromArguments = BuildGroupIdentityFromToken(arguments);
        if (fromArguments.Length > 0)
        {
            return fromArguments;
        }

        if (state?["mergeSubmitRule"] is JObject rule)
        {
            string fromRule = BuildGroupIdentityFromRule(rule);
            if (fromRule.Length > 0)
            {
                return fromRule;
            }
        }

        if (state?["legalMergeGroups"] is JArray groups && groups.Count == 1 && groups[0] is JObject group)
        {
            return BuildGroupIdentityFromToken(group);
        }

        return string.Empty;
    }

    private static string BuildGroupIdentityFromToken(JObject token)
    {
        List<string> parts = new();
        AddTextPart(parts, "material", token["materialVehicleType"]);
        AddTextPart(parts, "result", token["resultVehicleType"]);
        AddIntegerPart(parts, "required", token["requiredVehicleCount"]);
        AddIntegerArrayPart(parts, "indexes", token["candidateVehicleIndexes"]);
        AddIntegerArrayPart(parts, "items", token["candidateItemInstanceIds"]);
        AddIntegerArrayPart(parts, "vehicles", token["candidateVehicleInstanceIds"]);
        return string.Join(";", parts);
    }

    private static string BuildGroupIdentityFromRule(JObject rule)
    {
        List<string> parts = new();
        AddTextPart(parts, "result", rule["resultVehicleType"]);
        AddIntegerPart(parts, "required", rule["requiredVehicleCount"]);
        AddIntegerArrayPart(parts, "indexes", rule["materialIndexes"]);
        if (rule["selectedVehicles"] is JArray selectedVehicles)
        {
            int[] instanceIds = selectedVehicles
                .OfType<JObject>()
                .Select(item => ReadNonZeroInt(item["instanceId"]) ?? 0)
                .Where(instanceId => instanceId != 0)
                .ToArray();
            if (instanceIds.Length > 0)
            {
                parts.Add("vehicles:" + string.Join(",", instanceIds.Select(value =>
                    value.ToString(CultureInfo.InvariantCulture))));
            }
        }

        return string.Join(";", parts);
    }

    private static void AddTextPart(ICollection<string> parts, string name, JToken? token)
    {
        string? value = ReadText(token);
        if (value != null)
        {
            parts.Add(name + ":" + value);
        }
    }

    private static void AddIntegerPart(ICollection<string> parts, string name, JToken? token)
    {
        if (ReadNonNegativeInt(token) is int value)
        {
            parts.Add(name + ":" + value.ToString(CultureInfo.InvariantCulture));
        }
    }

    private static void AddIntegerArrayPart(ICollection<string> parts, string name, JToken? token)
    {
        if (token is not JArray array || array.Count == 0 ||
            array.Any(item => item.Type != JTokenType.Integer))
        {
            return;
        }

        parts.Add(name + ":" + string.Join(",", array.Values<int>().Select(value =>
            value.ToString(CultureInfo.InvariantCulture))));
    }

    private static bool HasUnknownOutcome(JObject? result) =>
        RuntimeResultInspector.IsUnsafe(result) ||
        result?.SelectToken("data.state.invocationStarted")?.Value<bool>() == true &&
        result.SelectToken("data.state.selectionWriteVerified")?.Value<bool>() != true;

    private static JObject? TryReadState(JObject? result)
    {
        if (result == null)
        {
            return null;
        }

        return result.SelectToken("data.state") as JObject
               ?? result["state"] as JObject
               ?? (result.Property("mergeOpen") != null ? result : null);
    }

    private static string NormalizeCommand(string? command)
    {
        string candidate = command?.Trim() ?? string.Empty;
        return SupportedCommands.FirstOrDefault(supported =>
                   string.Equals(supported, candidate, StringComparison.OrdinalIgnoreCase))
               ?? candidate;
    }

    private static bool? ReadBoolean(JToken? token) =>
        token?.Type == JTokenType.Boolean ? token.Value<bool>() : null;

    private static int? ReadPositiveInt(JToken? token) =>
        token?.Type == JTokenType.Integer && token.Value<int>() > 0
            ? token.Value<int>()
            : null;

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
        return value.Length > 0 ? value : null;
    }
}
