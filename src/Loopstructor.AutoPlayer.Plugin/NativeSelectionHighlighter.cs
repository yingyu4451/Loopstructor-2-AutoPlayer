using System;
using System.Linq;
using System.Reflection;
using HarmonyLib;
using UnityEngine;

namespace Loopstructor.AutoPlayer.Plugin;

/// <summary>
/// 在游戏原有 UI 对象内部创建不接收输入的绿色边框，用于显示自动游玩的下一次选择。
/// </summary>
internal sealed class NativeSelectionHighlighter
{
    private const string BorderObjectName = "Loopstructor.AutoPlayer.SelectionBorder";
    private static readonly Color BorderColor = new(0.34f, 1f, 0.24f, 1f);
    private GameObject? _borderRoot;
    private string _targetKey = string.Empty;

    public bool Show(NativeSelectionTarget target, out string error)
    {
        if (target == null) throw new ArgumentNullException(nameof(target));
        error = string.Empty;
        if (string.Equals(_targetKey, target.Key, StringComparison.Ordinal) && _borderRoot != null)
        {
            _borderRoot.transform.SetAsLastSibling();
            return true;
        }

        Clear();
        _targetKey = target.Key;
        Component? component = ResolveTarget(target);
        if (component == null || component.gameObject == null || !component.gameObject.activeInHierarchy)
        {
            error = "未找到仍在显示的游戏原始选项 UI。";
            return false;
        }

        RectTransform? targetRect = component.transform as RectTransform
                                    ?? component.GetComponent<RectTransform>();
        if (targetRect == null)
        {
            error = "目标游戏 UI 没有可描边的 RectTransform。";
            return false;
        }

        Type? imageType = AccessTools.TypeByName("UnityEngine.UI.Image");
        PropertyInfo? colorProperty = imageType == null ? null : AccessTools.Property(imageType, "color");
        PropertyInfo? raycastTargetProperty = imageType == null ? null : AccessTools.Property(imageType, "raycastTarget");
        if (imageType == null || colorProperty?.CanWrite != true || raycastTargetProperty?.CanWrite != true)
        {
            error = "当前游戏版本缺少 Unity UI 描边组件。";
            return false;
        }

        try
        {
            _borderRoot = new GameObject(BorderObjectName, typeof(RectTransform))
            {
                hideFlags = HideFlags.HideAndDontSave
            };
            RectTransform rootRect = (RectTransform)_borderRoot.transform;
            rootRect.SetParent(targetRect, false);
            rootRect.anchorMin = Vector2.zero;
            rootRect.anchorMax = Vector2.one;
            rootRect.offsetMin = Vector2.zero;
            rootRect.offsetMax = Vector2.zero;
            rootRect.localScale = Vector3.one;
            Type? layoutElementType = AccessTools.TypeByName("UnityEngine.UI.LayoutElement");
            PropertyInfo? ignoreLayoutProperty = layoutElementType == null
                ? null
                : AccessTools.Property(layoutElementType, "ignoreLayout");
            if (layoutElementType != null && ignoreLayoutProperty?.CanWrite == true)
            {
                Component layoutElement = _borderRoot.AddComponent(layoutElementType);
                ignoreLayoutProperty.SetValue(layoutElement, true, null);
            }

            AddBar(rootRect, imageType, colorProperty, raycastTargetProperty, "Top", new Vector2(0f, 1f), new Vector2(1f, 1f), new Vector2(0f, -4f), Vector2.zero);
            AddBar(rootRect, imageType, colorProperty, raycastTargetProperty, "Bottom", Vector2.zero, new Vector2(1f, 0f), Vector2.zero, new Vector2(0f, 4f));
            AddBar(rootRect, imageType, colorProperty, raycastTargetProperty, "Left", Vector2.zero, new Vector2(0f, 1f), Vector2.zero, new Vector2(4f, 0f));
            AddBar(rootRect, imageType, colorProperty, raycastTargetProperty, "Right", new Vector2(1f, 0f), Vector2.one, new Vector2(-4f, 0f), Vector2.zero);
            rootRect.SetAsLastSibling();
            return true;
        }
        catch (Exception exception)
        {
            error = "创建游戏原始 UI 绿色边框失败：" + exception.Message;
            Clear();
            return false;
        }
    }

    public void Clear()
    {
        _targetKey = string.Empty;
        if (_borderRoot == null) return;
        _borderRoot.SetActive(false);
        UnityEngine.Object.Destroy(_borderRoot);
        _borderRoot = null;
    }

    private static void AddBar(
        RectTransform parent,
        Type imageType,
        PropertyInfo colorProperty,
        PropertyInfo raycastTargetProperty,
        string name,
        Vector2 anchorMin,
        Vector2 anchorMax,
        Vector2 offsetMin,
        Vector2 offsetMax)
    {
        GameObject bar = new(BorderObjectName + "." + name, typeof(RectTransform))
        {
            hideFlags = HideFlags.HideAndDontSave
        };
        RectTransform rect = (RectTransform)bar.transform;
        rect.SetParent(parent, false);
        rect.anchorMin = anchorMin;
        rect.anchorMax = anchorMax;
        rect.offsetMin = offsetMin;
        rect.offsetMax = offsetMax;
        Component image = bar.AddComponent(imageType);
        colorProperty.SetValue(image, BorderColor, null);
        raycastTargetProperty.SetValue(image, false, null);
    }

    private static Component? ResolveTarget(NativeSelectionTarget target)
    {
        Type? componentType = ResolveType(target.ComponentTypeName);
        if (componentType == null || !typeof(Component).IsAssignableFrom(componentType)) return null;

        foreach (Component component in Resources.FindObjectsOfTypeAll(componentType).OfType<Component>())
        {
            if (component == null || component.gameObject == null || !component.gameObject.scene.IsValid()) continue;
            if (target.InstanceId != 0 &&
                component.GetInstanceID() != target.InstanceId &&
                component.gameObject.GetInstanceID() != target.InstanceId)
            {
                continue;
            }

            if (!string.IsNullOrWhiteSpace(target.MemberName) &&
                !MemberMatches(component, target.MemberName, target.MemberValue))
            {
                continue;
            }

            if (!string.IsNullOrWhiteSpace(target.Path) &&
                target.InstanceId == 0 &&
                !string.Equals(BuildHierarchyPath(component.transform), target.Path, StringComparison.Ordinal))
            {
                continue;
            }

            return component;
        }

        return null;
    }

    private static Type? ResolveType(string typeName) =>
        AccessTools.TypeByName(typeName) ??
        AppDomain.CurrentDomain.GetAssemblies()
            .SelectMany(assembly =>
            {
                try { return assembly.GetTypes(); }
                catch (ReflectionTypeLoadException exception) { return exception.Types.Where(type => type != null)!; }
            })
            .FirstOrDefault(type => type != null && string.Equals(type.Name, typeName, StringComparison.Ordinal));

    private static bool MemberMatches(Component component, string memberName, string expected)
    {
        FieldInfo? field = AccessTools.Field(component.GetType(), memberName);
        PropertyInfo? property = AccessTools.Property(component.GetType(), memberName);
        object? value = field?.GetValue(component) ?? property?.GetValue(component, null);
        return value != null && string.Equals(value.ToString(), expected, StringComparison.Ordinal);
    }

    private static string BuildHierarchyPath(Transform? transform)
    {
        if (transform == null) return string.Empty;
        string path = transform.name;
        Transform? current = transform.parent;
        while (current != null)
        {
            path = current.name + "/" + path;
            current = current.parent;
        }

        return path;
    }
}

internal sealed class NativeSelectionTarget
{
    private NativeSelectionTarget(
        string componentTypeName,
        int instanceId,
        string path,
        string memberName,
        string memberValue)
    {
        ComponentTypeName = componentTypeName ?? string.Empty;
        InstanceId = instanceId;
        Path = path ?? string.Empty;
        MemberName = memberName ?? string.Empty;
        MemberValue = memberValue ?? string.Empty;
        Key = string.Join("|", ComponentTypeName, InstanceId, Path, MemberName, MemberValue);
    }

    public string ComponentTypeName { get; }
    public int InstanceId { get; }
    public string Path { get; }
    public string MemberName { get; }
    public string MemberValue { get; }
    public string Key { get; }

    public static NativeSelectionTarget ByInstance(string componentTypeName, int instanceId, string? path = null) =>
        new(componentTypeName, instanceId, path ?? string.Empty, string.Empty, string.Empty);

    public static NativeSelectionTarget ByMember(string componentTypeName, string memberName, string memberValue) =>
        new(componentTypeName, 0, string.Empty, memberName, memberValue);
}
