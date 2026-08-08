using Loopstructor.AutoPlayer.Core;
using Newtonsoft.Json.Linq;

namespace Loopstructor.AutoPlayer.Tests;

public sealed class PendingDisposableMutationGuardTests
{
    [Fact]
    public void PendingUse_LocksCommandDisposableAndStableActionIdentity()
    {
        PendingDisposableMutationGuard guard = new();
        AutomationAction action = Action("useDisposable", new
        {
            itemInstanceId = 501
        });

        Assert.True(guard.TryArm(action, "Bomb", 10f));
        Assert.False(guard.TryArm(action, "Bomb", 10.1f));
        Assert.False(guard.TryArm(Action("useDisposable", new { itemInstanceId = 502 }), "Bomb", 10.1f));
        Assert.Equal("useDisposable", guard.Command);
        Assert.Equal("Bomb", guard.DisposableEnum);
        Assert.Equal("instance:501", guard.ActionIdentity);
        Assert.Equal("useDisposable|Bomb|instance:501", guard.MutationIdentity);
        Assert.Equal(PendingDisposableMutationResolution.Waiting, guard.Resolution);
    }

    [Theory]
    [InlineData("item-path", -1, "path:item-path")]
    [InlineData(null, 0, "index:0")]
    public void PendingUse_AcceptsEveryStableIdentityEmittedByBattleDecisionEngine(
        string? path,
        int index,
        string expectedIdentity)
    {
        JObject arguments = new();
        if (path != null)
        {
            arguments["path"] = path;
        }
        else
        {
            arguments["index"] = index;
        }

        PendingDisposableMutationGuard guard = new();

        Assert.True(guard.TryArm(
            new AutomationAction("useDisposable", arguments, AutomationStage.Battle, "test"),
            "Buff",
            1f));
        Assert.Equal(expectedIdentity, guard.ActionIdentity);
    }

    [Fact]
    public void MissingUseIdentityOrUnsupportedCommand_NeverArms()
    {
        PendingDisposableMutationGuard guard = new();

        Assert.False(guard.TryArm(Action("useDisposable", new { }), "Bomb", 0f));
        Assert.False(guard.TryArm(Action("confirmDisposableWorld", new { }), "Bomb", 0f));
        Assert.False(guard.TryArm(Action("confirmDisposableGrid", new { grid = new { x = 1 } }), "Bomb", 0f));
        Assert.False(guard.TryArm(Action("useDisposable", new { itemInstanceId = 1 }), " ", 0f));
        Assert.False(guard.IsArmed);
    }

    [Fact]
    public void MatchingPreviewIdentity_ResolvesPendingUseWithoutResending()
    {
        PendingDisposableMutationGuard guard = ArmedUse(10f);

        PendingDisposableMutationResolution resolution = guard.Observe(
            Result(new
            {
                isInPreview = true,
                disposableEnum = "Bomb",
                interactionInstanceId = 901
            }),
            null,
            10.5f,
            20f);

        Assert.Equal(PendingDisposableMutationResolution.InteractionObserved, resolution);
        Assert.Equal(901, guard.ResolvedInteractionInstanceId);
        Assert.True(guard.IsArmed);
        Assert.False(guard.TryArm(Action("useDisposable", new { itemInstanceId = 501 }), "Bomb", 11f));
    }

    [Theory]
    [InlineData("Other", 901)]
    [InlineData("Bomb", 0)]
    public void PreviewWithoutMatchingVerifiedInteraction_KeepsWaiting(
        string disposableEnum,
        int interactionInstanceId)
    {
        PendingDisposableMutationGuard guard = ArmedUse(10f);

        Assert.Equal(
            PendingDisposableMutationResolution.Waiting,
            guard.Observe(
                Result(new
                {
                    isInPreview = true,
                    disposableEnum,
                    interactionInstanceId
                }),
                null,
                11f,
                20f));
        Assert.Equal(0, guard.ResolvedInteractionInstanceId);
    }

    [Fact]
    public void MatchingAttributeCatapult_ResolvesPendingGridConfirmation()
    {
        PendingDisposableMutationGuard guard = ArmedGridConfirmation(10f, 7, -3);

        PendingDisposableMutationResolution resolution = guard.Observe(
            Result(new { isInPreview = false }),
            Result(new
            {
                catapults = new object[]
                {
                    new { isAttribute = true, grid = new { x = 7, y = -3 } }
                }
            }),
            11f,
            20f);

        Assert.Equal(PendingDisposableMutationResolution.TargetAttributeCatapultObserved, resolution);
        Assert.Equal("confirmDisposableGrid|FreePoint_Attribute|grid:7,-3", guard.MutationIdentity);
    }

    [Fact]
    public void SameEnumPreview_CannotProvePendingGridConfirmation()
    {
        PendingDisposableMutationGuard guard = ArmedGridConfirmation(10f, 7, -3);

        PendingDisposableMutationResolution resolution = guard.Observe(
            Result(new
            {
                isInPreview = true,
                disposableEnum = "FreePoint_Attribute",
                interactionInstanceId = 901
            }),
            Result(new { catapults = Array.Empty<object>() }),
            11f,
            20f);

        Assert.Equal(PendingDisposableMutationResolution.Waiting, resolution);
        Assert.Equal(901, guard.ResolvedInteractionInstanceId);
        Assert.True(guard.IsArmed);
    }

    [Fact]
    public void TargetGridCatapult_WinsEvenWhenAnotherSameEnumPreviewIsVisible()
    {
        PendingDisposableMutationGuard guard = ArmedGridConfirmation(10f, 7, -3);

        PendingDisposableMutationResolution resolution = guard.Observe(
            Result(new
            {
                isInPreview = true,
                disposableEnum = "FreePoint_Attribute",
                interactionInstanceId = 901
            }),
            Result(new
            {
                catapults = new object[]
                {
                    new { isAttribute = true, grid = new { x = 7, y = -3 } }
                }
            }),
            11f,
            20f);

        Assert.Equal(PendingDisposableMutationResolution.TargetAttributeCatapultObserved, resolution);
    }

    [Theory]
    [InlineData(8, -3, true)]
    [InlineData(7, -3, false)]
    public void WrongGridOrCommonCatapult_DoesNotProveGridConfirmation(
        int x,
        int y,
        bool isAttribute)
    {
        PendingDisposableMutationGuard guard = ArmedGridConfirmation(10f, 7, -3);

        Assert.Equal(
            PendingDisposableMutationResolution.Waiting,
            guard.Observe(
                Result(new { isInPreview = false }),
                Result(new
                {
                    catapults = new object[]
                    {
                        new { isAttribute, grid = new { x, y } }
                    }
                }),
                11f,
                20f));
    }

    [Fact]
    public void AttributeCatapultSnapshot_CannotSettleADifferentGridDisposable()
    {
        PendingDisposableMutationGuard guard = new();
        Assert.True(guard.TryArm(
            Action("confirmDisposableGrid", new { grid = new { x = 7, y = -3 } }),
            "SomeOtherGridDisposable",
            10f));

        Assert.Equal(
            PendingDisposableMutationResolution.Waiting,
            guard.Observe(
                Result(new { isInPreview = false }),
                Result(new
                {
                    catapults = new object[]
                    {
                        new { isAttribute = true, grid = new { x = 7, y = -3 } }
                    }
                }),
                11f,
                20f));
    }

    [Fact]
    public void CleanNoPreview_WaitsUntilDeadlineThenLatchesUnknown()
    {
        PendingDisposableMutationGuard guard = ArmedGridConfirmation(10f, 7, -3);
        JObject cleanDisposable = Result(new { isInPreview = false });
        JObject noTarget = Result(new { catapults = Array.Empty<object>() });

        Assert.Equal(
            PendingDisposableMutationResolution.Waiting,
            guard.Observe(cleanDisposable, noTarget, 29.99f, 20f));
        Assert.Equal(
            PendingDisposableMutationResolution.Unknown,
            guard.Observe(cleanDisposable, noTarget, 30f, 20f));
        Assert.Equal(
            PendingDisposableMutationResolution.Unknown,
            guard.Observe(
                Result(new
                {
                    isInPreview = true,
                    disposableEnum = "FreePoint_Attribute",
                    interactionInstanceId = 123
                }),
                null,
                30.1f,
                20f));
        Assert.True(guard.IsArmed);
    }

    [Fact]
    public void UnavailableReadOnlySnapshots_NeverFalselyProveSettlement()
    {
        PendingDisposableMutationGuard guard = ArmedUse(10f);

        Assert.Equal(
            PendingDisposableMutationResolution.Waiting,
            guard.Observe(null, null, 11f, 20f));
    }

    [Fact]
    public void Reset_IsRequiredBeforeAnotherPendingWriteCanBeTracked()
    {
        PendingDisposableMutationGuard guard = ArmedUse(10f);

        guard.Reset();

        Assert.False(guard.IsArmed);
        Assert.Equal(PendingDisposableMutationResolution.None, guard.Resolution);
        Assert.True(guard.TryArm(
            Action("confirmDisposableGrid", new { grid = new { x = 2, y = 4 } }),
            "FreePoint_Attribute",
            12f));
    }

    private static PendingDisposableMutationGuard ArmedUse(float now)
    {
        PendingDisposableMutationGuard guard = new();
        Assert.True(guard.TryArm(
            Action("useDisposable", new { itemInstanceId = 501 }),
            "Bomb",
            now));
        return guard;
    }

    private static PendingDisposableMutationGuard ArmedGridConfirmation(float now, int x, int y)
    {
        PendingDisposableMutationGuard guard = new();
        Assert.True(guard.TryArm(
            Action("confirmDisposableGrid", new { grid = new { x, y } }),
            "FreePoint_Attribute",
            now));
        return guard;
    }

    private static AutomationAction Action(string command, object arguments) =>
        new(command, JObject.FromObject(arguments), AutomationStage.Battle, "test");

    private static JObject Result(object state) =>
        new()
        {
            ["success"] = true,
            ["data"] = new JObject
            {
                ["state"] = JObject.FromObject(state)
            }
        };
}
