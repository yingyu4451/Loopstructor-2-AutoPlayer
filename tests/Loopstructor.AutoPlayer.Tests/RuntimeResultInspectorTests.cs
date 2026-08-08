using Loopstructor.AutoPlayer.Core;
using Newtonsoft.Json.Linq;

namespace Loopstructor.AutoPlayer.Tests;

public sealed class RuntimeResultInspectorTests
{
    [Fact]
    public void Message_UsesChineseFallbackWhenRuntimeOmitsMessage()
    {
        Assert.Equal("未知结果。", RuntimeResultInspector.Message(new JObject()));
    }

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

    [Theory]
    [InlineData("statePolluted")]
    [InlineData("needsReset")]
    [InlineData("outcomeUnknown")]
    public void Classify_DataRootMutationFlagAsUnsafe(string flag)
    {
        JObject result = new()
        {
            ["success"] = false,
            ["data"] = new JObject
            {
                [flag] = true,
                ["state"] = new JObject()
            }
        };

        Assert.Equal(RuntimeResultDisposition.Unsafe, RuntimeResultInspector.Classify(result));
        Assert.Equal(flag + "=true", RuntimeResultInspector.UnsafeMutationReason(result));
    }

    [Fact]
    public void Classify_DataRootMutationFlagWinsOverPending()
    {
        JObject result = JObject.FromObject(new
        {
            success = true,
            data = new
            {
                pending = true,
                statePolluted = true,
                state = new { }
            }
        });

        Assert.Equal(RuntimeResultDisposition.Unsafe, RuntimeResultInspector.Classify(result));
        Assert.Equal(RuntimeResultDisposition.Pending, RuntimeResultInspector.ClassifyReadOnly(result));
    }

    [Fact]
    public void Classify_IgnoresDataRootHistoryMutationFlag()
    {
        JObject result = JObject.FromObject(new
        {
            success = false,
            data = new
            {
                statePolluted = false,
                diff = new
                {
                    before = new { statePolluted = true, needsReset = true }
                },
                state = new { }
            }
        });

        Assert.Equal(RuntimeResultDisposition.Failure, RuntimeResultInspector.Classify(result));
    }

    [Fact]
    public void Classification_DoesNotTreatObjectMetadataAsMutationStatus()
    {
        JObject result = JObject.FromObject(new
        {
            success = true,
            data = new
            {
                state = new
                {
                    vehicles = new[]
                    {
                        new { metadata = new { statePolluted = true, needsReset = true } }
                    }
                }
            }
        });

        Assert.Equal(RuntimeResultDisposition.Success, RuntimeResultInspector.Classify(result));
        Assert.Equal(RuntimeResultDisposition.Success, RuntimeResultInspector.ClassifyReadOnly(result));
    }

    [Fact]
    public void ClassifyReadOnly_PendingWinsWhenSnapshotContainsMutationFlags()
    {
        JObject result = JObject.FromObject(new
        {
            success = true,
            data = new
            {
                pending = true,
                state = new
                {
                    diagnostics = new { statePolluted = true, needsReset = true }
                }
            }
        });

        Assert.Equal(RuntimeResultDisposition.Pending, RuntimeResultInspector.ClassifyReadOnly(result));
    }

    [Fact]
    public void Classify_WritePendingWithNestedCurrentPollutionAsUnsafe()
    {
        JObject result = JObject.FromObject(new
        {
            success = true,
            data = new
            {
                pending = true,
                state = new
                {
                    drawResult = new
                    {
                        success = false,
                        data = new { state = new { statePolluted = true } }
                    }
                }
            }
        });

        Assert.Equal(RuntimeResultDisposition.Unsafe, RuntimeResultInspector.Classify(result));
    }

    [Fact]
    public void Classify_WriteIgnoresPollutionAndCommittedPlacementInsideBeforeSnapshot()
    {
        JObject result = JObject.FromObject(new
        {
            success = false,
            data = new
            {
                state = new
                {
                    statePolluted = false,
                    before = new
                    {
                        statePolluted = true,
                        previousResult = new
                        {
                            success = false,
                            data = new { state = new { needsReset = true } }
                        },
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

        Assert.False(RuntimeResultInspector.IsUnsafe(result));
        Assert.Equal(RuntimeResultDisposition.Failure, RuntimeResultInspector.Classify(result));
    }

    [Theory]
    [InlineData("statePolluted", "statePolluted=true")]
    [InlineData("needsReset", "needsReset=true")]
    [InlineData("outcomeUnknown", "outcomeUnknown=true")]
    public void UnsafeMutationReason_NamesTheRuntimeFlag(string flag, string expected)
    {
        JObject state = new() { [flag] = true };
        JObject result = new()
        {
            ["success"] = false,
            ["data"] = new JObject { ["state"] = state }
        };

        Assert.Equal(expected, RuntimeResultInspector.UnsafeMutationReason(result));
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
    public void Classify_CommittedAttributePlacementAsRecoverableWhenRailLayerRolledBackCleanly()
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
                    after = new
                    {
                        railCount = 0,
                        illegalRailCount = 0,
                        trainCount = 0,
                        placedPlayerVehicleCount = 0
                    },
                    attributePlacement = new
                    {
                        beforeAttributeCount = 0,
                        afterAttributeCount = 1,
                        confirmResult = new { success = true }
                    },
                    drawResult = new
                    {
                        success = false,
                        data = new { state = new { statePolluted = false, needsReset = false } }
                    }
                }
            }
        });

        Assert.True(RuntimeResultInspector.IsRecoverableDefaultDefenseCheckpoint(result));
        Assert.False(RuntimeResultInspector.IsUnsafe(result));
        Assert.True(RuntimeResultInspector.IsRetryableDefaultDefenseFailure(result));
        Assert.Equal(RuntimeResultDisposition.Failure, RuntimeResultInspector.Classify(result));
    }

    [Fact]
    public void Classify_CommittedAttributePlacementAsRecoverableBeforeRailPlanning()
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
                    rollbackApplied = false,
                    defense = new
                    {
                        railCount = 0,
                        illegalRailCount = 0,
                        trainCount = 0,
                        placedPlayerVehicleCount = 0
                    },
                    extra = new
                    {
                        policy = "minimalLegalLoop",
                        attributePlacement = new
                        {
                            beforeAttributeCount = 0,
                            afterAttributeCount = 1,
                            confirmResult = new
                            {
                                success = true,
                                data = new { state = new { statePolluted = false } }
                            }
                        }
                    }
                }
            }
        });

        Assert.True(RuntimeResultInspector.IsRecoverableDefaultDefenseCheckpoint(result));
        Assert.False(RuntimeResultInspector.IsUnsafe(result));
        Assert.True(RuntimeResultInspector.IsRetryableDefaultDefenseFailure(result));
        Assert.Equal(RuntimeResultDisposition.Failure, RuntimeResultInspector.Classify(result));
    }

    [Fact]
    public void Classify_CommittedAttributePlacementRemainsUnsafeWhenIllegalRailRemains()
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
                    after = new
                    {
                        railCount = 1,
                        illegalRailCount = 1,
                        trainCount = 0,
                        placedPlayerVehicleCount = 0
                    },
                    attributePlacement = new
                    {
                        beforeAttributeCount = 0,
                        afterAttributeCount = 1,
                        confirmResult = new { success = true }
                    },
                    drawResult = new
                    {
                        success = false,
                        data = new { state = new { statePolluted = false, needsReset = false } }
                    }
                }
            }
        });

        Assert.False(RuntimeResultInspector.IsRecoverableDefaultDefenseCheckpoint(result));
        Assert.True(RuntimeResultInspector.IsUnsafe(result));
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
