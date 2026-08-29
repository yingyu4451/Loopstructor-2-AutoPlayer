using Mono.Cecil;
using Mono.Cecil.Cil;
using Loopstructor.AutoPlayer.Core;
using Newtonsoft.Json.Linq;
using System.Reflection;

namespace Loopstructor.AutoPlayer.Tests;

public sealed class RailExpansionRuntimeContractTests
{
    private const string ControllerType = "Loopstructor.AutoPlayer.Plugin.AutoPlayController";
    private const string BridgeType = "Loopstructor.AutoPlayer.Plugin.RuntimeBridge";
    private const string StructuralGuardType = "Loopstructor.AutoPlayer.Core.PendingDefenseMutationGuard";
    private const string StationGridProbeType =
        "Loopstructor.AutoPlayer.Plugin.IncrementalDefenseStationGridProbe";
    private const string JointLayoutProbeType =
        "Loopstructor.AutoPlayer.Plugin.IncrementalRailJointLayoutProbe";

    [Fact]
    public void RuntimeBridge_UsesFormalRailAndStationCommandsWithoutPseudoRightDragFallback()
    {
        using AssemblyDefinition assembly = ReadPlugin();
        TypeDefinition bridge = RequireType(assembly, BridgeType);
        MethodDefinition initializer = bridge.Methods.Single(method => method.Name == ".cctor");
        string[] values = LoadedStrings(initializer).ToArray();

        Assert.Contains("insertPointFromLine", values);
        Assert.Contains("deleteLinePoint", values);
        Assert.Contains("InsertPointFromLine", values);
        Assert.Contains("queryMovableStationState", values);
        Assert.Contains("QueryMovableStationState", values);
        Assert.Contains("startStationMove", values);
        Assert.Contains("StartStationMove", values);
        Assert.Contains("confirmStationMoveGrid", values);
        Assert.Contains("ConfirmStationMoveGrid", values);
        Assert.DoesNotContain("startRightDragStationToGrid", values);
        Assert.DoesNotContain("StartRightDragStationToGrid", values);
        Assert.DoesNotContain("serviceFallback", values);
    }

    [Fact]
    public void Tick_ReconcilesArmedStructuralMutationBeforeOutcomeSettlement()
    {
        using AssemblyDefinition assembly = ReadPlugin();
        MethodDefinition tick = RequireMethod(RequireType(assembly, ControllerType), "Tick");
        Instruction[] instructions = tick.Body.Instructions.ToArray();

        int armed = FindCall(instructions, StructuralGuardType, "get_IsArmed");
        int reconcile = FindCall(instructions, ControllerType, "TryMaintainDefense", armed + 1);
        int outcome = FindCall(
            instructions,
            "Loopstructor.AutoPlayer.Plugin.GameOutcomeObserver",
            "get_Outcome",
            armed + 1);
        int settlement = FindCall(instructions, ControllerType, "TickSettlement", outcome + 1);

        Assert.True(armed >= 0 && reconcile > armed && outcome > reconcile && settlement > outcome);
    }

    [Fact]
    public void BattleTactics_ReplansSpecialStationMaintenanceThroughoutTheWave()
    {
        using AssemblyDefinition assembly = ReadPlugin();
        TypeDefinition controller = RequireType(assembly, ControllerType);
        TypeDefinition steps = controller.NestedTypes.Single(type => type.Name == "BattleTacticStep");
        MethodDefinition tactics = RequireMethod(controller, "RunBattleTacticStep");
        MethodDefinition begin = RequireMethod(controller, "TryBeginBattleSpecialStationMaintenance");
        MethodDefinition reset = RequireMethod(controller, "ResetBattleTactics");

        Assert.Contains(steps.Fields, field => field.Name == "RunSpecialStationMaintenance");
        Assert.Contains(Calls(tactics), call => call.DeclaringType.FullName == ControllerType &&
                                               call.Name == "TryBeginBattleSpecialStationMaintenance");
        Assert.Contains(Calls(tactics), call => call.DeclaringType.FullName == ControllerType &&
                                               call.Name == "TryMaintainDefense");
        Assert.Contains("startStationMove", LoadedStrings(begin));
        Assert.Contains("confirmStationMoveGrid", LoadedStrings(begin));
        Assert.Contains("queryMovableStationState", LoadedStrings(begin));
        Assert.DoesNotContain(
            controller.Fields,
            field => field.Name is "_battleSpecialMoveAttemptedThisWave" or
                                   "_defenseSpecialMoveAttempted" or
                                   "_nextDefenseRailMaintenanceAt");
        Assert.Contains(
            Calls(reset),
            call => call.DeclaringType.FullName == ControllerType &&
                    call.Name == "ResetDefenseRailMaintenanceSession");
    }

    [Fact]
    public void VerifiedRailMutations_UnlockThenContinueWithFreshQueries()
    {
        using AssemblyDefinition assembly = ReadPlugin();
        TypeDefinition controller = RequireType(assembly, ControllerType);
        MethodDefinition maintain = RequireMethod(controller, "TryMaintainDefense");
        MethodDefinition continueOptimization = RequireMethod(
            controller,
            "ContinueDefenseRailOptimization");

        Assert.Contains(
            Calls(maintain),
            call => call.DeclaringType.FullName == "Loopstructor.AutoPlayer.Core.RailExpansionPlanner" &&
                    call.Name == "VerifyInsertion");
        Assert.Contains(
            Calls(maintain),
            call => call.DeclaringType.FullName == "Loopstructor.AutoPlayer.Core.RailInsertionVerification" &&
                    call.Name == "get_Beneficial");
        Assert.Contains(
            LoadedStrings(maintain),
            value => value.Contains("结构写入已完整对账，不要求重启", StringComparison.Ordinal));
        Assert.Contains(
            Calls(maintain),
            call => call.DeclaringType.FullName == "Loopstructor.AutoPlayer.Core.RailExpansionPlanner" &&
                    call.Name == "VerifyMove");
        Assert.True(
            Calls(maintain).Count(call =>
                call.DeclaringType.FullName == ControllerType &&
                call.Name == continueOptimization.Name) >= 4);
        Assert.Contains(
            Calls(continueOptimization),
            call => call.DeclaringType.FullName == ControllerType &&
                    call.Name == "ScheduleDefenseMaintenanceStep");
        Assert.Contains(
            Calls(continueOptimization),
            call => call.DeclaringType.FullName == StructuralGuardType &&
                    call.Name == "get_IsArmed");
        Assert.Contains(
            continueOptimization.Body.Instructions,
            instruction => instruction.Operand is FieldReference field &&
                           field.Name == "_defenseMaintenanceStep");
        Assert.Contains(
            LoadedStrings(continueOptimization),
            value => value.Contains("重新读取站点与轨道", StringComparison.Ordinal));
    }

    [Fact]
    public void RailMaintenance_UsesLayoutAndActionFingerprintsWithoutLegacyAttemptLatches()
    {
        using AssemblyDefinition assembly = ReadPlugin();
        TypeDefinition controller = RequireType(assembly, ControllerType);
        MethodDefinition maintain = RequireMethod(controller, "TryMaintainDefense");
        MethodDefinition request = RequireMethod(controller, "RequestDefenseMaintenance");
        MethodDefinition finish = RequireMethod(controller, "FinishDefenseMaintenance");

        Assert.DoesNotContain(
            controller.Fields,
            field => field.Name is "_battleSpecialMoveAttemptedThisWave" or
                                   "_defenseSpecialMoveAttempted");
        Assert.Contains(
            controller.Fields,
            field => field.Name == "_defenseRailMaintenanceActionFingerprints");
        Assert.Contains(
            controller.Fields,
            field => field.Name == "_defenseRailMaintenanceLayoutFingerprint");
        Assert.Contains(
            controller.Fields,
            field => field.Name == "_defenseRailMaintenanceStableLayoutFingerprint");
        Assert.Contains(
            Calls(maintain),
            call => call.DeclaringType.FullName == ControllerType &&
                    call.Name == "BuildDefenseRailMaintenanceLayoutFingerprint");
        Assert.Contains(
            Calls(maintain),
            call => call.DeclaringType.FullName == ControllerType &&
                    call.Name == "BuildDefenseRailMoveActionFingerprint");
        Assert.Contains(
            Calls(maintain),
            call => call.DeclaringType.FullName == "Loopstructor.AutoPlayer.Core.RailExpansionPlanner" &&
                    call.Name == "IsBeneficialMove");
        Assert.Contains(
            Calls(request),
            call => call.DeclaringType.FullName == ControllerType &&
                    call.Name == "ResetDefenseRailMaintenanceSession");
        Assert.DoesNotContain(
            Calls(finish),
            call => call.DeclaringType.FullName == ControllerType &&
                    call.Name == "MarkDefenseRailMaintenanceStable");
        Assert.True(
            Calls(maintain).Count(call =>
                call.DeclaringType.FullName == ControllerType &&
                call.Name == "MarkDefenseRailMaintenanceStable") >= 2);
    }

    [Fact]
    public void RailMaintenanceFingerprint_IgnoresDynamicCycleButTracksStructureAndIndependentVehicles()
    {
        Assembly plugin = Assembly.LoadFrom(PluginPath());
        Type controller = plugin.GetType(ControllerType, throwOnError: true)!;
        MethodInfo fingerprint = controller.GetMethod(
            "BuildDefenseRailMaintenanceLayoutFingerprint",
            BindingFlags.Static | BindingFlags.NonPublic)!;

        JObject catapults = JObject.Parse(
            """
            {"catapults":[{"catapultInstanceId":100,"linePointInstanceId":200,"path":"station/a","recycleDisposableEnum":"FreePoint_Attribute","grid":{"x":4,"y":0},"railId":7,"railMembershipCount":1,"isAttribute":true,"canMove":true}]}
            """);
        JObject vehicles = JObject.Parse(
            """
            {"vehicles":[{"instanceId":300,"railInstanceId":70,"runState":"Running","currentSpeed":1.5,"configuredSpeed":2.0}]}
            """);
        JObject fasterCycle = RailState(1.25d, 4);
        JObject slowerCycle = RailState(2.75d, 4);

        string baseline = InvokeFingerprint(fingerprint, fasterCycle, catapults, vehicles);
        string dynamicCycleChanged = InvokeFingerprint(fingerprint, slowerCycle, catapults, vehicles);
        Assert.Equal(baseline, dynamicCycleChanged);

        JObject changedGeometry = RailState(1.25d, 6);
        Assert.NotEqual(
            baseline,
            InvokeFingerprint(fingerprint, changedGeometry, catapults, vehicles));

        JObject changedVehicle = (JObject)vehicles.DeepClone();
        changedVehicle.SelectToken("vehicles[0].instanceId")!.Replace(301);
        Assert.NotEqual(
            baseline,
            InvokeFingerprint(fingerprint, fasterCycle, catapults, changedVehicle));
    }

    [Fact]
    public void BattleOnlyMaintenance_UsesStationPlacementOrRebuildBeforeOrdinaryInsertionPlanning()
    {
        using AssemblyDefinition assembly = ReadPlugin();
        MethodDefinition maintain = RequireMethod(
            RequireType(assembly, ControllerType),
            "TryMaintainDefense");
        Instruction[] instructions = maintain.Body.Instructions.ToArray();
        int movableCandidates = FindCall(
            instructions,
            "Loopstructor.AutoPlayer.Core.RailExpansionPlanner",
            "BuildExistingSpecialMoveCandidates");
        int insertionCandidates = FindCall(
            instructions,
            "Loopstructor.AutoPlayer.Core.RailExpansionPlanner",
            "BuildCandidates",
            movableCandidates + 1);

        Assert.True(movableCandidates >= 0 && insertionCandidates > movableCandidates);
        Instruction[] battleGate = instructions
            .Skip(movableCandidates + 1)
            .Take(insertionCandidates - movableCandidates - 1)
            .ToArray();
        Assert.Contains(
            battleGate,
            instruction => instruction.Operand is FieldReference field &&
                           field.Name == "_defenseBattleSpecialMoveOnly");
        Assert.Contains(
            LoadedStrings(maintain),
            value => value == "__runtime_movable_station__");
        Assert.Contains(
            LoadedStrings(maintain),
            value => value == "deleteLinePoint");
    }

    [Fact]
    public void FreshMovableStationMismatch_IsTransientAndNeverConsumesOrWritesTheCandidate()
    {
        using AssemblyDefinition assembly = ReadPlugin();
        TypeDefinition controller = RequireType(assembly, ControllerType);
        MethodDefinition maintain = RequireMethod(controller, "TryMaintainDefense");
        MethodDefinition transientMismatch = RequireMethod(
            controller,
            "HandleTransientFreshMovableStationMismatch");
        MethodReference[] mismatchCalls = Calls(transientMismatch).ToArray();

        Instruction[] maintainInstructions = maintain.Body.Instructions.ToArray();
        int freshnessCheck = FindCall(
            maintainInstructions,
            "Loopstructor.AutoPlayer.Core.RailExpansionPlanner",
            "IsFreshMovableSpecial");
        int transientHandler = FindCall(
            maintainInstructions,
            ControllerType,
            transientMismatch.Name,
            freshnessCheck + 1);

        Assert.True(freshnessCheck >= 0 && transientHandler > freshnessCheck);
        Assert.DoesNotContain(
            maintainInstructions.Skip(freshnessCheck + 1).Take(transientHandler - freshnessCheck - 1),
            instruction => instruction.Operand is MethodReference call &&
                           (call.Name is "BuildDefenseRailMoveCandidateFingerprint" or
                                         "MarkDefenseRailMaintenanceStable" or
                                         "IssueGuardedDefenseMutation" ||
                            call.DeclaringType.FullName.StartsWith(
                                "System.Collections.Generic.HashSet`1",
                                StringComparison.Ordinal) &&
                            call.Name == "Add"));

        Assert.Contains(
            mismatchCalls,
            call => call.DeclaringType.FullName == ControllerType &&
                    call.Name == "FinishDefenseMaintenance");
        Assert.Contains(
            transientMismatch.Body.Instructions,
            instruction => instruction.Operand is FieldReference field &&
                           field.Name == "_defenseFreshMovableStationRetryAttempts");
        Assert.Contains(
            transientMismatch.Body.Instructions,
            instruction => instruction.Operand is FieldReference field &&
                           field.Name == "_nextTickAt");
        Assert.Contains(
            LoadedStrings(transientMismatch),
            value => value.Contains("未发送写命令", StringComparison.Ordinal));
        Assert.DoesNotContain(
            mismatchCalls,
            call => call.DeclaringType.FullName == ControllerType &&
                    call.Name is "BuildDefenseRailMoveCandidateFingerprint" or
                                 "MarkDefenseRailMaintenanceStable" or
                                 "ContinueDefenseRailOptimization" or
                                 "IssueGuardedDefenseMutation");
        Assert.DoesNotContain(
            mismatchCalls,
            call => call.DeclaringType.FullName.StartsWith(
                        "System.Collections.Generic.HashSet`1",
                        StringComparison.Ordinal) &&
                    call.Name == "Add");
    }

    [Fact]
    public void MoveGridInitialization_ClassifiesDeterministicAndTransientFailuresSeparately()
    {
        Assembly plugin = Assembly.LoadFrom(PluginPath());
        Type probeType = plugin.GetType(StationGridProbeType, throwOnError: true)!;
        MethodInfo initializeMove = probeType.GetMethods(
                BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
            .Single(method =>
                method.Name == "TryInitializeMove" &&
                method.GetParameters().Length == 2 &&
                method.GetParameters()[0].ParameterType == typeof(RailStationMoveCandidate));
        PropertyInfo failure = probeType.GetProperty(
            "InitializationFailure",
            BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)!;

        object deterministicProbe = Activator.CreateInstance(probeType, nonPublic: true)!;
        object?[] deterministicArguments =
        {
            new RailStationMoveCandidate(),
            null
        };
        Assert.False(Assert.IsType<bool>(initializeMove.Invoke(
            deterministicProbe,
            deterministicArguments)));
        Assert.Equal("NoBeneficialCandidate", failure.GetValue(deterministicProbe)?.ToString());

        object transientProbe = Activator.CreateInstance(probeType, nonPublic: true)!;
        object?[] transientArguments =
        {
            new RailStationMoveCandidate
            {
                StationDisposableEnum = "FreePoint_Attribute"
            },
            null
        };
        Assert.False(Assert.IsType<bool>(initializeMove.Invoke(
            transientProbe,
            transientArguments)));
        Assert.Equal("TransientUnavailable", failure.GetValue(transientProbe)?.ToString());
    }

    [Fact]
    public void TransientMoveGridInitialization_DoesNotConsumeCandidateOrMarkLayoutStable()
    {
        using AssemblyDefinition assembly = ReadPlugin();
        TypeDefinition controller = RequireType(assembly, ControllerType);
        TypeDefinition probe = RequireType(assembly, JointLayoutProbeType);
        MethodDefinition maintain = RequireMethod(controller, "TryMaintainDefense");
        MethodDefinition handler = RequireMethod(
            controller,
            "HandleTransientMoveGridInitializationFailure");
        Instruction[] instructions = maintain.Body.Instructions.ToArray();

        int initialize = FindCall(instructions, JointLayoutProbeType, "TryInitialize");
        int transientHandler = FindCall(
            instructions,
            ControllerType,
            handler.Name,
            initialize + 1);
        int consumeCandidate = instructions
            .Select((instruction, index) => (instruction, index))
            .Where(item => item.index > transientHandler)
            .Where(item => item.instruction.Operand is MethodReference call &&
                           call.DeclaringType.FullName.StartsWith(
                               "System.Collections.Generic.HashSet`1",
                               StringComparison.Ordinal) &&
                           call.Name == "Add")
            .Select(item => item.index)
            .DefaultIfEmpty(-1)
            .First();

        Assert.True(
            initialize >= 0 &&
            transientHandler > initialize &&
            consumeCandidate > transientHandler);
        Assert.Contains(probe.Fields, field => field.Name == "_candidateReader");
        Assert.Contains(
            handler.Body.Instructions,
            instruction => instruction.Operand is FieldReference field &&
                           field.Name == "_defenseMoveGridInitializationRetryAttempts");
        Assert.Contains(
            handler.Body.Instructions,
            instruction => instruction.Operand is FieldReference field &&
                           field.Name == "_nextTickAt");
        Assert.True(Constant<float>(controller, "MoveGridInitializationRetryDelaySeconds") >= 1f);
        Assert.True(Constant<int>(controller, "MaxMoveGridInitializationRetryAttempts") >= 1);
        Assert.Contains(
            LoadedStrings(handler),
            value => value.Contains("不会消费候选", StringComparison.Ordinal));
        Assert.DoesNotContain(
            Calls(handler),
            call => call.DeclaringType.FullName == ControllerType &&
                    call.Name is "MarkDefenseRailMaintenanceStable" or
                                 "IssueGuardedDefenseMutation" or
                                 "BuildDefenseRailMoveCandidateFingerprint");
        Assert.DoesNotContain(
            Calls(handler),
            call => call.DeclaringType.FullName.StartsWith(
                        "System.Collections.Generic.HashSet`1",
                        StringComparison.Ordinal) &&
                    call.Name == "Add");
    }

    [Fact]
    public void StationMoveAutoCancellation_ReconcilesOriginalRailAndStationBeforeUnlocking()
    {
        using AssemblyDefinition assembly = ReadPlugin();
        TypeDefinition controller = RequireType(assembly, ControllerType);
        TypeDefinition steps = controller.NestedTypes.Single(type => type.Name == "DefenseMaintenanceStep");
        MethodDefinition maintain = RequireMethod(controller, "TryMaintainDefense");
        MethodDefinition beginRollback = RequireMethod(
            controller,
            "BeginSpecialStationMoveRollbackVerificationIfInactive");
        MethodDefinition finishCommitted = RequireMethod(controller, "FinishCommittedSpecialStationMove");

        Assert.Contains(steps.Fields, field => field.Name == "VerifySpecialStationMoveRollbackRail");
        Assert.Contains(steps.Fields, field => field.Name == "VerifySpecialStationMoveRollbackResult");
        Assert.True(Calls(maintain).Count(call => call.DeclaringType.FullName == ControllerType &&
                                                   call.Name == beginRollback.Name) >= 3);
        Assert.Contains(
            Calls(maintain),
            call => call.DeclaringType.FullName == "Loopstructor.AutoPlayer.Core.RailExpansionPlanner" &&
                    call.Name == "VerifyMoveCancellationRollback");
        Assert.Contains("currentMoveInteraction.active", LoadedStrings(beginRollback));
        Assert.Contains("queryRail", LoadedStrings(maintain));
        Assert.Contains("queryCatapults", LoadedStrings(maintain));
        Assert.Contains(
            Calls(maintain),
            call => call.DeclaringType.FullName == ControllerType && call.Name == finishCommitted.Name);
        Assert.Contains(
            finishCommitted.Body.Instructions,
            instruction =>
                (instruction.Operand is FieldReference field &&
                 field.Name == "_defenseSpecialMoveConfirmationAccepted") ||
                (instruction.Operand is MethodReference method &&
                 method.Name == "Reset"));
        Assert.DoesNotContain(
            Calls(finishCommitted),
            call => call.DeclaringType.FullName == ControllerType && call.Name == "FaultRequiringProcessRestart");
    }

    [Theory]
    [InlineData("Start")]
    [InlineData("Pause")]
    [InlineData("Stop")]
    [InlineData("ApplyPause")]
    [InlineData("ApplyStop")]
    public void LifecycleTransitions_ReadStructuralGuardBeforeChangingState(string methodName)
    {
        using AssemblyDefinition assembly = ReadPlugin();
        MethodDefinition method = RequireMethod(RequireType(assembly, ControllerType), methodName);

        Assert.Contains(
            Calls(method),
            call => call.DeclaringType.FullName == StructuralGuardType && call.Name == "get_IsArmed");
    }

    private static AssemblyDefinition ReadPlugin()
    {
        string path = Path.Combine(AppContext.BaseDirectory, "Loopstructor.AutoPlayer.Plugin.dll");
        Assert.True(File.Exists(path), "Plugin assembly was not copied to the test output: " + path);
        return AssemblyDefinition.ReadAssembly(path);
    }

    private static string PluginPath() =>
        Path.Combine(AppContext.BaseDirectory, "Loopstructor.AutoPlayer.Plugin.dll");

    private static string InvokeFingerprint(
        MethodInfo method,
        JObject rail,
        JObject catapults,
        JObject trains) =>
        Assert.IsType<string>(method.Invoke(null, new object?[] { rail, catapults, trains }));

    private static JObject RailState(double loopCycleSeconds, int rightX) => new()
    {
        ["rails"] = new JArray
        {
            new JObject
            {
                ["instanceId"] = 70,
                ["railInternalId"] = 7,
                ["stationCount"] = 4,
                ["isLegalPlayerLoop"] = true,
                ["loopCycleSeconds"] = loopCycleSeconds,
                ["railLength"] = 20,
                ["lines"] = new JArray
                {
                    new JObject
                    {
                        ["from"] = new JObject { ["x"] = -4, ["y"] = 0 },
                        ["to"] = new JObject { ["x"] = rightX, ["y"] = 0 }
                    }
                }
            }
        }
    };

    private static TypeDefinition RequireType(AssemblyDefinition assembly, string fullName) =>
        assembly.MainModule.Types.Single(type => type.FullName == fullName);

    private static MethodDefinition RequireMethod(TypeDefinition type, string name) =>
        type.Methods.Single(method => method.Name == name);

    private static T Constant<T>(TypeDefinition type, string name)
    {
        FieldDefinition field = type.Fields.Single(field => field.Name == name);
        Assert.True(field.HasConstant);
        return (T)field.Constant;
    }

    private static IEnumerable<MethodReference> Calls(MethodDefinition method) =>
        method.Body.Instructions
            .Where(instruction => instruction.OpCode.Code is Code.Call or Code.Callvirt)
            .Select(instruction => instruction.Operand)
            .OfType<MethodReference>();

    private static IEnumerable<string> LoadedStrings(MethodDefinition method) =>
        method.Body.Instructions
            .Where(instruction => instruction.OpCode.Code == Code.Ldstr)
            .Select(instruction => instruction.Operand)
            .OfType<string>();

    private static int FindCall(
        IReadOnlyList<Instruction> instructions,
        string declaringType,
        string methodName,
        int startIndex = 0)
    {
        for (int index = Math.Max(0, startIndex); index < instructions.Count; index++)
        {
            if (instructions[index].Operand is MethodReference call &&
                call.DeclaringType.FullName == declaringType &&
                call.Name == methodName)
            {
                return index;
            }
        }

        return -1;
    }
}
