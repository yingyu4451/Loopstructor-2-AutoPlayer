using Loopstructor.AutoPlayer.Core;
using Newtonsoft.Json.Linq;

namespace Loopstructor.AutoPlayer.Tests;

public sealed class RuntimeResultInspectorTests
{
    [Fact]
    public void Classify_SuccessfulPendingResultAsPending()
    {
        JObject result = JObject.FromObject(new
        {
            success = true,
            data = new { state = new { pending = true, needsPolling = true } }
        });

        Assert.Equal(RuntimeResultDisposition.Pending, RuntimeResultInspector.Classify(result));
    }

    [Fact]
    public void Classify_UnsuccessfulPendingResultAsPending()
    {
        JObject result = JObject.FromObject(new
        {
            success = false,
            data = new { state = new { pending = true, needsPolling = true } }
        });

        Assert.Equal(RuntimeResultDisposition.Pending, RuntimeResultInspector.Classify(result));
    }

    [Fact]
    public void RetryableDefaultDefense_RequiresAnUnpollutedIncompleteState()
    {
        JObject clean = JObject.FromObject(new
        {
            success = false,
            data = new { state = new { prepared = false, statePolluted = false, rollbackApplied = false } }
        });
        JObject polluted = JObject.FromObject(new
        {
            success = false,
            data = new { state = new { prepared = false, statePolluted = true, needsReset = true } }
        });

        Assert.True(RuntimeResultInspector.IsRetryableDefaultDefenseFailure(clean));
        Assert.False(RuntimeResultInspector.IsRetryableDefaultDefenseFailure(polluted));
    }

    [Fact]
    public void Classify_UnsafeBeforePendingAndSuccess()
    {
        JObject result = JObject.FromObject(new
        {
            success = true,
            data = new { state = new { pending = true, statePolluted = true } }
        });

        Assert.Equal(RuntimeResultDisposition.Unsafe, RuntimeResultInspector.Classify(result));
    }

    [Fact]
    public void Classify_NestedCommandPollutionAsUnsafeEvenWhenWrapperClaimsClean()
    {
        JObject result = JObject.FromObject(new
        {
            success = false,
            data = new
            {
                state = new
                {
                    prepared = false,
                    statePolluted = false,
                    drawResult = new
                    {
                        success = false,
                        data = new { state = new { statePolluted = true, needsReset = true } }
                    }
                }
            }
        });

        Assert.Equal(RuntimeResultDisposition.Unsafe, RuntimeResultInspector.Classify(result));
        Assert.False(RuntimeResultInspector.IsRetryableDefaultDefenseFailure(result));
    }

    [Fact]
    public void Classify_CommittedAttributePlacementAsUnsafeEvenWhenWrapperClaimsClean()
    {
        JObject result = JObject.FromObject(new
        {
            success = false,
            data = new
            {
                state = new
                {
                    prepared = false,
                    statePolluted = false,
                    extra = new
                    {
                        attributePlacement = new
                        {
                            beforeAttributeCount = 0,
                            afterAttributeCount = 1,
                            confirmResult = new { success = true }
                        }
                    }
                }
            }
        });

        Assert.Equal(RuntimeResultDisposition.Unsafe, RuntimeResultInspector.Classify(result));
        Assert.False(RuntimeResultInspector.IsRetryableDefaultDefenseFailure(result));
    }

    [Fact]
    public void RetryableDefaultDefense_AllowsCancelledUncommittedAttributePreview()
    {
        JObject result = JObject.FromObject(new
        {
            success = false,
            data = new
            {
                state = new
                {
                    prepared = false,
                    statePolluted = false,
                    attributePlacement = new
                    {
                        useResult = new { success = true },
                        confirmResult = new { success = false },
                        cancelResult = new { success = true }
                    }
                }
            }
        });

        Assert.False(RuntimeResultInspector.IsUnsafe(result));
        Assert.True(RuntimeResultInspector.IsRetryableDefaultDefenseFailure(result));
    }

    [Fact]
    public void WishReturn_RequiresReadyLeftButtonInsideWishPanel()
    {
        JObject result = Interactables(
            new { instanceId = 11, name = "Return", path = "Canvas/Settings/Return", btnActive = true, useLeft = true },
            new { instanceId = 22, name = "Return", path = "Canvas/P_WishPanel(Clone)/Main/Buttons/Return", btnActive = false, useLeft = true },
            new { instanceId = -33, name = "Return", path = "Canvas/P_WishPanel(Clone)/Main/Buttons/Return", btnActive = true, useLeft = true });

        Assert.True(RuntimeResultInspector.TryGetWishPanelReturnInstanceId(result, out int instanceId));
        Assert.Equal(-33, instanceId);
    }

    [Fact]
    public void Settlement_RequiresActiveButtonUnderSettlementPrefabRoot()
    {
        JObject inactive = Interactables(
            new { instanceId = 1, name = "Again", path = "Canvas/P_UI_SettlementPanel(Clone)/Main/Buttons/Again", btnActive = false, useLeft = true });
        JObject active = Interactables(
            new { instanceId = 2, name = "Again", path = "Canvas/P_UI_SettlementPanel(Clone)/Main/Buttons/Again", btnActive = true, useLeft = true });

        Assert.False(RuntimeResultInspector.HasActiveSettlementInteractable(inactive));
        Assert.True(RuntimeResultInspector.HasActiveSettlementInteractable(active));
    }

    [Theory]
    [InlineData("chooseNode")]
    [InlineData("pendingSubLevelNode")]
    public void CommittedMapNode_RequiresASelectedNode(string property)
    {
        JObject committed = JObject.FromObject(new
        {
            success = true,
            data = new { state = new JObject { [property] = JObject.FromObject(new { instanceId = 42 }) } }
        });
        JObject noOp = JObject.FromObject(new
        {
            success = true,
            data = new { state = new { chooseNode = (object?)null, pendingSubLevelNode = (object?)null } }
        });

        Assert.True(RuntimeResultInspector.HasCommittedMapNode(committed));
        Assert.False(RuntimeResultInspector.HasCommittedMapNode(noOp));
    }

    private static JObject Interactables(params object[] items) => JObject.FromObject(new
    {
        success = true,
        data = new { state = new { items } }
    });
}
