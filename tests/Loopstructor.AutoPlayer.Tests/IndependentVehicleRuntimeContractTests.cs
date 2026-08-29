using Mono.Cecil;
using Mono.Cecil.Cil;

namespace Loopstructor.AutoPlayer.Tests;

public sealed class IndependentVehicleRuntimeContractTests
{
    [Fact]
    public void VerifiedRailInsertion_RefreshesTheStallWatchdogBeforeTheNextFinitePreviewBatch()
    {
        string source = File.ReadAllText(Path.Combine(
            RepositoryRoot(),
            "src",
            "Loopstructor.AutoPlayer.Plugin",
            "AutoPlayController.cs"));
        int verified = source.IndexOf("if (insertionVerification.Verified)", StringComparison.Ordinal);
        int nextCase = source.IndexOf(
            "case DefenseMaintenanceStep.ProbeSpecialStationMoveGrid",
            verified,
            StringComparison.Ordinal);

        Assert.True(verified >= 0 && nextCase > verified);
        string verificationBranch = source.Substring(verified, nextCase - verified);
        Assert.Contains("MarkProgress();", verificationBranch);

        int selected = source.IndexOf(
            "_defenseRailMaintenanceActionFingerprints.Add(",
            StringComparison.Ordinal);
        int timeline = source.IndexOf("AddTimeline(", selected, StringComparison.Ordinal);
        Assert.True(selected >= 0 && timeline > selected);
        Assert.Contains("MarkProgress();", source.Substring(selected, timeline - selected));
    }

    [Fact]
    public void RequiredTopologyStillEvaluatesWhetherCapacityNeedsASeparateEnergyLoop()
    {
        string source = File.ReadAllText(Path.Combine(
            RepositoryRoot(),
            "src",
            "Loopstructor.AutoPlayer.Plugin",
            "AutoPlayController.cs"));
        int required = source.IndexOf("if (_requiredRailTopologyMaintenance)", StringComparison.Ordinal);
        int reinforcement = source.IndexOf("AutomationAction? reinforcement", required, StringComparison.Ordinal);

        Assert.True(required >= 0 && reinforcement > required);
        Assert.Contains(
            "NeedsIndependentDefenseExpansion(independentState)",
            source.Substring(required, reinforcement - required));
    }

    private const string BridgeType = "Loopstructor.AutoPlayer.Plugin.RuntimeBridge";
    private const string ControllerType = "Loopstructor.AutoPlayer.Plugin.AutoPlayController";
    private const string VehicleFallbackType = "Loopstructor.AutoPlayer.Plugin.IndependentVehicleRuntimeFallback";
    private const string UpgradeFallbackType = "Loopstructor.AutoPlayer.Plugin.DirectUpgradeUiRuntimeFallback";

    [Fact]
    public void CapacityAdapter_BindsAuthoritativeServiceAndPublishesRunningPlusFifoOccupancy()
    {
        using AssemblyDefinition assembly = ReadPlugin();
        TypeDefinition fallback = RequireType(assembly, VehicleFallbackType);
        MethodDefinition contract = RequireMethod(fallback, "TryGetContract");
        MethodDefinition rails = RequireMethod(fallback, "BuildRails");
        MethodDefinition deploy = RequireMethod(fallback, "TryDeploy");
        MethodDefinition baseComponent = RequireMethod(fallback, "ResolveUnenchantedBaseComponent");

        string[] contractStrings = LoadedStrings(contract).ToArray();
        Assert.Contains("MetroTD.CatapultSystem.EnergyCatapultTrainCacheService", contractStrings);
        Assert.Contains("EvaluateDeployment", contractStrings);
        Assert.Contains("TryDeployVehicle", contractStrings);
        Assert.Contains("GetWaitingVehicleCount", contractStrings);
        Assert.Contains("GetSaveData", contractStrings);
        Assert.Contains("MetroTD.VehicleSystem.VehicleDataManager", LoadedStrings(baseComponent));
        Assert.Contains("GetMainRazorComponent", LoadedStrings(baseComponent));

        foreach (string field in new[]
                 {
                     "energyPointCount", "energyPointInstanceId", "capacity", "runningCount",
                     "waitingCount", "occupiedCount", "freeCapacity", "runningVehicleIds", "waitingVehicleIds"
                 })
        {
            Assert.Contains(field, LoadedStrings(rails));
        }
        Assert.Contains(Calls(rails), call =>
            call.DeclaringType.FullName == "System.Reflection.MethodBase" && call.Name == "Invoke");

        Assert.Contains("AlreadyQueued", LoadedStrings(deploy));
        Assert.Contains("Accepted", LoadedStrings(deploy));
        Assert.Contains("outcomeUnknown", LoadedStrings(deploy));
        Assert.Contains("needsReconciliation", LoadedStrings(deploy));
        Assert.Contains(Calls(deploy), call => call.DeclaringType.FullName == "System.Reflection.MethodBase" && call.Name == "Invoke");
    }

    [Fact]
    public void RuntimeBridge_ExposesOnlyIndependentDeploymentAndDecorationFactoryUpgradeCommands()
    {
        using AssemblyDefinition assembly = ReadPlugin();
        TypeDefinition bridge = RequireType(assembly, BridgeType);
        string[] bridgeStrings = bridge.Methods.SelectMany(LoadedStrings).ToArray();
        foreach (string command in new[]
                 {
                     "queryIndependentVehicleState", "deployVehicleToEnergyPoint", "queryDirectUpgradeState",
                     "selectDirectUpgradeVehicle", "confirmDirectUpgradeVehicle",
                     "chooseDirectUpgradeEnchantment", "confirmDirectUpgradeSettlement"
                 })
        {
            Assert.Contains(command, bridgeStrings);
        }

        foreach (string obsolete in new[]
                 {
                     "queryTrain", "moveVehicleInTrain", "moveTrainToLine", "placeVehicleOnLine",
                     "mergeVehicles", "confirmVehicleMerge", "mergeEnchantments"
                 })
        {
            Assert.DoesNotContain(obsolete, bridgeStrings);
        }
        Assert.DoesNotContain(assembly.MainModule.Types, type => type.FullName.Contains("Merge", StringComparison.Ordinal));
    }

    [Fact]
    public void AutomaticControlSources_ContainNoLegacyTrainMovementOrMergeHelpers()
    {
        string root = RepositoryRoot();
        string sources = string.Join(
            "\n",
            File.ReadAllText(Path.Combine(root, "src", "Loopstructor.AutoPlayer.Core", "BattleDecisionEngine.cs")),
            File.ReadAllText(Path.Combine(root, "src", "Loopstructor.AutoPlayer.Plugin", "AutoPlayController.cs")),
            File.ReadAllText(Path.Combine(root, "src", "Loopstructor.AutoPlayer.Plugin", "RuntimeBridge.cs")));

        foreach (string obsolete in new[]
                 {
                     "DidTrainReachMovementTarget", "TrainMovementCandidate", "TrainCoveragePosition",
                     "moveVehicleInTrain", "moveTrainToLine", "placeVehicleOnLine",
                     "MergeAutomationPlanner", "MergeMutationSettlementGuard", "MergeUiRuntimeFallback"
                 })
        {
            Assert.DoesNotContain(obsolete, sources, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void UnknownDeployment_IsLockedForReadOnlyReconciliationInsteadOfProcessRestartOrReplay()
    {
        using AssemblyDefinition assembly = ReadPlugin();
        TypeDefinition controller = RequireType(assembly, ControllerType);
        MethodDefinition execute = RequireMethod(controller, "ExecuteWithResult");
        string[] strings = LoadedStrings(execute).ToArray();

        Assert.Contains("deployVehicleToEnergyPoint", strings);
        Assert.Contains(strings, value => value.Contains("只读对账且不会重放", StringComparison.Ordinal));
        Assert.Contains(Calls(execute), call =>
            call.DeclaringType.FullName == "Loopstructor.AutoPlayer.Core.RuntimeResultInspector" &&
            call.Name == "Classify");
    }

    [Fact]
    public void DecorationFactoryAdapter_BindsStableThreeChoicePanelContract()
    {
        using AssemblyDefinition assembly = ReadPlugin();
        TypeDefinition fallback = RequireType(assembly, UpgradeFallbackType);
        MethodDefinition contract = RequireMethod(fallback, "TryGetContract");
        MethodDefinition state = RequireMethod(fallback, "BuildState");

        string[] contractStrings = LoadedStrings(contract).ToArray();
        Assert.Contains("MetroTD.UISystem.RebuildUI_DirectUpgradePanel", contractStrings);
        Assert.Contains("MetroTD.UISystem.RebuildUI_DirectUpgradeVehicleItem", contractStrings);
        Assert.Contains("MetroTD.UISystem.RebuildUI_DirectUpgradeRewardItem", contractStrings);
        Assert.Contains("rewards", LoadedStrings(state));
        Assert.Contains("originalEnchantmentsPreserved", LoadedStrings(state));
        Assert.Contains("rewardApplied", LoadedStrings(state));
    }

    private static IEnumerable<MethodReference> Calls(MethodDefinition method) =>
        method.Body.Instructions
            .Where(instruction => instruction.OpCode.Code is Code.Call or Code.Callvirt or Code.Newobj)
            .Select(instruction => instruction.Operand)
            .OfType<MethodReference>();

    private static IEnumerable<string> LoadedStrings(MethodDefinition method) =>
        method.Body.Instructions
            .Where(instruction => instruction.OpCode.Code == Code.Ldstr)
            .Select(instruction => instruction.Operand)
            .OfType<string>();

    private static AssemblyDefinition ReadPlugin() => AssemblyDefinition.ReadAssembly(PluginPath());

    private static string PluginPath()
    {
        string path = Path.Combine(AppContext.BaseDirectory, "Loopstructor.AutoPlayer.Plugin.dll");
        Assert.True(File.Exists(path), "Plugin assembly was not copied to the test output: " + path);
        return path;
    }

    private static string RepositoryRoot()
    {
        DirectoryInfo? directory = new(AppContext.BaseDirectory);
        while (directory != null)
        {
            if (File.Exists(Path.Combine(directory.FullName, "Loopstructor.AutoPlayer.sln")))
            {
                return directory.FullName;
            }

            directory = directory.Parent;
        }

        throw new DirectoryNotFoundException("Unable to locate the repository root.");
    }

    private static TypeDefinition RequireType(AssemblyDefinition assembly, string fullName) =>
        assembly.MainModule.Types.Single(type => type.FullName == fullName);

    private static MethodDefinition RequireMethod(TypeDefinition type, string name) =>
        type.Methods.Single(method => method.Name == name);
}
