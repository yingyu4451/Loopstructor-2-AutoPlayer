using Loopstructor.AutoPlayer.Core;
using Newtonsoft.Json.Linq;

namespace Loopstructor.AutoPlayer.Tests;

public sealed class NormalEventUiInspectorTests
{
    [Fact]
    public void Inspect_RecognizesNormalEventStoryButtonsWithoutMatchingLegacyEventUi()
    {
        JObject result = Result(
            Item(11, "CountineButton", "Canvas/P_EventPanel_Normal New(Clone)/CorePanel/StoryView/CountineButton"),
            Item(12, "SkipButton", "Canvas/P_EventPanel_Normal New(Clone)/CorePanel/StoryView/SkipButton"),
            Item(13, "CountineButton", "Canvas/EventUI/CountineButton"));

        NormalEventUiSnapshot snapshot = NormalEventUiInspector.Inspect(result);

        Assert.True(snapshot.IsOpen);
        Assert.Equal(11, snapshot.ContinueButton?.InstanceId);
        Assert.Equal(12, snapshot.SkipButton?.InstanceId);
        Assert.DoesNotContain(snapshot.Buttons, button => button.InstanceId == 13);
    }

    [Fact]
    public void Inspect_OrdersSelectableDynamicOptionsAndExcludesDisabledOption()
    {
        JObject result = Result(
            Item(
                22,
                "ChoiceButton_2",
                "Canvas/P_EventPanel_Normal New(Clone)/CorePanel/ChoiceView/Content/ChoiceButton_2",
                components: new[] { "WaveFunctionNormalOptionButton", "UIButton" }),
            Item(
                21,
                "ChoiceButton_1",
                "Canvas/P_EventPanel_Normal New(Clone)/CorePanel/ChoiceView/Content/ChoiceButton_1",
                components: new[] { "MetroTD.UISystem.WaveFunctionNormalOptionButton", "UIButton" }),
            Item(
                23,
                "ChoiceButton_3",
                "Canvas/P_EventPanel_Normal New(Clone)/CorePanel/ChoiceView/Content/ChoiceButton_3",
                btnActive: false,
                components: new[] { "WaveFunctionNormalOptionButton", "UIButton" }));

        NormalEventUiSnapshot snapshot = NormalEventUiInspector.Inspect(result);

        Assert.Equal(new[] { 21, 22 }, snapshot.SelectableOptions.Select(option => option.InstanceId));
        Assert.Equal(new[] { 0, 1 }, snapshot.SelectableOptions.Select(option => option.OptionIndex));
        Assert.Contains(snapshot.Buttons, button => button.InstanceId == 23 && !button.IsInteractable);
    }

    [Fact]
    public void Inspect_UsesNormalEventOptionComponentWhenHierarchyRootIsRenamed()
    {
        JObject result = Result(Item(
            31,
            "ChoiceButton_1",
            "Canvas/RuntimePanel/ChoiceView/ChoiceButton_1",
            components: new[] { "WaveFunctionNormalOptionButton" }));

        NormalEventUiSnapshot snapshot = NormalEventUiInspector.Inspect(result);

        Assert.True(snapshot.IsOpen);
        Assert.Equal(31, Assert.Single(snapshot.SelectableOptions).InstanceId);
    }

    [Fact]
    public void Inspect_ReturnsClosedForUnrelatedButtons()
    {
        JObject result = Result(
            Item(41, "SkipButton", "Canvas/OtherPanel/SkipButton"),
            Item(42, "ChoiceButton_1", "Canvas/OtherPanel/ChoiceButton_1"));

        NormalEventUiSnapshot snapshot = NormalEventUiInspector.Inspect(result);

        Assert.False(snapshot.IsOpen);
        Assert.Empty(snapshot.Buttons);
        Assert.Empty(snapshot.Fingerprint);
    }

    [Fact]
    public void Inspect_FingerprintChangesWhenStoryTransitionsToChoices()
    {
        NormalEventUiSnapshot story = NormalEventUiInspector.Inspect(Result(
            Item(51, "CountineButton", "Canvas/EventUI_Normal/StoryView/CountineButton"),
            Item(52, "SkipButton", "Canvas/EventUI_Normal/StoryView/SkipButton")));
        NormalEventUiSnapshot choices = NormalEventUiInspector.Inspect(Result(Item(
            53,
            "ChoiceButton_1",
            "Canvas/EventUI_Normal/ChoiceView/ChoiceButton_1",
            components: new[] { "WaveFunctionNormalOptionButton" })));

        Assert.NotEqual(story.Fingerprint, choices.Fingerprint);
    }

    [Fact]
    public void Decision_UsesSkipButtonOnlyWhenStorySkippingIsEnabled()
    {
        NormalEventUiSnapshot snapshot = NormalEventUiInspector.Inspect(Result(
            Item(61, "CountineButton", "Canvas/EventUI_Normal/StoryView/CountineButton"),
            Item(62, "SkipButton", "Canvas/EventUI_Normal/StoryView/SkipButton")));

        Assert.Equal(61, NormalEventUiDecision.SelectTarget(snapshot, skipStory: false)?.InstanceId);
        Assert.Equal(62, NormalEventUiDecision.SelectTarget(snapshot, skipStory: true)?.InstanceId);
    }

    [Fact]
    public void Decision_AlwaysChoosesVisibleOptionBeforeStoryButtons()
    {
        NormalEventUiSnapshot snapshot = NormalEventUiInspector.Inspect(Result(
            Item(71, "SkipButton", "Canvas/EventUI_Normal/StoryView/SkipButton"),
            Item(
                72,
                "ChoiceButton_1",
                "Canvas/EventUI_Normal/ChoiceView/ChoiceButton_1",
                components: new[] { "WaveFunctionNormalOptionButton" })));

        NormalEventUiButton? target = NormalEventUiDecision.SelectTarget(snapshot, skipStory: true);

        Assert.Equal(NormalEventUiButtonRole.ChooseOption, target?.Role);
        Assert.Equal(72, target?.InstanceId);
    }

    private static JObject Result(params JObject[] items) => new()
    {
        ["success"] = true,
        ["data"] = new JObject
        {
            ["state"] = new JObject
            {
                ["count"] = items.Length,
                ["items"] = new JArray(items)
            }
        }
    };

    private static JObject Item(
        int instanceId,
        string name,
        string path,
        bool btnActive = true,
        bool useLeft = true,
        string[]? components = null) => new()
    {
        ["instanceId"] = instanceId,
        ["buttonInstanceId"] = instanceId,
        ["name"] = name,
        ["path"] = path,
        ["btnActive"] = btnActive,
        ["useLeft"] = useLeft,
        ["componentTypes"] = new JArray(components ?? new[] { "UIButton" })
    };
}
