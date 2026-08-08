using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Newtonsoft.Json.Linq;

namespace Loopstructor.AutoPlayer.Core;

public enum PendingDisposableMutationResolution
{
    None,
    Waiting,
    InteractionObserved,
    TargetAttributeCatapultObserved,
    TargetCatapultObserved,
    Unknown
}

/// <summary>
/// Retains the identity of a disposable write whose runtime response was Pending.
/// The guard never retries that write. It can only be resolved by later read-only
/// observations or, when no outcome can be proven, as Unknown after the deadline.
/// </summary>
public sealed class PendingDisposableMutationGuard
{
    private const string UseDisposableCommand = "useDisposable";
    private const string ConfirmDisposableGridCommand = "confirmDisposableGrid";
    private const string AttributeCatapultDisposableEnum = "FreePoint_Attribute";

    private int? _targetGridX;
    private int? _targetGridY;

    public bool IsArmed { get; private set; }
    public string Command { get; private set; } = string.Empty;
    public string DisposableEnum { get; private set; } = string.Empty;
    public string ActionIdentity { get; private set; } = string.Empty;
    public string MutationIdentity { get; private set; } = string.Empty;
    public float StartedAt { get; private set; } = -1f;
    public int ResolvedInteractionInstanceId { get; private set; }
    public PendingDisposableMutationResolution Resolution { get; private set; } =
        PendingDisposableMutationResolution.None;

    /// <summary>
    /// Arms the guard for one supported Pending write. While armed, no other
    /// mutation can be armed, including a repeat carrying the same identity.
    /// </summary>
    public bool TryArm(AutomationAction? action, string? disposableEnum, float now)
    {
        if (IsArmed || action == null || string.IsNullOrWhiteSpace(disposableEnum))
        {
            return false;
        }

        string normalizedCommand = NormalizeCommand(action.Command);
        string normalizedDisposableEnum = disposableEnum!.Trim();
        if (string.Equals(normalizedCommand, UseDisposableCommand, StringComparison.Ordinal))
        {
            if (!TryReadActionIdentity(action.Arguments, out string actionIdentity))
            {
                return false;
            }

            Arm(
                normalizedCommand,
                normalizedDisposableEnum,
                actionIdentity,
                targetGridX: null,
                targetGridY: null,
                now);
            return true;
        }

        if (string.Equals(normalizedCommand, ConfirmDisposableGridCommand, StringComparison.Ordinal))
        {
            if (!TryReadGrid(action.Arguments["grid"], out int gridX, out int gridY))
            {
                return false;
            }

            string actionIdentity = "grid:" +
                                    gridX.ToString(CultureInfo.InvariantCulture) + "," +
                                    gridY.ToString(CultureInfo.InvariantCulture);
            Arm(
                normalizedCommand,
                normalizedDisposableEnum,
                actionIdentity,
                gridX,
                gridY,
                now);
            return true;
        }

        return false;
    }

    /// <summary>
    /// Reconciles the Pending write exclusively from read-only disposable and
    /// catapult snapshots. Null snapshots mean that state was not observable and
    /// cannot prove success or failure.
    /// </summary>
    public PendingDisposableMutationResolution Observe(
        JObject? disposableResult,
        JObject? catapultResult,
        float now,
        float timeoutSeconds)
    {
        if (!IsArmed)
        {
            return PendingDisposableMutationResolution.None;
        }

        if (Resolution != PendingDisposableMutationResolution.Waiting)
        {
            return Resolution;
        }

        JObject? disposableState = TryReadState(disposableResult);
        if (disposableState != null &&
            disposableState["isInPreview"]?.Value<bool>() == true &&
            string.Equals(
                disposableState["disposableEnum"]?.Value<string>(),
                DisposableEnum,
                StringComparison.Ordinal) &&
            ReadNonZeroInt(disposableState["interactionInstanceId"]) is int interactionInstanceId)
        {
            ResolvedInteractionInstanceId = interactionInstanceId;
            if (string.Equals(Command, UseDisposableCommand, StringComparison.Ordinal))
            {
                Resolution = PendingDisposableMutationResolution.InteractionObserved;
                return Resolution;
            }

            // A grid confirmation cannot claim an arbitrary same-enum preview: it
            // may belong to the player or another system. Keep the observed ID only
            // for diagnostics and require the target-grid catapult as proof.
        }

        if (string.Equals(
                DisposableEnum,
                AttributeCatapultDisposableEnum,
                StringComparison.Ordinal) &&
            _targetGridX.HasValue &&
            _targetGridY.HasValue &&
            HasTargetAttributeCatapult(catapultResult, _targetGridX.Value, _targetGridY.Value))
        {
            Resolution = PendingDisposableMutationResolution.TargetAttributeCatapultObserved;
            return Resolution;
        }

        if (!string.Equals(
                DisposableEnum,
                AttributeCatapultDisposableEnum,
                StringComparison.Ordinal) &&
            _targetGridX.HasValue &&
            _targetGridY.HasValue &&
            HasTargetDisposableCatapult(
                catapultResult,
                DisposableEnum,
                _targetGridX.Value,
                _targetGridY.Value))
        {
            Resolution = PendingDisposableMutationResolution.TargetCatapultObserved;
            return Resolution;
        }

        float timeout = Math.Max(0.1f, timeoutSeconds);
        if (now - StartedAt >= timeout)
        {
            Resolution = PendingDisposableMutationResolution.Unknown;
        }

        return Resolution;
    }

    public void Reset()
    {
        IsArmed = false;
        Command = string.Empty;
        DisposableEnum = string.Empty;
        ActionIdentity = string.Empty;
        MutationIdentity = string.Empty;
        StartedAt = -1f;
        ResolvedInteractionInstanceId = 0;
        Resolution = PendingDisposableMutationResolution.None;
        _targetGridX = null;
        _targetGridY = null;
    }

    private void Arm(
        string command,
        string disposableEnum,
        string actionIdentity,
        int? targetGridX,
        int? targetGridY,
        float now)
    {
        IsArmed = true;
        Command = command;
        DisposableEnum = disposableEnum;
        ActionIdentity = actionIdentity;
        MutationIdentity = command + "|" + disposableEnum + "|" + actionIdentity;
        StartedAt = now;
        ResolvedInteractionInstanceId = 0;
        Resolution = PendingDisposableMutationResolution.Waiting;
        _targetGridX = targetGridX;
        _targetGridY = targetGridY;
    }

    private static string NormalizeCommand(string? command)
    {
        if (string.Equals(command, UseDisposableCommand, StringComparison.OrdinalIgnoreCase))
        {
            return UseDisposableCommand;
        }

        if (string.Equals(command, ConfirmDisposableGridCommand, StringComparison.OrdinalIgnoreCase))
        {
            return ConfirmDisposableGridCommand;
        }

        return command?.Trim() ?? string.Empty;
    }

    private static bool TryReadActionIdentity(JObject arguments, out string identity)
    {
        identity = string.Empty;
        int itemInstanceId = ReadNonZeroInt(arguments["itemInstanceId"])
                             ?? ReadNonZeroInt(arguments["instanceId"])
                             ?? 0;
        if (itemInstanceId != 0)
        {
            identity = "instance:" + itemInstanceId.ToString(CultureInfo.InvariantCulture);
            return true;
        }

        string? path = arguments["itemPath"]?.Value<string>()
                       ?? arguments["path"]?.Value<string>();
        if (!string.IsNullOrWhiteSpace(path))
        {
            identity = "path:" + path!.Trim();
            return true;
        }

        if (arguments["index"]?.Type == JTokenType.Integer)
        {
            int index = arguments["index"]!.Value<int>();
            if (index >= 0)
            {
                identity = "index:" + index.ToString(CultureInfo.InvariantCulture);
                return true;
            }
        }

        return false;
    }

    private static bool HasTargetAttributeCatapult(JObject? catapultResult, int gridX, int gridY)
    {
        JObject? state = TryReadState(catapultResult);
        IEnumerable<JObject> catapults = (state?["catapults"] as JArray)?.OfType<JObject>()
                                         ?? Enumerable.Empty<JObject>();
        return catapults.Any(catapult =>
            catapult["isAttribute"]?.Value<bool>() == true &&
            TryReadGrid(catapult["grid"], out int observedX, out int observedY) &&
            observedX == gridX &&
            observedY == gridY);
    }

    private static bool HasTargetDisposableCatapult(
        JObject? catapultResult,
        string disposableEnum,
        int gridX,
        int gridY)
    {
        JObject? state = TryReadState(catapultResult);
        IEnumerable<JObject> catapults = (state?["catapults"] as JArray)?.OfType<JObject>()
                                         ?? Enumerable.Empty<JObject>();
        return catapults.Any(catapult =>
            string.Equals(
                catapult["recycleDisposableEnum"]?.Value<string>(),
                disposableEnum,
                StringComparison.Ordinal) &&
            TryReadGrid(catapult["grid"], out int observedX, out int observedY) &&
            observedX == gridX &&
            observedY == gridY);
    }

    private static JObject? TryReadState(JObject? result)
    {
        if (result == null)
        {
            return null;
        }

        return result.SelectToken("data.state") as JObject
               ?? result["state"] as JObject
               ?? result;
    }

    private static bool TryReadGrid(JToken? token, out int x, out int y)
    {
        x = 0;
        y = 0;
        if (token is not JObject grid ||
            grid["x"]?.Type != JTokenType.Integer ||
            grid["y"]?.Type != JTokenType.Integer)
        {
            return false;
        }

        x = grid["x"]!.Value<int>();
        y = grid["y"]!.Value<int>();
        return true;
    }

    private static int? ReadNonZeroInt(JToken? token) =>
        token?.Type == JTokenType.Integer && token.Value<int>() != 0
            ? token.Value<int>()
            : null;
}
