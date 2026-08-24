using Mono.Cecil;
using Mono.Cecil.Cil;

namespace Loopstructor.AutoPlayer.Tests;

public sealed class PluginRuntimeHostContractTests
{
    private const string BootstrapType = "Loopstructor.AutoPlayer.Plugin.AutoPlayerPlugin";
    private const string SessionType = "Loopstructor.AutoPlayer.Plugin.AutoPlayerRuntimeSession";
    private const string HostType = "Loopstructor.AutoPlayer.Plugin.AutoPlayerRuntimeHost";
    private const string ControllerType = "Loopstructor.AutoPlayer.Plugin.AutoPlayController";
    private const string BridgeType = "Loopstructor.AutoPlayer.Plugin.RuntimeBridge";
    private const string CheatControllerType = "Loopstructor.AutoPlayer.Plugin.CheatController";
    private const string CheatBridgeType = "Loopstructor.AutoPlayer.Plugin.CheatRuntimeBridge";
    private const string SelectionHighlighterType = "Loopstructor.AutoPlayer.Plugin.NativeSelectionHighlighter";

    [Fact]
    public void SelectionPreview_HighlightsOriginalUiWithoutAddingASeparateGameOverlay()
    {
        using AssemblyDefinition assembly = ReadPlugin();
        TypeDefinition controller = RequireType(assembly, ControllerType);
        TypeDefinition highlighter = RequireType(assembly, SelectionHighlighterType);
        MethodDefinition timedPreview = RequireMethod(controller, "TryWaitForSelectionPreview");
        MethodDefinition frontEndPreview = RequireMethod(controller, "TryWaitForFrontEndSelectionPreview");
        MethodDefinition normalEvent = RequireMethod(controller, "TryHandleNormalEventUi");
        MethodDefinition rewardWait = RequireMethod(controller, "TryWaitForRewardOptions");
        MethodDefinition eventWait = RequireMethod(controller, "TryWaitForEventOptions");
        MethodDefinition merge = RequireMethod(controller, "RunMergeAutomationStep");
        MethodDefinition ensureMapOpen = RequireMethod(controller, "TryEnsureMapOpenForSelectionPreview");
        MethodDefinition show = RequireMethod(highlighter, "Show");

        Assert.Contains(controller.Fields, field => field.Name == "_selectionHighlighter");
        Assert.Contains(Calls(timedPreview), IsCall(ControllerType, "ShowSelectionHighlight"));
        Assert.Contains(Calls(RequireMethod(controller, "ShowSelectionHighlight")), IsCall(SelectionHighlighterType, "Show"));
        Assert.Contains(Calls(frontEndPreview), IsCall(ControllerType, "TryWaitForSelectionPreview"));
        Assert.Contains(Calls(normalEvent), IsCall(ControllerType, "ShowSelectionHighlight"));
        Assert.Contains(Calls(rewardWait), IsCall(ControllerType, "ShowRewardSelectionHighlight"));
        Assert.Contains(Calls(eventWait), IsCall(ControllerType, "ShowEventSelectionHighlight"));
        Assert.Contains(Calls(merge), IsCall(ControllerType, "TryWaitForSelectionPreview"));
        Assert.Contains(LoadedStrings(merge), value => value == "MetroTD.UISystem.RebuildUI_MergeRebuildPanel_VehicleItem");
        Assert.Contains(LoadedStrings(merge), value => value == "MetroTD.UISystem.RebuildUI_Option_Merge");
        Assert.DoesNotContain(LoadedStrings(merge), value => value == "MetroTD.UISystem.RebuildUI_Option_Fetter");
        Assert.Contains(LoadedStrings(ensureMapOpen), value => value == "uiClickMapButton");
        Assert.Contains(
            Calls(RequireMethod(controller, "TickInGame")),
            IsCall("Loopstructor.AutoPlayer.Core.MapRouteSelectionPolicy", "IsSelectionOutstanding"));
        Assert.Contains(Calls(ensureMapOpen), IsCall(ControllerType, "InvalidateFullWaveQueryCache"));
        Assert.Contains(Calls(ensureMapOpen), IsCall(ControllerType, "ScheduleContinuationFrame"));
        Assert.Contains(Calls(ensureMapOpen), IsCall(ControllerType, "ScheduleMapOpenAnimationPoll"));
        Assert.Contains(Calls(ensureMapOpen), IsCall(ControllerType, "ScheduleNormalPoll"));
        Assert.Contains(LoadedStrings(show), value => value == "UnityEngine.UI.Image");
        Assert.Contains(LoadedStrings(show), value => value == "raycastTarget");

        FieldDefinition thickness = highlighter.Fields.Single(field => field.Name == "BorderThickness");
        Assert.Equal(2f, thickness.Constant);
        FieldDefinition inset = highlighter.Fields.Single(field => field.Name == "BorderInset");
        Assert.Equal(2f, inset.Constant);
        FieldDefinition previewSeconds = controller.Fields.Single(field => field.Name == "SelectionPreviewObservationSeconds");
        Assert.Equal(1f, previewSeconds.Constant);
        FieldDefinition collectionSeconds = controller.Fields.Single(field => field.Name == "RewardCollectionObservationSeconds");
        Assert.Equal(0.75f, collectionSeconds.Constant);
        FieldDefinition mapFallbackSeconds = controller.Fields.Single(field => field.Name == "MapOpenAnimationFallbackSeconds");
        Assert.Equal(1.55f, mapFallbackSeconds.Constant);
        MethodDefinition initializeBorderColor = RequireMethod(highlighter, ".cctor");
        Assert.Contains(Calls(initializeBorderColor), call => call.DeclaringType.FullName == "UnityEngine.Color32");
        Assert.Contains(initializeBorderColor.Body.Instructions, instruction => IsLoadedInteger(instruction, 0x79));
        Assert.Contains(initializeBorderColor.Body.Instructions, instruction => IsLoadedInteger(instruction, 0xD5));
        Assert.Contains(initializeBorderColor.Body.Instructions, instruction => IsLoadedInteger(instruction, 0x3B));
        Assert.Contains(initializeBorderColor.Body.Instructions, instruction => IsLoadedInteger(instruction, 0xFF));

        TypeDefinition bridge = RequireType(assembly, BridgeType);
        MethodDefinition initializeBridgeContract = RequireMethod(bridge, ".cctor");
        Assert.Contains(LoadedStrings(initializeBridgeContract), value => value == "uiClickMapButton");
        Assert.Contains(LoadedStrings(initializeBridgeContract), value => value == "UiClickMapButton");
        MethodDefinition mapAnimation = RequireMethod(bridge, "TryGetMapOpenAnimationProgress");
        Assert.Contains(Calls(ensureMapOpen), IsCall(BridgeType, "TryGetMapOpenAnimationProgress"));
        Assert.Contains(
            Calls(ensureMapOpen),
            IsCall("Loopstructor.AutoPlayer.Core.MapOpenAnimationPolicy", "IsReady"));
        Assert.Contains(Calls(mapAnimation), call =>
            call.DeclaringType.FullName == "UnityEngine.Animator" &&
            call.Name == "GetCurrentAnimatorStateInfo");
        Assert.Contains(Calls(mapAnimation), call =>
            call.DeclaringType.FullName == "UnityEngine.Animator" &&
            call.Name == "IsInTransition");
        Assert.Single(LoadedStrings(ensureMapOpen), value => value == "uiClickMapButton");

        MethodDefinition drawOverlay = RequireMethod(RequireType(assembly, SessionType), "DrawOverlay");
        Assert.DoesNotContain(Calls(drawOverlay), IsCall(ControllerType, "ShowSelectionHighlight"));
        Assert.DoesNotContain(Calls(drawOverlay), IsCall(SelectionHighlighterType, "Show"));
    }

    private static bool IsLoadedInteger(Instruction instruction, int value) => instruction.OpCode.Code switch
    {
        Code.Ldc_I4_M1 => value == -1,
        Code.Ldc_I4_0 => value == 0,
        Code.Ldc_I4_1 => value == 1,
        Code.Ldc_I4_2 => value == 2,
        Code.Ldc_I4_3 => value == 3,
        Code.Ldc_I4_4 => value == 4,
        Code.Ldc_I4_5 => value == 5,
        Code.Ldc_I4_6 => value == 6,
        Code.Ldc_I4_7 => value == 7,
        Code.Ldc_I4_8 => value == 8,
        Code.Ldc_I4_S => instruction.Operand is sbyte shortValue && shortValue == value,
        Code.Ldc_I4 => instruction.Operand is int intValue && intValue == value,
        _ => false
    };

    [Fact]
    public void SelectionPreview_IsClearedOnSceneChangePauseStopFaultCompletionAndSessionDispose()
    {
        using AssemblyDefinition assembly = ReadPlugin();
        TypeDefinition controller = RequireType(assembly, ControllerType);
        foreach (string methodName in new[]
                 {
                     "ObserveActiveScene",
                     "CommitFault",
                     "Complete"
                 })
        {
            Assert.Contains(
                Calls(RequireMethod(controller, methodName)),
                IsCall(ControllerType, "ClearSelectionHighlight"));
        }

        foreach (string methodName in new[] { "ApplyPause", "ApplyStop" })
        {
            Assert.Contains(
                Calls(RequireMethod(controller, methodName)),
                IsCall(ControllerType, "ClearDeferredReadDecisions"));
        }
        Assert.Contains(
            Calls(RequireMethod(controller, "ClearDeferredReadDecisions")),
            IsCall(ControllerType, "ClearSelectionHighlight"));

        TypeDefinition session = RequireType(assembly, SessionType);
        Assert.Contains(
            session.NestedTypes.SelectMany(type => type.Methods).SelectMany(Calls),
            IsCall(ControllerType, "ClearNativeSelectionHighlight"));
    }

    [Fact]
    public void BattlePolling_UsesWaveQueryAndAvoidsRepeatedFullStateSerialization()
    {
        using AssemblyDefinition assembly = ReadPlugin();
        TypeDefinition controller = RequireType(assembly, ControllerType);
        MethodDefinition tickInGame = RequireMethod(controller, "TickInGame");
        MethodDefinition ensureReady = RequireMethod(controller, "EnsureInGameRuntimeReady");
        MethodDefinition observedWave = RequireMethod(controller, "TryHandleObservedWave");
        MethodDefinition transition = RequireMethod(controller, "TryHandlePendingMapSelection");
        MethodDefinition adaptiveQuery = RequireMethod(controller, "TryQueryAdaptiveWaveState");

        Assert.Contains(Calls(tickInGame), IsCall(ControllerType, "EnsureInGameRuntimeReady"));
        Assert.Contains(Calls(tickInGame), IsCall(ControllerType, "TryHandleObservedWave"));
        Assert.DoesNotContain(LoadedStrings(tickInGame), value => value == "queryAffordances");
        Assert.Contains(LoadedStrings(tickInGame), value => value == "queryWave");
        Assert.Contains(LoadedStrings(tickInGame), value => value == "queryMap");
        Assert.Contains(LoadedStrings(tickInGame), value => value == "queryReward");
        Assert.Contains(LoadedStrings(tickInGame), value => value == "queryVehicle");
        Assert.DoesNotContain(LoadedStrings(tickInGame), value => value == "queryState");
        Assert.Contains(LoadedStrings(ensureReady), value => value == "queryState");
        Assert.Contains(LoadedStrings(observedWave), value => value == "queryWave");
        Assert.Contains(Calls(tickInGame), IsCall(BridgeType, "TryGetWavePulse"));
        Assert.Contains(Calls(tickInGame), IsCall(ControllerType, "TryQueryAdaptiveWaveState"));
        Assert.Contains(Calls(observedWave), IsCall(BridgeType, "TryGetWavePulse"));
        Assert.Contains(Calls(observedWave), IsCall(ControllerType, "TryQueryAdaptiveWaveState"));
        Assert.Contains(Calls(transition), IsCall(ControllerType, "TryQueryAdaptiveWaveState"));
        Assert.Contains(Calls(observedWave), IsCall(ControllerType, "RunBattleTacticStep"));
        Assert.Contains(Calls(adaptiveQuery), IsCall(ControllerType, "UpdateFullWaveQuerySchedule"));
        Assert.Contains(controller.Fields, field => field.Name == "_cachedFullWaveQueryResult");
        Assert.Contains(controller.Fields, field => field.Name == "_adaptiveFullWaveQueryInterval");
        Assert.DoesNotContain(controller.Fields, field => field.Name == "_waveQueryAvailable");

        TypeDefinition bridge = RequireType(assembly, BridgeType);
        MethodDefinition invoke = RequireMethod(bridge, "Invoke");
        MethodDefinition initialize = RequireMethod(bridge, "Initialize");
        MethodDefinition pulse = RequireMethod(bridge, "TryGetWavePulse");
        Assert.Contains(Calls(initialize), IsCall(BridgeType, "InitializeWavePulseContract"));
        Assert.DoesNotContain(Calls(pulse), IsCall(BridgeType, "FindType"));
        Assert.Contains(Calls(invoke), IsCall(BridgeType, "AdaptRuntimeResult"));
        Assert.DoesNotContain(
            Calls(invoke),
            call => call.DeclaringType.FullName == "Newtonsoft.Json.JsonConvert"
                    && call.Name == "SerializeObject");
        Assert.DoesNotContain(
            Calls(invoke),
            call => call.DeclaringType.FullName == "Newtonsoft.Json.Linq.JObject"
                    && call.Name == "Parse");
    }

    [Fact]
    public void PendingWaveFunctionFlowPolling_UsesCachedReadOnlyReflection()
    {
        using AssemblyDefinition assembly = ReadPlugin();
        TypeDefinition bridge = RequireType(assembly, BridgeType);
        MethodDefinition initialize = RequireMethod(bridge, "Initialize");
        MethodDefinition initializeFlow = RequireMethod(bridge, "InitializeWaveFunctionOptionFlowContract");
        MethodDefinition queryFlow = RequireMethod(bridge, "TryGetWaveFunctionOptionFlow");

        Assert.Contains(
            Calls(initialize),
            IsCall(BridgeType, "InitializeWaveFunctionOptionFlowContract"));
        Assert.Contains(
            LoadedStrings(initializeFlow),
            value => value == "MetroTD.UISystem.WaveFunctionOptionFlowRuntime");
        Assert.Contains(LoadedStrings(initializeFlow), value => value == "Instance");
        Assert.Contains(LoadedStrings(initializeFlow), value => value == "HasPendingFlow");
        Assert.Contains(LoadedStrings(initializeFlow), value => value == "PendingFlowDescription");
        Assert.DoesNotContain(Calls(queryFlow), IsCall(BridgeType, "FindType"));
        Assert.Contains(
            Calls(queryFlow),
            call => call.DeclaringType.FullName == "System.Reflection.PropertyInfo" &&
                    call.Name == "GetValue");
        Assert.DoesNotContain(
            Calls(queryFlow),
            call => call.DeclaringType.FullName == "System.Reflection.PropertyInfo" &&
                    call.Name == "SetValue");
        Assert.DoesNotContain(
            Calls(queryFlow),
            call => call.Name.Contains("ClearPendingFlow", StringComparison.Ordinal));
    }

    [Fact]
    public void WaveStart_IsObservedBeforeRetry_AndPendingEventFlowBlocksIt()
    {
        using AssemblyDefinition assembly = ReadPlugin();
        TypeDefinition controller = RequireType(assembly, ControllerType);
        MethodDefinition tickInGame = RequireMethod(controller, "TickInGame");
        MethodDefinition executeDecision = RequireMethod(controller, "ExecuteInGameDecision");
        MethodDefinition pauseForRecovery = RequireMethod(controller, "PauseForWaveStartRecovery");
        MethodDefinition observeTransition = RequireMethod(controller, "ObserveWaveTransition");

        Assert.Contains(Calls(tickInGame), IsCall(ControllerType, "ExecuteInGameDecision"));
        Assert.Contains(
            Calls(executeDecision),
            IsCall(BridgeType, "TryGetWaveFunctionOptionFlow"));
        Assert.Contains(Calls(executeDecision), IsCall(ControllerType, "Execute"));
        Assert.Contains(
            Calls(executeDecision),
            IsCall(ControllerType, "PauseForWaveStartRecovery"));
        Assert.Contains(
            LoadedStrings(executeDecision),
            value => value.Contains("重复发送开波命令", StringComparison.Ordinal));
        Assert.Contains(
            LoadedStrings(executeDecision),
            value => value.Contains("有限重试", StringComparison.Ordinal));
        Assert.Contains(controller.Fields, field => field.Name == "_waveStartPending");
        Assert.Contains(controller.Fields, field => field.Name == "_waveStartPendingAt");
        Assert.Contains(controller.Fields, field => field.Name == "_waveStartAttemptCount");
        Assert.Contains(
            Calls(observeTransition),
            IsCall(ControllerType, "ResetWaveStartObservation"));
        Assert.DoesNotContain(Calls(pauseForRecovery), IsCall(ControllerType, "Fault"));
        Assert.DoesNotContain(Calls(pauseForRecovery), IsCall(ControllerType, "CommitFault"));
    }

    [Fact]
    public void NormalEvent_IsHandledBeforePendingMapAndWaveStartLogic()
    {
        using AssemblyDefinition assembly = ReadPlugin();
        TypeDefinition controller = RequireType(assembly, ControllerType);
        MethodDefinition tickInGame = RequireMethod(controller, "TickInGame");
        MethodDefinition handler = RequireMethod(controller, "TryHandleNormalEventUi");
        Instruction[] tickInstructions = tickInGame.Body.Instructions.ToArray();

        int normalEvent = FindCall(tickInstructions, ControllerType, "TryHandleNormalEventUi");
        int pendingMap = FindCall(tickInstructions, ControllerType, "TryHandlePendingMapSelection");
        int decideInGame = FindCall(
            tickInstructions,
            "Loopstructor.AutoPlayer.Core.DecisionEngine",
            "DecideInGame");

        Assert.True(
            normalEvent >= 0 && normalEvent < pendingMap && pendingMap < decideInGame,
            "EventUI_Normal must block pending-map and start-wave decisions before MCP can misreport canStartWave.");
        Assert.Contains(
            Calls(handler),
            IsCall("Loopstructor.AutoPlayer.Plugin.NormalEventUiRuntimeReader", "TryRead"));
        Assert.Contains(
            Calls(handler),
            IsCall("Loopstructor.AutoPlayer.Core.NormalEventUiInspector", "Inspect"));
        Assert.Contains(Calls(handler), IsCall(ControllerType, "ResetWaveStartObservation"));
        Assert.Contains(LoadedStrings(handler), value => value == "queryUiInteractables");
        Assert.Contains(
            LoadedStrings(handler),
            value => value.Contains("1 秒观察时间", StringComparison.Ordinal));
    }

    [Fact]
    public void NormalEvent_StartAndResumeForceOneProbeBeforeWaveDecisions()
    {
        using AssemblyDefinition assembly = ReadPlugin();
        TypeDefinition controller = RequireType(assembly, ControllerType);
        MethodDefinition start = RequireMethod(controller, "Start");
        MethodDefinition resume = RequireMethod(controller, "Resume");
        MethodDefinition handler = RequireMethod(controller, "TryHandleNormalEventUi");

        AssertStoresBooleanField(start, "_normalEventProbeRequired", true);
        AssertStoresBooleanField(resume, "_normalEventProbeRequired", true);

        Instruction[] handlerInstructions = handler.Body.Instructions.ToArray();
        int forcedProbeRead = FindFieldLoad(handlerInstructions, "_normalEventProbeRequired");
        int readerProbe = FindCall(
            handlerInstructions,
            "Loopstructor.AutoPlayer.Plugin.NormalEventUiRuntimeReader",
            "TryRead");

        Assert.True(
            forcedProbeRead >= 0 && forcedProbeRead < readerProbe,
            "The one-shot Start/Resume probe must participate in the handler gate before the runtime reader executes.");
    }

    [Fact]
    public void NormalEvent_TypingScansButtonsOnlyWhenStorySkippingIsEnabled()
    {
        using AssemblyDefinition assembly = ReadPlugin();
        TypeDefinition controller = RequireType(assembly, ControllerType);
        MethodDefinition handler = RequireMethod(controller, "TryHandleNormalEventUi");
        Instruction[] instructions = handler.Body.Instructions.ToArray();

        int typingState = FindCall(
            instructions,
            "Loopstructor.AutoPlayer.Plugin.NormalEventUiRuntimeState",
            "get_IsTypingStory");
        int uiScan = FindLoadedString(instructions, "queryUiInteractables");
        int skipStory = Array.FindIndex(
            instructions,
            typingState + 1,
            instruction => instruction.Operand is MethodReference call &&
                           IsCall("Loopstructor.AutoPlayer.Core.AutomationRunOptions", "get_SkipStory")(call));

        Assert.True(
            typingState >= 0 && typingState < uiScan,
            "The read-only typewriter state must be checked before the full UIButton scan.");
        Assert.True(
            skipStory > typingState && skipStory < uiScan,
            "Typing may reach the UIButton scan only when the run explicitly enables story skipping.");
        Assert.Contains(
            instructions.Skip(typingState + 1).Take(uiScan - typingState - 1),
            instruction => instruction.OpCode.Code == Code.Ret ||
                           (instruction.OpCode.FlowControl == FlowControl.Branch &&
                            instruction.Operand is Instruction target &&
                            Array.IndexOf(instructions, target) > uiScan));
    }

    [Fact]
    public void RailGodTransition_UsesLightweightPollingAndDefersEventOptionScan()
    {
        using AssemblyDefinition assembly = ReadPlugin();
        TypeDefinition controller = RequireType(assembly, ControllerType);
        MethodDefinition tickInGame = RequireMethod(controller, "TickInGame");
        MethodDefinition transition = RequireMethod(controller, "TryHandlePendingMapSelection");
        MethodDefinition execute = RequireMethod(controller, "Execute");
        Instruction[] tickInstructions = tickInGame.Body.Instructions.ToArray();

        int policy = FindCall(
            tickInstructions,
            "Loopstructor.AutoPlayer.Core.OpeningDefensePolicy",
            "ShouldPrepare");
        int prepareDefense = FindCall(
            tickInstructions,
            ControllerType,
            "PrepareOpeningDefenseIncrementally");
        int decideInGame = FindCall(
            tickInstructions,
            "Loopstructor.AutoPlayer.Core.DecisionEngine",
            "DecideInGame");
        Assert.True(
            policy >= 0 && policy < prepareDefense && prepareDefense < decideInGame,
            "Opening-defense policy must guard incremental preparation before the normal decision engine can emit startWave.");
        Assert.Contains(
            tickInstructions.Skip(policy + 1).Take(prepareDefense - policy - 1),
            instruction => instruction.OpCode.FlowControl == FlowControl.Cond_Branch
                           && instruction.Operand is Instruction target
                           && Array.IndexOf(tickInstructions, target) > prepareDefense);
        Assert.Contains(LoadedStrings(tickInGame), value => value == "mapOpen");
        Assert.Contains(LoadedStrings(tickInGame), value => value == "canStartWave");
        Assert.Contains(LoadedStrings(transition), value => value == "queryWave");
        Assert.Contains(LoadedStrings(transition), value => value == "queryEventOptions");
        Assert.DoesNotContain(LoadedStrings(transition), value => value == "queryAffordances");
        Assert.DoesNotContain(LoadedStrings(transition), value => value == "startWave");
        Assert.Contains(
            Calls(transition),
            call => call.DeclaringType.FullName == "System.Math" && call.Name == "Max");
        Assert.Contains(
            LoadedStrings(transition),
            value => value.Contains("轨神事件正在播放入场动画", StringComparison.Ordinal));
        Assert.DoesNotContain(
            LoadedStrings(execute),
            value => value.Contains("返回成功，但未提交", StringComparison.Ordinal));
    }

    [Fact]
    public void VisibleChoices_WaitForRecordingAndStatusExposesMeasuredProgress()
    {
        using AssemblyDefinition assembly = ReadPlugin();
        TypeDefinition controller = RequireType(assembly, ControllerType);
        TypeDefinition bridge = RequireType(assembly, BridgeType);
        TypeDefinition rewardFallback = RequireType(
            assembly,
            "Loopstructor.AutoPlayer.Plugin.RewardUiRuntimeFallback");
        MethodDefinition rewardWait = RequireMethod(controller, "TryWaitForRewardOptions");
        MethodDefinition rewardObjects = RequireMethod(rewardFallback, "BuildRewardObjects");
        MethodDefinition rewardDecision = RequireMethod(controller, "DecideObservedReward");
        MethodDefinition rewardContextGate = RequireMethod(controller, "RewardSelectionNeedsVehicleContext");
        MethodDefinition eventWait = RequireMethod(controller, "TryWaitForEventOptions");
        MethodDefinition observeProgress = RequireMethod(controller, "ObserveMapProgress");
        MethodDefinition snapshot = RequireMethod(controller, "Snapshot");
        MethodDefinition invoke = RequireMethod(bridge, "Invoke");
        MethodDefinition eventQuery = RequireMethod(bridge, "InvokeLightweightWaveFunctionQuery");
        MethodDefinition eventSelection = RequireMethod(
            bridge,
            "InvokeLightweightWaveFunctionSelection");

        Assert.Contains(LoadedStrings(rewardWait), value => value.Contains("1 秒观察时间", StringComparison.Ordinal));
        Assert.Contains(
            LoadedStrings(rewardWait),
            value => value.Contains("出现动画仍在播放", StringComparison.Ordinal));
        Assert.Contains(
            LoadedStrings(rewardWait),
            value => value.Contains("出现动画已结束", StringComparison.Ordinal));
        Assert.Contains(
            "appearanceReady",
            controller.Methods
                .Concat(controller.NestedTypes.SelectMany(type => type.Methods))
                .SelectMany(LoadedStrings));
        Assert.Contains("appearanceReady", LoadedStrings(rewardObjects));
        Assert.Contains("appearanceState", LoadedStrings(rewardObjects));
        Assert.Contains("appearanceNormalizedTime", LoadedStrings(rewardObjects));
        Assert.DoesNotContain(
            LoadedStrings(rewardWait),
            value => value.Contains("奖励物品已完整出现", StringComparison.Ordinal));
        Assert.Contains(Calls(rewardWait), IsCall(ControllerType, "BuildRewardObjectsFingerprint"));
        Assert.Contains(LoadedStrings(eventWait), value => value.Contains("1 秒观察时间", StringComparison.Ordinal));
        Assert.Contains(rewardWait.Body.Instructions, instruction =>
            instruction.OpCode.Code == Code.Ldc_R4 && Math.Abs((float)instruction.Operand - 1.25f) < 0.001f);
        Assert.Contains(rewardWait.Body.Instructions, instruction =>
            instruction.OpCode.Code == Code.Ldc_R4 && Math.Abs((float)instruction.Operand - 0.75f) < 0.001f);
        Assert.Contains(eventWait.Body.Instructions, instruction =>
            instruction.OpCode.Code == Code.Ldc_R4 && Math.Abs((float)instruction.Operand - 1f) < 0.001f);
        Instruction[] tickInstructions = RequireMethod(controller, "TickInGame").Body.Instructions.ToArray();
        int rewardObservation = FindCall(tickInstructions, ControllerType, "TryWaitForRewardOptions");
        int contextualDecision = FindCall(tickInstructions, ControllerType, "DecideObservedReward");
        Assert.True(rewardObservation >= 0 && rewardObservation < contextualDecision);
        Assert.Contains(LoadedStrings(rewardDecision), value => value == "queryVehicle");
        Assert.Contains(
            Calls(rewardDecision),
            IsCall(ControllerType, "RewardSelectionNeedsVehicleContext"));
        Assert.Contains(
            rewardContextGate.Body.Instructions,
            instruction => instruction.OpCode.Code == Code.Ldc_I4_1);
        Assert.Contains(
            Calls(rewardDecision),
            IsCall("Loopstructor.AutoPlayer.Core.DecisionEngine", "DecideReward"));
        Assert.Contains(
            LoadedStrings(rewardDecision),
            value => value.Contains("沿用无车辆上下文的战略评分", StringComparison.Ordinal));
        Assert.DoesNotContain(LoadedStrings(rewardWait), value => value == "queryVehicle");
        Assert.Contains(controller.Fields, field => field.Name == "_rewardVehicleContextFingerprint");
        Assert.Contains(controller.Fields, field => field.Name == "_rewardVehicleContextAttempted");
        Assert.Contains(controller.Fields, field => field.Name == "_rewardVehicleContextFailed");
        Assert.Contains(controller.Fields, field => field.Name == "_rewardVehicleContextResult");
        Assert.Contains(
            Calls(RequireMethod(controller, "ClearRewardOptionsObservation")),
            IsCall(ControllerType, "ClearRewardVehicleContext"));
        Assert.Contains(
            Calls(RequireMethod(controller, "ExecuteWithResult")),
            IsCall(ControllerType, "ClearRewardVehicleContext"));
        Assert.Contains(
            Calls(RequireMethod(controller, "TickInGame")),
            IsCall(ControllerType, "ResetRewardOptionObservation"));
        Assert.Contains(
            Calls(rewardWait),
            IsCall(ControllerType, "ClearRewardVehicleContext"));

        Instruction[] rewardInstructions = rewardDecision.Body.Instructions.ToArray();
        int vehicleQuery = Array.FindIndex(
            rewardInstructions,
            instruction => instruction.Operand is MethodReference call &&
                           call.DeclaringType.FullName == BridgeType &&
                           call.Name == "Invoke");
        int deferredWait = Array.FindIndex(
            rewardInstructions,
            vehicleQuery + 1,
            instruction => instruction.Operand is MethodReference call &&
                           call.DeclaringType.FullName == "Loopstructor.AutoPlayer.Core.AutomationAction" &&
                           call.Name == "Wait");
        int contextualRewardDecision = Array.FindLastIndex(
            rewardInstructions,
            instruction => instruction.Operand is MethodReference call &&
                           call.DeclaringType.FullName == "Loopstructor.AutoPlayer.Core.DecisionEngine" &&
                           call.Name == "DecideReward");
        Assert.True(vehicleQuery >= 0 && vehicleQuery < deferredWait && deferredWait < contextualRewardDecision);
        Assert.Contains(rewardDecision.Body.Instructions, instruction =>
            instruction.OpCode.Code == Code.Ldc_R4 && Math.Abs((float)instruction.Operand - 0.02f) < 0.001f);
        Assert.DoesNotContain(
            LoadedStrings(RequireMethod(controller, "RunBattleTacticStep")),
            value => value == "queryVehicle");
        Assert.Contains(Calls(observeProgress), IsCall(BridgeType, "TryGetMapProgress"));
        Assert.Contains(Calls(snapshot), call => call.Name == "set_CurrentMapStage");
        Assert.Contains(Calls(snapshot), call => call.Name == "set_LastRuntimeCommandDurationMs");
        Assert.Contains(Calls(snapshot), call => call.Name == "set_MaxRuntimeCommand");
        Assert.Contains(Calls(snapshot), call => call.Name == "set_CurrentFps");
        Assert.Contains(Calls(snapshot), call => call.Name == "set_OnePercentLowFps");
        Assert.Contains(Calls(snapshot), call => call.Name == "set_FrameSampleCount");
        Assert.Contains(Calls(invoke), IsCall(BridgeType, "InvokeLightweightWaveFunctionQuery"));
        Assert.Contains(Calls(invoke), IsCall(BridgeType, "InvokeLightweightWaveFunctionSelection"));
        Assert.Contains(
            Calls(eventQuery),
            IsCall("Loopstructor.AutoPlayer.Plugin.RepairUiRuntimeFallback", "TryQueryOptions"));
        Assert.Contains(
            Calls(eventQuery),
            IsCall("Loopstructor.AutoPlayer.Plugin.WaveFunctionUiRuntimeFallback", "TryQueryOptions"));
        Assert.Contains(
            Calls(eventSelection),
            IsCall("Loopstructor.AutoPlayer.Plugin.RepairUiRuntimeFallback", "TryChooseOption"));
        Assert.Contains(
            Calls(eventSelection),
            IsCall("Loopstructor.AutoPlayer.Plugin.WaveFunctionUiRuntimeFallback", "TryChooseOption"));
        Assert.Contains(Calls(invoke), call => call.DeclaringType.FullName == "System.Diagnostics.Stopwatch" && call.Name == "StartNew");
    }

    [Fact]
    public void BattleActionExecution_RequiresLightweightPulseAndNeverStacksFullWaveQuery()
    {
        using AssemblyDefinition assembly = ReadPlugin();
        TypeDefinition controller = RequireType(assembly, ControllerType);
        MethodDefinition executeBattle = RequireMethod(controller, "TryExecuteActiveBattleAction");
        Instruction[] instructions = executeBattle.Body.Instructions.ToArray();

        int pulse = FindCall(instructions, BridgeType, "TryGetWavePulse");
        int write = FindCall(instructions, ControllerType, "ExecuteWithResult");
        Assert.True(
            pulse >= 0 && write > pulse,
            "Every active battle write must first validate the lightweight wave pulse.");
        Assert.Contains(
            instructions.Skip(pulse + 1).Take(write - pulse - 1),
            instruction => instruction.OpCode.FlowControl == FlowControl.Cond_Branch);
        Assert.DoesNotContain(Calls(executeBattle), IsCall(BridgeType, "Invoke"));
        Assert.DoesNotContain(LoadedStrings(executeBattle), value => value == "queryWave");
        Assert.Contains(
            LoadedStrings(executeBattle),
            value => value.Contains("无法读取轻量波次脉冲", StringComparison.Ordinal));
        Assert.Contains(Calls(executeBattle), IsCall(ControllerType, "AddWarning"));
        Assert.Contains(Calls(executeBattle), IsCall(ControllerType, "SetStage"));
    }

    [Fact]
    public void ActivePlay_ValidatesDefenseAndRunsPlayerEquivalentBattleTactics()
    {
        using AssemblyDefinition assembly = ReadPlugin();
        TypeDefinition controller = RequireType(assembly, ControllerType);
        MethodDefinition tickInGame = RequireMethod(controller, "TickInGame");
        MethodDefinition tactics = RequireMethod(controller, "RunBattleTacticStep");
        MethodDefinition observedWave = RequireMethod(controller, "TryHandleObservedWave");
        MethodDefinition handleWave = RequireMethod(controller, "HandleWaveObservation");
        MethodDefinition maintenance = RequireMethod(controller, "TryMaintainDefense");
        MethodDefinition prepareDefense = RequireMethod(controller, "PrepareOpeningDefenseIncrementally");
        MethodDefinition beginPreviewRelease = RequireMethod(controller, "BeginOwnedPreviewRelease");
        MethodDefinition processPreviewRelease = RequireMethod(controller, "ProcessOwnedPreviewRelease");
        MethodDefinition queryPreviewForRelease = RequireMethod(controller, "TryQueryOwnedPreviewForRelease");
        MethodDefinition buildPreviewCancellation = RequireMethod(controller, "BuildOwnedPreviewCancellation");
        MethodDefinition executePreviewCancellation = RequireMethod(controller, "ExecuteOwnedPreviewCancellation");
        MethodDefinition executeBattle = RequireMethod(controller, "TryExecuteActiveBattleAction");
        MethodDefinition ownsPreview = RequireMethod(controller, "IsOwnedDisposablePreview");
        MethodDefinition bridgeContract = RequireMethod(RequireType(assembly, BridgeType), ".cctor");

        Assert.Contains(Calls(tickInGame), IsCall(ControllerType, "HasPlacedCombatVehicle"));
        Assert.Contains(Calls(tickInGame), IsCall(ControllerType, "TryMaintainDefense"));
        Assert.Contains(Calls(tickInGame), IsCall(ControllerType, "PrepareOpeningDefenseIncrementally"));
        Assert.Contains(
            Calls(prepareDefense),
            IsCall("Loopstructor.AutoPlayer.Core.OpeningDefensePreparationPlanner", "Decide"));
        Assert.Contains(
            Calls(prepareDefense),
            IsCall("Loopstructor.AutoPlayer.Core.OpeningDefensePreparationPlanner", "Observe"));
        Assert.Contains(Calls(prepareDefense), IsCall(ControllerType, "Fault"));
        Assert.DoesNotContain(
            LoadedStrings(prepareDefense),
            value => value == "queryDisposableGridOptions");
        Assert.DoesNotContain(
            LoadedStrings(prepareDefense),
            value => value == "prepareDefaultDefense");
        Assert.Contains(LoadedStrings(prepareDefense), value => value == "queryCatapults");
        Assert.Contains(LoadedStrings(prepareDefense), value => value == "queryVehicle");
        Assert.Contains(LoadedStrings(prepareDefense), value => value == "previewRailPath");
        Assert.Contains(LoadedStrings(prepareDefense), value => value == "queryRail");
        Assert.Contains(LoadedStrings(prepareDefense), value => value == "drawRailPath");
        Assert.Contains(LoadedStrings(prepareDefense), value => value == "queryTrain");
        Assert.Contains(LoadedStrings(prepareDefense), value => value == "moveVehicleInTrain");
        Assert.Contains(LoadedStrings(prepareDefense), value => value == "placeVehicleOnLine");
        Assert.Contains(
            Calls(prepareDefense),
            IsCall(ControllerType, "ExecuteOpeningDefenseReadOnly"));
        Assert.Contains(LoadedStrings(tactics), value => value == "queryWaveThreats");
        Assert.Contains(LoadedStrings(tactics), value => value == "queryDisposable");
        Assert.Contains(LoadedStrings(tactics), value => value == "cancelDisposable");
        Assert.Contains(Calls(tactics), IsCall(ControllerType, "ResolveSelectedDisposableEnum"));
        Assert.Contains(Calls(tactics), IsCall(ControllerType, "IsOwnedDisposablePreview"));
        Assert.Contains(Calls(tactics), IsCall(ControllerType, "TryExecuteActiveBattleAction"));
        Assert.DoesNotContain(LoadedStrings(executeBattle), value => value == "queryWave");
        Assert.Contains(Calls(executeBattle), IsCall(BridgeType, "TryGetWavePulse"));
        Assert.Contains(LoadedStrings(ownsPreview), value => value == "interactionInstanceId");
        Assert.Contains(controller.Fields, field => field.Name == "_ownedDisposableInteractionInstanceId");
        Assert.Contains(controller.Fields, field => field.Name == "_battleTrainIdentitiesMovedThisWave");
        Assert.Contains(Calls(handleWave), IsCall(ControllerType, "BeginBattleTacticCycle"));
        Assert.Contains(
            LoadedStrings(tactics),
            value => value.Contains("不会接管该交互", StringComparison.Ordinal));
        Assert.Contains(LoadedStrings(bridgeContract), value => value == "moveTrainToLine");
        Assert.Contains(LoadedStrings(bridgeContract), value => value == "queryCatapults");
        Assert.Contains(LoadedStrings(bridgeContract), value => value == "previewRailPath");
        Assert.Contains(LoadedStrings(bridgeContract), value => value == "drawRailPath");
        Assert.Contains(LoadedStrings(bridgeContract), value => value == "placeVehicleOnLine");
        Assert.DoesNotContain(LoadedStrings(bridgeContract), value => value == "prepareDefaultDefense");
        Assert.Contains(LoadedStrings(maintenance), value => value == "queryTrain");
        Assert.Contains(LoadedStrings(maintenance), value => value == "queryVehicle");
        Assert.Contains(LoadedStrings(maintenance), value => value == "moveVehicleInTrain");
        Assert.Contains(LoadedStrings(maintenance), value => value == "queryCatapults");
        Assert.Contains(LoadedStrings(maintenance), value => value == "queryRail");
        Assert.Contains(LoadedStrings(maintenance), value => value == "previewRailPath");
        Assert.Contains(LoadedStrings(maintenance), value => value == "drawRailPath");
        Assert.Contains(LoadedStrings(maintenance), value => value == "placeVehicleOnLine");
        Assert.Contains(LoadedStrings(maintenance), value => value == "queryDisposableGridOptions");
        Assert.Contains(LoadedStrings(maintenance), value => value == "queryMovableStationState");
        Assert.Contains(LoadedStrings(maintenance), value => value == "startStationMove");
        Assert.Contains(LoadedStrings(maintenance), value => value == "confirmStationMoveGrid");
        Assert.DoesNotContain(LoadedStrings(maintenance), value => value == "startRightDragStationToGrid");
        Assert.DoesNotContain(LoadedStrings(maintenance), value => value == "serviceFallback");
        Assert.DoesNotContain(LoadedStrings(maintenance), value => value == "useDisposable");
        Assert.Contains(LoadedStrings(maintenance), value => value == "confirmDisposableGrid");
        Assert.Contains(LoadedStrings(maintenance), value => value == "cancelDisposable");
        Assert.Contains(Calls(maintenance), IsCall(BridgeType, "TryGetWavePulse"));
        Assert.Contains(
            Calls(maintenance),
            IsCall(
                "Loopstructor.AutoPlayer.Plugin.IncrementalDefenseStationGridProbe",
                "TryInitializePlacement"));
        Assert.Contains(
            Calls(maintenance),
            IsCall(
                "Loopstructor.AutoPlayer.Plugin.IncrementalDefenseStationGridProbe",
                "ProbeNext"));
        Assert.Contains(Calls(maintenance), IsCall("Loopstructor.AutoPlayer.Core.BattleDecisionEngine", "NeedsDefenseExpansion"));
        Assert.Contains(Calls(maintenance), IsCall("Loopstructor.AutoPlayer.Core.BattleDecisionEngine", "DecideDefenseExpansion"));
        Assert.Contains(Calls(maintenance), IsCall("Loopstructor.AutoPlayer.Core.BattleDecisionEngine", "IsLegalDefenseExpansionPreview"));
        Assert.Contains(Calls(maintenance), IsCall("Loopstructor.AutoPlayer.Core.BattleDecisionEngine", "IsUsableDefenseExpansionRailBaseline"));
        Assert.Contains(Calls(maintenance), IsCall("Loopstructor.AutoPlayer.Core.BattleDecisionEngine", "ReadDrawnRailInstanceId"));
        Assert.Contains(Calls(maintenance), IsCall("Loopstructor.AutoPlayer.Core.BattleDecisionEngine", "VerifyDefenseExpansionRail"));
        Assert.Contains(Calls(maintenance), IsCall("Loopstructor.AutoPlayer.Core.BattleDecisionEngine", "DecideExpansionVehiclePlacement"));
        Assert.Contains(Calls(maintenance), IsCall("Loopstructor.AutoPlayer.Core.BattleDecisionEngine", "RequiredExpansionDisposable"));
        Assert.Contains(Calls(maintenance), IsCall("Loopstructor.AutoPlayer.Core.BattleDecisionEngine", "ReadExpansionInteractionId"));
        Assert.Contains(Calls(maintenance), IsCall("Loopstructor.AutoPlayer.Core.BattleDecisionEngine", "DecideExpansionDirectConfirmation"));
        Assert.Contains(
            LoadedStrings(maintenance),
            value => value.Contains("单次命令中打开并确认", StringComparison.Ordinal));
        Assert.DoesNotContain(
            LoadedStrings(maintenance),
            value => value.Contains("预览已消失或交互身份发生变化", StringComparison.Ordinal));
        Assert.Contains(controller.Fields, field => field.Name == "_defenseRailBaselineResult");
        Assert.Contains(controller.Fields, field => field.Name == "_defenseVerifiedRailResult");
        Assert.Contains(controller.Fields, field => field.Name == "_defenseExpectedRailInstanceId");
        Assert.Contains(controller.Fields, field => field.Name == "_ownedPreviewReleaseOperation");
        Assert.Contains(controller.Fields, field => field.Name == "_ownedPreviewReleaseStep");
        Assert.Contains(
            controller.Fields,
            field => field.Name == "_battleLiveDisposableGridProbe" &&
                     field.FieldType.FullName ==
                     "Loopstructor.AutoPlayer.Plugin.IncrementalBattleLiveDisposableGridProbe");
        Assert.Contains(
            controller.Fields,
            field => field.Name == "_defenseExpansionAttributeGridProbe" &&
                     field.FieldType.FullName ==
                     "Loopstructor.AutoPlayer.Plugin.IncrementalDefenseExpansionAttributeGridProbe");
        Assert.Contains(
            Calls(tactics),
            IsCall(
                "Loopstructor.AutoPlayer.Plugin.IncrementalBattleLiveDisposableGridProbe",
                "TryInitialize"));
        Assert.Contains(
            Calls(tactics),
            IsCall(
                "Loopstructor.AutoPlayer.Plugin.IncrementalBattleLiveDisposableGridProbe",
                "ProbeNext"));
        Assert.DoesNotContain(LoadedStrings(maintenance), value => value == "queryWave");
        Assert.DoesNotContain(
            LoadedStrings(tactics),
            value => value.StartsWith("cheat.", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(Calls(RequireMethod(controller, "Pause")), IsCall(ControllerType, "BeginOwnedPreviewRelease"));
        Assert.Contains(Calls(RequireMethod(controller, "Stop")), IsCall(ControllerType, "BeginOwnedPreviewRelease"));
        Assert.Contains(Calls(RequireMethod(controller, "Fault")), IsCall(ControllerType, "BeginOwnedPreviewRelease"));
        Assert.Contains(Calls(RequireMethod(controller, "Tick")), IsCall(ControllerType, "ProcessOwnedPreviewRelease"));
        Assert.DoesNotContain(Calls(beginPreviewRelease), IsCall(BridgeType, "Invoke"));
        Assert.Contains(
            Calls(processPreviewRelease),
            IsCall(ControllerType, "TryQueryOwnedPreviewForRelease"));
        Assert.Contains(
            Calls(queryPreviewForRelease),
            IsCall("Loopstructor.AutoPlayer.Plugin.OpeningDefenseInteractionGuard", "Query"));
        Assert.Contains(
            Calls(queryPreviewForRelease),
            IsCall("Loopstructor.AutoPlayer.Core.RuntimeResultInspector", "ClassifyReadOnly"));
        Assert.DoesNotContain(LoadedStrings(processPreviewRelease), value => value == "queryDisposable");
        Assert.DoesNotContain(LoadedStrings(queryPreviewForRelease), value => value == "queryDisposable");
        Assert.Contains(LoadedStrings(processPreviewRelease), value => value == "cancelDisposable");
        Assert.Contains(Calls(processPreviewRelease), IsCall(ControllerType, "BuildOwnedPreviewCancellation"));
        Assert.Contains(Calls(processPreviewRelease), IsCall(ControllerType, "ExecuteOwnedPreviewCancellation"));
        Assert.Contains(
            Calls(buildPreviewCancellation),
            IsCall("Loopstructor.AutoPlayer.Core.BattleDecisionEngine", "DecideExpansionAttributeCancellation"));
        Assert.Contains(
            Calls(processPreviewRelease),
            IsCall("Loopstructor.AutoPlayer.Core.BattleDecisionEngine", "IsOwnedExpansionAttributePreview"));
        Assert.Contains(Calls(executePreviewCancellation), IsCall(BridgeType, "Invoke"));
    }

    [Fact]
    public void OpeningDefensePreview_WaitsForOwnedLateAnimationAndVerifiesPlacementBeforeMacro()
    {
        using AssemblyDefinition assembly = ReadPlugin();
        TypeDefinition controller = RequireType(assembly, ControllerType);
        MethodDefinition tickInGame = RequireMethod(controller, "TickInGame");
        MethodDefinition handleBlocker = RequireMethod(controller, "TryHandleOpeningDefensePreviewBlocker");
        MethodDefinition prepare = RequireMethod(controller, "PrepareOpeningDefenseIncrementally");
        MethodDefinition captureIdentity = RequireMethod(controller, "CaptureOpeningDefensePreviewIdentity");
        MethodDefinition hasOwnedPreview = RequireMethod(controller, "HasOwnedAutomationPreviewIdentity");
        MethodDefinition processRelease = RequireMethod(controller, "ProcessOwnedPreviewRelease");
        MethodDefinition completeRelease = RequireMethod(controller, "CompleteOwnedPreviewRelease");
        Instruction[] tickInstructions = tickInGame.Body.Instructions.ToArray();

        Assert.Contains(
            controller.Fields,
            field => field.Name == "_openingDefenseInteractionInstanceId" &&
                     field.FieldType.FullName == "System.Int32");

        int ownedPreviewGate = FindCall(
            tickInstructions,
            ControllerType,
            "TryHandleOpeningDefensePreviewBlocker");
        int genericOwnedPreviewGate = FindCall(
            tickInstructions,
            ControllerType,
            "HasOwnedAutomationPreviewIdentity");
        Assert.True(
            ownedPreviewGate >= 0 && genericOwnedPreviewGate > ownedPreviewGate,
            "The owned opening preview must be handled before the generic ownership/fault path.");
        Assert.DoesNotContain(LoadedStrings(tickInGame), value => value == "cancelDisposable");

        Assert.Contains(LoadedStrings(handleBlocker), value => value == "queryDisposable");
        Assert.DoesNotContain(LoadedStrings(handleBlocker), value => value == "cancelDisposable");
        Assert.Contains(
            Calls(handleBlocker),
            IsCall("Loopstructor.AutoPlayer.Core.BattleDecisionEngine", "IsOwnedExpansionAttributePreview"));
        Assert.Contains(
            Calls(handleBlocker),
            IsCall("Loopstructor.AutoPlayer.Core.OpeningDefensePreparationPlanner", "MarkPlacementPreviewReleased"));

        Assert.Contains(
            Calls(prepare),
            IsCall(ControllerType, "CaptureOpeningDefensePreviewIdentity"));
        Assert.Contains(Calls(prepare), IsCall(ControllerType, "Fault"));
        Instruction[] prepareInstructions = prepare.Body.Instructions.ToArray();
        int capturePreviewIdentity = FindCall(
            prepareInstructions,
            ControllerType,
            "CaptureOpeningDefensePreviewIdentity");
        int observeConfirmation = FindCall(
            prepareInstructions,
            "Loopstructor.AutoPlayer.Core.OpeningDefensePreparationPlanner",
            "Observe");
        Assert.True(
            capturePreviewIdentity >= 0 && capturePreviewIdentity < observeConfirmation,
            "Confirmation results must record any owned preview before the planner enters fallback handling.");
        Assert.Contains(
            Calls(prepare),
            IsCall("Loopstructor.AutoPlayer.Core.OpeningDefensePreparationPlanner", "MarkPlacementPreviewReleased"));
        Assert.Contains(
            Calls(captureIdentity),
            IsCall("Loopstructor.AutoPlayer.Core.BattleDecisionEngine", "ReadExpansionAttributeInteractionId"));
        Assert.Contains(Calls(captureIdentity), IsCall(ControllerType, "FaultRequiringProcessRestart"));

        Assert.Contains(
            hasOwnedPreview.Body.Instructions,
            instruction => instruction.OpCode.Code == Code.Ldfld &&
                           instruction.Operand is FieldReference field &&
                           field.Name == "_openingDefenseInteractionInstanceId");
        Assert.Contains(
            processRelease.Body.Instructions,
            instruction => instruction.OpCode.Code == Code.Ldfld &&
                           instruction.Operand is FieldReference field &&
                           field.Name == "_openingDefenseInteractionInstanceId");
        Assert.Contains(
            Calls(completeRelease),
            IsCall(ControllerType, "ResumeOrResetOpeningDefensePreparation"));
        Assert.DoesNotContain(
            Calls(completeRelease),
            IsCall(ControllerType, "ResetOpeningDefensePreparation"));
        Assert.DoesNotContain(
            Calls(RequireMethod(controller, "Resume")),
            IsCall(ControllerType, "ResetOpeningDefensePreparation"));
        MethodDefinition resumeOrResetOpening = RequireMethod(
            controller,
            "ResumeOrResetOpeningDefensePreparation");
        Assert.Contains(
            Calls(RequireMethod(controller, "ApplyStop")),
            IsCall(ControllerType, "ResumeOrResetOpeningDefensePreparation"));
        Assert.Contains(
            Calls(resumeOrResetOpening),
            IsCall(
                "Loopstructor.AutoPlayer.Core.OpeningDefensePreparationPlanner",
                "get_HasCommittedWrite"));
        Assert.Contains(
            Calls(resumeOrResetOpening),
            IsCall(
                "Loopstructor.AutoPlayer.Core.OpeningDefensePreparationPlanner",
                "ResumeCommittedTransaction"));
        Assert.Contains(
            Calls(resumeOrResetOpening),
            IsCall(
                "Loopstructor.AutoPlayer.Core.OpeningDefensePreparationPlanner",
                "Reset"));
    }

    [Fact]
    public void BattleTrainMovement_RequeriesCurrentState_VerifiesArrival_AndContinuesWithStableIdentity()
    {
        using AssemblyDefinition assembly = ReadPlugin();
        TypeDefinition controller = RequireType(assembly, ControllerType);
        MethodDefinition tactics = RequireMethod(controller, "RunBattleTacticStep");
        MethodDefinition beginCycle = RequireMethod(controller, "BeginBattleTacticCycle");
        Instruction[] instructions = tactics.Body.Instructions.ToArray();

        Assert.Contains(
            controller.Fields,
            field => field.Name == "_battleTrainIdentitiesMovedThisWave" &&
                     field.FieldType.FullName == "System.Collections.Generic.HashSet`1<System.Int32>");
        Assert.DoesNotContain(controller.Fields, field => field.Name == "_battleTrainIndexesMovedThisWave");

        int verifyArrival = FindCall(instructions, ControllerType, "DidTrainReachMovementTarget");
        int executeMovement = FindLastCallBefore(
            instructions,
            ControllerType,
            "TryExecuteActiveBattleAction",
            verifyArrival);
        int latestRailQuery = FindLastLoadedStringBefore(instructions, "queryRail", executeMovement);
        int latestTrainQuery = FindLastLoadedStringBefore(instructions, "queryTrain", executeMovement);
        Assert.True(
            latestRailQuery >= 0 && latestRailQuery < latestTrainQuery && latestTrainQuery < executeMovement,
            "MoveTrain must refresh rail and train state immediately before committing movement.");
        Assert.True(
            instructions.Count(instruction => instruction.OpCode.Code == Code.Ldstr &&
                                              string.Equals(instruction.Operand as string, "queryRail", StringComparison.Ordinal)) >= 2,
            "The movement commit path must re-query rail after the earlier planning query.");
        Assert.True(
            instructions.Count(instruction => instruction.OpCode.Code == Code.Ldstr &&
                                              string.Equals(instruction.Operand as string, "queryTrain", StringComparison.Ordinal)) >= 2,
            "The movement commit path must re-query train after the earlier planning query.");

        Assert.True(
            executeMovement >= 0 && executeMovement < verifyArrival,
            "A successful movement result must be verified against the requested target before bookkeeping.");

        int loadStableIdentity = FindLoadedString(instructions, "trainIdentity");
        int rememberMovedTrain = Array.FindIndex(
            instructions,
            verifyArrival + 1,
            instruction => instruction.Operand is MethodReference call &&
                           call.DeclaringType.FullName == "System.Collections.Generic.HashSet`1<System.Int32>" &&
                           call.Name == "Add");
        Assert.True(
            loadStableIdentity >= 0 && loadStableIdentity < verifyArrival && verifyArrival < rememberMovedTrain,
            "Only a verified move may record the stable train identity as already dispatched this wave.");

        int clearThreatSnapshot = FindFieldStore(instructions, "_battleThreats", rememberMovedTrain + 1);
        int continueWithThreatQuery = FindFieldStore(instructions, "_battleTacticStep", clearThreatSnapshot + 1);
        Assert.True(clearThreatSnapshot > rememberMovedTrain);
        Assert.Equal(Code.Ldnull, PreviousMeaningfulInstruction(instructions, clearThreatSnapshot).OpCode.Code);
        Assert.True(continueWithThreatQuery > clearThreatSnapshot);
        Assert.Equal(0, ReadLoadedInt32(PreviousMeaningfulInstruction(instructions, continueWithThreatQuery)));
        Assert.Contains(
            tactics.Body.Instructions,
            instruction => instruction.OpCode.Code == Code.Ldc_R4 &&
                           Math.Abs((float)instruction.Operand - 1f) < 0.001f);
        Assert.DoesNotContain(
            tactics.Body.Instructions,
            instruction => instruction.OpCode.Code == Code.Ldc_R4 &&
                           Math.Abs((float)instruction.Operand - 12f) < 0.001f);

        Instruction[] beginInstructions = beginCycle.Body.Instructions.ToArray();
        int cycleThreatClear = FindFieldStore(beginInstructions, "_battleThreats");
        int cycleThreatQuery = FindFieldStore(beginInstructions, "_battleTacticStep", cycleThreatClear + 1);
        Assert.True(cycleThreatClear >= 0);
        Assert.Equal(Code.Ldnull, PreviousMeaningfulInstruction(beginInstructions, cycleThreatClear).OpCode.Code);
        Assert.True(cycleThreatQuery > cycleThreatClear);
        Assert.Equal(0, ReadLoadedInt32(PreviousMeaningfulInstruction(beginInstructions, cycleThreatQuery)));
    }

    [Fact]
    public void Bootstrap_OnlyStartsSession_AndNeverOwnsRuntimeCleanup()
    {
        using AssemblyDefinition assembly = ReadPlugin();
        TypeDefinition bootstrap = RequireType(assembly, BootstrapType);

        Assert.Contains(Calls(RequireMethod(bootstrap, "Awake")), IsCall(SessionType, "TryStart"));
        Assert.DoesNotContain(bootstrap.Methods, method => method.Name is "Update" or "OnGUI");

        MethodReference[] allCalls = Calls(bootstrap.Methods).ToArray();
        Assert.DoesNotContain(allCalls, call => call.DeclaringType.FullName == "UnityEngine.Object"
                                                 && call.Name is "DontDestroyOnLoad" or "Destroy" or "DestroyImmediate");
        Assert.DoesNotContain(allCalls, IsCall("Loopstructor.AutoPlayer.Plugin.PipeControlServer", "Dispose"));
        Assert.DoesNotContain(allCalls, call => call.Name == "UnpatchSelf");
    }

    [Fact]
    public void EnsureHost_CreatesIndependentHiddenPersistentRoot()
    {
        using AssemblyDefinition assembly = ReadPlugin();
        TypeDefinition session = RequireType(assembly, SessionType);
        MethodDefinition ensureHost = RequireMethod(session, "EnsureHost");
        Instruction[] instructions = ensureHost.Body.Instructions.ToArray();

        int create = FindCall(instructions, "UnityEngine.GameObject", ".ctor", Code.Newobj);
        int hide = FindCall(instructions, "UnityEngine.Object", "set_hideFlags");
        int persist = FindCall(instructions, "UnityEngine.Object", "DontDestroyOnLoad");
        int addHost = FindGenericCall(instructions, "UnityEngine.GameObject", "AddComponent", HostType);
        int attach = FindCall(instructions, HostType, "Attach");

        Assert.True(create < hide && hide < persist && persist < addHost && addHost < attach);
        Assert.Equal(61, ReadLoadedInt32(instructions[hide - 1]));
        Assert.DoesNotContain(Calls(ensureHost), IsCall("UnityEngine.Transform", "SetParent"));
        Assert.Equal("System.Object", session.BaseType.FullName);
    }

    [Fact]
    public void HostLoss_DoesNotDisposeSession_AndEveryMainThreadWatchdogCanRebuild()
    {
        using AssemblyDefinition assembly = ReadPlugin();
        TypeDefinition session = RequireType(assembly, SessionType);
        TypeDefinition host = RequireType(assembly, HostType);

        MethodDefinition hostDestroy = RequireMethod(host, "OnDestroy");
        Assert.Contains(Calls(hostDestroy), IsCall(SessionType, "HostDestroyed"));
        Assert.DoesNotContain(Calls(hostDestroy), IsCall(SessionType, "Dispose"));

        MethodDefinition hostLost = RequireMethod(session, "HostDestroyed");
        MethodReference[] lossCalls = Calls(hostLost).ToArray();
        Assert.Contains(
            hostLost.Body.Instructions,
            instruction => instruction.OpCode.Code is Code.Beq or Code.Beq_S or Code.Ceq);
        Assert.Contains(lossCalls, IsCall("Loopstructor.AutoPlayer.Plugin.CheatController", "OnRuntimeHostLost"));
        Assert.DoesNotContain(lossCalls, call => call.Name is "Dispose" or "UnpatchSelf");
        Assert.DoesNotContain(lossCalls, call => call.DeclaringType.FullName
            == "Loopstructor.AutoPlayer.Plugin.PipeControlServer");

        Assert.Contains(Calls(RequireMethod(session, "OnSceneLoaded")), IsCall(SessionType, "RecoverHostIfNeeded"));
        Assert.Contains(Calls(RequireMethod(session, "OnActiveSceneChanged")), IsCall(SessionType, "RecoverHostIfNeeded"));
        Assert.Contains(Calls(RequireMethod(session, "OnBeforeRender")), IsCall(SessionType, "RecoverHostIfNeeded"));
        Assert.Contains(Calls(RequireMethod(session, "RecoverHostIfNeeded")), IsCall(SessionType, "EnsureHost"));
    }

    [Fact]
    public void LifecycleEvents_AreSymmetric_AndBothQuitSignalsStopSession()
    {
        using AssemblyDefinition assembly = ReadPlugin();
        TypeDefinition session = RequireType(assembly, SessionType);
        TypeDefinition host = RequireType(assembly, HostType);

        MethodReference[] attach = Calls(RequireMethod(session, "AttachLifecycleEvents")).ToArray();
        MethodReference[] detach = Calls(RequireMethod(session, "DetachLifecycleEvents")).ToArray();
        foreach (string eventName in new[] { "sceneLoaded", "activeSceneChanged", "onBeforeRender", "quitting" })
        {
            Assert.Contains(attach, call => call.Name == "add_" + eventName);
            Assert.Contains(detach, call => call.Name == "remove_" + eventName);
        }

        Assert.Contains(Calls(RequireMethod(session, "OnApplicationQuitting")), IsCall(SessionType, "BeginQuit"));
        Assert.Contains(Calls(RequireMethod(host, "OnApplicationQuit")), IsCall(SessionType, "BeginQuit"));
        Assert.Contains(Calls(RequireMethod(session, "BeginQuit")), IsCall(SessionType, "Dispose"));
    }

    [Fact]
    public void IndependentHost_OwnsUnityCallbacks_AndSessionOwnsFinalCleanup()
    {
        using AssemblyDefinition assembly = ReadPlugin();
        TypeDefinition session = RequireType(assembly, SessionType);
        TypeDefinition host = RequireType(assembly, HostType);

        Assert.Equal("UnityEngine.MonoBehaviour", host.BaseType.FullName);
        Assert.Contains(Calls(RequireMethod(host, "Update")), IsCall(SessionType, "PumpFrame"));
        Assert.Contains(Calls(RequireMethod(host, "OnGUI")), IsCall(SessionType, "DrawOverlay"));

        MethodReference[] pumpCalls = Calls(RequireMethod(session, "PumpFrame")).ToArray();
        int frameSample = Array.FindIndex(pumpCalls, IsCall(ControllerType, "RecordFrame"));
        int automationTick = Array.FindIndex(pumpCalls, IsCall(ControllerType, "Tick"));
        Assert.True(frameSample >= 0 && frameSample < automationTick);
        Assert.Contains(
            Calls(RequireMethod(RequireType(assembly, ControllerType), "RecordFrame")),
            IsCall("Loopstructor.AutoPlayer.Core.FrameTimingSampler", "Record"));

        MethodReference[] cleanup = Calls(AllMethods(session)).ToArray();
        Assert.Contains(cleanup, IsCall(SessionType, "DetachLifecycleEvents"));
        Assert.Contains(cleanup, IsCall("Loopstructor.AutoPlayer.Plugin.PipeControlServer", "Dispose"));
        Assert.Contains(cleanup, IsCall("Loopstructor.AutoPlayer.Plugin.CheatController", "Dispose"));
        Assert.Contains(cleanup, IsCall("Loopstructor.AutoPlayer.Plugin.SpawnPointCaptureInputPatch", "Detach"));
        Assert.Contains(cleanup, IsCall("Loopstructor.AutoPlayer.Plugin.MapSkipPatch", "Reset"));
        Assert.Contains(cleanup, call => call.Name == "UnpatchSelf");
    }

    [Fact]
    public void EnemyBuffOverlay_SnapshotsInTick_AndOnGuiOnlyDrawsCachedSprites()
    {
        using AssemblyDefinition assembly = ReadPlugin();
        TypeDefinition session = RequireType(assembly, SessionType);
        TypeDefinition controller = RequireType(assembly, CheatControllerType);
        TypeDefinition bridge = RequireType(assembly, CheatBridgeType);

        Assert.Contains(
            Calls(RequireMethod(session, "DrawOverlay")),
            IsCall(CheatControllerType, "DrawEnemyOverlays"));
        Assert.Contains(
            Calls(RequireMethod(controller, "Tick")),
            IsCall(CheatBridgeType, "TickEnemyOverlays"));
        Assert.Contains(
            Calls(RequireMethod(controller, "DrawEnemyOverlays")),
            IsCall(CheatBridgeType, "DrawEnemyOverlays"));

        MethodDefinition tick = RequireMethod(bridge, "TickEnemyOverlays");
        MethodDefinition refresh = RequireMethod(bridge, "RefreshEnemyOverlayCache");
        MethodDefinition snapshotBuffs = RequireMethod(bridge, "SnapshotEnemyBuffIcons");
        MethodDefinition duration = RequireMethod(bridge, "ResolveEnemyBuffDuration");
        MethodDefinition resolveIcon = RequireMethod(bridge, "TryResolveEnemyBuffIcon");
        MethodDefinition draw = RequireMethod(bridge, "DrawEnemyOverlays");
        MethodDefinition drawIcons = RequireMethod(bridge, "DrawEnemyBuffIcons");

        Assert.Contains(Calls(tick), IsCall(CheatBridgeType, "RefreshEnemyOverlayCache"));
        Assert.Contains(Calls(refresh), IsCall(CheatBridgeType, "CollectEnemyTargets"));
        Assert.Contains(Calls(refresh), IsCall(CheatBridgeType, "SnapshotEnemyBuffIcons"));
        Assert.Contains(LoadedStrings(snapshotBuffs), value => value == "IsEnd");
        Assert.Contains(LoadedStrings(snapshotBuffs), value => value == "Key");
        Assert.Contains(LoadedStrings(snapshotBuffs), value => value == "LifeRule");
        Assert.Contains(LoadedStrings(duration), value => value == "RemainingDuration");
        Assert.Contains(LoadedStrings(duration), value => value == "Timer");
        Assert.Contains(LoadedStrings(duration), value => value == "duration");
        Assert.Contains(LoadedStrings(duration), value => value == "time");
        Assert.Contains(
            Calls(resolveIcon),
            IsCall(CheatBridgeType, "CreateEnemyBuffFallbackIcon"));

        Assert.Contains(Calls(draw), IsCall("UnityEngine.Event", "get_current"));
        Assert.Contains(Calls(draw), IsCall("UnityEngine.Camera", "get_pixelRect"));
        Assert.Contains(Calls(draw), IsCall(CheatBridgeType, "DrawEnemyBuffIcons"));
        Assert.DoesNotContain(Calls(draw), IsCall(CheatBridgeType, "SnapshotEnemyTargets"));
        Assert.DoesNotContain(Calls(draw), IsCall(CheatBridgeType, "SnapshotEnemyBuffIcons"));
        Assert.Contains(
            Calls(drawIcons),
            IsCall("UnityEngine.GUI", "DrawTextureWithTexCoords"));
        Assert.Contains(Calls(drawIcons), IsCall("UnityEngine.GUI", "DrawTexture"));
        Assert.Contains(LoadedStrings(drawIcons), value => value == "?");
        Assert.DoesNotContain(
            Calls(drawIcons),
            call => (call.DeclaringType.FullName is "UnityEngine.RenderTexture" or "UnityEngine.Texture2D")
                    && (call.Name is ".ctor" or "ReadPixels" or "EncodeToPNG"));
        Assert.DoesNotContain(
            Calls(drawIcons),
            call => call.DeclaringType.Namespace == "System.Reflection");
    }

    [Fact]
    public void DuplicateActivation_MustMatchExistingSecurityBinding_AndStartupFailureIsFailClosed()
    {
        using AssemblyDefinition assembly = ReadPlugin();
        TypeDefinition session = RequireType(assembly, SessionType);

        Assert.Contains(Calls(RequireMethod(session, "TryStart")), IsCall(SessionType, "MatchesActivation"));
        Assert.Contains(Calls(RequireMethod(session, "MatchesActivation")), IsCall(SessionType, "TokensEqual"));
        Assert.Contains(session.Fields, field => field.Name == "_startupFailure"
                                                && field.IsStatic
                                                && field.FieldType.FullName == "System.String");
    }

    [Fact]
    public void PipeServer_StartsOnlyAfterAListenerIsReady()
    {
        using AssemblyDefinition assembly = ReadPlugin();
        TypeDefinition server = RequireType(assembly, "Loopstructor.AutoPlayer.Plugin.PipeControlServer");

        MethodReference[] startCalls = Calls(RequireMethod(server, "Start")).ToArray();
        Assert.Contains(startCalls, IsCall("System.Threading.ManualResetEventSlim", "Wait"));
        Assert.Contains(startCalls, IsCall("Loopstructor.AutoPlayer.Plugin.PipeControlServer", "Dispose"));
        Assert.Contains(
            Calls(RequireMethod(server, "RegisterActiveServer")),
            IsCall("System.Threading.ManualResetEventSlim", "Set"));
    }

    private static AssemblyDefinition ReadPlugin()
    {
        string path = Path.Combine(AppContext.BaseDirectory, "Loopstructor.AutoPlayer.Plugin.dll");
        Assert.True(File.Exists(path), "Plugin assembly was not copied to the test output: " + path);
        return AssemblyDefinition.ReadAssembly(path);
    }

    private static TypeDefinition RequireType(AssemblyDefinition assembly, string fullName) =>
        assembly.MainModule.Types.Single(type => type.FullName == fullName);

    private static MethodDefinition RequireMethod(TypeDefinition type, string name) =>
        type.Methods.Single(method => method.Name == name);

    private static IEnumerable<MethodReference> Calls(MethodDefinition method) =>
        method.HasBody ? Calls(new[] { method }) : Enumerable.Empty<MethodReference>();

    private static IEnumerable<MethodReference> Calls(IEnumerable<MethodDefinition> methods) =>
        methods.Where(method => method.HasBody)
            .SelectMany(method => method.Body.Instructions)
            .Where(instruction => instruction.OpCode.Code
                is Code.Call or Code.Callvirt or Code.Newobj or Code.Ldftn or Code.Ldvirtftn)
            .Select(instruction => instruction.Operand)
            .OfType<MethodReference>();

    private static IEnumerable<string> LoadedStrings(MethodDefinition method) =>
        method.Body.Instructions
            .Where(instruction => instruction.OpCode.Code == Code.Ldstr)
            .Select(instruction => instruction.Operand)
            .OfType<string>();

    private static IEnumerable<MethodDefinition> AllMethods(TypeDefinition type) =>
        type.Methods.Concat(type.NestedTypes.SelectMany(AllMethods));

    private static Predicate<MethodReference> IsCall(string declaringType, string methodName) =>
        call => call.DeclaringType.FullName == declaringType && call.Name == methodName;

    private static int FindCall(
        IReadOnlyList<Instruction> instructions,
        string declaringType,
        string methodName,
        Code? requiredCode = null)
    {
        for (int index = 0; index < instructions.Count; index++)
        {
            Instruction instruction = instructions[index];
            if (instruction.Operand is MethodReference call
                && call.DeclaringType.FullName == declaringType
                && call.Name == methodName
                && (!requiredCode.HasValue || instruction.OpCode.Code == requiredCode.Value))
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
            if (instructions[index].Operand is GenericInstanceMethod call
                && call.DeclaringType.FullName == declaringType
                && call.Name == methodName
                && call.GenericArguments.Any(argument => argument.FullName == genericArgument))
            {
                return index;
            }
        }

        return -1;
    }

    private static int FindLoadedString(IReadOnlyList<Instruction> instructions, string value)
    {
        for (int index = 0; index < instructions.Count; index++)
        {
            if (instructions[index].OpCode.Code == Code.Ldstr
                && string.Equals(instructions[index].Operand as string, value, StringComparison.Ordinal))
            {
                return index;
            }
        }

        return -1;
    }

    private static int FindLastLoadedStringBefore(
        IReadOnlyList<Instruction> instructions,
        string value,
        int exclusiveEnd)
    {
        for (int index = Math.Min(exclusiveEnd, instructions.Count) - 1; index >= 0; index--)
        {
            if (instructions[index].OpCode.Code == Code.Ldstr &&
                string.Equals(instructions[index].Operand as string, value, StringComparison.Ordinal))
            {
                return index;
            }
        }

        return -1;
    }

    private static int FindLastCallBefore(
        IReadOnlyList<Instruction> instructions,
        string declaringType,
        string methodName,
        int exclusiveEnd)
    {
        for (int index = Math.Min(exclusiveEnd, instructions.Count) - 1; index >= 0; index--)
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

    private static int FindFieldLoad(
        IReadOnlyList<Instruction> instructions,
        string fieldName,
        int startIndex = 0)
    {
        for (int index = Math.Max(0, startIndex); index < instructions.Count; index++)
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

    private static void AssertStoresBooleanField(
        MethodDefinition method,
        string fieldName,
        bool expected)
    {
        Instruction[] instructions = method.Body.Instructions.ToArray();
        int store = FindFieldStore(instructions, fieldName);
        Assert.True(store >= 0, $"Expected {method.Name} to store {fieldName}.");
        Assert.Equal(
            expected ? 1 : 0,
            ReadLoadedInt32(PreviousMeaningfulInstruction(instructions, store)));
    }

    private static Instruction PreviousMeaningfulInstruction(
        IReadOnlyList<Instruction> instructions,
        int index)
    {
        for (int previous = index - 1; previous >= 0; previous--)
        {
            if (instructions[previous].OpCode.Code != Code.Nop)
            {
                return instructions[previous];
            }
        }

        throw new Xunit.Sdk.XunitException("Expected a preceding IL instruction.");
    }

    private static int ReadLoadedInt32(Instruction instruction) => instruction.OpCode.Code switch
    {
        Code.Ldc_I4_M1 => -1,
        Code.Ldc_I4_0 => 0,
        Code.Ldc_I4_1 => 1,
        Code.Ldc_I4_2 => 2,
        Code.Ldc_I4_3 => 3,
        Code.Ldc_I4_4 => 4,
        Code.Ldc_I4_5 => 5,
        Code.Ldc_I4_6 => 6,
        Code.Ldc_I4_7 => 7,
        Code.Ldc_I4_8 => 8,
        Code.Ldc_I4_S => (sbyte)instruction.Operand,
        Code.Ldc_I4 => (int)instruction.Operand,
        _ => throw new Xunit.Sdk.XunitException("Expected an int32 load before set_hideFlags.")
    };
}
