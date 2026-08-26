using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Newtonsoft.Json.Linq;
using UnityEngine;

namespace Loopstructor.AutoPlayer.Plugin;

internal static class RandomModeVisibleFetterReader
{
    internal const string ComponentTypeName = "Systems.UISystem.RandomMode_Selected_Fetter";
    private const BindingFlags InstanceMembers = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance;
    private static readonly Dictionary<int, VisibleFetter> VisibleByInstanceId = new();
    private static Type? _componentType;
    private static FieldInfo? _fetterField;
    private static PropertyInfo? _fetterProperty;
    private static FieldInfo? _ownerField;
    private static MethodInfo? _selectMethod;
    private static bool _contractInitialized;
    private static bool _contractAvailable;

    public static JArray ReadVisible()
    {
        if (!EnsureContract()) return new JArray();

        List<VisibleFetter> visible = Resources.FindObjectsOfTypeAll(_componentType!)
            .OfType<Component>()
            .Where(IsVisible)
            .Select(component => new VisibleFetter(
                component,
                ReadFetter(component),
                BuildHierarchyPath(component.transform),
                BuildSiblingOrder(component.transform)))
            .Where(item => !string.IsNullOrWhiteSpace(item.FetterEnum) &&
                           !string.Equals(item.FetterEnum, "None", StringComparison.OrdinalIgnoreCase))
            .OrderBy(item => item.SiblingOrder, StringComparer.Ordinal)
            .ToList();

        VisibleByInstanceId.Clear();
        foreach (VisibleFetter item in visible)
        {
            VisibleByInstanceId[item.Component.GetInstanceID()] = item;
            VisibleByInstanceId[item.Component.gameObject.GetInstanceID()] = item;
        }

        return new JArray(visible.Select((item, index) => new JObject
        {
            ["index"] = index,
            ["fetterEnum"] = item.FetterEnum,
            ["instanceId"] = item.Component.GetInstanceID(),
            ["gameObjectInstanceId"] = item.Component.gameObject.GetInstanceID(),
            ["path"] = item.Path,
            ["displayOrder"] = index
        }));
    }

    public static bool Matches(int instanceId, string fetterEnum) =>
        TryResolve(instanceId, fetterEnum, out _);

    public static bool TryResolve(int instanceId, string? fetterEnum, out Component? component)
    {
        component = null;
        if (instanceId == 0 ||
            !VisibleByInstanceId.TryGetValue(instanceId, out VisibleFetter? item) ||
            !IsVisible(item.Component) ||
            !string.Equals(ReadFetter(item.Component), item.FetterEnum, StringComparison.Ordinal) ||
            (!string.IsNullOrWhiteSpace(fetterEnum) &&
             !string.Equals(item.FetterEnum, fetterEnum, StringComparison.Ordinal)))
        {
            return false;
        }

        component = item.Component;
        return true;
    }

    public static JObject SelectExact(JObject? arguments, out bool mutationStarted)
    {
        mutationStarted = false;
        int instanceId = arguments?["targetInstanceId"]?.Value<int>() ?? 0;
        string fetterEnum = arguments?["fetterEnum"]?.Value<string>() ?? string.Empty;
        if (!TryResolve(instanceId, fetterEnum, out Component? component) || component == null)
        {
            return Failure("附魔候选界面已变化；没有执行选择。", statePolluted: false);
        }

        if (!EnsureContract() || _selectMethod == null)
        {
            return Failure("当前游戏版本缺少附魔卡片的原生选择方法；没有执行选择。", statePolluted: false);
        }

        try
        {
            mutationStarted = true;
            _selectMethod.Invoke(component, null);
            string selected = ReadSelectedFetter(component);
            if (!string.Equals(selected, fetterEnum, StringComparison.Ordinal))
            {
                return Failure(
                    "附魔卡片已执行，但游戏回报的实际附魔与目标不一致。",
                    statePolluted: true,
                    selected);
            }

            return new JObject
            {
                ["success"] = true,
                ["message"] = "已通过绿色边框对应的游戏原生附魔卡片完成选择。",
                ["statePolluted"] = false,
                ["data"] = new JObject
                {
                    ["state"] = new JObject
                    {
                        ["selectedFetter"] = selected,
                        ["targetInstanceId"] = component.GetInstanceID(),
                        ["targetPath"] = BuildHierarchyPath(component.transform)
                    }
                }
            };
        }
        catch (Exception exception)
        {
            Exception actual = exception is TargetInvocationException target && target.InnerException != null
                ? target.InnerException
                : exception;
            return Failure(
                "执行绿色边框对应的附魔卡片失败：" + actual.Message,
                statePolluted: mutationStarted);
        }
    }

    private static bool EnsureContract()
    {
        if (_contractInitialized) return _contractAvailable;
        _contractInitialized = true;
        _componentType = ResolveType(ComponentTypeName);
        if (_componentType == null || !typeof(Component).IsAssignableFrom(_componentType)) return false;
        _fetterField = _componentType.GetField("m_fetter", InstanceMembers);
        _fetterProperty = _componentType.GetProperty("Fetter", InstanceMembers);
        _ownerField = _componentType.GetField("m_oner", InstanceMembers);
        _selectMethod = _componentType.GetMethod("InitFetter", InstanceMembers, null, Type.EmptyTypes, null);
        _contractAvailable = (_fetterField != null || _fetterProperty != null) &&
                             _ownerField != null &&
                             _selectMethod != null;
        return _contractAvailable;
    }

    private static bool IsVisible(Component component)
    {
        if (component == null || component.gameObject == null ||
            !component.gameObject.scene.IsValid() || !component.gameObject.activeInHierarchy)
        {
            return false;
        }

        if (component is Behaviour behaviour && !behaviour.isActiveAndEnabled) return false;
        return EnsureContract() && _ownerField?.GetValue(component) is Component owner &&
               owner != null && owner.gameObject != null && owner.gameObject.activeInHierarchy;
    }

    private static string ReadFetter(Component component) =>
        (_fetterField?.GetValue(component) ?? _fetterProperty?.GetValue(component, null))?.ToString() ?? string.Empty;

    private static string ReadSelectedFetter(Component component)
    {
        object? owner = _ownerField?.GetValue(component);
        if (owner == null) return string.Empty;
        Type ownerType = owner.GetType();
        return (ownerType.GetProperty("CurrentFetter", InstanceMembers)?.GetValue(owner, null) ??
                ownerType.GetField("m_currentFetter", InstanceMembers)?.GetValue(owner))?.ToString() ?? string.Empty;
    }

    private static JObject Failure(string message, bool statePolluted, string? selected = null) => new()
    {
        ["success"] = false,
        ["message"] = message,
        ["suggestion"] = "重新读取当前可见附魔卡片后再选择。",
        ["statePolluted"] = statePolluted,
        ["data"] = new JObject
        {
            ["state"] = new JObject { ["selectedFetter"] = selected ?? string.Empty }
        }
    };

    private static Type? ResolveType(string typeName) => AppDomain.CurrentDomain.GetAssemblies()
        .SelectMany(assembly =>
        {
            try { return assembly.GetTypes(); }
            catch (ReflectionTypeLoadException exception) { return exception.Types.Where(type => type != null)!; }
        })
        .FirstOrDefault(type => type != null &&
            (string.Equals(type.FullName, typeName, StringComparison.Ordinal) ||
             string.Equals(type.Name, typeName, StringComparison.Ordinal)));

    private static string BuildHierarchyPath(Transform? transform)
    {
        if (transform == null) return string.Empty;
        string path = transform.name;
        for (Transform? current = transform.parent; current != null; current = current.parent)
        {
            path = current.name + "/" + path;
        }

        return path;
    }

    private static string BuildSiblingOrder(Transform? transform)
    {
        if (transform == null) return string.Empty;
        string result = transform.GetSiblingIndex().ToString("D6");
        for (Transform? current = transform.parent; current != null; current = current.parent)
        {
            result = current.GetSiblingIndex().ToString("D6") + "/" + result;
        }

        return result;
    }

    private sealed class VisibleFetter
    {
        public VisibleFetter(Component component, string fetterEnum, string path, string siblingOrder)
        {
            Component = component;
            FetterEnum = fetterEnum;
            Path = path;
            SiblingOrder = siblingOrder;
        }

        public Component Component { get; }
        public string FetterEnum { get; }
        public string Path { get; }
        public string SiblingOrder { get; }
    }
}
