using Loopstructor.AutoPlayer.Core;
using Newtonsoft.Json.Linq;

namespace Loopstructor.AutoPlayer.Tests;

public sealed class DecisionEngineFrontEndTests
{
    [Fact]
    public void CommonMode_ContinuesExistingIsolatedSaveWhenRequested()
    {
        JObject result = FrontEndState(new
        {
            sceneName = "StartGameScene",
            startMenu = new { hasDefaultSave = true },
            commonMode = new { panelActive = false, readyForSubmit = false }
        });
        AutomationRunOptions options = new() { ContinueExistingProfile = true };

        AutomationAction action = new DecisionEngine().DecideFrontEnd(result, options);

        Assert.Equal("continueGame", action.Command);
        Assert.Equal(AutomationStage.FrontEnd, action.Stage);
        Assert.Equal("继续使用隔离的测试存档。", action.Reason);
    }

    [Fact]
    public void CommonMode_PreparesConfiguredChoicesUntilPanelIsReady()
    {
        JObject result = FrontEndState(new
        {
            sceneName = "StartGameScene",
            startMenu = new { hasDefaultSave = false },
            commonMode = new { panelActive = true, readyForSubmit = false }
        });
        AutomationRunOptions options = new()
        {
            CharacterIndex = 2,
            DifficultyIndex = 3,
            SuperModuleIndex = 4
        };

        AutomationAction action = new DecisionEngine().DecideFrontEnd(result, options);

        Assert.Equal("prepareCommonMode", action.Command);
        Assert.Equal(2, action.Arguments.Value<int>("characterIndex"));
        Assert.Equal(3, action.Arguments.Value<int>("difficultyIndex"));
        Assert.Equal(4, action.Arguments.Value<int>("superModuleIndex"));
    }

    [Fact]
    public void CommonMode_SubmitsConfiguredChoicesWhenReady()
    {
        JObject result = FrontEndState(new
        {
            sceneName = "StartGameScene",
            startMenu = new { hasDefaultSave = false },
            commonMode = new { panelActive = true, readyForSubmit = true }
        });
        AutomationRunOptions options = new()
        {
            CharacterIndex = 1,
            DifficultyIndex = 2,
            SuperModuleIndex = 3
        };

        AutomationAction action = new DecisionEngine().DecideFrontEnd(result, options);

        Assert.Equal("submitCommonMode", action.Command);
        Assert.Equal(1, action.Arguments.Value<int>("characterIndex"));
        Assert.Equal(2, action.Arguments.Value<int>("difficultyIndex"));
        Assert.Equal(3, action.Arguments.Value<int>("superModuleIndex"));
    }

    [Fact]
    public void RandomMode_EntersSelectionSceneFromStartMenu()
    {
        JObject result = FrontEndState(new
        {
            sceneName = "StartGameScene",
            startMenu = new { hasDefaultSave = false }
        });
        AutomationRunOptions options = new() { Mode = AutomationGameMode.Random };

        AutomationAction action = new DecisionEngine().DecideFrontEnd(result, options);

        Assert.Equal("enterRandomMode", action.Command);
        Assert.Equal(AutomationStage.FrontEnd, action.Stage);
    }

    [Theory]
    [InlineData(false, false, "selectRandomVehicle", "index", 6)]
    [InlineData(true, false, "selectRandomFetter", "index", 7)]
    [InlineData(true, true, "submitRandomMode", "autoStop", true)]
    public void RandomMode_SelectsMissingChoiceThenSubmits(
        bool vehicleSelectable,
        bool fetterSelectable,
        string expectedCommand,
        string argumentName,
        object expectedArgument)
    {
        JObject result = FrontEndState(new
        {
            sceneName = "RandomChooseScene",
            managerExists = true,
            selectedVehicleSelectable = vehicleSelectable,
            selectedFetterSelectable = fetterSelectable
        });
        AutomationRunOptions options = new()
        {
            Mode = AutomationGameMode.Random,
            RandomVehicleIndex = 6,
            RandomFetterIndex = 7
        };

        AutomationAction action = new DecisionEngine().DecideFrontEnd(result, options);

        Assert.Equal(expectedCommand, action.Command);
        Assert.Equal(AutomationStage.RandomSelection, action.Stage);
        Assert.Equal(JToken.FromObject(expectedArgument), action.Arguments[argumentName]);
    }

    [Fact]
    public void RandomMode_WaitsUntilManagerExists()
    {
        JObject result = FrontEndState(new
        {
            sceneName = "RandomChooseScene",
            managerExists = false
        });

        AutomationAction action = new DecisionEngine().DecideFrontEnd(result, new AutomationRunOptions());

        Assert.Equal("wait", action.Command);
        Assert.Equal(AutomationStage.RandomSelection, action.Stage);
    }

    private static JObject FrontEndState(object state) => JObject.FromObject(new
    {
        success = true,
        data = new { state }
    });
}
