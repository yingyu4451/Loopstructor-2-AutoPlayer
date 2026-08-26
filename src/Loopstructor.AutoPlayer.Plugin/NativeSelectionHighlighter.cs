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
    private const float BorderThickness = 2f;
    private const float BorderInset = 2f;
    private static readonly Color BorderColor = new Color32(0x79, 0xD5, 0x3B, 0xFF);
    private GameObject? _borderRoot;
    private NativeSelectionBorderFollower? _borderFollower;
    private string _targetKey = string.Empty;

    public bool Show(NativeSelectionTarget target, out string error)
    {
        if (target == null) throw new ArgumentNullException(nameof(target));
        error = string.Empty;
        if (string.Equals(_targetKey, target.Key, StringComparison.Ordinal) && _borderRoot != null)
        {
            _borderRoot.transform.SetAsLastSibling();
            return _borderFollower?.Refresh() == true;
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

        Canvas? rootCanvas = targetRect.GetComponentsInParent<Canvas>(true).LastOrDefault();
        RectTransform? canvasRect = rootCanvas?.transform as RectTransform;
        if (rootCanvas == null || canvasRect == null)
        {
            error = "目标游戏 UI 不属于可描边的 Canvas。";
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
            rootRect.SetParent(canvasRect, false);
            rootRect.anchorMin = new Vector2(0.5f, 0.5f);
            rootRect.anchorMax = new Vector2(0.5f, 0.5f);
            rootRect.pivot = new Vector2(0.5f, 0.5f);
            rootRect.localScale = Vector3.one;
            CanvasGroup canvasGroup = _borderRoot.AddComponent<CanvasGroup>();
            canvasGroup.interactable = false;
            canvasGroup.blocksRaycasts = false;
            Type? layoutElementType = AccessTools.TypeByName("UnityEngine.UI.LayoutElement");
            PropertyInfo? ignoreLayoutProperty = layoutElementType == null
                ? null
                : AccessTools.Property(layoutElementType, "ignoreLayout");
            if (layoutElementType != null && ignoreLayoutProperty?.CanWrite == true)
            {
                Component layoutElement = _borderRoot.AddComponent(layoutElementType);
                ignoreLayoutProperty.SetValue(layoutElement, true, null);
            }

            AddBar(rootRect, imageType, colorProperty, raycastTargetProperty, "Top", new Vector2(0f, 1f), new Vector2(1f, 1f), new Vector2(0f, -BorderThickness), Vector2.zero);
            AddBar(rootRect, imageType, colorProperty, raycastTargetProperty, "Bottom", Vector2.zero, new Vector2(1f, 0f), Vector2.zero, new Vector2(0f, BorderThickness));
            AddBar(rootRect, imageType, colorProperty, raycastTargetProperty, "Left", Vector2.zero, new Vector2(0f, 1f), Vector2.zero, new Vector2(BorderThickness, 0f));
            AddBar(rootRect, imageType, colorProperty, raycastTargetProperty, "Right", new Vector2(1f, 0f), Vector2.one, new Vector2(-BorderThickness, 0f), Vector2.zero);
            _borderFollower = _borderRoot.AddComponent<NativeSelectionBorderFollower>();
            _borderFollower.Initialize(targetRect, canvasRect, rootCanvas, BorderInset);
            if (!_borderFollower.Refresh())
            {
                error = "目标游戏 UI 的可见区域尚未稳定。";
                Clear();
                return false;
            }
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
        _borderFollower = null;
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
        if (string.Equals(
                target.ComponentTypeName,
                RandomModeVisibleFetterReader.ComponentTypeName,
                StringComparison.Ordinal) &&
            RandomModeVisibleFetterReader.TryResolve(target.InstanceId, null, out Component? visibleFetter))
        {
            return visibleFetter;
        }

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

internal sealed class NativeSelectionBorderFollower : MonoBehaviour
{
    private readonly Vector3[] _worldCorners = new Vector3[4];
    private RectTransform? _target;
    private RectTransform? _canvasRect;
    private Canvas? _canvas;
    private RectTransform? _borderRect;
    private float _inset;

    public void Initialize(RectTransform target, RectTransform canvasRect, Canvas canvas, float inset)
    {
        _target = target;
        _canvasRect = canvasRect;
        _canvas = canvas;
        _borderRect = transform as RectTransform;
        _inset = inset;
    }

    public bool Refresh()
    {
        if (_target == null || _canvasRect == null || _canvas == null || _borderRect == null ||
            _target.gameObject == null || !_target.gameObject.activeInHierarchy)
        {
            if (gameObject.activeSelf) gameObject.SetActive(false);
            return false;
        }

        Camera? camera = _canvas.renderMode == RenderMode.ScreenSpaceOverlay ? null : _canvas.worldCamera;
        _target.GetWorldCorners(_worldCorners);
        Vector2 minimum = new(float.PositiveInfinity, float.PositiveInfinity);
        Vector2 maximum = new(float.NegativeInfinity, float.NegativeInfinity);
        for (int index = 0; index < _worldCorners.Length; index++)
        {
            Vector2 screen = RectTransformUtility.WorldToScreenPoint(camera, _worldCorners[index]);
            if (!RectTransformUtility.ScreenPointToLocalPointInRectangle(_canvasRect, screen, camera, out Vector2 local))
            {
                return false;
            }

            minimum = Vector2.Min(minimum, local);
            maximum = Vector2.Max(maximum, local);
        }

        minimum += new Vector2(_inset, _inset);
        maximum -= new Vector2(_inset, _inset);
        Vector2 size = maximum - minimum;
        if (size.x <= 0f || size.y <= 0f) return false;

        if (!gameObject.activeSelf) gameObject.SetActive(true);
        _borderRect.anchoredPosition = (minimum + maximum) * 0.5f;
        _borderRect.sizeDelta = size;
        _borderRect.SetAsLastSibling();
        return true;
    }

    private void LateUpdate() => Refresh();
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
