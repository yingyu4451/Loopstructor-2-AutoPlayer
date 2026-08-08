using Mono.Cecil;
using Mono.Cecil.Cil;

namespace Loopstructor.AutoPlayer.Tests;

public sealed class PreviewOwnershipRuntimeContractTests
{
    private const string ControllerType = "Loopstructor.AutoPlayer.Plugin.AutoPlayController";
    private const string CheatControllerType = "Loopstructor.AutoPlayer.Plugin.CheatController";
    private const string BridgeType = "Loopstructor.AutoPlayer.Plugin.RuntimeBridge";
    private const string BattleDecisionEngineType =
        "Loopstructor.AutoPlayer.Core.BattleDecisionEngine";
    private const string OpeningDefensePlannerType =
        "Loopstructor.AutoPlayer.Core.OpeningDefensePreparationPlanner";
    private const string RuntimeResultInspectorType =
        "Loopstructor.AutoPlayer.Core.RuntimeResultInspector";
    private const string RuntimeResultDispositionType =
        "Loopstructor.AutoPlayer.Core.RuntimeResultDisposition";
    private const string OpeningDefenseInteractionGuardType =
        "Loopstructor.AutoPlayer.Plugin.OpeningDefenseInteractionGuard";

    [Fact]
    public void TickInGame_NeverDirectlyCancelsAnUnknownDisposablePreview()
    {
        using AssemblyDefinition assembly = ReadPlugin();
        TypeDefinition controller = RequireType(assembly, ControllerType);
        MethodDefinition tick = RequireMethod(controller, "TickInGame");
        Instruction[] instructions = tick.Body.Instructions.ToArray();

        Assert.DoesNotContain(LoadedStrings(tick), value => value == "cancelDisposable");
        Assert.DoesNotContain(Calls(tick), IsCall(BridgeType, "Invoke"));

        int openingPreviewGate = FindCall(
            instructions,
            ControllerType,
            "TryHandleOpeningDefensePreviewBlocker");
        int ownedPreviewGate = FindCall(
            instructions,
            ControllerType,
            "HasOwnedAutomationPreviewIdentity");

        Assert.True(
            openingPreviewGate >= 0 && ownedPreviewGate > openingPreviewGate,
            "TickInGame must first recognize its owned opening preview and must never cancel a generic blocker directly.");
    }

    [Fact]
    public void OwnedPreviewRelease_BuildsAndExecutesEachCancellationInTheSameTick()
    {
        using AssemblyDefinition assembly = ReadPlugin();
        TypeDefinition controller = RequireType(assembly, ControllerType);
        MethodDefinition process = RequireMethod(controller, "ProcessOwnedPreviewRelease");
        Instruction[] instructions = process.Body.Instructions.ToArray();
        int[] buildCalls = FindCallIndices(
            instructions,
            ControllerType,
            "BuildOwnedPreviewCancellation");
        int[] executeCalls = FindCallIndices(
            instructions,
            ControllerType,
            "ExecuteOwnedPreviewCancellation");

        Assert.Equal(2, buildCalls.Length);
        Assert.Equal(buildCalls.Length, executeCalls.Length);

        for (int index = 0; index < buildCalls.Length; index++)
        {
            int build = buildCalls[index];
            int execute = executeCalls[index];

            Assert.True(build < execute, "Ownership must be built before cancellation executes.");
            Assert.Contains(
                instructions.Skip(build + 1).Take(execute - build - 1),
                IsFieldStore("_ownedPreviewReleaseCancelAction"));
            Assert.DoesNotContain(
                instructions.Skip(build + 1).Take(execute - build - 1),
                instruction => instruction.OpCode.Code == Code.Ret);
            Assert.DoesNotContain(
                CallsBetween(instructions, build, execute),
                IsCall(ControllerType, "ScheduleOwnedPreviewRelease"));
        }
    }

    [Fact]
    public void BattleDisposableStateMachine_PreservesIdentityThroughSettlementAndCancelVerification()
    {
        using AssemblyDefinition assembly = ReadPlugin();
        TypeDefinition controller = RequireType(assembly, ControllerType);
        TypeDefinition steps = RequireNestedType(controller, "BattleTacticStep");
        MethodDefinition run = RequireMethod(controller, "RunBattleTacticStep");

        int confirmValue = EnumValue(steps, "ConfirmDisposable");
        int settlementValue = EnumValue(steps, "WaitForDisposableSettlement");
        int cancelValue = EnumValue(steps, "CancelDisposable");
        int verifyCancelValue = EnumValue(steps, "VerifyDisposableCancellation");

        Instruction[] confirm = SwitchCase(run, confirmValue);
        Instruction[] settlement = SwitchCase(run, settlementValue);
        Instruction[] cancel = SwitchCase(run, cancelValue);
        Instruction[] verifyCancel = SwitchCase(run, verifyCancelValue);

        int confirmWrite = FindLastCall(confirm, ControllerType, "TryExecuteActiveBattleAction");
        int enterSettlement = FindEnumFieldStore(
            confirm,
            "_battleTacticStep",
            settlementValue,
            confirmWrite + 1);
        Assert.True(
            confirmWrite >= 0 && enterSettlement > confirmWrite,
            "A successful confirmation must enter a dedicated settlement phase.");
        Assert.DoesNotContain(
            CallsBetween(confirm, confirmWrite, enterSettlement),
            IsCall(ControllerType, "ClearOwnedDisposable"));

        int settlementOwnershipCheck = FindCall(
            settlement,
            ControllerType,
            "IsOwnedDisposablePreview");
        int settlementIdentityRelease = FindCall(
            settlement,
            ControllerType,
            "ClearOwnedDisposable");
        Assert.True(
            settlementOwnershipCheck >= 0 && settlementIdentityRelease > settlementOwnershipCheck,
            "The owned identity may be cleared only after the settlement query proves that preview is gone.");

        int cancelWrite = FindCall(cancel, ControllerType, "ExecuteWithResult");
        int enterCancelVerification = FindEnumFieldStore(
            cancel,
            "_battleTacticStep",
            verifyCancelValue,
            cancelWrite + 1);
        Assert.True(
            cancelWrite >= 0 && enterCancelVerification > cancelWrite,
            "A submitted cancellation must enter a read-only verification phase.");
        Assert.DoesNotContain(
            CallsBetween(cancel, cancelWrite, enterCancelVerification),
            IsCall(ControllerType, "ClearOwnedDisposable"));

        int cancelVerificationOwnershipCheck = FindCall(
            verifyCancel,
            ControllerType,
            "IsOwnedDisposablePreview");
        int verifiedIdentityRelease = FindCall(
            verifyCancel,
            ControllerType,
            "ClearOwnedDisposable");
        Assert.True(
            cancelVerificationOwnershipCheck >= 0 &&
            verifiedIdentityRelease > cancelVerificationOwnershipCheck,
            "Cancellation verification must retain the interaction identity while the same preview remains.");
    }

    [Fact]
    public void DefenseAttributeStateMachine_PreservesIdentityThroughSettlementAndCancelVerification()
    {
        using AssemblyDefinition assembly = ReadPlugin();
        TypeDefinition controller = RequireType(assembly, ControllerType);
        TypeDefinition steps = RequireNestedType(controller, "DefenseMaintenanceStep");
        MethodDefinition maintain = RequireMethod(controller, "TryMaintainDefense");

        int useValue = EnumValue(steps, "UseExpansionAttributeDisposable");
        int confirmValue = EnumValue(steps, "ConfirmExpansionAttributeDisposable");
        int settlementValue = EnumValue(steps, "WaitForExpansionAttributeSettlement");
        int verifyPlacementValue = EnumValue(steps, "VerifyExpansionAttribute");
        int cleanupValue = EnumValue(steps, "QueryExpansionAttributeCleanup");
        int verifyCleanupValue = EnumValue(steps, "VerifyExpansionAttributeCleanup");

        Instruction[] use = SwitchCase(maintain, useValue);
        Instruction[] confirm = SwitchCase(maintain, confirmValue);
        Instruction[] settlement = SwitchCase(maintain, settlementValue);
        Instruction[] cleanup = SwitchCase(maintain, cleanupValue);
        Instruction[] verifyCleanup = SwitchCase(maintain, verifyCleanupValue);

        int directConfirmationBuilder = FindCall(
            use,
            BattleDecisionEngineType,
            "DecideExpansionDirectConfirmation");
        int deferredConfirmation = FindFieldStore(
            use,
            "_defenseAttributeConfirmAction",
            directConfirmationBuilder + 1);
        int enterConfirmation = FindEnumFieldStore(
            use,
            "_defenseMaintenanceStep",
            confirmValue,
            deferredConfirmation + 1);
        Assert.True(
            directConfirmationBuilder >= 0 &&
            deferredConfirmation > directConfirmationBuilder &&
            enterConfirmation > deferredConfirmation,
            "The preparation tick must save one exact direct confirmation without opening a transient preview.");
        Assert.Equal(-1, FindCall(use, ControllerType, "ExecuteWithResult"));
        Assert.Equal(-1, FindCall(use, BridgeType, "Invoke"));

        int ownershipQuery = FindCall(
            confirm,
            OpeningDefenseInteractionGuardType,
            "Query");
        int ownershipClassification = FindCall(
            confirm,
            RuntimeResultInspectorType,
            "ClassifyReadOnly",
            ownershipQuery + 1);
        int idleProof = FindCall(
            confirm,
            BattleDecisionEngineType,
            "IsCleanDisposableInteractionIdle",
            ownershipClassification + 1);
        int confirmWrite = FindLastCall(confirm, ControllerType, "ExecuteWithResult");
        int refreshedIdentityRead = FindCall(
            confirm,
            BattleDecisionEngineType,
            "ReadExpansionInteractionId",
            confirmWrite + 1);
        int refreshedIdentityStore = FindFieldStore(
            confirm,
            "_defenseAttributeInteractionInstanceId",
            refreshedIdentityRead + 1);
        int enterSettlement = FindEnumFieldStore(
            confirm,
            "_defenseMaintenanceStep",
            settlementValue,
            confirmWrite + 1);
        Assert.True(
            ownershipQuery >= 0 &&
            ownershipClassification > ownershipQuery &&
            idleProof > ownershipClassification &&
            confirmWrite > idleProof &&
            refreshedIdentityRead > confirmWrite &&
            refreshedIdentityStore > refreshedIdentityRead &&
            enterSettlement > confirmWrite,
            "Direct confirmation must prove the executor idle, write once, capture the newly-created preview identity, then settle.");

        int settlementOwnershipCheck = FindCall(
            settlement,
            BattleDecisionEngineType,
            "IsOwnedExpansionPreview");
        int identityRelease = FindFieldStore(
            settlement,
            "_defenseAttributeInteractionInstanceId");
        int enterPlacementVerification = FindEnumFieldStore(
            settlement,
            "_defenseMaintenanceStep",
            verifyPlacementValue,
            identityRelease + 1);
        Assert.True(
            settlementOwnershipCheck >= 0 &&
            identityRelease > settlementOwnershipCheck &&
            enterPlacementVerification > identityRelease,
            "The interaction identity must survive until the read-only settlement check proves the preview exited.");

        int cleanupDecision = FindCall(
            cleanup,
            BattleDecisionEngineType,
            "DecideExpansionCancellation");
        int cleanupWrite = FindCall(cleanup, ControllerType, "ExecuteWithResult");
        int enterCleanupVerification = FindEnumFieldStore(
            cleanup,
            "_defenseMaintenanceStep",
            verifyCleanupValue,
            cleanupWrite + 1);
        Assert.True(
            cleanupDecision >= 0 && cleanupWrite > cleanupDecision &&
            enterCleanupVerification > cleanupWrite,
            "Cleanup must re-check ownership, submit once, then enter verification.");
        Assert.DoesNotContain(
            cleanup.Skip(cleanupWrite + 1).Take(enterCleanupVerification - cleanupWrite - 1),
            IsFieldStore("_defenseAttributeInteractionInstanceId"));

        int cleanupOwnershipCheck = FindCall(
            verifyCleanup,
            BattleDecisionEngineType,
            "IsOwnedExpansionPreview");
        int finishCleanup = FindCall(
            verifyCleanup,
            ControllerType,
            "FinishDefenseMaintenance");
        Assert.True(
            cleanupOwnershipCheck >= 0 && finishCleanup > cleanupOwnershipCheck,
            "The cleanup verifier must retain identity until it proves that the owned preview no longer remains.");
    }

    [Fact]
    public void FailedOwnedPreviewRelease_RequiresRestartOnlyAfterAnUncertainCancellation()
    {
        using AssemblyDefinition assembly = ReadPlugin();
        TypeDefinition controller = RequireType(assembly, ControllerType);
        MethodDefinition fail = RequireMethod(controller, "FailOwnedPreviewRelease");
        Instruction[] instructions = fail.Body.Instructions.ToArray();

        int uncertaintyRead = FindFieldLoad(
            instructions,
            "_ownedPreviewReleaseCancellationOutcomeUncertain");
        int requireRestart = FindCall(
            instructions,
            ControllerType,
            "RequireProcessRestart");
        int guardingBranch = FindConditionalBranchThatSkips(
            instructions,
            startIndex: uncertaintyRead + 1,
            skippedIndex: requireRestart);

        Assert.True(uncertaintyRead >= 0, "The release failure policy must read cancellation uncertainty.");
        Assert.True(requireRestart > uncertaintyRead, "Restart may be considered only after that read.");
        Assert.True(
            guardingBranch >= 0 && guardingBranch < requireRestart,
            "A conditional branch must bypass RequireProcessRestart when no write outcome is uncertain.");
        Assert.DoesNotContain(
            Calls(fail),
            IsCall(ControllerType, "FaultRequiringProcessRestart"));
    }

    [Fact]
    public void OpeningDefense_UsesOnlyIncrementalReconciliationAndNeverCallsLegacyMacro()
    {
        using AssemblyDefinition assembly = ReadPlugin();
        TypeDefinition controller = RequireType(assembly, ControllerType);
        MethodDefinition prepare = RequireMethod(controller, "PrepareOpeningDefenseIncrementally");
        Instruction[] instructions = prepare.Body.Instructions.ToArray();

        Assert.DoesNotContain(LoadedStrings(prepare), value => value == "prepareDefaultDefense");
        Assert.DoesNotContain(
            Calls(prepare),
            IsCall(RuntimeResultInspectorType, "IsRecoverableDefaultDefenseCheckpoint"));
        Assert.DoesNotContain(
            Calls(prepare),
            IsCall(RuntimeResultInspectorType, "IsRetryableDefaultDefenseFailure"));
        Assert.Contains(
            Calls(prepare),
            IsCall(ControllerType, "ExecuteOpeningDefenseReadOnly"));
        Assert.Contains(
            Calls(prepare),
            IsCall(OpeningDefensePlannerType, "Observe"));
    }

    [Fact]
    public void MissingBattlePreviewIdentity_HardFaultsWithoutAnonymousCancellation()
    {
        using AssemblyDefinition assembly = ReadPlugin();
        TypeDefinition controller = RequireType(assembly, ControllerType);
        TypeDefinition steps = RequireNestedType(controller, "BattleTacticStep");
        MethodDefinition run = RequireMethod(controller, "RunBattleTacticStep");
        Instruction[] use = SwitchCase(run, EnumValue(steps, "UseDisposable"));

        int identityStore = FindFieldStore(use, "_ownedDisposableInteractionInstanceId");
        int hardFault = FindCall(
            use,
            ControllerType,
            "FaultRequiringProcessRestart",
            identityStore + 1);
        int enterOwnedPreviewQuery = FindEnumFieldStore(
            use,
            "_battleTacticStep",
            EnumValue(steps, "QueryDisposablePreview"),
            hardFault + 1);

        Assert.True(
            identityStore >= 0 && hardFault > identityStore,
            "A successful use result without an interaction identity must stop with a hard fault.");
        Assert.True(
            enterOwnedPreviewQuery > hardFault,
            "Only the nonzero-identity path may continue into owned-preview handling.");
        Assert.DoesNotContain(LoadedStrings(use), value => value == "cancelDisposable");
        Assert.DoesNotContain(
            Calls(use),
            IsCall(ControllerType, "Execute"));
    }

    [Fact]
    public void DefenseExpansion_DoesNotDispatchTransientUseDisposablePreview()
    {
        using AssemblyDefinition assembly = ReadPlugin();
        TypeDefinition controller = RequireType(assembly, ControllerType);
        TypeDefinition steps = RequireNestedType(controller, "DefenseMaintenanceStep");
        MethodDefinition maintain = RequireMethod(controller, "TryMaintainDefense");
        Instruction[] use = SwitchCase(
            maintain,
            EnumValue(steps, "UseExpansionAttributeDisposable"));

        int confirmationBuilder = FindCall(
            use,
            BattleDecisionEngineType,
            "DecideExpansionDirectConfirmation");

        Assert.True(confirmationBuilder >= 0);
        Assert.Equal(-1, FindCall(use, ControllerType, "ExecuteWithResult"));
        Assert.Equal(-1, FindCall(use, BattleDecisionEngineType, "ReadExpansionInteractionId"));
        Assert.DoesNotContain(LoadedStrings(use), value => value == "useDisposable");
    }

    [Fact]
    public void PendingBattleConfirmation_EntersSettlementInsteadOfCancellation()
    {
        using AssemblyDefinition assembly = ReadPlugin();
        TypeDefinition controller = RequireType(assembly, ControllerType);
        TypeDefinition steps = RequireNestedType(controller, "BattleTacticStep");
        MethodDefinition run = RequireMethod(controller, "RunBattleTacticStep");
        Instruction[] confirm = SwitchCase(run, EnumValue(steps, "ConfirmDisposable"));

        int classify = FindCall(confirm, RuntimeResultInspectorType, "Classify");
        int enterSettlement = FindEnumFieldStore(
            confirm,
            "_battleTacticStep",
            EnumValue(steps, "WaitForDisposableSettlement"),
            classify + 1);
        int enterCancellation = FindEnumFieldStore(
            confirm,
            "_battleTacticStep",
            EnumValue(steps, "CancelDisposable"),
            enterSettlement + 1);
        int uncertaintyStore = FindFieldStore(
            confirm,
            "_ownedPreviewConfirmationOutcomeUncertain",
            classify + 1);
        int settlementDecision = FindPendingDispositionDecision(
            confirm,
            uncertaintyStore + 1,
            enterSettlement,
            enterCancellation);

        Assert.True(classify >= 0 && enterSettlement > classify);
        Assert.True(
            uncertaintyStore > classify &&
            settlementDecision > uncertaintyStore,
            "RuntimeResultDisposition.Pending must feed the accepted-or-pending decision that enters settlement observation.");
        Assert.True(
            enterCancellation > enterSettlement,
            "Only a disposition other than accepted or Pending may enter cancellation.");
        Assert.DoesNotContain(
            CallsBetween(confirm, settlementDecision, enterSettlement),
            IsCall(ControllerType, "ClearOwnedDisposable"));
    }

    [Fact]
    public void BattleConfirm_ReadOnlyOwnershipFailure_PreservesIdentityAndDoesNotWrite()
    {
        using AssemblyDefinition assembly = ReadPlugin();
        TypeDefinition controller = RequireType(assembly, ControllerType);
        TypeDefinition steps = RequireNestedType(controller, "BattleTacticStep");
        MethodDefinition run = RequireMethod(controller, "RunBattleTacticStep");
        Instruction[] confirm = SwitchCase(run, EnumValue(steps, "ConfirmDisposable"));
        int ownershipQuery = FindCall(
            confirm,
            OpeningDefenseInteractionGuardType,
            "Query");
        int classification = FindCall(
            confirm,
            RuntimeResultInspectorType,
            "ClassifyReadOnly",
            ownershipQuery + 1);
        Instruction[] failurePath = ReadOnlyQueryFailurePath(
            confirm,
            OpeningDefenseInteractionGuardType,
            "Query");

        Assert.True(ownershipQuery >= 0 && classification > ownershipQuery);
        Assert.Contains(
            failurePath,
            IsFieldStore("_battlePendingAction"));
        AssertBattleReadOnlyFailurePreservesOwnedPreview(failurePath);
    }

    [Fact]
    public void BattleCancel_ReadOnlyOwnershipFailure_PreservesIdentityAndDoesNotWrite()
    {
        using AssemblyDefinition assembly = ReadPlugin();
        TypeDefinition controller = RequireType(assembly, ControllerType);
        TypeDefinition steps = RequireNestedType(controller, "BattleTacticStep");
        MethodDefinition run = RequireMethod(controller, "RunBattleTacticStep");
        Instruction[] cancel = SwitchCase(run, EnumValue(steps, "CancelDisposable"));
        Instruction[] failurePath = ReadOnlyQueryFailurePath(cancel);

        AssertBattleReadOnlyFailurePreservesOwnedPreview(failurePath);
    }

    [Fact]
    public void WaveEnd_WithOwnedPreview_DefersBattleResetAndPreservesIdentity()
    {
        using AssemblyDefinition assembly = ReadPlugin();
        TypeDefinition controller = RequireType(assembly, ControllerType);
        MethodDefinition transition = RequireMethod(controller, "ObserveWaveTransition");
        Instruction[] instructions = transition.Body.Instructions.ToArray();

        int enumIdentityRead = FindFieldLoad(instructions, "_ownedDisposableEnum");
        int interactionIdentityRead = FindFieldLoad(
            instructions,
            "_ownedDisposableInteractionInstanceId");
        int deferredRelease = FindFieldStore(
            instructions,
            "_battleWaveEndPendingPreviewRelease");
        int resetAfterDeferral = FindCall(
            instructions,
            ControllerType,
            "ResetBattleTactics",
            deferredRelease + 1);
        int earlyReturn = FindReturnExit(
            instructions,
            deferredRelease + 1,
            resetAfterDeferral);
        int markWaveComplete = FindFieldStore(instructions, "_wasInWave", deferredRelease + 1);

        Assert.True(
            enumIdentityRead >= 0 && interactionIdentityRead > enumIdentityRead &&
            deferredRelease > interactionIdentityRead,
            "The wave-end branch must be gated by both pieces of the owned battle-preview identity.");
        Assert.True(
            earlyReturn > deferredRelease && resetAfterDeferral > earlyReturn,
            "The owned-preview branch must return before ResetBattleTactics clears its identity.");
        Assert.True(
            markWaveComplete > earlyReturn,
            "The wave may be marked complete only after preview release is proven.");
        Assert.DoesNotContain(
            CallsBetween(instructions, deferredRelease, earlyReturn),
            IsCall(ControllerType, "ClearOwnedDisposable"));
        Assert.DoesNotContain(
            instructions.Skip(deferredRelease + 1).Take(earlyReturn - deferredRelease - 1),
            IsFieldStore("_ownedDisposableInteractionInstanceId"));
    }

    [Fact]
    public void AlreadyIssuedCancellation_ForcesVerificationAndCannotReplayTheWrite()
    {
        using AssemblyDefinition assembly = ReadPlugin();
        TypeDefinition controller = RequireType(assembly, ControllerType);
        TypeDefinition releaseSteps = RequireNestedType(controller, "OwnedPreviewReleaseStep");
        int verifyReleasedValue = EnumValue(releaseSteps, "VerifyReleased");
        MethodDefinition begin = RequireMethod(controller, "BeginOwnedPreviewRelease");
        Instruction[] beginInstructions = begin.Body.Instructions.ToArray();

        int issuedRead = FindFieldLoad(
            beginInstructions,
            "_ownedPreviewCancellationAlreadyIssued");
        int issuedBranch = FindInstruction(
            beginInstructions,
            instruction => instruction.OpCode.FlowControl == FlowControl.Cond_Branch &&
                           instruction.Operand is Instruction target &&
                           TryReadInt32(target, out int value) &&
                           value == verifyReleasedValue,
            issuedRead + 1);
        int releaseStepStore = FindFieldStore(
            beginInstructions,
            "_ownedPreviewReleaseStep",
            issuedBranch + 1);

        Assert.True(
            issuedRead >= 0 && issuedBranch > issuedRead && releaseStepStore > issuedBranch,
            "An already-issued cancellation must select VerifyReleased instead of QueryOwnership.");

        MethodDefinition process = RequireMethod(controller, "ProcessOwnedPreviewRelease");
        Instruction[] verifyCase = SwitchCase(process, verifyReleasedValue);
        Assert.Contains(
            Calls(verifyCase),
            IsCall(ControllerType, "TryQueryOwnedPreviewForRelease"));
        Assert.DoesNotContain(
            Calls(verifyCase),
            IsCall(ControllerType, "BuildOwnedPreviewCancellation"));
        Assert.DoesNotContain(
            Calls(verifyCase),
            IsCall(ControllerType, "ExecuteOwnedPreviewCancellation"));

        MethodDefinition resetRelease = RequireMethod(controller, "ResetOwnedPreviewReleaseState");
        Assert.DoesNotContain(
            resetRelease.Body.Instructions,
            IsFieldStore("_ownedPreviewCancellationAlreadyIssued"));
    }

    [Fact]
    public void ProvenSuccessfulCancellation_CanClearOutcomeUncertainty()
    {
        using AssemblyDefinition plugin = ReadPlugin();
        using AssemblyDefinition core = ReadCore();
        TypeDefinition controller = RequireType(plugin, ControllerType);
        TypeDefinition dispositions = RequireType(core, RuntimeResultDispositionType);
        MethodDefinition observe = RequireMethod(
            controller,
            "ObserveOwnedPreviewCancellationResult");
        Instruction[] success = SwitchCase(observe, EnumValue(dispositions, "Success"));

        int booleanValue = FindGenericCall(
            success,
            "Newtonsoft.Json.Linq.Extensions",
            "Value",
            "System.Boolean");
        Assert.Contains(LoadedStrings(success), value => value == "isInPreview");
        Assert.Contains(
            Calls(success),
            IsCall("Newtonsoft.Json.Linq.JToken", "get_Type"));
        Assert.True(
            booleanValue >= 0,
            "A successful result must read the proven boolean isInPreview value.");

        Instruction valueBranch = NextMeaningfulInstruction(success, booleanValue);
        Assert.True(valueBranch.OpCode.FlowControl == FlowControl.Branch);
        Assert.True(
            valueBranch.Operand is Instruction valueSink &&
            valueSink.OpCode.Code is Code.Stloc or Code.Stloc_0 or Code.Stloc_1 or
                Code.Stloc_2 or Code.Stloc_3 or Code.Stloc_S,
            "The proven isInPreview value must flow directly into the uncertainty result, allowing false.");
        Assert.Contains(
            observe.Body.Instructions,
            IsFieldStore("_ownedPreviewReleaseCancellationOutcomeUncertain"));
    }

    [Fact]
    public void FailedOwnedPreviewRelease_DistinguishesDeterminateSentCancelFromNoCancel()
    {
        using AssemblyDefinition assembly = ReadPlugin();
        TypeDefinition controller = RequireType(assembly, ControllerType);
        MethodDefinition fail = RequireMethod(controller, "FailOwnedPreviewRelease");
        Instruction[] instructions = fail.Body.Instructions.ToArray();
        string[] messages = LoadedStrings(instructions).ToArray();

        int issuedRead = FindFieldLoad(
            instructions,
            "_ownedPreviewCancellationAlreadyIssued");
        int issuedMessageBranch = FindInstruction(
            instructions,
            instruction => instruction.OpCode.FlowControl == FlowControl.Cond_Branch,
            issuedRead + 1);

        Assert.True(
            issuedRead >= 0 && issuedMessageBranch > issuedRead,
            "The recoverable failure message must branch on whether a cancellation was already issued.");
        Assert.Contains(
            messages,
            value => value.Contains("尚未发送", StringComparison.Ordinal) &&
                     value.Contains("取消", StringComparison.Ordinal));
        Assert.Contains(
            messages,
            value => value.Contains("取消", StringComparison.Ordinal) &&
                     value.Contains("结果", StringComparison.Ordinal) &&
                     (value.Contains("已发送", StringComparison.Ordinal) ||
                      value.Contains("发送过", StringComparison.Ordinal) ||
                     value.Contains("已经", StringComparison.Ordinal)));
    }

    [Fact]
    public void SoftPreviewCleanupFailure_RetainsIdentityAndBlocksCheatEnable()
    {
        using AssemblyDefinition assembly = ReadPlugin();
        TypeDefinition controller = RequireType(assembly, ControllerType);
        MethodDefinition fail = RequireMethod(controller, "FailOwnedPreviewRelease");

        Assert.Contains(
            Calls(fail),
            IsCall(ControllerType, "ResetOwnedPreviewReleaseState"));
        Assert.DoesNotContain(
            Calls(fail),
            call => call.DeclaringType.FullName == ControllerType &&
                    call.Name is "ClearOwnedDisposable" or "ResetBattleTactics" or
                        "ResetOpeningDefensePreparation" or "ClearDefenseAttributePlacementState");
        foreach (string identityField in new[]
                 {
                     "_ownedDisposableEnum",
                     "_ownedDisposableInteractionInstanceId",
                     "_openingDefenseInteractionInstanceId",
                     "_defenseAttributeInteractionInstanceId"
                 })
        {
            Assert.DoesNotContain(fail.Body.Instructions, IsFieldStore(identityField));
        }

        MethodDefinition setCheat = RequireMethod(controller, "TrySetCheatMode");
        Instruction[] instructions = setCheat.Body.Instructions.ToArray();
        int restartGate = FindFieldLoad(instructions, "_needsProcessRestart");
        int retainedIdentityGate = FindCall(
            instructions,
            ControllerType,
            "HasOwnedAutomationPreviewIdentity");
        int beginRelease = FindCall(
            instructions,
            ControllerType,
            "BeginOwnedPreviewRelease",
            retainedIdentityGate + 1);
        int rejectExit = FindInstruction(
            instructions,
            instruction => instruction.OpCode.Code is Code.Leave or Code.Leave_S or Code.Ret,
            beginRelease + 1);
        int enableStore = FindFieldStore(
            instructions,
            "_cheatModeEnabled",
            rejectExit + 1);

        Assert.True(
            restartGate >= 0 && retainedIdentityGate > restartGate,
            "Retained identity must be checked independently after the hard-restart gate, including soft faults.");
        Assert.True(
            beginRelease > retainedIdentityGate && rejectExit > beginRelease &&
            enableStore > rejectExit,
            "Cheat enable must re-enter owned-preview cleanup and return false before changing cheat mode.");
        Assert.DoesNotContain(
            instructions.Skip(retainedIdentityGate + 1).Take(rejectExit - retainedIdentityGate - 1),
            IsFieldStore("_cheatModeEnabled"));
    }

    [Fact]
    public void CheatController_CannotBypassAutoPlayPreviewSafetyGate()
    {
        using AssemblyDefinition assembly = ReadPlugin();
        TypeDefinition cheatController = RequireType(assembly, CheatControllerType);
        MethodDefinition setEnabled = RequireMethod(cheatController, "SetEnabled");
        Instruction[] instructions = setEnabled.Body.Instructions.ToArray();

        int processSafetyGate = FindCall(
            instructions,
            "Loopstructor.AutoPlayer.Core.AutoPlayerSafetyGate",
            "IsReady");
        int previewSafetyGate = FindCall(
            instructions,
            ControllerType,
            "TrySetCheatMode");
        int enabledMutation = FindCall(
            instructions,
            CheatControllerType,
            "set_Enabled",
            previewSafetyGate + 1);
        int rejectionReturn = FindReturnExit(
            instructions,
            previewSafetyGate + 1,
            enabledMutation);

        Assert.True(
            processSafetyGate >= 0 && previewSafetyGate > processSafetyGate,
            "Cheat enable must pass both the process-isolation and AutoPlayer preview gates.");
        Assert.True(
            rejectionReturn > previewSafetyGate && enabledMutation > rejectionReturn,
            "A rejected AutoPlayer gate must return before CheatController mutates Enabled.");
    }

    [Fact]
    public void PendingPreviewConfirmations_RemainUncertainUntilReadOnlySettlementProof()
    {
        using AssemblyDefinition assembly = ReadPlugin();
        TypeDefinition controller = RequireType(assembly, ControllerType);
        FieldDefinition uncertainty = controller.Fields.Single(
            field => field.Name == "_ownedPreviewConfirmationOutcomeUncertain");
        Assert.Equal("System.Boolean", uncertainty.FieldType.FullName);

        TypeDefinition battleSteps = RequireNestedType(controller, "BattleTacticStep");
        MethodDefinition battle = RequireMethod(controller, "RunBattleTacticStep");
        Instruction[] battleConfirm = SwitchCase(
            battle,
            EnumValue(battleSteps, "ConfirmDisposable"));
        Instruction[] battleSettlement = SwitchCase(
            battle,
            EnumValue(battleSteps, "WaitForDisposableSettlement"));
        AssertPendingDispositionFeedsUncertainty(battleConfirm);
        AssertReadOnlyProofClearsUncertainty(
            battleSettlement,
            ControllerType,
            "IsOwnedDisposablePreview");

        TypeDefinition defenseSteps = RequireNestedType(controller, "DefenseMaintenanceStep");
        MethodDefinition defense = RequireMethod(controller, "TryMaintainDefense");
        Instruction[] defenseConfirm = SwitchCase(
            defense,
            EnumValue(defenseSteps, "ConfirmExpansionAttributeDisposable"));
        Instruction[] defenseSettlement = SwitchCase(
            defense,
            EnumValue(defenseSteps, "WaitForExpansionAttributeSettlement"));
        AssertPendingDispositionFeedsUncertainty(defenseConfirm);
        AssertReadOnlyProofClearsUncertainty(
            defenseSettlement,
            BattleDecisionEngineType,
            "IsOwnedExpansionPreview");

        MethodDefinition fail = RequireMethod(controller, "FailOwnedPreviewRelease");
        int failUncertaintyRead = FindFieldLoad(
            fail.Body.Instructions.ToArray(),
            "_ownedPreviewConfirmationOutcomeUncertain");
        int requireRestart = FindCall(
            fail.Body.Instructions.ToArray(),
            ControllerType,
            "RequireProcessRestart");
        Assert.True(
            failUncertaintyRead >= 0 && requireRestart > failUncertaintyRead,
            "Unresolved confirmation writes must participate in the hard-restart decision.");

        MethodDefinition reset = RequireMethod(controller, "ResetOwnedPreviewCancellationTracking");
        Assert.Contains(
            reset.Body.Instructions,
            instruction => IsFieldStore("_ownedPreviewConfirmationOutcomeUncertain")(instruction) &&
                           PreviousMeaningfulInstruction(
                               reset.Body.Instructions.ToArray(),
                               reset.Body.Instructions.IndexOf(instruction)) is { } valueLoad &&
                           TryReadInt32(valueLoad, out int value) && value == 0);
    }

    private static AssemblyDefinition ReadPlugin()
    {
        string path = Path.Combine(AppContext.BaseDirectory, "Loopstructor.AutoPlayer.Plugin.dll");
        Assert.True(File.Exists(path), "Plugin assembly was not copied to the test output: " + path);
        return AssemblyDefinition.ReadAssembly(path);
    }

    private static AssemblyDefinition ReadCore()
    {
        string path = Path.Combine(AppContext.BaseDirectory, "Loopstructor.AutoPlayer.Core.dll");
        Assert.True(File.Exists(path), "Core assembly was not copied to the test output: " + path);
        return AssemblyDefinition.ReadAssembly(path);
    }

    private static TypeDefinition RequireType(AssemblyDefinition assembly, string fullName) =>
        assembly.MainModule.Types.Single(type => type.FullName == fullName);

    private static TypeDefinition RequireNestedType(TypeDefinition type, string name) =>
        type.NestedTypes.Single(nested => nested.Name == name);

    private static MethodDefinition RequireMethod(TypeDefinition type, string name) =>
        type.Methods.Single(method => method.Name == name);

    private static int EnumValue(TypeDefinition enumType, string name) =>
        Convert.ToInt32(enumType.Fields.Single(field => field.Name == name).Constant);

    private static IEnumerable<MethodReference> Calls(MethodDefinition method) =>
        method.Body.Instructions
            .Where(instruction => instruction.OpCode.Code is Code.Call or Code.Callvirt)
            .Select(instruction => instruction.Operand)
            .OfType<MethodReference>();

    private static IEnumerable<MethodReference> Calls(IReadOnlyList<Instruction> instructions) =>
        instructions
            .Where(instruction => instruction.OpCode.Code is Code.Call or Code.Callvirt)
            .Select(instruction => instruction.Operand)
            .OfType<MethodReference>();

    private static IEnumerable<MethodReference> CallsBetween(
        IReadOnlyList<Instruction> instructions,
        int exclusiveStart,
        int exclusiveEnd) =>
        instructions.Skip(exclusiveStart + 1)
            .Take(exclusiveEnd - exclusiveStart - 1)
            .Where(instruction => instruction.OpCode.Code is Code.Call or Code.Callvirt)
            .Select(instruction => instruction.Operand)
            .OfType<MethodReference>();

    private static IEnumerable<string> LoadedStrings(MethodDefinition method) =>
        method.Body.Instructions
            .Where(instruction => instruction.OpCode.Code == Code.Ldstr)
            .Select(instruction => instruction.Operand)
            .OfType<string>();

    private static IEnumerable<string> LoadedStrings(IReadOnlyList<Instruction> instructions) =>
        instructions
            .Where(instruction => instruction.OpCode.Code == Code.Ldstr)
            .Select(instruction => instruction.Operand)
            .OfType<string>();

    private static Predicate<MethodReference> IsCall(string declaringType, string methodName) =>
        call => call.DeclaringType.FullName == declaringType && call.Name == methodName;

    private static Predicate<Instruction> IsFieldStore(string fieldName) =>
        instruction => instruction.OpCode.Code == Code.Stfld &&
                       instruction.Operand is FieldReference field &&
                       field.Name == fieldName;

    private static int[] FindCallIndices(
        IReadOnlyList<Instruction> instructions,
        string declaringType,
        string methodName) =>
        instructions.Select((instruction, index) => (instruction, index))
            .Where(item => item.instruction.Operand is MethodReference call &&
                           call.DeclaringType.FullName == declaringType &&
                           call.Name == methodName)
            .Select(item => item.index)
            .ToArray();

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

    private static int FindLastCall(
        IReadOnlyList<Instruction> instructions,
        string declaringType,
        string methodName)
    {
        for (int index = instructions.Count - 1; index >= 0; index--)
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

    private static int FindGenericCall(
        IReadOnlyList<Instruction> instructions,
        string declaringType,
        string methodName,
        string genericArgument)
    {
        for (int index = 0; index < instructions.Count; index++)
        {
            if (instructions[index].Operand is GenericInstanceMethod call &&
                call.DeclaringType.FullName == declaringType &&
                call.Name == methodName &&
                call.GenericArguments.Any(argument => argument.FullName == genericArgument))
            {
                return index;
            }
        }

        return -1;
    }

    private static int FindInstruction(
        IReadOnlyList<Instruction> instructions,
        Predicate<Instruction> predicate,
        int startIndex = 0)
    {
        for (int index = Math.Max(0, startIndex); index < instructions.Count; index++)
        {
            if (predicate(instructions[index])) return index;
        }

        return -1;
    }

    private static int FindFieldLoad(
        IReadOnlyList<Instruction> instructions,
        string fieldName)
    {
        for (int index = 0; index < instructions.Count; index++)
        {
            if (instructions[index].OpCode.Code == Code.Ldfld &&
                instructions[index].Operand is FieldReference field &&
                field.Name == fieldName)
            {
                return index;
            }
        }

        return -1;
    }

    private static int FindFieldStore(
        IReadOnlyList<Instruction> instructions,
        string fieldName,
        int startIndex = 0)
    {
        for (int index = Math.Max(0, startIndex); index < instructions.Count; index++)
        {
            if (instructions[index].OpCode.Code == Code.Stfld &&
                instructions[index].Operand is FieldReference field &&
                field.Name == fieldName)
            {
                return index;
            }
        }

        return -1;
    }

    private static int FindEnumFieldStore(
        IReadOnlyList<Instruction> instructions,
        string fieldName,
        int expectedValue,
        int startIndex = 0)
    {
        for (int index = Math.Max(0, startIndex); index < instructions.Count; index++)
        {
            if (instructions[index].OpCode.Code != Code.Stfld ||
                instructions[index].Operand is not FieldReference field ||
                field.Name != fieldName)
            {
                continue;
            }

            Instruction? valueLoad = PreviousMeaningfulInstruction(instructions, index);
            if (valueLoad != null && TryReadInt32(valueLoad, out int value) && value == expectedValue)
            {
                return index;
            }
        }

        return -1;
    }

    private static Instruction[] SwitchCase(MethodDefinition method, int enumValue)
    {
        Instruction[] instructions = method.Body.Instructions.ToArray();
        Instruction switchInstruction = instructions.Single(
            instruction => instruction.OpCode.Code == Code.Switch);
        Instruction[] targets = (Instruction[])switchInstruction.Operand;
        Assert.InRange(enumValue, 0, targets.Length - 1);

        int start = Array.IndexOf(instructions, targets[enumValue]);
        int[] targetIndices = targets.Select(target => Array.IndexOf(instructions, target)).ToArray();
        int end = targetIndices.Where(index => index > start).DefaultIfEmpty(instructions.Length).Min();
        return instructions.Skip(start).Take(end - start).ToArray();
    }

    private static Instruction[] ReadOnlyQueryFailurePath(
        Instruction[] stateMachineCase,
        string queryType = ControllerType,
        string queryMethod = "TryInvokeOptionalReadOnly")
    {
        int query = FindCall(
            stateMachineCase,
            queryType,
            queryMethod);
        int successBranch = FindInstruction(
            stateMachineCase,
            instruction =>
                instruction.OpCode.FlowControl == FlowControl.Cond_Branch &&
                instruction.Operand is Instruction target &&
                IndexOf(stateMachineCase, target) > query,
            query + 1);
        int successStart = successBranch < 0 ||
                           stateMachineCase[successBranch].Operand is not Instruction successTarget
            ? -1
            : IndexOf(stateMachineCase, successTarget);
        Instruction? failureExit = successStart < 0
            ? null
            : PreviousMeaningfulInstruction(stateMachineCase, successStart);

        Assert.True(
            query >= 0 && successBranch > query && successStart > successBranch,
            "Expected a forward control-flow guard around the read-only failure handling.");
        Assert.True(
            failureExit != null && IsReturnExit(failureExit),
            "The read-only failure path must leave the state-machine case without falling through to a write.");
        return stateMachineCase.Skip(query).Take(successStart - query).ToArray();
    }

    private static void AssertBattleReadOnlyFailurePreservesOwnedPreview(
        Instruction[] failurePath)
    {
        Assert.DoesNotContain(failurePath, IsFieldStore("_battleTacticStep"));
        Assert.DoesNotContain(failurePath, IsFieldStore("_ownedDisposableEnum"));
        Assert.DoesNotContain(
            failurePath,
            IsFieldStore("_ownedDisposableInteractionInstanceId"));
        Assert.DoesNotContain(
            Calls(failurePath),
            IsCall(ControllerType, "ClearOwnedDisposable"));
        Assert.DoesNotContain(
            Calls(failurePath),
            call => call.DeclaringType.FullName == ControllerType &&
                    call.Name is "TryExecuteActiveBattleAction" or "ExecuteWithResult" or
                        "Execute" or "MarkOwnedPreviewCancellationIssued");
        Assert.DoesNotContain(
            LoadedStrings(failurePath),
            value => value == "cancelDisposable");
    }

    private static void AssertPendingDispositionFeedsUncertainty(Instruction[] stateMachineCase)
    {
        int classify = FindCall(
            stateMachineCase,
            RuntimeResultInspectorType,
            "Classify");
        int uncertaintyStore = FindFieldStore(
            stateMachineCase,
            "_ownedPreviewConfirmationOutcomeUncertain",
            classify + 1);
        Instruction? equality = PreviousMeaningfulInstruction(stateMachineCase, uncertaintyStore);
        int equalityIndex = equality == null ? -1 : Array.IndexOf(stateMachineCase, equality);
        Instruction? pendingValue = equalityIndex < 0
            ? null
            : PreviousMeaningfulInstruction(stateMachineCase, equalityIndex);

        Assert.True(classify >= 0 && uncertaintyStore > classify);
        Assert.NotNull(equality);
        Assert.Equal(Code.Ceq, equality!.OpCode.Code);
        Assert.NotNull(pendingValue);
        Assert.True(
            TryReadInt32(pendingValue!, out int value) && value == 1,
            "RuntimeResultDisposition.Pending must set confirmation uncertainty.");
    }

    private static void AssertReadOnlyProofClearsUncertainty(
        Instruction[] settlementCase,
        string ownershipType,
        string ownershipMethod)
    {
        int ownershipProof = FindCall(
            settlementCase,
            ownershipType,
            ownershipMethod);
        int uncertaintyClear = FindFieldStore(
            settlementCase,
            "_ownedPreviewConfirmationOutcomeUncertain",
            ownershipProof + 1);
        Instruction? clearValue = PreviousMeaningfulInstruction(
            settlementCase,
            uncertaintyClear);

        Assert.True(
            ownershipProof >= 0 && uncertaintyClear > ownershipProof,
            "Confirmation uncertainty may clear only after the read-only ownership proof.");
        Assert.NotNull(clearValue);
        Assert.True(
            TryReadInt32(clearValue!, out int value) && value == 0,
            "The successful settlement proof must clear confirmation uncertainty.");
    }

    private static int FindConditionalBranchThatSkips(
        IReadOnlyList<Instruction> instructions,
        int startIndex,
        int skippedIndex)
    {
        for (int index = Math.Max(0, startIndex); index < skippedIndex; index++)
        {
            Instruction instruction = instructions[index];
            if (instruction.OpCode.FlowControl != FlowControl.Cond_Branch ||
                instruction.Operand is not Instruction target)
            {
                continue;
            }

            if (IndexOf(instructions, target) > skippedIndex)
            {
                return index;
            }
        }

        return -1;
    }

    private static int FindPendingDispositionDecision(
        IReadOnlyList<Instruction> instructions,
        int startIndex,
        int settlementIndex,
        int cancellationIndex)
    {
        const int pendingDispositionValue = 1;
        int pendingEquality = -1;
        for (int index = Math.Max(0, startIndex);
             index < Math.Min(settlementIndex, instructions.Count);
             index++)
        {
            Instruction instruction = instructions[index];
            if (instruction.OpCode.Code == Code.Ceq)
            {
                Instruction? equalityValue = PreviousMeaningfulInstruction(instructions, index);
                if (equalityValue != null &&
                    TryReadInt32(equalityValue, out int equalityExpected) &&
                    equalityExpected == pendingDispositionValue)
                {
                    pendingEquality = index;
                }
            }

            if (instruction.OpCode.FlowControl != FlowControl.Cond_Branch ||
                instruction.Operand is not Instruction target)
            {
                continue;
            }

            int targetIndex = IndexOf(instructions, target);
            Instruction? comparison = PreviousMeaningfulInstruction(instructions, index);
            Instruction? pendingValue = comparison;
            if (comparison?.OpCode.Code == Code.Ceq)
            {
                int comparisonIndex = IndexOf(instructions, comparison);
                pendingValue = comparisonIndex < 0
                    ? null
                    : PreviousMeaningfulInstruction(instructions, comparisonIndex);
            }

            if (pendingValue == null ||
                !TryReadInt32(pendingValue, out int value) ||
                value != pendingDispositionValue)
            {
                continue;
            }

            bool pendingFallsThroughToSettlement =
                instruction.OpCode.Code is Code.Bne_Un or Code.Bne_Un_S or Code.Brfalse or Code.Brfalse_S &&
                targetIndex > settlementIndex &&
                targetIndex <= cancellationIndex;
            bool pendingBranchesToSettlement =
                instruction.OpCode.Code is Code.Beq or Code.Beq_S or Code.Brtrue or Code.Brtrue_S &&
                targetIndex <= settlementIndex;
            if (pendingFallsThroughToSettlement || pendingBranchesToSettlement)
            {
                return index;
            }
        }

        if (pendingEquality >= 0)
        {
            for (int index = pendingEquality + 1;
                 index < Math.Min(settlementIndex, instructions.Count);
                 index++)
            {
                Instruction branch = instructions[index];
                if (branch.OpCode.FlowControl == FlowControl.Cond_Branch &&
                    branch.Operand is Instruction target)
                {
                    int targetIndex = IndexOf(instructions, target);
                    if (targetIndex > settlementIndex && targetIndex <= cancellationIndex)
                    {
                        return index;
                    }
                }
            }
        }

        return -1;
    }

    private static int FindReturnExit(
        IReadOnlyList<Instruction> instructions,
        int startIndex,
        int endIndex)
    {
        for (int index = Math.Max(0, startIndex);
             index < Math.Min(endIndex, instructions.Count);
             index++)
        {
            if (IsReturnExit(instructions[index])) return index;
        }

        return -1;
    }

    private static bool IsReturnExit(Instruction instruction)
    {
        if (instruction.OpCode.Code == Code.Ret) return true;
        if (instruction.OpCode.Code is not (Code.Br or Code.Br_S or Code.Leave or Code.Leave_S) ||
            instruction.Operand is not Instruction target)
        {
            return false;
        }

        var visited = new HashSet<Instruction>();
        Instruction? current = target;
        while (current != null && visited.Add(current))
        {
            switch (current.OpCode.Code)
            {
                case Code.Ret:
                    return true;
                case Code.Br:
                case Code.Br_S:
                case Code.Leave:
                case Code.Leave_S:
                    current = current.Operand as Instruction;
                    continue;
                case Code.Nop:
                case Code.Ldloc:
                case Code.Ldloc_S:
                case Code.Ldloc_0:
                case Code.Ldloc_1:
                case Code.Ldloc_2:
                case Code.Ldloc_3:
                case Code.Ldc_I4_M1:
                case Code.Ldc_I4_0:
                case Code.Ldc_I4_1:
                case Code.Ldc_I4_2:
                case Code.Ldc_I4_3:
                case Code.Ldc_I4_4:
                case Code.Ldc_I4_5:
                case Code.Ldc_I4_6:
                case Code.Ldc_I4_7:
                case Code.Ldc_I4_8:
                case Code.Ldc_I4_S:
                case Code.Ldc_I4:
                case Code.Ldnull:
                    current = current.Next;
                    continue;
                default:
                    return false;
            }
        }

        return false;
    }

    private static int IndexOf(IReadOnlyList<Instruction> instructions, Instruction target)
    {
        for (int index = 0; index < instructions.Count; index++)
        {
            if (ReferenceEquals(instructions[index], target)) return index;
        }

        return -1;
    }

    private static Instruction? PreviousMeaningfulInstruction(
        IReadOnlyList<Instruction> instructions,
        int index)
    {
        for (int previous = index - 1; previous >= 0; previous--)
        {
            if (instructions[previous].OpCode.Code != Code.Nop) return instructions[previous];
        }

        return null;
    }

    private static Instruction NextMeaningfulInstruction(
        IReadOnlyList<Instruction> instructions,
        int index)
    {
        for (int next = index + 1; next < instructions.Count; next++)
        {
            if (instructions[next].OpCode.Code != Code.Nop) return instructions[next];
        }

        throw new Xunit.Sdk.XunitException("Expected a following IL instruction.");
    }

    private static bool TryReadInt32(Instruction instruction, out int value)
    {
        switch (instruction.OpCode.Code)
        {
            case Code.Ldc_I4_M1: value = -1; return true;
            case Code.Ldc_I4_0: value = 0; return true;
            case Code.Ldc_I4_1: value = 1; return true;
            case Code.Ldc_I4_2: value = 2; return true;
            case Code.Ldc_I4_3: value = 3; return true;
            case Code.Ldc_I4_4: value = 4; return true;
            case Code.Ldc_I4_5: value = 5; return true;
            case Code.Ldc_I4_6: value = 6; return true;
            case Code.Ldc_I4_7: value = 7; return true;
            case Code.Ldc_I4_8: value = 8; return true;
            case Code.Ldc_I4_S: value = (sbyte)instruction.Operand; return true;
            case Code.Ldc_I4: value = (int)instruction.Operand; return true;
            default: value = 0; return false;
        }
    }
}
