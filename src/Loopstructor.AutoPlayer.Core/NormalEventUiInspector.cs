using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Newtonsoft.Json.Linq;

namespace Loopstructor.AutoPlayer.Core;

public enum NormalEventUiButtonRole
{
    ContinueStory,
    SkipStory,
    EnterChoices,
    ChooseOption
}

public sealed class NormalEventUiButton
{
    internal NormalEventUiButton(
        NormalEventUiButtonRole role,
        int instanceId,
        string name,
        string path,
        bool isInteractable,
        int optionIndex)
    {
        Role = role;
        InstanceId = instanceId;
        Name = name;
        Path = path;
        IsInteractable = isInteractable;
        OptionIndex = optionIndex;
    }

    public NormalEventUiButtonRole Role { get; }
    public int InstanceId { get; }
    public string Name { get; }
    public string Path { get; }
    public bool IsInteractable { get; }

    /// <summary>
    /// Zero-based event option index. Non-option buttons use -1.
    /// </summary>
    public int OptionIndex { get; }
}

public sealed class NormalEventUiSnapshot
{
    internal NormalEventUiSnapshot(IReadOnlyList<NormalEventUiButton> buttons)
    {
        Buttons = buttons;
        ContinueButton = FindInteractable(NormalEventUiButtonRole.ContinueStory);
        SkipButton = FindInteractable(NormalEventUiButtonRole.SkipStory);
        EnterChoicesButton = FindInteractable(NormalEventUiButtonRole.EnterChoices);
        SelectableOptions = buttons
            .Where(button => button.Role == NormalEventUiButtonRole.ChooseOption && button.IsInteractable)
            .OrderBy(button => button.OptionIndex)
            .ThenBy(button => button.InstanceId)
            .ToArray();
        Fingerprint = string.Join(
            ";",
            buttons
                .OrderBy(button => button.Role)
                .ThenBy(button => button.OptionIndex)
                .ThenBy(button => button.InstanceId)
                .Select(button => string.Join(
                    ":",
                    button.Role,
                    button.OptionIndex.ToString(CultureInfo.InvariantCulture),
                    button.InstanceId.ToString(CultureInfo.InvariantCulture),
                    button.IsInteractable ? "1" : "0")));
    }

    public static NormalEventUiSnapshot Closed { get; } = new(Array.Empty<NormalEventUiButton>());

    public bool IsOpen => Buttons.Count > 0;
    public IReadOnlyList<NormalEventUiButton> Buttons { get; }
    public NormalEventUiButton? ContinueButton { get; }
    public NormalEventUiButton? SkipButton { get; }
    public NormalEventUiButton? EnterChoicesButton { get; }
    public IReadOnlyList<NormalEventUiButton> SelectableOptions { get; }
    public string Fingerprint { get; }

    private NormalEventUiButton? FindInteractable(NormalEventUiButtonRole role) =>
        Buttons.FirstOrDefault(button => button.Role == role && button.IsInteractable);
}

/// <summary>
/// Reads the ordinary-event UI introduced as EventUI_Normal from queryUiInteractables.
/// The game's legacy queryEventOptions contract only reports EventUI/RepairUI items and
/// therefore cannot see WaveFunctionNormalOptionButton instances.
/// </summary>
public static class NormalEventUiInspector
{
    private const string PanelPrefabName = "P_EventPanel_Normal New";
    private const string PanelUiKey = "EventUI_Normal";
    private const string OptionComponentType = "WaveFunctionNormalOptionButton";

    public static NormalEventUiSnapshot Inspect(JObject? interactablesResult)
    {
        JArray items = ResolveState(interactablesResult)?["items"] as JArray ?? new JArray();
        List<NormalEventUiButton> buttons = new();

        foreach (JObject item in items.OfType<JObject>())
        {
            string name = item["name"]?.Value<string>()?.Trim() ?? string.Empty;
            string path = (item["path"]?.Value<string>() ?? item["buttonPath"]?.Value<string>() ?? string.Empty)
                .Trim();
            bool hasOptionComponent = HasComponentType(item["componentTypes"] as JArray, OptionComponentType);
            if (!IsNormalEventPath(path) && !hasOptionComponent)
            {
                continue;
            }

            if (!TryClassify(name, hasOptionComponent, out NormalEventUiButtonRole role, out int optionIndex))
            {
                continue;
            }

            int instanceId = item["buttonInstanceId"]?.Value<int?>()
                             ?? item["instanceId"]?.Value<int?>()
                             ?? 0;
            if (instanceId == 0)
            {
                continue;
            }

            bool isInteractable = item["btnActive"]?.Value<bool>() == true &&
                                  item["useLeft"]?.Value<bool>() != false;
            buttons.Add(new NormalEventUiButton(
                role,
                instanceId,
                name,
                path,
                isInteractable,
                optionIndex));
        }

        return buttons.Count == 0
            ? NormalEventUiSnapshot.Closed
            : new NormalEventUiSnapshot(buttons);
    }

    private static JObject? ResolveState(JObject? result) =>
        result?.SelectToken("data.state") as JObject ?? result?["state"] as JObject;

    private static bool IsNormalEventPath(string path) =>
        path.IndexOf(PanelPrefabName, StringComparison.OrdinalIgnoreCase) >= 0 ||
        path.IndexOf(PanelUiKey, StringComparison.OrdinalIgnoreCase) >= 0;

    private static bool HasComponentType(JArray? componentTypes, string simpleTypeName)
    {
        if (componentTypes == null)
        {
            return false;
        }

        return componentTypes.Values<string>().Any(typeName =>
            string.Equals(typeName, simpleTypeName, StringComparison.OrdinalIgnoreCase) ||
            (typeName?.EndsWith("." + simpleTypeName, StringComparison.OrdinalIgnoreCase) ?? false));
    }

    private static bool TryClassify(
        string name,
        bool hasOptionComponent,
        out NormalEventUiButtonRole role,
        out int optionIndex)
    {
        optionIndex = -1;
        if (hasOptionComponent || name.StartsWith("ChoiceButton_", StringComparison.OrdinalIgnoreCase))
        {
            role = NormalEventUiButtonRole.ChooseOption;
            optionIndex = ParseOptionIndex(name);
            return true;
        }

        if (string.Equals(name, "CountineButton", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(name, "ContinueButton", StringComparison.OrdinalIgnoreCase))
        {
            role = NormalEventUiButtonRole.ContinueStory;
            return true;
        }

        if (string.Equals(name, "SkipButton", StringComparison.OrdinalIgnoreCase))
        {
            role = NormalEventUiButtonRole.SkipStory;
            return true;
        }

        if (string.Equals(name, "ChooseButton", StringComparison.OrdinalIgnoreCase))
        {
            role = NormalEventUiButtonRole.EnterChoices;
            return true;
        }

        role = default;
        return false;
    }

    private static int ParseOptionIndex(string name)
    {
        int separator = name.LastIndexOf('_');
        if (separator < 0 || separator >= name.Length - 1 ||
            !int.TryParse(name.Substring(separator + 1), NumberStyles.Integer, CultureInfo.InvariantCulture, out int oneBased))
        {
            return int.MaxValue;
        }

        return Math.Max(0, oneBased - 1);
    }
}

public static class NormalEventUiDecision
{
    public static NormalEventUiButton? SelectTarget(NormalEventUiSnapshot snapshot, bool skipStory)
    {
        if (snapshot == null) throw new ArgumentNullException(nameof(snapshot));

        if (snapshot.SelectableOptions.Count > 0)
        {
            return snapshot.SelectableOptions[0];
        }

        if (skipStory && snapshot.SkipButton != null)
        {
            return snapshot.SkipButton;
        }

        return snapshot.ContinueButton ?? snapshot.EnterChoicesButton;
    }
}
