using Loopstructor.AutoPlayer.Plugin;

namespace Loopstructor.AutoPlayer.Tests;

public sealed class IndependentVehiclePlacementPatchTests
{
    [Theory]
    [InlineData(1, 2)]
    [InlineData(2, 3)]
    public void LiveCapacityOverridesRetiredSingleTrainGate(int driverCount, int driverMaxCount)
    {
        Assert.True(IndependentVehiclePlacementPatch.ShouldOverrideLegacySingleTrainGate(
            independentVehicleMode: true,
            isLoop: true,
            driverCount,
            driverMaxCount,
            isDriverReachToMax: false));
    }

    [Theory]
    [InlineData(false, true, 1, 2, false)]
    [InlineData(true, false, 1, 2, false)]
    [InlineData(true, true, 0, 2, false)]
    [InlineData(true, true, 2, 2, true)]
    public void InvalidOrFullRailKeepsGameCommandGate(
        bool independentVehicleMode,
        bool isLoop,
        int driverCount,
        int driverMaxCount,
        bool isDriverReachToMax)
    {
        Assert.False(IndependentVehiclePlacementPatch.ShouldOverrideLegacySingleTrainGate(
            independentVehicleMode,
            isLoop,
            driverCount,
            driverMaxCount,
            isDriverReachToMax));
    }
}
