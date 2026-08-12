using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using HarmonyLib;
using UnityEngine;

namespace Loopstructor.AutoPlayer.Plugin;

internal static class VehicleEnchantmentDisplayPatch
{
    private static readonly Dictionary<int, LayoutSnapshot> Layouts = new();
    private static Type? _vehicleItemType;

    public static bool IsInstalled { get; private set; }
    public static bool Enabled { get; private set; }

    public static bool Install(Harmony harmony, Action<string> log)
    {
        if (IsInstalled) return true;
        try
        {
            Type? utility = AccessTools.TypeByName("MetroTD.UISystem.GeneralUIUtility")
                            ?? AccessTools.TypeByName("GeneralUIUtility");
            MethodInfo? target = utility?.GetMethods(BindingFlags.Public | BindingFlags.Static)
                .FirstOrDefault(method =>
                {
                    ParameterInfo[] parameters = method.GetParameters();
                    return method.Name == "SpawnFetterHide"
                           && method.ReturnType == typeof(bool)
                           && parameters.Length == 8
                           && parameters[0].ParameterType == typeof(Transform)
                           && parameters[2].ParameterType == typeof(int);
                });
            MethodInfo? prefix = AccessTools.Method(typeof(VehicleEnchantmentDisplayPatch), nameof(Prefix));
            MethodInfo? postfix = AccessTools.Method(typeof(VehicleEnchantmentDisplayPatch), nameof(Postfix));
            if (target == null || prefix == null || postfix == null)
            {
                log("无法安装战车附魔图标布局补丁：未找到受限图标生成入口。");
                return false;
            }
            harmony.Patch(target, new HarmonyMethod(prefix), new HarmonyMethod(postfix));
            _vehicleItemType = AccessTools.TypeByName("MetroTD.UISystem.UIItem_Vehicle")
                               ?? AccessTools.TypeByName("UIItem_Vehicle");
            IsInstalled = true;
            log("战车附魔图标布局补丁已安装；仅在作弊模式启用时生效。");
            return true;
        }
        catch (Exception exception)
        {
            log("无法安装战车附魔图标布局补丁：" + exception.Message);
            return false;
        }
    }

    public static void SetEnabled(bool enabled)
    {
        Enabled = enabled && IsInstalled;
        if (!Enabled) RestoreLayouts();
        RefreshVisibleVehicleItems();
    }

    public static void Reset() => SetEnabled(false);

    private static void Prefix(object moduleDatas, ref int displayMaxCount, ref int hideAmount)
    {
        if (!Enabled || moduleDatas is not ICollection collection) return;
        displayMaxCount = collection.Count == int.MaxValue ? int.MaxValue : collection.Count + 1;
        hideAmount = 0;
    }

    private static void Postfix(Transform content, object moduleDatas)
    {
        if (!Enabled || content == null || moduleDatas is not ICollection collection) return;
        Component? layout = content.GetComponents<Component>()
            .FirstOrDefault(component => component != null && component.GetType().Name == "CustomGridLayout");
        if (layout == null || content is not RectTransform rect) return;

        int id = layout.GetInstanceID();
        if (!Layouts.ContainsKey(id)) Layouts[id] = LayoutSnapshot.Capture(layout, rect);
        if (collection.Count <= 0) return;
        float width = rect.rect.width > 1f ? rect.rect.width : Mathf.Max(180f, rect.sizeDelta.x);
        int columns = Mathf.Clamp(Mathf.CeilToInt(Mathf.Sqrt(collection.Count * 1.8f)), 1, 8);
        float cell = Mathf.Clamp((width - ((columns - 1) * 3f)) / columns, 26f, 48f);
        int rows = Mathf.CeilToInt(collection.Count / (float)columns);

        SetField(layout, "fixedMode", 1);
        SetField(layout, "fixedXCount", columns);
        SetField(layout, "cellSize", new Vector2(cell, cell));
        SetField(layout, "space", new Vector2(3f, 3f));
        SetField(layout, "fixedContentVertical", true);
        rect.SetSizeWithCurrentAnchors(RectTransform.Axis.Vertical, rows * cell + Mathf.Max(0, rows - 1) * 3f);
    }

    private static void RestoreLayouts()
    {
        foreach (LayoutSnapshot snapshot in Layouts.Values) snapshot.Restore();
        Layouts.Clear();
    }

    private static void RefreshVisibleVehicleItems()
    {
        if (_vehicleItemType == null) return;
        try
        {
            MethodInfo? refresh = AccessTools.Method(_vehicleItemType, "RefreshUI");
            foreach (UnityEngine.Object item in Resources.FindObjectsOfTypeAll(_vehicleItemType))
            {
                if (item is Component component && component != null && component.gameObject.activeInHierarchy)
                {
                    refresh?.Invoke(item, null);
                }
            }
        }
        catch
        {
            // Some panels are disposed while the scene changes; the next Load restores them.
        }
    }

    private static object? GetField(object target, string name) =>
        AccessTools.Field(target.GetType(), name)?.GetValue(target);

    private static void SetField(object target, string name, object value)
    {
        FieldInfo? field = AccessTools.Field(target.GetType(), name);
        if (field == null) return;
        if (field.FieldType.IsEnum && value is int numeric) value = Enum.ToObject(field.FieldType, numeric);
        field.SetValue(target, value);
    }

    private sealed class LayoutSnapshot
    {
        private readonly Component _layout;
        private readonly RectTransform _rect;
        private readonly object? _fixedMode;
        private readonly object? _fixedXCount;
        private readonly object? _cellSize;
        private readonly object? _space;
        private readonly object? _fixedContentVertical;
        private readonly Vector2 _sizeDelta;

        private LayoutSnapshot(Component layout, RectTransform rect)
        {
            _layout = layout;
            _rect = rect;
            _fixedMode = GetField(layout, "fixedMode");
            _fixedXCount = GetField(layout, "fixedXCount");
            _cellSize = GetField(layout, "cellSize");
            _space = GetField(layout, "space");
            _fixedContentVertical = GetField(layout, "fixedContentVertical");
            _sizeDelta = rect.sizeDelta;
        }

        public static LayoutSnapshot Capture(Component layout, RectTransform rect) => new(layout, rect);

        public void Restore()
        {
            if (_layout == null || _rect == null) return;
            if (_fixedMode != null) SetField(_layout, "fixedMode", _fixedMode);
            if (_fixedXCount != null) SetField(_layout, "fixedXCount", _fixedXCount);
            if (_cellSize != null) SetField(_layout, "cellSize", _cellSize);
            if (_space != null) SetField(_layout, "space", _space);
            if (_fixedContentVertical != null) SetField(_layout, "fixedContentVertical", _fixedContentVertical);
            _rect.sizeDelta = _sizeDelta;
        }
    }
}
