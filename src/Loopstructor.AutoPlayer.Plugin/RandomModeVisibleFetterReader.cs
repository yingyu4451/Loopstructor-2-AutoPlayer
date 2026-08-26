using System;
using System.Linq;
using System.Reflection;
using HarmonyLib;
using Newtonsoft.Json.Linq;
using UnityEngine;

namespace Loopstructor.AutoPlayer.Plugin;

internal static class RandomModeVisibleFetterReader
{
    private const string ComponentTypeName = "Systems.UISystem.RandomMode_Selected_Fetter";

    public static JArray ReadVisible()
    {
        Type? type = AccessTools.TypeByName(ComponentTypeName);
        if (type == null || !typeof(Component).IsAssignableFrom(type)) return new JArray();

        FieldInfo? field = AccessTools.Field(type, "m_fetter");
        PropertyInfo? property = AccessTools.Property(type, "Fetter");
        return new JArray(Resources.FindObjectsOfTypeAll(type).OfType<Component>()
            .Where(IsVisible)
            .Select(component => new
            {
                Component = component,
                EnumName = (field?.GetValue(component) ?? property?.GetValue(component, null))?.ToString() ?? string.Empty,
                Path = BuildHierarchyPath(component.transform),
                Order = BuildSiblingOrder(component.transform)
            })
            .Where(item => !string.IsNullOrWhiteSpace(item.EnumName) &&
                           !string.Equals(item.EnumName, "None", StringComparison.OrdinalIgnoreCase))
            .OrderBy(item => item.Order, StringComparer.Ordinal)
            .Select((item, index) => new JObject
            {
                ["index"] = index,
                ["fetterEnum"] = item.EnumName,
                ["instanceId"] = item.Component.GetInstanceID(),
                ["gameObjectInstanceId"] = item.Component.gameObject.GetInstanceID(),
                ["path"] = item.Path,
                ["displayOrder"] = index
            }));
    }

    public static bool Matches(int instanceId, string fetterEnum)
    {
        return ReadVisible().OfType<JObject>().Any(item =>
            (item["instanceId"]?.Value<int>() == instanceId ||
             item["gameObjectInstanceId"]?.Value<int>() == instanceId) &&
            string.Equals(item["fetterEnum"]?.Value<string>(), fetterEnum, StringComparison.Ordinal));
    }

    private static bool IsVisible(Component component) =>
        component != null && component.gameObject != null && component.gameObject.scene.IsValid() &&
        component.gameObject.activeInHierarchy;

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
}
