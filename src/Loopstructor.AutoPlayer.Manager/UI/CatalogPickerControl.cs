using System.Collections.ObjectModel;
using System.Globalization;
using System.Reflection;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using Newtonsoft.Json.Linq;

namespace Loopstructor.AutoPlayer.Manager.UI;

internal sealed partial class CatalogPickerControl : UserControl
{
    public static readonly DependencyProperty ShowIconsProperty = DependencyProperty.Register(
        nameof(ShowIcons),
        typeof(bool),
        typeof(CatalogPickerControl),
        new PropertyMetadata(true, ShowIconsChanged));

    private readonly List<CatalogPickerItem> _allItems = new();
    private readonly ObservableCollection<CatalogPickerItem> _visibleItems = new();
    private bool _updatingText;
    private bool _suppressNextFocusOpen;
    private string _selectedId = string.Empty;
    private string _selectionBeforeSearch = string.Empty;

    public CatalogPickerControl()
    {
        InitializeComponent();
        ResultsList.ItemsSource = _visibleItems;
        ResultsList.Tag = ShowIcons;
        ResultsPopup.PlacementTarget = PickerRoot;
        AddHandler(Keyboard.LostKeyboardFocusEvent, new KeyboardFocusChangedEventHandler(OnPickerLostKeyboardFocus), true);
        ResultsList.AddHandler(Keyboard.LostKeyboardFocusEvent, new KeyboardFocusChangedEventHandler(OnPickerLostKeyboardFocus), true);
        Unloaded += (_, _) => ResultsPopup.IsOpen = false;
        SyncIconPresentation();
    }

    public event EventHandler? SelectedItemChanged;

    public bool ShowIcons
    {
        get => (bool)GetValue(ShowIconsProperty);
        set => SetValue(ShowIconsProperty, value);
    }

    public CatalogPickerItem? SelectedCatalogItem => _allItems.FirstOrDefault(item =>
        string.Equals(item.Id, _selectedId, StringComparison.Ordinal));

    public string SelectedId => _selectedId;

    public int ItemCount => _allItems.Count;

    public CatalogPickerItem? FindItem(string? idOrEnumName)
    {
        if (string.IsNullOrWhiteSpace(idOrEnumName)) return null;
        return _allItems.FirstOrDefault(item =>
            string.Equals(item.Id, idOrEnumName, StringComparison.OrdinalIgnoreCase)
            || string.Equals(item.EnumName, idOrEnumName, StringComparison.OrdinalIgnoreCase));
    }

    public void SetItems(IEnumerable<CatalogPickerItem> items)
    {
        string previousId = _selectedId;
        _allItems.Clear();
        _allItems.AddRange(items);
        string nextId = _allItems.Any(item => string.Equals(item.Id, previousId, StringComparison.Ordinal))
            ? previousId
            : _allItems.FirstOrDefault()?.Id ?? string.Empty;
        _selectionBeforeSearch = nextId;
        ReplaceVisibleItems(_allItems, nextId);
        SetSelectedId(nextId, updateText: true);
    }

    public void ClearItems()
    {
        _allItems.Clear();
        _selectionBeforeSearch = string.Empty;
        ReplaceVisibleItems(Array.Empty<CatalogPickerItem>(), string.Empty);
        SetSelectedId(string.Empty, updateText: true);
        ResultsPopup.IsOpen = false;
    }

    private void SearchBox_OnGotKeyboardFocus(object sender, KeyboardFocusChangedEventArgs eventArgs)
    {
        if (_suppressNextFocusOpen)
        {
            _suppressNextFocusOpen = false;
            return;
        }

        OpenResults();
        SearchBox.SelectAll();
    }

    private void SearchBox_OnPreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs eventArgs)
    {
        // GotKeyboardFocus only runs the first time. Re-clicking an already focused
        // search box must also reopen the candidate list after an explicit close.
        if (SearchBox.IsKeyboardFocused) OpenResults();
    }

    private void SearchBox_OnTextChanged(object sender, TextChangedEventArgs eventArgs)
    {
        if (_updatingText) return;
        string query = SearchBox.Text;
        SearchHint.Visibility = string.IsNullOrEmpty(query) ? Visibility.Visible : Visibility.Collapsed;
        CatalogPickerItem[] matches = _allItems.Where(item => item.Matches(query)).ToArray();
        ReplaceVisibleItems(matches, string.Empty);
        SetSelectedId(string.Empty, updateText: false);
        if (IsEnabled && SearchBox.IsKeyboardFocused)
        {
            SyncPopupWidth();
            ResultsPopup.IsOpen = true;
        }
    }

    private void SearchBox_OnPreviewKeyDown(object sender, KeyEventArgs eventArgs)
    {
        if (eventArgs.Key is Key.Down or Key.Up)
        {
            MoveSelection(eventArgs.Key == Key.Down ? 1 : -1);
            eventArgs.Handled = true;
            return;
        }

        if (eventArgs.Key == Key.Enter)
        {
            CommitHighlightedItem();
            eventArgs.Handled = true;
            return;
        }

        if (eventArgs.Key == Key.Escape)
        {
            RestoreSelection();
            eventArgs.Handled = true;
        }
    }

    private void ResultsList_OnPreviewKeyDown(object sender, KeyEventArgs eventArgs)
    {
        if (eventArgs.Key == Key.Enter)
        {
            CommitHighlightedItem();
            eventArgs.Handled = true;
        }
        else if (eventArgs.Key == Key.Escape)
        {
            RestoreSelection();
            _suppressNextFocusOpen = true;
            if (!SearchBox.Focus()) _suppressNextFocusOpen = false;
            eventArgs.Handled = true;
        }
    }

    private void ResultsList_OnPreviewMouseLeftButtonUp(object sender, MouseButtonEventArgs eventArgs)
    {
        DependencyObject? current = eventArgs.OriginalSource as DependencyObject;
        while (current != null && current is not ListBoxItem)
        {
            current = current is FrameworkContentElement contentElement
                ? contentElement.Parent
                : VisualTreeHelper.GetParent(current);
        }

        if (current is not ListBoxItem itemContainer || itemContainer.DataContext is not CatalogPickerItem item) return;
        CommitItem(item);
    }

    private void DropButton_OnClick(object sender, RoutedEventArgs eventArgs)
    {
        SearchBox.Focus();
        OpenResults();
        SearchBox.SelectAll();
    }

    private void OpenResults()
    {
        if (!IsEnabled || _allItems.Count == 0) return;
        if (!ResultsPopup.IsOpen && !string.IsNullOrWhiteSpace(_selectedId))
        {
            _selectionBeforeSearch = _selectedId;
        }

        PrepareResultsForOpen();
        SyncPopupWidth();
        ResultsPopup.IsOpen = true;
    }

    private void OnPickerLostKeyboardFocus(object sender, KeyboardFocusChangedEventArgs eventArgs)
    {
        // Popup content lives in a separate presentation source. Defer the check
        // until WPF has moved focus, then treat both trees as one picker.
        Dispatcher.BeginInvoke(DispatcherPriority.Input, () =>
        {
            if (!ResultsPopup.IsOpen || HasPickerKeyboardFocus()) return;
            RestoreSelection();
        });
    }

    private bool HasPickerKeyboardFocus() =>
        SearchBox.IsKeyboardFocusWithin
        || ResultsList.IsKeyboardFocusWithin
        || DropButton.IsKeyboardFocusWithin
        || (ResultsPopup.Child?.IsKeyboardFocusWithin ?? false);

    private void MoveSelection(int delta)
    {
        if (_visibleItems.Count == 0) return;
        if (!ResultsPopup.IsOpen)
        {
            SyncPopupWidth();
            ResultsPopup.IsOpen = true;
        }
        int index = ResultsList.SelectedIndex;
        if (index < 0) index = delta > 0 ? 0 : _visibleItems.Count - 1;
        else index = Math.Clamp(index + delta, 0, _visibleItems.Count - 1);
        ResultsList.SelectedIndex = index;
        ResultsList.ScrollIntoView(ResultsList.SelectedItem);
    }

    private void CommitHighlightedItem()
    {
        CatalogPickerItem? item = ResultsList.SelectedItem as CatalogPickerItem
                                  ?? _visibleItems.FirstOrDefault();
        if (item != null) CommitItem(item);
    }

    private void CommitItem(CatalogPickerItem item)
    {
        _selectionBeforeSearch = item.Id;
        SetSelectedId(item.Id, updateText: true);
        ReplaceVisibleItems(_allItems, item.Id);
        ResultsPopup.IsOpen = false;
        SearchBox.CaretIndex = SearchBox.Text.Length;
    }

    private void RestoreSelection()
    {
        string restoreId = _allItems.Any(item => string.Equals(item.Id, _selectionBeforeSearch, StringComparison.Ordinal))
            ? _selectionBeforeSearch
            : string.Empty;
        ReplaceVisibleItems(_allItems, restoreId);
        SetSelectedId(restoreId, updateText: true);
        ResultsPopup.IsOpen = false;
    }

    private void ReplaceVisibleItems(IEnumerable<CatalogPickerItem> items, string selectedId)
    {
        _visibleItems.Clear();
        foreach (CatalogPickerItem item in items) _visibleItems.Add(item);
        ResultsList.SelectedItem = _visibleItems.FirstOrDefault(item =>
            string.Equals(item.Id, selectedId, StringComparison.Ordinal));
        EmptyText.Visibility = _visibleItems.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    private void PrepareResultsForOpen()
    {
        if (!string.IsNullOrWhiteSpace(_selectedId))
        {
            ReplaceVisibleItems(_allItems, _selectedId);
            return;
        }

        string query = SearchBox.Text;
        ReplaceVisibleItems(_allItems.Where(item => item.Matches(query)), string.Empty);
    }

    private void SetSelectedId(string id, bool updateText)
    {
        bool changed = !string.Equals(_selectedId, id, StringComparison.Ordinal);
        _selectedId = id;
        if (updateText)
        {
            _updatingText = true;
            try
            {
                SearchBox.Text = SelectedCatalogItem?.SelectionText ?? string.Empty;
                SearchHint.Visibility = string.IsNullOrEmpty(SearchBox.Text) ? Visibility.Visible : Visibility.Collapsed;
            }
            finally
            {
                _updatingText = false;
            }
        }

        SyncSelectedIcon();

        if (changed) SelectedItemChanged?.Invoke(this, EventArgs.Empty);
    }

    private void SyncPopupWidth() => ResultsPopup.Width = Math.Max(ActualWidth, MinWidth);

    private static void ShowIconsChanged(DependencyObject dependencyObject, DependencyPropertyChangedEventArgs eventArgs)
    {
        if (dependencyObject is CatalogPickerControl picker && picker.ResultsList != null)
        {
            picker.ResultsList.Tag = eventArgs.NewValue;
            picker.SyncIconPresentation();
        }
    }

    private void SyncIconPresentation()
    {
        if (SelectedIconColumn == null || SelectedIconFrame == null) return;
        SelectedIconColumn.Width = ShowIcons ? new GridLength(42) : new GridLength(0);
        SelectedIconFrame.Visibility = ShowIcons ? Visibility.Visible : Visibility.Collapsed;
        SyncSelectedIcon();
    }

    private void SyncSelectedIcon()
    {
        if (SelectedIcon == null || SelectedIconFallback == null) return;
        SelectedIcon.Source = ShowIcons ? SelectedCatalogItem?.Icon : null;
        SelectedIconFallback.Visibility = ShowIcons && SelectedIcon.Source == null
            ? Visibility.Visible
            : Visibility.Collapsed;
    }
}

internal sealed class CatalogPickerItem
{
    private static readonly Regex LevelSuffix = new(
        @"_L(\d+)$",
        RegexOptions.Compiled | RegexOptions.CultureInvariant | RegexOptions.IgnoreCase);

    public CatalogPickerItem(
        string id,
        string name,
        string fallbackName,
        ImageSource? icon,
        IReadOnlyList<string> tags,
        object? payload = null,
        string? enumName = null)
    {
        Id = id;
        Name = name;
        FallbackName = fallbackName;
        EnumName = string.IsNullOrWhiteSpace(enumName) ? id : enumName.Trim();
        Icon = icon;
        Tags = tags;
        Payload = payload;
        LevelLabel = ResolveLevelLabel(payload, id);
        GroupKey = ReadText(payload, "groupKey", id);
        GroupName = ReadText(payload, "groupName", GroupKey);
        GroupOrder = ReadInt(payload, "groupOrder", int.MaxValue);
        ItemOrder = ReadInt(payload, "itemOrder", int.MaxValue);
        TypeKey = ReadText(payload, "typeKey", GroupKey);
        TypeName = ReadText(payload, "typeName", TypeKey);
        TypeOrder = ReadInt(payload, "typeOrder", GroupOrder);
        FamilyKey = ReadText(payload, "familyKey", GroupKey);
        FamilyOrder = ReadInt(payload, "familyOrder", GroupOrder);
        Description = ReadText(payload, "description", "游戏未提供描述");
    }

    public string Id { get; }

    public string Name { get; }

    public string FallbackName { get; }

    public string EnumName { get; }

    public ImageSource? Icon { get; }

    public IReadOnlyList<string> Tags { get; }

    public object? Payload { get; }

    public string LevelLabel { get; }

    public string GroupKey { get; }

    public string GroupName { get; }

    public int GroupOrder { get; }

    public int ItemOrder { get; }

    public string TypeKey { get; }

    public string TypeName { get; }

    public int TypeOrder { get; }

    public string FamilyKey { get; }

    public int FamilyOrder { get; }

    public string Description { get; }

    public string DisplayName => !string.IsNullOrWhiteSpace(Name)
        ? Name
        : !string.IsNullOrWhiteSpace(FallbackName) ? FallbackName : Id;

    public string TechnicalLabel => string.Equals(EnumName, Id, StringComparison.OrdinalIgnoreCase)
        ? $"枚举 {EnumName}"
        : $"枚举 {EnumName} · ID {Id}";

    public string SelectionText
    {
        get
        {
            string identity = string.IsNullOrWhiteSpace(EnumName)
                ? DisplayName
                : $"{DisplayName} · {EnumName}";
            return string.IsNullOrWhiteSpace(LevelLabel)
                ? identity
                : $"{identity} · {LevelLabel}";
        }
    }

    public bool Matches(string query)
    {
        if (string.IsNullOrWhiteSpace(query)) return true;
        string[] terms = query.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        return terms.All(term => SearchValues().Any(value =>
            value.Contains(term, StringComparison.CurrentCultureIgnoreCase)));
    }

    public override string ToString() => DisplayName;

    private IEnumerable<string> SearchValues()
    {
        yield return Id;
        yield return Name;
        yield return FallbackName;
        yield return EnumName;
        yield return LevelLabel;
        yield return GroupKey;
        yield return GroupName;
        yield return TypeKey;
        yield return TypeName;
        foreach (string tag in Tags) yield return tag;
    }

    private static string ResolveLevelLabel(object? payload, string id)
    {
        if (TryReadLevel(payload, out int payloadLevel) && payloadLevel >= 0) return $"Lv.{payloadLevel}";
        Match match = LevelSuffix.Match(id);
        return match.Success ? $"Lv.{match.Groups[1].Value}" : string.Empty;
    }

    private static bool TryReadLevel(object? payload, out int level)
    {
        level = 0;
        object? value = payload switch
        {
            JObject json => json.GetValue("level", StringComparison.OrdinalIgnoreCase),
            JProperty property when string.Equals(property.Name, "level", StringComparison.OrdinalIgnoreCase) => property.Value,
            _ => null
        };

        if (value == null && payload != null)
        {
            Type type = payload.GetType();
            PropertyInfo? property = type.GetProperty("Level", BindingFlags.Instance | BindingFlags.Public | BindingFlags.IgnoreCase);
            value = property?.GetValue(payload);
            if (value == null)
            {
                FieldInfo? field = type.GetField("Level", BindingFlags.Instance | BindingFlags.Public | BindingFlags.IgnoreCase);
                value = field?.GetValue(payload);
            }
        }

        string text = value switch
        {
            null => string.Empty,
            JValue token => token.ToString(CultureInfo.InvariantCulture),
            JToken token => token.ToString(),
            IFormattable formattable => formattable.ToString(null, CultureInfo.InvariantCulture),
            _ => value.ToString() ?? string.Empty
        };
        if (int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out level)) return true;
        if (!decimal.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out decimal numeric)
            || numeric != decimal.Truncate(numeric)
            || numeric is < int.MinValue or > int.MaxValue) return false;
        level = Decimal.ToInt32(numeric);
        return true;
    }

    private static string ReadText(object? payload, string name, string fallback) => payload switch
    {
        JObject json => json.Value<string>(name) ?? fallback,
        _ => fallback
    };

    private static int ReadInt(object? payload, string name, int fallback) => payload switch
    {
        JObject json => json.Value<int?>(name) ?? fallback,
        _ => fallback
    };
}
