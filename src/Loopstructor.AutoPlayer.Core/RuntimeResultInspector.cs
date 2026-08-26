using System;
using System.Collections.Generic;
using System.Linq;
using Newtonsoft.Json.Linq;

namespace Loopstructor.AutoPlayer.Core;

public enum RuntimeResultDisposition
{
    Unsafe,
    Pending,
    Failure,
    Success
}

public static class RuntimeResultInspector
{
    public static RuntimeResultDisposition Classify(JObject? result)
    {
        if (IsUnsafe(result)) return RuntimeResultDisposition.Unsafe;
        if (IsPending(result)) return RuntimeResultDisposition.Pending;
        return IsSuccess(result) ? RuntimeResultDisposition.Success : RuntimeResultDisposition.Failure;
    }

    public static RuntimeResultDisposition ClassifyReadOnly(JObject? result)
    {
        if (IsPending(result)) return RuntimeResultDisposition.Pending;
        return IsSuccess(result) ? RuntimeResultDisposition.Success : RuntimeResultDisposition.Failure;
    }

    public static bool IsSuccess(JObject? result) => result?["success"]?.Value<bool>() == true;

    public static bool IsPending(JObject? result) =>
        result?.SelectToken("data.pending")?.Value<bool>() == true ||
        result?.SelectToken("data.needsPolling")?.Value<bool>() == true ||
        result?.SelectToken("data.state.pending")?.Value<bool>() == true ||
        result?.SelectToken("data.state.needsPolling")?.Value<bool>() == true;

    public static bool IsUnsafe(JObject? result) =>
        HasTrueFlag(result, "statePolluted", "needsReset", "outcomeUnknown") ||
        HasCommittedDefaultDefenseMutation(result) && !IsRecoverableDefaultDefenseCheckpoint(result);

    public static string UnsafeMutationReason(JObject? result)
    {
        if (HasTrueFlag(result, "statePolluted")) return "statePolluted=true";
        if (HasTrueFlag(result, "needsReset")) return "needsReset=true";
        if (HasTrueFlag(result, "outcomeUnknown")) return "outcomeUnknown=true";
        if (HasCommittedDefaultDefenseMutation(result) && !IsRecoverableDefaultDefenseCheckpoint(result))
        {
            return "未确认回滚的部分写入";
        }

        return "未确认的写入状态";
    }

    public static bool IsRecoverableDefaultDefenseCheckpoint(JObject? result)
    {
        if (result == null || IsSuccess(result) || IsPending(result) ||
            HasTrueFlag(result, "statePolluted", "needsReset", "outcomeUnknown") ||
            !HasCommittedDefaultDefenseMutation(result))
        {
            return false;
        }

        JObject? state = result.SelectToken("data.state") as JObject;
        JObject? after = state?["after"] as JObject ?? state?["defense"] as JObject;
        JObject? drawResult = state?["drawResult"] as JObject;
        if (state?["prepared"]?.Value<bool>() != false ||
            state["statePolluted"]?.Value<bool>() != false ||
            state["needsReset"]?.Value<bool>() == true ||
            after == null)
        {
            return false;
        }

        if (drawResult != null &&
            (drawResult["success"]?.Value<bool>() != false ||
             HasTrueFlag(drawResult, "statePolluted", "needsReset") ||
             drawResult.SelectToken("data.state.statePolluted")?.Value<bool>() != false))
        {
            return false;
        }

        return ReadInt(after["railCount"]) == 0 &&
               ReadInt(after["illegalRailCount"]) == 0 &&
               ReadInt(after["trainCount"]) == 0 &&
               ReadInt(after["placedPlayerVehicleCount"]) == 0;
    }

    public static bool IsRetryableDefaultDefenseFailure(JObject? result) =>
        !IsSuccess(result) &&
        !IsPending(result) &&
        !IsUnsafe(result) &&
        result?.SelectToken("data.state.prepared")?.Value<bool>() == false &&
        result.SelectToken("data.state.statePolluted")?.Value<bool>() == false &&
        result.SelectToken("data.state.needsReset")?.Value<bool>() != true;

    public static bool IsCleanUncommittedRailDrawFailure(JObject? result)
    {
        if (result == null || IsSuccess(result) || IsPending(result) || IsUnsafe(result))
        {
            return false;
        }

        JObject? state = result.SelectToken("data.state") as JObject;
        JToken? before = state?["beforeRailState"];
        JToken? after = state?["afterRailState"];
        JObject? interaction = state?["interactionState"] as JObject;
        if (state?["statePolluted"]?.Value<bool>() != false ||
            before == null ||
            after == null ||
            !JToken.DeepEquals(before, after) ||
            interaction == null)
        {
            return false;
        }

        return ReadIntOrDefault(interaction["pickingCount"], 0) == 0 &&
               interaction["hasTemporaryLine"]?.Value<bool>() != true &&
               interaction["hasPickLine"]?.Value<bool>() != true &&
               interaction["dragSuccess"]?.Value<bool>() != true &&
               interaction["makeDirty"]?.Value<bool>() != true;
    }

    public static string Message(JObject? result) =>
        result?["message"]?.Value<string>() ?? "未知结果。";

    public static bool HasCommittedMapNode(JObject? result) =>
        IsPresent(result?.SelectToken("data.state.chooseNode")) ||
        IsPresent(result?.SelectToken("data.state.pendingSubLevelNode"));

    public static bool TryGetWishPanelReturnInstanceId(JObject? result, out int instanceId)
    {
        foreach (JObject item in Items(result))
        {
            string name = item["name"]?.Value<string>() ?? string.Empty;
            string path = item["path"]?.Value<string>() ?? string.Empty;
            if (!string.Equals(name, "Return", StringComparison.OrdinalIgnoreCase) ||
                (!HasPathSegment(path, "P_WishPanel") && !HasPathSegment(path, "WishPanel")) ||
                item["btnActive"]?.Value<bool>() != true ||
                item["useLeft"]?.Value<bool>() != true)
            {
                continue;
            }

            JToken? id = item["instanceId"];
            if (id?.Type == JTokenType.Integer)
            {
                instanceId = id.Value<int>();
                if (instanceId != 0) return true;
            }
        }

        instanceId = 0;
        return false;
    }

    public static bool HasActiveSettlementInteractable(JObject? result)
    {
        return Items(result).Any(item =>
            item["btnActive"]?.Value<bool>() == true &&
            HasPathSegment(item["path"]?.Value<string>() ?? string.Empty, "P_UI_SettlementPanel"));
    }

    private static IEnumerable<JObject> Items(JObject? result) =>
        (result?.SelectToken("data.state.items") as JArray)?.OfType<JObject>() ?? Enumerable.Empty<JObject>();

    private static bool IsPresent(JToken? token) =>
        token != null && token.Type is not JTokenType.Null and not JTokenType.Undefined;

    private static int ReadInt(JToken? token) =>
        token?.Type == JTokenType.Integer ? token.Value<int>() : int.MinValue;

    private static int ReadIntOrDefault(JToken? token, int fallback) =>
        token?.Type == JTokenType.Integer ? token.Value<int>() : fallback;

    private static bool HasTrueFlag(JObject? result, params string[] names)
    {
        JObject? data = result?["data"] as JObject;
        if (data == null) return false;

        if (HasDirectTrueFlag(data, names) ||
            data["state"] is JObject state && HasDirectTrueFlag(state, names))
        {
            return true;
        }

        foreach (JObject nestedResult in CurrentNestedResults(data))
        {
            if (HasTrueFlag(nestedResult, names)) return true;
        }

        return false;
    }

    private static bool HasDirectTrueFlag(JObject state, IEnumerable<string> names) =>
        state.Properties().Any(property =>
            names.Contains(property.Name, StringComparer.OrdinalIgnoreCase) &&
            property.Value.Type == JTokenType.Boolean &&
            property.Value.Value<bool>());

    private static IEnumerable<JObject> CurrentNestedResults(JToken token)
    {
        Stack<JToken> pending = new();
        foreach (JToken child in token.Children()) pending.Push(child);

        while (pending.Count > 0)
        {
            JToken current = pending.Pop();
            if (current is JProperty property)
            {
                if (IsHistoricalContainer(property.Name)) continue;
                if (property.Value is JObject candidate && IsResultEnvelope(candidate))
                {
                    yield return candidate;
                    continue;
                }
            }

            foreach (JToken child in current.Children()) pending.Push(child);
        }
    }

    private static bool IsResultEnvelope(JObject candidate) =>
        candidate["success"]?.Type == JTokenType.Boolean &&
        candidate["data"] is JObject;

    private static bool IsHistoricalContainer(string name) =>
        name.StartsWith("before", StringComparison.OrdinalIgnoreCase) ||
        name.StartsWith("previous", StringComparison.OrdinalIgnoreCase) ||
        name.StartsWith("old", StringComparison.OrdinalIgnoreCase) ||
        string.Equals(name, "history", StringComparison.OrdinalIgnoreCase) ||
        string.Equals(name, "histories", StringComparison.OrdinalIgnoreCase);

    private static bool HasCommittedDefaultDefenseMutation(JObject? result)
    {
        if (result == null || IsSuccess(result)) return false;

        JObject? state = result.SelectToken("data.state") as JObject;
        if (state == null) return false;

        foreach (JProperty property in CurrentProperties(state))
        {
            if (!string.Equals(property.Name, "attributePlacement", StringComparison.OrdinalIgnoreCase) ||
                property.Value is not JObject placement)
            {
                continue;
            }

            int? beforeCount = placement["beforeAttributeCount"]?.Value<int>();
            int? afterCount = placement["afterAttributeCount"]?.Value<int>();
            if (beforeCount.HasValue && afterCount > beforeCount)
            {
                return true;
            }

            if (placement.SelectToken("confirmResult.success")?.Value<bool>() == true)
            {
                return true;
            }
        }

        return false;
    }

    private static IEnumerable<JProperty> CurrentProperties(JToken token)
    {
        Stack<JToken> pending = new();
        foreach (JToken child in token.Children()) pending.Push(child);

        while (pending.Count > 0)
        {
            JToken current = pending.Pop();
            if (current is JProperty property)
            {
                if (IsHistoricalContainer(property.Name)) continue;
                yield return property;
            }

            foreach (JToken child in current.Children()) pending.Push(child);
        }
    }

    private static bool HasPathSegment(string path, string expected)
    {
        foreach (string rawSegment in path.Split(new[] { '/', '\\' }, StringSplitOptions.RemoveEmptyEntries))
        {
            string segment = rawSegment.Trim();
            const string cloneSuffix = "(Clone)";
            if (segment.EndsWith(cloneSuffix, StringComparison.OrdinalIgnoreCase))
            {
                segment = segment.Substring(0, segment.Length - cloneSuffix.Length);
            }

            if (string.Equals(segment, expected, StringComparison.OrdinalIgnoreCase)) return true;
        }

        return false;
    }
}
