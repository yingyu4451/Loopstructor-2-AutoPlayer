using System.Collections;
using System.Reflection;
using Newtonsoft.Json.Linq;
using UnityEngine;

namespace Loopstructor.AutoPlayer.Plugin;

/// <summary>
/// Reads RepairUI options without relying on the game's hierarchy-name heuristic.
/// </summary>
internal static class RepairUiRuntimeFallback
{
    private const string RepairPanelName = "RepairUI";
    private const string RepairUiTypeName = "MetroTD.UISystem.RepairUI";
    private const string RepairChoiceTypeName = "MetroTD.UISystem.UIItem_RepairChoose";
    private const string WaveFunctionItemTypeName = "MetroTD.UISystem.WaveFunctionUI_Item_Behaviour";
    private const string ResultSource = "pluginReflection:RepairUI";

    private static ReflectionContract? _contract;

    /// <summary>
    /// Returns a RuntimeBridge-compatible result while an open RepairUI exists.
    /// </summary>
    internal static bool TryQueryOptions(out JObject result)
    {
        result = null!;
        if (!TryGetContract(out ReflectionContract contract)
            || !TryFindActiveRepairPanel(contract, out Component panel))
        {
            return false;
        }

        try
        {
            List<object> items = GetOrderedItems(contract, panel);
            JObject repairPanel = BuildPanelState(contract, panel, items);
            result = Success(
                "\u5df2\u901a\u8fc7\u63d2\u4ef6\u56de\u9000\u67e5\u8be2\u4fee\u7406\u9009\u9879\u3002",
                new JObject { ["repairPanel"] = repairPanel });
            return true;
        }
        catch (Exception exception)
        {
            result = Error(
                "\u67e5\u8be2\u4fee\u7406\u9009\u9879\u5931\u8d25\uff1a" + Unwrap(exception).Message,
                new JObject { ["panel"] = RepairPanelName });
            return true;
        }
    }

    /// <summary>
    /// Queries the expected RepairUI even after it has closed so a retained click
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
            !TryFindRegisteredRepairPanel(contract, out Component panel, out bool open) ||
            open)
        {
            return false;
        }

        result = Success(
            "已确认修整面板关闭。",
            new JObject { ["repairPanel"] = BuildClosedPanelState(panel) });
        return true;
    }

    /// <summary>
    /// Invokes the currently visible RepairUI option at <c>arguments.index</c>.
    /// </summary>
    internal static bool TryChooseOption(JObject? arguments, out JObject result)
    {
        result = null!;
        string requestedPanel = arguments?["panel"]?.Value<string>() ?? "EventUI";
        if (!string.Equals(requestedPanel, RepairPanelName, StringComparison.OrdinalIgnoreCase)
            || !TryGetContract(out ReflectionContract contract)
            || !TryFindActiveRepairPanel(contract, out Component panel))
        {
            return false;
        }

        List<object> items;
        try
        {
            items = GetOrderedItems(contract, panel);
        }
        catch (Exception exception)
        {
            result = Error(
                "\u8bfb\u53d6\u4fee\u7406\u9009\u9879\u5931\u8d25\uff1a" + Unwrap(exception).Message,
                new JObject { ["panel"] = RepairPanelName });
            return true;
        }

        int index = ReadIndex(arguments);
        if (index < 0 || index >= items.Count)
        {
            result = Error(
                "\u4fee\u7406\u9009\u9879\u7d22\u5f15\u8d8a\u754c\u3002",
                new JObject
                {
                    ["panel"] = RepairPanelName,
                    ["index"] = index,
                    ["count"] = items.Count,
                    ["source"] = ResultSource
                });
            return true;
        }

        object item = items[index];
        int requestedPanelInstanceId = ReadIdentity(arguments, "panelInstanceId");
        int requestedItemInstanceId = ReadIdentity(arguments, "instanceId");
        int actualPanelInstanceId = panel.GetInstanceID();
        int actualItemInstanceId = item is UnityEngine.Object unityItem
            ? unityItem.GetInstanceID()
            : 0;
        if (requestedPanelInstanceId == 0 || requestedItemInstanceId == 0 ||
            requestedPanelInstanceId != actualPanelInstanceId ||
            requestedItemInstanceId != actualItemInstanceId)
        {
            result = Error(
                "修整选项身份已变化，未执行点击。",
                new JObject
                {
                    ["panel"] = RepairPanelName,
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

        JObject? selectedState = null;
        bool clickStarted = false;
        try
        {
            // Snapshot before OnClick because the option may close and destroy its own panel.
            selectedState = BuildOptionState(contract, item, index, panel.GetInstanceID());
            if (selectedState["buttonActive"]?.Value<bool>() != true ||
                selectedState["conditionPass"]?.Value<bool>() != true)
            {
                selectedState["invocationStarted"] = false;
                result = Error("修整选项当前不可用，未执行点击。", selectedState);
                return true;
            }

            clickStarted = true;
            contract.OnClick.Invoke(item, null);
            result = Success(
                "\u5df2\u901a\u8fc7\u63d2\u4ef6\u56de\u9000\u9009\u62e9\u4fee\u7406\u9009\u9879\u3002",
                selectedState);
            return true;
        }
        catch (Exception exception)
        {
            JObject failureState = selectedState == null
                ? new JObject()
                : (JObject)selectedState.DeepClone();
            failureState.Merge(new JObject
            {
                ["panel"] = RepairPanelName,
                ["index"] = index,
                ["source"] = ResultSource
            });
            if (clickStarted)
            {
                failureState["outcomeUnknown"] = true;
                failureState["needsReconciliation"] = true;
                failureState["invocationStarted"] = true;
            }
            result = Error(
                "\u9009\u62e9\u4fee\u7406\u9009\u9879\u5931\u8d25\uff1a" + Unwrap(exception).Message,
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
        for (int index = 0; index < items.Count; index++)
        {
            options.Add(BuildOptionState(contract, items[index], index, panelInstanceId));
        }

        return new JObject
        {
            ["panel"] = RepairPanelName,
            ["panelOpen"] = true,
            ["panelInstanceId"] = panelInstanceId,
            ["snapshotComplete"] = true,
            ["count"] = options.Count,
            ["options"] = options,
            ["source"] = ResultSource
        };
    }

    private static JObject BuildClosedPanelState(Component panel) => new()
    {
        ["panel"] = RepairPanelName,
        ["panelOpen"] = false,
        ["panelInstanceId"] = panel.GetInstanceID(),
        ["snapshotComplete"] = true,
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
        JArray behaviourTypes = new();
        JArray behaviourTypeIds = new();
        JArray behaviourNames = new();

        if (currentItem != null
            && contract.BehaviourListField.GetValue(currentItem) is IEnumerable behaviours)
        {
            foreach (object? behaviour in behaviours)
            {
                if (behaviour == null)
                {
                    continue;
                }

                Type behaviourType = behaviour.GetType();
                behaviourTypes.Add(behaviourType.Name);
                behaviourTypeIds.Add(behaviourType.FullName ?? behaviourType.Name);
                behaviourNames.Add(behaviour is UnityEngine.Object unityBehaviour && unityBehaviour != null
                    ? unityBehaviour.name
                    : string.Empty);
            }
        }

        bool buttonActive = button != null
                            && contract.ButtonActiveProperty.GetValue(button, null) is bool active
                            && active;
        bool conditionPass = currentItem == null
                             || contract.ConditionPassMethod.Invoke(currentItem, null) is not bool pass
                             || pass;
        string displayText = contentText == null
            ? string.Empty
            : contract.TextProperty.GetValue(contentText, null)?.ToString() ?? string.Empty;
        int instanceId = item is UnityEngine.Object unityObject ? unityObject.GetInstanceID() : 0;

        return new JObject
        {
            ["panel"] = RepairPanelName,
            ["index"] = index,
            ["panelInstanceId"] = panelInstanceId,
            ["instanceId"] = instanceId,
            ["buttonActive"] = buttonActive,
            ["conditionPass"] = conditionPass,
            ["displayText"] = displayText,
            ["currentItemType"] = currentItem?.GetType().FullName ?? string.Empty,
            ["optionName"] = currentItem == null
                ? string.Empty
                : contract.OptionNameField?.GetValue(currentItem)?.ToString() ?? string.Empty,
            ["behaviourTypes"] = behaviourTypes,
            ["behaviourTypeIds"] = behaviourTypeIds,
            ["behaviourNames"] = behaviourNames,
            ["extraDataType"] = currentItem == null
                ? string.Empty
                : contract.ExtraDataField?.GetValue(currentItem)?.GetType().FullName ?? string.Empty,
            ["source"] = ResultSource
        };
    }

    private static List<object> GetOrderedItems(ReflectionContract contract, Component panel)
    {
        List<object> items = new();
        HashSet<int> seenInstanceIds = new();

        if (contract.ConstChoicesField?.GetValue(panel) is IEnumerable constChoices
            && contract.ChoiceItemBehaviourField != null)
        {
            foreach (object? choice in constChoices)
            {
                object? item = choice == null ? null : contract.ChoiceItemBehaviourField.GetValue(choice);
                AddActiveChildItem(contract, panel, item, seenInstanceIds, items);
            }
        }

        Component[] childItems = panel.GetComponentsInChildren(contract.WaveFunctionItemType, true);
        foreach (Component childItem in childItems)
        {
            AddActiveChildItem(contract, panel, childItem, seenInstanceIds, items);
        }

        return items;
    }

    private static void AddActiveChildItem(
        ReflectionContract contract,
        Component panel,
        object? candidate,
        ISet<int> seenInstanceIds,
        ICollection<object> items)
    {
        if (!contract.WaveFunctionItemType.IsInstanceOfType(candidate)
            || candidate is not Component component
            || !IsActiveSceneComponent(component)
            || !component.transform.IsChildOf(panel.transform))
        {
            return;
        }

        int instanceId = component.GetInstanceID();
        if (seenInstanceIds.Add(instanceId))
        {
            items.Add(candidate);
        }
    }

    private static bool TryFindActiveRepairPanel(ReflectionContract contract, out Component panel)
    {
        if (TryFindRegisteredRepairPanel(contract, out Component registered, out bool open) &&
            open &&
            IsActiveSceneComponent(registered))
        {
            panel = registered;
            return true;
        }

        panel = null!;
        return false;
    }

    private static bool TryFindRegisteredRepairPanel(
        ReflectionContract contract,
        out Component panel,
        out bool open)
    {
        panel = null!;
        open = false;
        try
        {
            if (contract.InstanceProperty?.GetValue(null, null) is not Component registered ||
                registered == null ||
                registered.gameObject == null ||
                !registered.gameObject.scene.IsValid() ||
                contract.IsOpenMethod?.Invoke(registered, null) is not bool registeredOpen)
            {
                return false;
            }

            panel = registered;
            open = registeredOpen;
            return true;
        }
        catch
        {
            // Discovery is fail-closed so RuntimeBridge can reject the command safely.
            return false;
        }
    }

    private static bool IsActiveSceneComponent(Component component) =>
        component != null
        && component.gameObject != null
        && component.gameObject.scene.IsValid()
        && component.gameObject.activeInHierarchy;

    private static bool TryGetContract(out ReflectionContract contract)
    {
        if (_contract != null)
        {
            contract = _contract;
            return true;
        }

        Type? repairUiType = FindType(RepairUiTypeName);
        Type? repairChoiceType = FindType(RepairChoiceTypeName);
        Type? waveFunctionItemType = FindType(WaveFunctionItemTypeName);
        const BindingFlags InstanceMembers = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance;
        if (repairUiType == null || waveFunctionItemType == null)
        {
            contract = null!;
            return false;
        }

        FieldInfo? buttonField = waveFunctionItemType.GetField("btn", InstanceMembers);
        FieldInfo? contentTextField = waveFunctionItemType.GetField("contentText", InstanceMembers);
        FieldInfo? currentItemField = waveFunctionItemType.GetField("currentItem", InstanceMembers);
        MethodInfo? onClick = waveFunctionItemType.GetMethod(
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
        PropertyInfo? instanceProperty = repairUiType.GetProperty(
            "Instance",
            BindingFlags.Public | BindingFlags.Static);
        MethodInfo? isOpenMethod = repairUiType.GetMethod(
            "IsOpen",
            BindingFlags.Public | BindingFlags.Instance,
            null,
            Type.EmptyTypes,
            null);
        if (buttonField == null || contentTextField == null || currentItemField == null
            || onClick == null || buttonActive == null || text == null || conditionPass == null
            || behaviourList == null || instanceProperty == null || isOpenMethod == null)
        {
            contract = null!;
            return false;
        }

        _contract = new ReflectionContract(
            repairUiType,
            waveFunctionItemType,
            instanceProperty,
            isOpenMethod,
            repairUiType.GetField("constChoices", InstanceMembers),
            repairChoiceType?.GetField("itemBehaviour", InstanceMembers),
            buttonField,
            buttonActive,
            contentTextField,
            text,
            currentItemField,
            conditionPass,
            behaviourList,
            currentItemField.FieldType.GetField("optionName", InstanceMembers),
            currentItemField.FieldType.GetField("extraData", InstanceMembers),
            onClick);
        contract = _contract;
        return true;
    }

    private static Type? FindType(string fullName)
    {
        foreach (Assembly assembly in AppDomain.CurrentDomain.GetAssemblies())
        {
            Type? type = assembly.GetType(fullName, false);
            if (type != null)
            {
                return type;
            }
        }

        return null;
    }

    private static int ReadIndex(JObject? arguments)
    {
        try
        {
            return arguments?["index"]?.Value<int>() ?? 0;
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
        ["suggestion"] = "\u8bf7\u91cd\u65b0\u67e5\u8be2\u5f53\u524d\u4fee\u7406\u9009\u9879\uff0c\u5e76\u4f7f\u7528\u8fd4\u56de\u7684 index\u3002",
        ["data"] = new JObject { ["state"] = state }
    };

    private static Exception Unwrap(Exception exception) =>
        exception is TargetInvocationException target && target.InnerException != null
            ? target.InnerException
            : exception;

    private sealed class ReflectionContract
    {
        public ReflectionContract(
            Type repairUiType,
            Type waveFunctionItemType,
            PropertyInfo? instanceProperty,
            MethodInfo? isOpenMethod,
            FieldInfo? constChoicesField,
            FieldInfo? choiceItemBehaviourField,
            FieldInfo buttonField,
            PropertyInfo buttonActiveProperty,
            FieldInfo contentTextField,
            PropertyInfo textProperty,
            FieldInfo currentItemField,
            MethodInfo conditionPassMethod,
            FieldInfo behaviourListField,
            FieldInfo? optionNameField,
            FieldInfo? extraDataField,
            MethodInfo onClick)
        {
            RepairUiType = repairUiType;
            WaveFunctionItemType = waveFunctionItemType;
            InstanceProperty = instanceProperty;
            IsOpenMethod = isOpenMethod;
            ConstChoicesField = constChoicesField;
            ChoiceItemBehaviourField = choiceItemBehaviourField;
            ButtonField = buttonField;
            ButtonActiveProperty = buttonActiveProperty;
            ContentTextField = contentTextField;
            TextProperty = textProperty;
            CurrentItemField = currentItemField;
            ConditionPassMethod = conditionPassMethod;
            BehaviourListField = behaviourListField;
            OptionNameField = optionNameField;
            ExtraDataField = extraDataField;
            OnClick = onClick;
        }

        public Type RepairUiType { get; }
        public Type WaveFunctionItemType { get; }
        public PropertyInfo? InstanceProperty { get; }
        public MethodInfo? IsOpenMethod { get; }
        public FieldInfo? ConstChoicesField { get; }
        public FieldInfo? ChoiceItemBehaviourField { get; }
        public FieldInfo ButtonField { get; }
        public PropertyInfo ButtonActiveProperty { get; }
        public FieldInfo ContentTextField { get; }
        public PropertyInfo TextProperty { get; }
        public FieldInfo CurrentItemField { get; }
        public MethodInfo ConditionPassMethod { get; }
        public FieldInfo BehaviourListField { get; }
        public FieldInfo? OptionNameField { get; }
        public FieldInfo? ExtraDataField { get; }
        public MethodInfo OnClick { get; }
    }
}
