using System.Reflection;
using Newtonsoft.Json.Linq;
using UnityEngine;

namespace Loopstructor.AutoPlayer.Plugin;

/// <summary>
/// Reads the registered disposable interaction executor without scanning the scene or mutating it.
/// A missing or inconsistent contract is fail-closed: callers must not proceed with a write.
/// </summary>
internal sealed class OpeningDefenseInteractionGuard
{
    private const string ExecutorTypeName = "MetroTD.DisposableSystem.DisposableInteractionExecutor";
    private const string DisposableInteractionTypeName = "MetroTD.DisposableSystem.DisposableInteraction";
    private const string DisposableObjectTypeName = "MetroTD.DisposableSystem.DisposableObject";
    private const string GridInteractionTypeName = "MetroTD.DisposableSystem.GridChooseInteraction";
    private const string ResultSource = "pluginReflection:OpeningDefenseInteractionGuard";

    private ReflectionContract? _contract;

    internal JObject Query()
    {
        if (!TryGetContract(out ReflectionContract contract, out string contractError))
        {
            return Error("开局交互守卫契约不可用：" + contractError);
        }

        try
        {
            object? executor = contract.Instance.GetValue(null, null);
            if (!IsLiveObject(executor))
            {
                return Error("道具交互执行器尚未初始化。");
            }

            object? previewValue = contract.IsInPreview.GetValue(executor, null);
            if (previewValue is not bool isInPreview)
            {
                return Error("道具交互执行器没有返回明确的预览状态。");
            }

            object? interaction = contract.GetLastInteraction.Invoke(executor, null);
            bool hasLastInteraction = IsLiveObject(interaction);
            if (hasLastInteraction && !contract.DisposableInteractionType.IsInstanceOfType(interaction))
            {
                return Error("道具交互执行器返回了未知类型的活动交互。");
            }

            object? disposableObject = hasLastInteraction
                ? contract.InteractionDisposableObject.GetValue(interaction)
                : null;
            string disposableEnum = IsLiveObject(disposableObject)
                ? contract.DisposableObjectEnum.GetValue(disposableObject)?.ToString() ?? string.Empty
                : string.Empty;
            bool interactionIdentityComplete = !hasLastInteraction ||
                                               IsLiveObject(disposableObject) &&
                                               !string.IsNullOrWhiteSpace(disposableEnum);
            bool observationConsistent = isInPreview == hasLastInteraction && interactionIdentityComplete;
            bool noActiveInteraction = observationConsistent && !isInPreview && !hasLastInteraction;
            JObject state = new()
            {
                ["contractAvailable"] = true,
                ["noActiveInteraction"] = noActiveInteraction,
                ["observationConsistent"] = observationConsistent,
                ["isInPreview"] = isInPreview,
                ["hasLastInteraction"] = hasLastInteraction,
                ["isGridChooseInteraction"] = hasLastInteraction &&
                                                contract.GridInteractionType.IsInstanceOfType(interaction),
                ["executorInstanceId"] = ReadInstanceId(executor),
                ["interactionInstanceId"] = hasLastInteraction ? ReadInstanceId(interaction) : 0,
                ["interactionType"] = hasLastInteraction
                    ? interaction!.GetType().Name
                    : string.Empty,
                ["interactionFullType"] = hasLastInteraction
                    ? interaction!.GetType().FullName ?? interaction.GetType().Name
                    : string.Empty,
                ["disposableEnum"] = disposableEnum,
                ["source"] = ResultSource
            };

            return observationConsistent
                ? Success(
                    noActiveInteraction
                        ? "已确认当前没有活动中的道具交互。"
                        : "检测到活动中的道具交互；开局写命令必须等待。",
                    state)
                : Error("道具交互执行器的预览标志与最后交互不一致。", state);
        }
        catch (Exception exception)
        {
            return Error("读取开局道具交互状态失败：" + Unwrap(exception).Message);
        }
    }

    private bool TryGetContract(out ReflectionContract contract, out string error)
    {
        if (_contract != null)
        {
            contract = _contract;
            error = string.Empty;
            return true;
        }

        Type? executorType = FindType(ExecutorTypeName);
        Type? disposableInteractionType = FindType(DisposableInteractionTypeName);
        Type? disposableObjectType = FindType(DisposableObjectTypeName);
        Type? gridInteractionType = FindType(GridInteractionTypeName);
        const BindingFlags StaticMembers = BindingFlags.Public | BindingFlags.Static | BindingFlags.FlattenHierarchy;
        const BindingFlags InstanceMembers = BindingFlags.Public | BindingFlags.Instance;
        PropertyInfo? instance = executorType?.GetProperty("Instance", StaticMembers);
        PropertyInfo? isInPreview = executorType?.GetProperty("isInPreview", InstanceMembers);
        MethodInfo? getLastInteraction = executorType?.GetMethod(
            "GetLastDisposableInteraction",
            InstanceMembers,
            null,
            Type.EmptyTypes,
            null);
        FieldInfo? interactionDisposableObject = disposableInteractionType?.GetField(
            "disposableObject",
            BindingFlags.Public | BindingFlags.Instance);
        FieldInfo? disposableObjectEnum = disposableObjectType?.GetField(
            "disposableEnum",
            BindingFlags.Public | BindingFlags.Instance);

        List<string> missing = new();
        if (executorType == null) missing.Add(ExecutorTypeName);
        if (disposableInteractionType == null) missing.Add(DisposableInteractionTypeName);
        if (disposableObjectType == null) missing.Add(DisposableObjectTypeName);
        if (gridInteractionType == null) missing.Add(GridInteractionTypeName);
        if (instance == null ||
            instance.GetMethod == null ||
            !executorType!.IsAssignableFrom(instance.PropertyType))
        {
            missing.Add("DisposableInteractionExecutor.Instance");
        }
        if (isInPreview == null || isInPreview.PropertyType != typeof(bool))
        {
            missing.Add("DisposableInteractionExecutor.isInPreview");
        }
        if (getLastInteraction == null ||
            disposableInteractionType == null ||
            !disposableInteractionType.IsAssignableFrom(getLastInteraction.ReturnType))
        {
            missing.Add("DisposableInteractionExecutor.GetLastDisposableInteraction");
        }
        if (interactionDisposableObject == null ||
            disposableObjectType == null ||
            !disposableObjectType.IsAssignableFrom(interactionDisposableObject.FieldType))
        {
            missing.Add("DisposableInteraction.disposableObject");
        }
        if (disposableObjectEnum == null || !disposableObjectEnum.FieldType.IsEnum)
        {
            missing.Add("DisposableObject.disposableEnum");
        }
        if (disposableInteractionType != null &&
            gridInteractionType != null &&
            !disposableInteractionType.IsAssignableFrom(gridInteractionType))
        {
            missing.Add("GridChooseInteraction : DisposableInteraction");
        }

        if (missing.Count > 0)
        {
            contract = null!;
            error = string.Join("、", missing);
            return false;
        }

        _contract = new ReflectionContract(
            instance!,
            isInPreview!,
            getLastInteraction!,
            interactionDisposableObject!,
            disposableObjectEnum!,
            disposableInteractionType!,
            gridInteractionType!);
        contract = _contract;
        error = string.Empty;
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

    private static bool IsLiveObject(object? candidate) => candidate switch
    {
        null => false,
        UnityEngine.Object unityObject => unityObject != null,
        _ => true
    };

    private static int ReadInstanceId(object? candidate) =>
        candidate is UnityEngine.Object unityObject && unityObject != null
            ? unityObject.GetInstanceID()
            : 0;

    private static JObject Success(string message, JObject state) => new()
    {
        ["success"] = true,
        ["message"] = message,
        ["suggestion"] = JValue.CreateNull(),
        ["data"] = new JObject { ["state"] = state }
    };

    private static JObject Error(string message, JObject? observedState = null)
    {
        JObject state = observedState?.DeepClone() as JObject ?? new JObject();
        state["contractAvailable"] ??= false;
        state["noActiveInteraction"] = false;
        state["source"] = ResultSource;
        return new JObject
        {
            ["success"] = false,
            ["message"] = message,
            ["suggestion"] = "等待游戏交互状态稳定后重新查询；未确认安全前不要提交开局写命令。",
            ["data"] = new JObject { ["state"] = state }
        };
    }

    private static Exception Unwrap(Exception exception) =>
        exception is TargetInvocationException target && target.InnerException != null
            ? target.InnerException
            : exception;

    private sealed class ReflectionContract
    {
        public ReflectionContract(
            PropertyInfo instance,
            PropertyInfo isInPreview,
            MethodInfo getLastInteraction,
            FieldInfo interactionDisposableObject,
            FieldInfo disposableObjectEnum,
            Type disposableInteractionType,
            Type gridInteractionType)
        {
            Instance = instance;
            IsInPreview = isInPreview;
            GetLastInteraction = getLastInteraction;
            InteractionDisposableObject = interactionDisposableObject;
            DisposableObjectEnum = disposableObjectEnum;
            DisposableInteractionType = disposableInteractionType;
            GridInteractionType = gridInteractionType;
        }

        public PropertyInfo Instance { get; }
        public PropertyInfo IsInPreview { get; }
        public MethodInfo GetLastInteraction { get; }
        public FieldInfo InteractionDisposableObject { get; }
        public FieldInfo DisposableObjectEnum { get; }
        public Type DisposableInteractionType { get; }
        public Type GridInteractionType { get; }
    }
}
