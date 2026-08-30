using System.Collections;
using System.Reflection;
using Newtonsoft.Json.Linq;
using UnityEngine;

namespace Loopstructor.AutoPlayer.Plugin;

/// <summary>
/// Reads and selects EventUI options through the registered WaveFunctionUI instance.
/// </summary>
internal static class WaveFunctionUiRuntimeFallback
{
    private const string EventPanelName = "EventUI";
    private const string WaveFunctionUiTypeName = "MetroTD.UISystem.WaveFunctionUI";
    private const string WaveFunctionItemTypeName =
        "MetroTD.UISystem.WaveFunctionUI_Item_Behaviour";
    private const string SpineAnimationControllerTypeName =
        "ActFramework_ByHZR.SpineUtil.SpineAnimationController";
    private const string ResultSource = "pluginReflection:WaveFunctionUI";

    private static ReflectionContract? _contract;

    internal static bool TryQueryOptions(out JObject result)
    {
        result = null!;
        if (!TryGetContract(out ReflectionContract contract) ||
            !TryFindActiveEventPanel(contract, out Component panel))
        {
            return false;
        }

        try
        {
            List<object> items = GetOrderedItems(contract, panel);
            JObject eventPanel = BuildPanelState(contract, panel, items);
            result = Success(
                "已通过轻量事件面板契约查询轨神事件选项。",
                new JObject { ["eventPanel"] = eventPanel });
            return true;
        }
        catch (Exception exception)
        {
            result = Error(
                "查询轨神事件选项失败：" + Unwrap(exception).Message,
                new JObject
                {
                    ["panel"] = EventPanelName,
                    ["source"] = ResultSource
                });
            return true;
        }
    }

    /// <summary>
    /// Queries the expected EventUI even after it has closed so a retained click
    /// identity can be reconciled without falling back to a whole-scene scan.
    /// </summary>
    internal static bool TryQueryPanelState(out JObject result)
    {
        if (TryQueryOptions(out result))
        {
            return true;
        }

        result = null!;
        if (!TryGetContract(out ReflectionContract contract) ||
            !TryFindRegisteredEventPanel(contract, out Component panel, out bool open) ||
            open)
        {
            return false;
        }

        result = Success(
            "已确认轨神事件面板关闭。",
            new JObject { ["eventPanel"] = BuildClosedPanelState(panel) });
        return true;
    }

    internal static bool TryChooseOption(JObject? arguments, out JObject result)
    {
        result = null!;
        string requestedPanel = arguments?["panel"]?.Value<string>() ?? string.Empty;
        if (!string.Equals(requestedPanel, EventPanelName, StringComparison.Ordinal) ||
            !TryGetContract(out ReflectionContract contract) ||
            !TryFindActiveEventPanel(contract, out Component panel))
        {
            return false;
        }

        List<object> items;
        JObject eventPanel;
        try
        {
            items = GetOrderedItems(contract, panel);
            eventPanel = BuildPanelState(contract, panel, items);
        }
        catch (Exception exception)
        {
            result = Error(
                "读取轨神事件选项失败：" + Unwrap(exception).Message,
                new JObject
                {
                    ["panel"] = EventPanelName,
                    ["source"] = ResultSource
                });
            return true;
        }

        int index = ReadIndex(arguments);
        int requestedPanelInstanceId = ReadIdentity(arguments, "panelInstanceId");
        int requestedItemInstanceId = ReadIdentity(arguments, "instanceId");
        JArray options = eventPanel["options"] as JArray ?? new JArray();
        if (eventPanel["snapshotComplete"]?.Value<bool>() != true ||
            index < 0 ||
            index >= options.Count ||
            options[index] is not JObject selectedState)
        {
            result = Error(
                "轨神事件选项快照尚未完整生成或索引越界，未执行点击。",
                new JObject
                {
                    ["panel"] = EventPanelName,
                    ["panelInstanceId"] = eventPanel["panelInstanceId"],
                    ["snapshotComplete"] = eventPanel["snapshotComplete"],
                    ["index"] = index,
                    ["count"] = options.Count,
                    ["invocationStarted"] = false,
                    ["source"] = ResultSource
                });
            return true;
        }

        int actualPanelInstanceId = eventPanel["panelInstanceId"]?.Value<int>() ?? 0;
        int actualItemInstanceId = selectedState["instanceId"]?.Value<int>() ?? 0;
        if (requestedPanelInstanceId == 0 ||
            requestedItemInstanceId == 0 ||
            requestedPanelInstanceId != actualPanelInstanceId ||
            requestedItemInstanceId != actualItemInstanceId ||
            selectedState["index"]?.Value<int>() != index)
        {
            result = Error(
                "轨神事件面板或选项身份已变化，未执行点击。",
                new JObject
                {
                    ["panel"] = EventPanelName,
                    ["index"] = index,
                    ["requestedPanelInstanceId"] = requestedPanelInstanceId,
                    ["actualPanelInstanceId"] = actualPanelInstanceId,
                    ["requestedInstanceId"] = requestedItemInstanceId,
                    ["actualInstanceId"] = actualItemInstanceId,
                    ["invocationStarted"] = false,
                    ["source"] = ResultSource
                });
            return true;
        }

        if (selectedState["buttonActive"]?.Value<bool>() != true ||
            selectedState["conditionPass"]?.Value<bool>() != true)
        {
            selectedState["invocationStarted"] = false;
            result = Error("轨神事件选项当前不可用，未执行点击。", selectedState);
            return true;
        }

        bool invocationStarted = false;
        try
        {
            object item = items[index];
            if (item is not UnityEngine.Object unityItem ||
                unityItem == null ||
                unityItem.GetInstanceID() != actualItemInstanceId)
            {
                selectedState["invocationStarted"] = false;
                result = Error("轨神事件选项在点击前已经被替换，未执行点击。", selectedState);
                return true;
            }

            invocationStarted = true;
            contract.OnClick.Invoke(item, null);
            selectedState["invocationStarted"] = true;
            selectedState["selectionSubmitted"] = true;
            result = Success("已选择指定轨神事件选项。", selectedState);
            return true;
        }
        catch (Exception exception)
        {
            JObject failureState = (JObject)selectedState.DeepClone();
            failureState["panel"] = EventPanelName;
            failureState["index"] = index;
            failureState["source"] = ResultSource;
            if (invocationStarted)
            {
                failureState["outcomeUnknown"] = true;
                failureState["needsReconciliation"] = true;
                failureState["invocationStarted"] = true;
            }

            result = Error(
                "选择轨神事件选项失败：" + Unwrap(exception).Message,
                failureState);
            return true;
        }
    }

    private static JObject BuildPanelState(
        ReflectionContract contract,
        Component panel,
        IReadOnlyList<object> items)
    {
        int panelInstanceId = panel.GetInstanceID();
        JArray options = new();
        bool itemBindingsComplete = true;
        for (int index = 0; index < items.Count; index++)
        {
            JObject option = BuildOptionState(contract, items[index], index, panelInstanceId);
            itemBindingsComplete &= option["bindingComplete"]?.Value<bool>() == true;
            options.Add(option);
        }

        object? currentData = contract.CurrentDataField.GetValue(panel);
        object? eventTag = currentData == null ? null : contract.EventTagField.GetValue(currentData);
        bool optionsGenerated = contract.OptionsGeneratedField.GetValue(panel) is bool generated && generated;
        JObject appearanceAnimation;
        try
        {
            appearanceAnimation = BuildAppearanceAnimationState(contract, panel);
        }
        catch
        {
            // Animation telemetry is an additional fail-closed gate. A missing or changing Spine
            // binding must not make the otherwise valid event option snapshot unreadable.
            appearanceAnimation = UnreadableAppearanceAnimation();
        }
        return new JObject
        {
            ["panel"] = EventPanelName,
            ["panelOpen"] = true,
            ["panelInstanceId"] = panelInstanceId,
            ["snapshotComplete"] = optionsGenerated && itemBindingsComplete,
            ["optionsGenerated"] = optionsGenerated,
            ["count"] = options.Count,
            ["eventKey"] = currentData == null
                ? string.Empty
                : contract.EventKeyField.GetValue(currentData)?.ToString() ?? string.Empty,
            ["eventTag"] = EnumName(eventTag),
            ["eventTagValue"] = EnumValue(eventTag),
            ["shouldShowAppearanceAnimation"] = appearanceAnimation["shouldShow"],
            ["appearanceAnimationReadable"] = appearanceAnimation["readable"],
            ["appearanceAnimationComplete"] = appearanceAnimation["complete"],
            ["appearanceAnimationName"] = appearanceAnimation["name"],
            ["appearanceAnimationDurationSeconds"] = appearanceAnimation["durationSeconds"],
            ["options"] = options,
            ["source"] = ResultSource
        };
    }

    private static JObject BuildAppearanceAnimationState(ReflectionContract contract, Component panel)
    {
        bool shouldShow = contract.ShouldShowEventSpineField?.GetValue(panel) is not bool configured || configured;
        if (!shouldShow)
        {
            return new JObject
            {
                ["shouldShow"] = false,
                ["readable"] = true,
                ["complete"] = true,
                ["name"] = string.Empty,
                ["durationSeconds"] = 0f
            };
        }

        if (contract.SpineAnimationControllerType == null ||
            contract.GetCurrentAnimationNameMethod == null ||
            contract.AnimationIsCompleteMethod == null)
        {
            return UnreadableAppearanceAnimation();
        }

        Component? controller = panel
            .GetComponentsInChildren(contract.SpineAnimationControllerType, true)
            .FirstOrDefault(IsActiveSceneComponent);
        if (controller == null)
        {
            return UnreadableAppearanceAnimation();
        }

        string animationName =
            contract.GetCurrentAnimationNameMethod.Invoke(controller, new object[] { 0 })?.ToString()
            ?? string.Empty;
        float durationSeconds = 0f;
        if (contract.GetAnimationTimeMethod?.Invoke(controller, new object[] { "Appear" }) is object duration)
        {
            try
            {
                durationSeconds = Convert.ToSingle(duration);
            }
            catch
            {
                durationSeconds = 0f;
            }
        }

        if (string.IsNullOrWhiteSpace(animationName))
        {
            return new JObject
            {
                ["shouldShow"] = true,
                ["readable"] = false,
                ["complete"] = false,
                ["name"] = string.Empty,
                ["durationSeconds"] = durationSeconds
            };
        }

        bool complete = !string.Equals(animationName, "Appear", StringComparison.Ordinal) ||
                        contract.AnimationIsCompleteMethod.Invoke(
                            controller,
                            new object[] { 0, "Appear" }) is bool isComplete && isComplete;
        return new JObject
        {
            ["shouldShow"] = true,
            ["readable"] = true,
            ["complete"] = complete,
            ["name"] = animationName,
            ["durationSeconds"] = durationSeconds
        };
    }

    private static JObject UnreadableAppearanceAnimation() => new()
    {
        ["shouldShow"] = true,
        ["readable"] = false,
        ["complete"] = false,
        ["name"] = string.Empty,
        ["durationSeconds"] = 0f
    };

    private static JObject BuildClosedPanelState(Component panel) => new()
    {
        ["panel"] = EventPanelName,
        ["panelOpen"] = false,
        ["panelInstanceId"] = panel.GetInstanceID(),
        ["snapshotComplete"] = true,
        ["optionsGenerated"] = false,
        ["shouldShowAppearanceAnimation"] = false,
        ["appearanceAnimationReadable"] = true,
        ["appearanceAnimationComplete"] = true,
        ["appearanceAnimationName"] = string.Empty,
        ["appearanceAnimationDurationSeconds"] = 0f,
        ["count"] = 0,
        ["options"] = new JArray(),
        ["source"] = ResultSource
    };

    private static JObject BuildOptionState(
        ReflectionContract contract,
        object item,
        int index,
        int panelInstanceId)
    {
        object? button = contract.ButtonField.GetValue(item);
        object? currentItem = contract.CurrentItemField.GetValue(item);
        object? contentText = contract.ContentTextField.GetValue(item);
        object? optionSide = currentItem == null
            ? null
            : contract.OptionSideField.GetValue(currentItem);
        JArray behaviourTypes = new();
        JArray behaviourTypeIds = new();
        JArray behaviourNames = new();
        JArray tagEnums = new();
        JArray tagValues = new();

        if (currentItem != null &&
            contract.BehaviourListField.GetValue(currentItem) is IEnumerable behaviours)
        {
            foreach (object? behaviour in behaviours)
            {
                if (behaviour == null) continue;
                Type behaviourType = behaviour.GetType();
                behaviourTypes.Add(behaviourType.Name);
                behaviourTypeIds.Add(behaviourType.FullName ?? behaviourType.Name);
                behaviourNames.Add(behaviour is UnityEngine.Object unityBehaviour && unityBehaviour != null
                    ? unityBehaviour.name
                    : string.Empty);
            }
        }

        if (currentItem != null && contract.TagListField.GetValue(currentItem) is IEnumerable tags)
        {
            foreach (object? tag in tags)
            {
                tagEnums.Add(EnumName(tag));
                tagValues.Add(EnumValue(tag));
            }
        }

        bool buttonActive = button != null &&
                            contract.ButtonActiveProperty.GetValue(button, null) is bool active &&
                            active;
        bool conditionPass = currentItem != null &&
                             contract.ConditionPassMethod.Invoke(currentItem, null) is bool pass &&
                             pass;
        bool bindingComplete = button != null && currentItem != null && contentText != null;
        int buttonInstanceId = button is UnityEngine.Object unityButton && unityButton != null
            ? unityButton.GetInstanceID()
            : 0;
        int instanceId = item is UnityEngine.Object unityItem && unityItem != null
            ? unityItem.GetInstanceID()
            : 0;

        return new JObject
        {
            ["panel"] = EventPanelName,
            ["index"] = index,
            ["panelInstanceId"] = panelInstanceId,
            ["instanceId"] = instanceId,
            ["buttonInstanceId"] = buttonInstanceId,
            ["buttonActive"] = buttonActive,
            ["conditionPass"] = conditionPass,
            ["bindingComplete"] = bindingComplete,
            ["displayText"] = contentText == null
                ? string.Empty
                : contract.TextProperty.GetValue(contentText, null)?.ToString() ?? string.Empty,
            ["currentItemType"] = currentItem?.GetType().FullName ?? string.Empty,
            ["optionName"] = currentItem == null
                ? string.Empty
                : contract.OptionNameField.GetValue(currentItem)?.ToString() ?? string.Empty,
            ["optionSide"] = EnumName(optionSide),
            ["optionSideValue"] = EnumValue(optionSide),
            ["styleTag"] = currentItem == null
                ? string.Empty
                : contract.StyleTagField.GetValue(currentItem)?.ToString() ?? string.Empty,
            ["tagEnums"] = tagEnums,
            ["tagValues"] = tagValues,
            ["behaviourTypes"] = behaviourTypes,
            ["behaviourTypeIds"] = behaviourTypeIds,
            ["behaviourNames"] = behaviourNames,
            ["extraDataType"] = currentItem == null
                ? string.Empty
                : contract.ExtraDataField.GetValue(currentItem)?.GetType().FullName ?? string.Empty,
            ["source"] = ResultSource
        };
    }

    private static List<object> GetOrderedItems(ReflectionContract contract, Component panel)
    {
        if (contract.ContentField.GetValue(panel) is not Transform content || content == null)
        {
            throw new InvalidOperationException("EventUI.content 未绑定。");
        }

        List<object> items = new();
        Component[] childItems = content.GetComponentsInChildren(contract.WaveFunctionItemType, true);
        foreach (Component item in childItems)
        {
            if (IsActiveSceneComponent(item) && item.transform.IsChildOf(content))
            {
                items.Add(item);
            }
        }

        return items;
    }

    private static bool TryFindActiveEventPanel(ReflectionContract contract, out Component panel)
    {
        if (TryFindRegisteredEventPanel(contract, out Component registered, out bool open) &&
            open &&
            IsActiveSceneComponent(registered))
        {
            panel = registered;
            return true;
        }

        panel = null!;
        return false;
    }

    private static bool TryFindRegisteredEventPanel(
        ReflectionContract contract,
        out Component panel,
        out bool open)
    {
        panel = null!;
        open = false;
        try
        {
            if (contract.InstanceProperty.GetValue(null, null) is not Component registered ||
                registered == null ||
                registered.gameObject == null ||
                !registered.gameObject.scene.IsValid() ||
                contract.IsOpenMethod.Invoke(registered, null) is not bool registeredOpen)
            {
                return false;
            }

            panel = registered;
            open = registeredOpen;
            return true;
        }
        catch
        {
            // Contract and panel discovery are fail-closed so RuntimeBridge can reject the command.
            return false;
        }
    }

    private static bool IsActiveSceneComponent(Component component) =>
        component != null &&
        component.gameObject != null &&
        component.gameObject.scene.IsValid() &&
        component.gameObject.activeInHierarchy;

    private static bool TryGetContract(out ReflectionContract contract)
    {
        if (_contract != null)
        {
            contract = _contract;
            return true;
        }

        Type? panelType = FindType(WaveFunctionUiTypeName);
        Type? itemType = FindType(WaveFunctionItemTypeName);
        const BindingFlags InstanceMembers =
            BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance;
        if (panelType == null || itemType == null)
        {
            contract = null!;
            return false;
        }

        PropertyInfo? instanceProperty = panelType.GetProperty(
            "Instance",
            BindingFlags.Public | BindingFlags.Static);
        MethodInfo? isOpenMethod = panelType.GetMethod(
            "IsOpen",
            BindingFlags.Public | BindingFlags.Instance,
            null,
            Type.EmptyTypes,
            null);
        FieldInfo? contentField = panelType.GetField("content", InstanceMembers);
        FieldInfo? currentDataField = panelType.GetField("m_currentData", InstanceMembers);
        FieldInfo? optionsGeneratedField = panelType.GetField("m_optionsGenerated", InstanceMembers);
        FieldInfo? shouldShowEventSpineField = panelType.GetField("m_shouldShowEventSpine", InstanceMembers);
        Type? spineAnimationControllerType = FindType(SpineAnimationControllerTypeName);
        MethodInfo? getCurrentAnimationName = spineAnimationControllerType?.GetMethod(
            "GetCurrentAnimationName",
            InstanceMembers,
            null,
            new[] { typeof(int) },
            null);
        MethodInfo? animationIsComplete = spineAnimationControllerType?.GetMethod(
            "AnimationIsComplete",
            InstanceMembers,
            null,
            new[] { typeof(int), typeof(string) },
            null);
        MethodInfo? getAnimationTime = spineAnimationControllerType?.GetMethod(
            "GetAnimationTime",
            InstanceMembers,
            null,
            new[] { typeof(string) },
            null);
        FieldInfo? buttonField = itemType.GetField("btn", InstanceMembers);
        FieldInfo? contentTextField = itemType.GetField("contentText", InstanceMembers);
        FieldInfo? currentItemField = itemType.GetField("currentItem", InstanceMembers);
        MethodInfo? onClick = itemType.GetMethod(
            "OnClick",
            InstanceMembers,
            null,
            Type.EmptyTypes,
            null);
        PropertyInfo? buttonActive = buttonField?.FieldType.GetProperty("BtnActive", InstanceMembers);
        PropertyInfo? text = contentTextField?.FieldType.GetProperty("text", InstanceMembers);
        MethodInfo? conditionPass = currentItemField?.FieldType.GetMethod(
            "IsConditionPass",
            InstanceMembers,
            null,
            Type.EmptyTypes,
            null);
        FieldInfo? behaviourList = currentItemField?.FieldType.GetField("behaviourList", InstanceMembers);
        FieldInfo? optionName = currentItemField?.FieldType.GetField("optionName", InstanceMembers);
        FieldInfo? optionSide = currentItemField?.FieldType.GetField("optionSide", InstanceMembers);
        FieldInfo? styleTag = currentItemField?.FieldType.GetField("styleTag", InstanceMembers);
        FieldInfo? tagList = currentItemField?.FieldType.GetField("tagList", InstanceMembers);
        FieldInfo? extraData = currentItemField?.FieldType.GetField("extraData", InstanceMembers);
        FieldInfo? eventKey = currentDataField?.FieldType.GetField("eventKey", InstanceMembers);
        FieldInfo? eventTag = currentDataField?.FieldType.GetField("tag", InstanceMembers);

        if (instanceProperty == null || isOpenMethod == null || contentField == null ||
            currentDataField == null || optionsGeneratedField == null || buttonField == null ||
            contentTextField == null || currentItemField == null || onClick == null ||
            buttonActive == null || text == null || conditionPass == null ||
            behaviourList == null || optionName == null || optionSide == null ||
            styleTag == null || tagList == null || extraData == null ||
            eventKey == null || eventTag == null)
        {
            contract = null!;
            return false;
        }

        _contract = new ReflectionContract(
            panelType,
            itemType,
            instanceProperty,
            isOpenMethod,
            contentField,
            currentDataField,
            optionsGeneratedField,
            shouldShowEventSpineField,
            spineAnimationControllerType,
            getCurrentAnimationName,
            animationIsComplete,
            getAnimationTime,
            buttonField,
            buttonActive,
            contentTextField,
            text,
            currentItemField,
            conditionPass,
            behaviourList,
            optionName,
            optionSide,
            styleTag,
            tagList,
            extraData,
            eventKey,
            eventTag,
            onClick);
        contract = _contract;
        return true;
    }

    private static Type? FindType(string fullName)
    {
        foreach (Assembly assembly in AppDomain.CurrentDomain.GetAssemblies())
        {
            Type? type = assembly.GetType(fullName, false);
            if (type != null) return type;
        }

        return null;
    }

    private static int ReadIndex(JObject? arguments)
    {
        try
        {
            return arguments?["index"]?.Value<int>() ?? -1;
        }
        catch
        {
            return -1;
        }
    }

    private static int ReadIdentity(JObject? arguments, string name)
    {
        try
        {
            int value = arguments?[name]?.Value<int>() ?? 0;
            return value != 0 ? value : 0;
        }
        catch
        {
            return 0;
        }
    }

    private static string EnumName(object? value) =>
        value != null && value.GetType().IsEnum ? value.ToString() ?? string.Empty : string.Empty;

    private static JToken EnumValue(object? value)
    {
        if (value == null || !value.GetType().IsEnum) return JValue.CreateNull();
        try
        {
            return new JValue(Convert.ToInt64(value));
        }
        catch
        {
            return JValue.CreateNull();
        }
    }

    private static JObject Success(string message, JObject state) => new()
    {
        ["success"] = true,
        ["message"] = message,
        ["suggestion"] = JValue.CreateNull(),
        ["data"] = new JObject { ["state"] = state }
    };

    private static JObject Error(string message, JObject state) => new()
    {
        ["success"] = false,
        ["message"] = message,
        ["suggestion"] = "请重新查询 EventUI，并使用同一快照返回的面板、索引和选项身份。",
        ["data"] = new JObject { ["state"] = state }
    };

    private static Exception Unwrap(Exception exception) =>
        exception is TargetInvocationException target && target.InnerException != null
            ? target.InnerException
            : exception;

    private sealed class ReflectionContract
    {
        public ReflectionContract(
            Type waveFunctionUiType,
            Type waveFunctionItemType,
            PropertyInfo instanceProperty,
            MethodInfo isOpenMethod,
            FieldInfo contentField,
            FieldInfo currentDataField,
            FieldInfo optionsGeneratedField,
            FieldInfo? shouldShowEventSpineField,
            Type? spineAnimationControllerType,
            MethodInfo? getCurrentAnimationNameMethod,
            MethodInfo? animationIsCompleteMethod,
            MethodInfo? getAnimationTimeMethod,
            FieldInfo buttonField,
            PropertyInfo buttonActiveProperty,
            FieldInfo contentTextField,
            PropertyInfo textProperty,
            FieldInfo currentItemField,
            MethodInfo conditionPassMethod,
            FieldInfo behaviourListField,
            FieldInfo optionNameField,
            FieldInfo optionSideField,
            FieldInfo styleTagField,
            FieldInfo tagListField,
            FieldInfo extraDataField,
            FieldInfo eventKeyField,
            FieldInfo eventTagField,
            MethodInfo onClick)
        {
            WaveFunctionUiType = waveFunctionUiType;
            WaveFunctionItemType = waveFunctionItemType;
            InstanceProperty = instanceProperty;
            IsOpenMethod = isOpenMethod;
            ContentField = contentField;
            CurrentDataField = currentDataField;
            OptionsGeneratedField = optionsGeneratedField;
            ShouldShowEventSpineField = shouldShowEventSpineField;
            SpineAnimationControllerType = spineAnimationControllerType;
            GetCurrentAnimationNameMethod = getCurrentAnimationNameMethod;
            AnimationIsCompleteMethod = animationIsCompleteMethod;
            GetAnimationTimeMethod = getAnimationTimeMethod;
            ButtonField = buttonField;
            ButtonActiveProperty = buttonActiveProperty;
            ContentTextField = contentTextField;
            TextProperty = textProperty;
            CurrentItemField = currentItemField;
            ConditionPassMethod = conditionPassMethod;
            BehaviourListField = behaviourListField;
            OptionNameField = optionNameField;
            OptionSideField = optionSideField;
            StyleTagField = styleTagField;
            TagListField = tagListField;
            ExtraDataField = extraDataField;
            EventKeyField = eventKeyField;
            EventTagField = eventTagField;
            OnClick = onClick;
        }

        public Type WaveFunctionUiType { get; }
        public Type WaveFunctionItemType { get; }
        public PropertyInfo InstanceProperty { get; }
        public MethodInfo IsOpenMethod { get; }
        public FieldInfo ContentField { get; }
        public FieldInfo CurrentDataField { get; }
        public FieldInfo OptionsGeneratedField { get; }
        public FieldInfo? ShouldShowEventSpineField { get; }
        public Type? SpineAnimationControllerType { get; }
        public MethodInfo? GetCurrentAnimationNameMethod { get; }
        public MethodInfo? AnimationIsCompleteMethod { get; }
        public MethodInfo? GetAnimationTimeMethod { get; }
        public FieldInfo ButtonField { get; }
        public PropertyInfo ButtonActiveProperty { get; }
        public FieldInfo ContentTextField { get; }
        public PropertyInfo TextProperty { get; }
        public FieldInfo CurrentItemField { get; }
        public MethodInfo ConditionPassMethod { get; }
        public FieldInfo BehaviourListField { get; }
        public FieldInfo OptionNameField { get; }
        public FieldInfo OptionSideField { get; }
        public FieldInfo StyleTagField { get; }
        public FieldInfo TagListField { get; }
        public FieldInfo ExtraDataField { get; }
        public FieldInfo EventKeyField { get; }
        public FieldInfo EventTagField { get; }
        public MethodInfo OnClick { get; }
    }
}
