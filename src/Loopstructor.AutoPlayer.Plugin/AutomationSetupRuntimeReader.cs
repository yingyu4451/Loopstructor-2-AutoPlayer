using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Newtonsoft.Json.Linq;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace Loopstructor.AutoPlayer.Plugin;

internal static class AutomationSetupRuntimeReader
{
    private const BindingFlags AllMembers = BindingFlags.Public | BindingFlags.NonPublic |
                                            BindingFlags.Instance | BindingFlags.Static |
                                            BindingFlags.FlattenHierarchy;
    private static readonly object ReflectionSync = new();
    private static readonly Dictionary<string, Type?> TypeCache = new(StringComparer.Ordinal);
    private static readonly Dictionary<(Type Type, string Name), MemberInfo?> MemberCache = new();
    private static readonly Dictionary<(Type Type, string Name), MethodInfo?> MethodCache = new();

    public static bool TryQuery(out JObject data, out string message)
    {
        data = new JObject();
        try
        {
            string sceneName = SceneManager.GetActiveScene().name ?? string.Empty;
            bool atStartMenu = string.Equals(sceneName, "StartGameScene", StringComparison.OrdinalIgnoreCase);
            if (!atStartMenu)
            {
                data = new JObject
                {
                    ["sceneName"] = sceneName,
                    ["characters"] = new JArray(),
                    ["modes"] = new JArray
                    {
                        Mode("common", "普通模式", false, "请回到开始菜单读取普通模式"),
                        Mode("random", "随机模式", false, "请回到开始菜单读取随机模式")
                    }
                };
                message = "当前不在开始菜单；已跳过新游戏目录读取。";
                return true;
            }

            IList? runtimeCharacters = ReadRuntimeCharacters();
            JArray characters = new();
            if (runtimeCharacters != null)
            {
                for (int runtimeIndex = 0; runtimeIndex < runtimeCharacters.Count; runtimeIndex++)
                {
                    object? character = runtimeCharacters[runtimeIndex];
                    if (character == null) continue;
                    bool unlocked = ReadBool(character, "isCharacterUnlock", true);
                    int difficultyIndex = FirstUnlockedPairIndex(ReadMember(character, "gameLevelDatas") as IList);
                    int moduleIndex = FirstUnlockedPairIndex(ReadMember(character, "superModuleEnums") as IList);
                    bool available = unlocked && difficultyIndex >= 0 && moduleIndex >= 0;
                    string reason = !unlocked
                        ? "角色尚未解锁"
                        : difficultyIndex < 0
                            ? "没有可用难度"
                            : moduleIndex < 0
                                ? "没有可用初始遗物"
                                : string.Empty;
                    if (!available) continue;

                    characters.Add(new JObject
                    {
                        ["cfgIndex"] = ReadInt(character, "index", runtimeIndex),
                        ["runtimeIndex"] = runtimeIndex,
                        ["displayName"] = ResolveLocalizedText(ReadMember(character, "characterName"), $"角色 {runtimeIndex + 1}"),
                        ["available"] = true,
                        ["reason"] = reason,
                        ["difficultyIndex"] = difficultyIndex,
                        ["superModuleIndex"] = moduleIndex
                    });
                }
            }

            bool commonAvailable = characters.Count > 0;
            string commonReason = characters.Count == 0
                    ? "当前没有同时具备可用难度和初始遗物的已解锁角色"
                    : string.Empty;

            bool randomEntryAvailable = HasActiveComponent("StartGameButton");
            int randomCharacterCount = InvokeStaticListCount(
                "Systems.UISystem.RandomMode_Item_Character",
                "BuildRandomModeCharacterCandidates");
            int randomVehicleCount = InvokeStaticListCount(
                "MetroTD.AchievementSystem.RandomModeContentPatch.RandomModeAchievementContentPatchService",
                "GetRandomModeVehiclePool");
            int randomFetterCount = InvokeStaticListCount(
                "MetroTD.AchievementSystem.RandomModeContentPatch.RandomModeAchievementContentPatchService",
                "GetRandomModeBasicFetterPool");
            bool randomAvailable = randomEntryAvailable && randomCharacterCount > 0 &&
                                   randomVehicleCount > 0 && randomFetterCount > 0;
            string randomReason = !randomEntryAvailable
                    ? "当前没有可用的随机模式入口"
                    : randomCharacterCount == 0
                        ? "当前没有可选角色"
                        : randomVehicleCount == 0
                            ? "当前没有可选战车"
                            : randomFetterCount == 0
                                ? "当前没有可选附魔"
                                : string.Empty;

            data = new JObject
            {
                ["sceneName"] = sceneName,
                ["characters"] = characters,
                ["modes"] = new JArray
                {
                    Mode("common", "普通模式", commonAvailable, commonReason),
                    Mode("random", "随机模式", randomAvailable, randomReason)
                }
            };
            message = commonAvailable || randomAvailable
                ? "已读取当前游戏可玩的角色和模式。"
                : "当前游戏还没有可开始的自动游玩模式。";
            return true;
        }
        catch (Exception exception)
        {
            message = "读取当前游戏可玩内容失败：" + exception.Message;
            return false;
        }
    }

    private static JObject Mode(string key, string name, bool available, string reason) => new()
    {
        ["mode"] = key,
        ["displayName"] = name,
        ["available"] = available,
        ["reason"] = reason
    };

    private static IList? ReadRuntimeCharacters()
    {
        Type? managerType = ResolveType("MetroTD.CharacterSystem.CharacterManager");
        object? manager = ReadStaticSingleton(managerType);
        return manager == null ? null : ReadMember(manager, "runtimeCharacterData") as IList;
    }

    private static int FirstUnlockedPairIndex(IList? pairs)
    {
        if (pairs == null) return -1;
        for (int index = 0; index < pairs.Count; index++)
        {
            object? pair = pairs[index];
            if (pair == null) continue;
            object? value = ReadMember(pair, "first");
            object? condition = ReadMember(pair, "second");
            if (value == null || IsNone(value) || !IsUnlocked(condition)) continue;
            return index;
        }

        return -1;
    }

    private static bool IsUnlocked(object? condition)
    {
        if (condition == null) return true;
        MethodInfo? method = ResolveNoArgumentMethod(condition.GetType(), "IsUnLock");
        return method?.Invoke(condition, null) as bool? == true;
    }

    private static bool IsNone(object value) =>
        string.Equals(value.ToString(), "None", StringComparison.OrdinalIgnoreCase);

    private static int InvokeStaticListCount(string typeName, string methodName)
    {
        Type? type = ResolveType(typeName);
        MethodInfo? method = type == null ? null : ResolveNoArgumentMethod(type, methodName);
        return method?.Invoke(null, null) is IList list ? list.Count : 0;
    }

    private static bool HasActiveComponent(string typeName)
    {
        Type? type = ResolveType(typeName);
        if (type == null || !typeof(Component).IsAssignableFrom(type)) return false;
        return Resources.FindObjectsOfTypeAll(type).OfType<Component>().Any(component =>
            component != null && component.gameObject != null && component.gameObject.scene.IsValid() &&
            component.gameObject.activeInHierarchy);
    }

    private static object? ReadStaticSingleton(Type? type)
    {
        if (type == null) return null;
        return ReadMemberValue(null, ResolveMember(type, "Instance")) ??
               ReadMemberValue(null, ResolveMember(type, "instance"));
    }

    private static object? ReadMember(object target, string name) =>
        ReadMemberValue(target, ResolveMember(target.GetType(), name));

    private static int ReadInt(object target, string name, int fallback) =>
        ReadMember(target, name) is int value ? value : fallback;

    private static bool ReadBool(object target, string name, bool fallback) =>
        ReadMember(target, name) is bool value ? value : fallback;

    private static string ResolveLocalizedText(object? localized, string fallback)
    {
        if (localized == null) return fallback;
        try
        {
            MethodInfo? method = ResolveNoArgumentMethod(localized.GetType(), "GetText") ??
                                 ResolveNoArgumentMethod(localized.GetType(), "GetLocalizedString");
            string? value = method?.Invoke(localized, null) as string;
            return string.IsNullOrWhiteSpace(value) ? fallback : value.Trim();
        }
        catch
        {
            return fallback;
        }
    }

    private static Type? ResolveType(string typeName)
    {
        lock (ReflectionSync)
        {
            if (TypeCache.TryGetValue(typeName, out Type? cached)) return cached;
        }

        Type? resolved = Type.GetType(typeName, false) ?? AppDomain.CurrentDomain.GetAssemblies()
            .SelectMany(assembly =>
            {
                try { return assembly.GetTypes(); }
                catch (ReflectionTypeLoadException exception) { return exception.Types.Where(type => type != null)!; }
            })
            .FirstOrDefault(type => type != null &&
                (string.Equals(type.FullName, typeName, StringComparison.Ordinal) ||
                 string.Equals(type.Name, typeName, StringComparison.Ordinal)));
        lock (ReflectionSync)
        {
            TypeCache[typeName] = resolved;
        }

        return resolved;
    }

    private static MemberInfo? ResolveMember(Type type, string name)
    {
        (Type Type, string Name) key = (type, name);
        lock (ReflectionSync)
        {
            if (MemberCache.TryGetValue(key, out MemberInfo? cached)) return cached;
        }

        MemberInfo? resolved = type.GetProperty(name, AllMembers) ??
                               (MemberInfo?)type.GetField(name, AllMembers);
        lock (ReflectionSync)
        {
            MemberCache[key] = resolved;
        }

        return resolved;
    }

    private static MethodInfo? ResolveNoArgumentMethod(Type type, string name)
    {
        (Type Type, string Name) key = (type, name);
        lock (ReflectionSync)
        {
            if (MethodCache.TryGetValue(key, out MethodInfo? cached)) return cached;
        }

        MethodInfo? resolved = type.GetMethod(name, AllMembers, null, Type.EmptyTypes, null);
        lock (ReflectionSync)
        {
            MethodCache[key] = resolved;
        }

        return resolved;
    }

    private static object? ReadMemberValue(object? target, MemberInfo? member) => member switch
    {
        PropertyInfo property => property.GetValue(target, null),
        FieldInfo field => field.GetValue(target),
        _ => null
    };
}
