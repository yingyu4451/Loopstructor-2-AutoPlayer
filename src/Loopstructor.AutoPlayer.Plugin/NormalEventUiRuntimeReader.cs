using System;
using System.Collections;
using System.Reflection;
using UnityEngine;

namespace Loopstructor.AutoPlayer.Plugin;

internal readonly struct NormalEventUiRuntimeState
{
    public NormalEventUiRuntimeState(
        bool isOpen,
        bool isTypingStory,
        bool optionChosen,
        string typingStage,
        int storyIndex,
        int storyCount,
        int itemStoryIndex,
        int itemStoryCount)
    {
        IsOpen = isOpen;
        IsTypingStory = isTypingStory;
        OptionChosen = optionChosen;
        TypingStage = typingStage;
        StoryIndex = storyIndex;
        StoryCount = storyCount;
        ItemStoryIndex = itemStoryIndex;
        ItemStoryCount = itemStoryCount;
    }

    public bool IsOpen { get; }
    public bool IsTypingStory { get; }
    public bool OptionChosen { get; }
    public bool IsPreChoiceStory => IsOpen && !OptionChosen;
    public bool IsPostChoiceStory => IsOpen && OptionChosen;
    public string TypingStage { get; }
    public int StoryIndex { get; }
    public int StoryCount { get; }
    public int ItemStoryIndex { get; }
    public int ItemStoryCount { get; }
    public int CurrentStoryIndex => OptionChosen ? ItemStoryIndex : StoryIndex;
    public int CurrentStoryCount => OptionChosen ? ItemStoryCount : StoryCount;
}

/// <summary>
/// Cached, read-only reflection for EventUI_Normal. queryUiInteractables can identify its buttons,
/// but only this runtime state distinguishes an active typewriter animation from a stable story frame.
/// </summary>
internal sealed class NormalEventUiRuntimeReader
{
    private const string PanelTypeName = "MetroTD.UISystem.WaveFunctionNormalUI";

    private Type? _panelType;
    private MethodInfo? _isOpen;
    private FieldInfo? _isTypingStory;
    private FieldInfo? _currentTypingStage;
    private FieldInfo? _currentChooseItem;
    private FieldInfo? _currentStoryIndex;
    private FieldInfo? _currentStoryList;
    private FieldInfo? _currentItemStoryIndex;
    private FieldInfo? _currentItemStoryList;
    private Component? _cachedPanel;

    public bool IsAvailable { get; private set; }

    public void Initialize()
    {
        IsAvailable = false;
        _cachedPanel = null;
        _panelType = FindType(PanelTypeName);
        if (_panelType == null)
        {
            return;
        }

        const BindingFlags publicInstance = BindingFlags.Public | BindingFlags.Instance | BindingFlags.FlattenHierarchy;
        const BindingFlags privateInstance = BindingFlags.NonPublic | BindingFlags.Instance;
        _isOpen = _panelType.GetMethod("IsOpen", publicInstance, null, Type.EmptyTypes, null);
        _isTypingStory = _panelType.GetField("m_isTypingStory", privateInstance);
        _currentTypingStage = _panelType.GetField("m_currentTypingStage", privateInstance);
        _currentChooseItem = _panelType.GetField("m_currentChooseItem", privateInstance);
        _currentStoryIndex = _panelType.GetField("m_currentStoryIndex", privateInstance);
        _currentStoryList = _panelType.GetField("m_currentStoryList", privateInstance);
        _currentItemStoryIndex = _panelType.GetField("m_currentItemStoryIndex", privateInstance);
        _currentItemStoryList = _panelType.GetField("m_currentItemStoryList", privateInstance);

        IsAvailable = _isOpen != null &&
                      _isTypingStory != null &&
                      _currentTypingStage != null &&
                      _currentChooseItem != null &&
                      _currentStoryIndex != null &&
                      _currentStoryList != null &&
                      _currentItemStoryIndex != null &&
                      _currentItemStoryList != null;
    }

    public bool TryRead(out NormalEventUiRuntimeState state)
    {
        state = default;
        if (!IsAvailable)
        {
            return false;
        }

        try
        {
            Component? panel = ResolveActivePanel();
            if (panel == null || _isOpen!.Invoke(panel, null) is not bool isOpen || !isOpen)
            {
                state = ClosedState();
                return true;
            }

            state = new NormalEventUiRuntimeState(
                true,
                ReadBoolean(_isTypingStory!, panel),
                _currentChooseItem!.GetValue(panel) != null,
                _currentTypingStage!.GetValue(panel)?.ToString() ?? string.Empty,
                ReadInt32(_currentStoryIndex!, panel),
                ReadCount(_currentStoryList!.GetValue(panel)),
                ReadInt32(_currentItemStoryIndex!, panel),
                ReadCount(_currentItemStoryList!.GetValue(panel)));
            return true;
        }
        catch
        {
            _cachedPanel = null;
            state = default;
            return false;
        }
    }

    private Component? ResolveActivePanel()
    {
        if (_cachedPanel != null &&
            _cachedPanel.gameObject != null &&
            _cachedPanel.gameObject.activeInHierarchy)
        {
            return _cachedPanel;
        }

        _cachedPanel = null;
        UnityEngine.Object[] candidates = Resources.FindObjectsOfTypeAll(_panelType!);
        for (int index = 0; index < candidates.Length; index++)
        {
            if (candidates[index] is not Component component ||
                component.gameObject == null ||
                !component.gameObject.scene.IsValid() ||
                !component.gameObject.activeInHierarchy)
            {
                continue;
            }

            _cachedPanel = component;
            return component;
        }

        return null;
    }

    private static NormalEventUiRuntimeState ClosedState() => new(
        false,
        false,
        false,
        string.Empty,
        0,
        0,
        0,
        0);

    private static bool ReadBoolean(FieldInfo field, object target) =>
        field.GetValue(target) is bool value && value;

    private static int ReadInt32(FieldInfo field, object target) =>
        field.GetValue(target) is int value ? value : 0;

    private static int ReadCount(object? value) => value is ICollection collection ? collection.Count : 0;

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
}
