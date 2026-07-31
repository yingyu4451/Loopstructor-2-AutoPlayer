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

    public static bool IsSuccess(JObject? result) => result?["success"]?.Value<bool>() == true;

    public static bool IsPending(JObject? result) =>
        result?.SelectToken("data.pending")?.Value<bool>() == true ||
        result?.SelectToken("data.needsPolling")?.Value<bool>() == true ||
        result?.SelectToken("data.state.pending")?.Value<bool>() == true ||
        result?.SelectToken("data.state.needsPolling")?.Value<bool>() == true;

    public static bool IsUnsafe(JObject? result) =>
        HasTrueFlag(result, "statePolluted", "needsReset") ||
        HasCommittedDefaultDefenseMutation(result);

    public static bool IsRetryableDefaultDefenseFailure(JObject? result) =>
        !IsSuccess(result) &&
        !IsPending(result) &&
        !IsUnsafe(result) &&
        result?.SelectToken("data.state.prepared")?.Value<bool>() == false &&
        result.SelectToken("data.state.statePolluted")?.Value<bool>() == false &&
        result.SelectToken("data.state.needsReset")?.Value<bool>() != true;

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

    private static bool HasTrueFlag(JToken? token, params string[] names)
    {
        if (token == null) return false;
        Stack<JToken> pending = new();
        pending.Push(token);
        while (pending.Count > 0)
        {
            JToken current = pending.Pop();
            if (current is JProperty property &&
                names.Contains(property.Name, StringComparer.OrdinalIgnoreCase) &&
                property.Value.Type == JTokenType.Boolean &&
                property.Value.Value<bool>())
            {
                return true;
            }

            foreach (JToken child in current.Children()) pending.Push(child);
        }

        return false;
    }

    private static bool HasCommittedDefaultDefenseMutation(JObject? result)
    {
        if (result == null || IsSuccess(result)) return false;

        foreach (JProperty property in result.Descendants().OfType<JProperty>())
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
