using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace Loopstructor.AutoPlayer.Manager.UI;

internal sealed partial class VehicleQuickSelectorControl : UserControl
{
    private const string AllTypesKey = "*";

    private readonly List<CatalogPickerItem> _allItems = new();
    private readonly ObservableCollection<VehicleTypeChoice> _types = new();
    private readonly ObservableCollection<VehicleSeriesChoice> _series = new();
    private string _selectedId = string.Empty;
    private string _activeTypeKey = AllTypesKey;

    public VehicleQuickSelectorControl()
    {
        InitializeComponent();
        TypeHost.ItemsSource = _types;
        SeriesHost.ItemsSource = _series;
    }

    public event EventHandler? SelectedItemChanged;

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
        _allItems.AddRange(items
            .OrderBy(item => item.TypeOrder)
            .ThenBy(item => item.FamilyOrder)
            .ThenBy(item => item.ItemOrder)
            .ThenBy(item => item.EnumName, StringComparer.Ordinal));

        BuildTypes();
        CatalogPickerItem? selected = _allItems.FirstOrDefault(item =>
                                          string.Equals(item.Id, previousId, StringComparison.Ordinal))
                                      ?? _allItems.FirstOrDefault();
        _selectedId = selected?.Id ?? string.Empty;
        if (selected != null)
        {
            _activeTypeKey = selected.TypeKey;
        }
        else
        {
            _activeTypeKey = AllTypesKey;
        }

        SearchBox.Clear();
        RefreshView();
        SelectedItemChanged?.Invoke(this, EventArgs.Empty);
    }

    public void ClearItems()
    {
        _allItems.Clear();
        _types.Clear();
        _series.Clear();
        _selectedId = string.Empty;
        _activeTypeKey = AllTypesKey;
        SearchBox.Clear();
        EmptyText.Visibility = Visibility.Visible;
    }

    private void BuildTypes()
    {
        _types.Clear();
        _types.Add(new VehicleTypeChoice(AllTypesKey, "全部", int.MinValue));
        foreach (IGrouping<string, CatalogPickerItem> group in _allItems
                     .GroupBy(item => item.TypeKey, StringComparer.Ordinal)
                     .OrderBy(group => group.Min(item => item.TypeOrder))
                     .ThenBy(group => group.Key, StringComparer.Ordinal))
        {
            _types.Add(new VehicleTypeChoice(
                group.Key,
                string.IsNullOrWhiteSpace(group.Key) ? "其他" : group.Key,
                group.Min(item => item.TypeOrder)));
        }
    }

    private void SearchBox_OnTextChanged(object sender, TextChangedEventArgs eventArgs)
    {
        SearchHint.Visibility = string.IsNullOrEmpty(SearchBox.Text) ? Visibility.Visible : Visibility.Collapsed;
        RefreshView();
    }

    private void TypeButton_OnClick(object sender, RoutedEventArgs eventArgs)
    {
        if (sender is not Button { DataContext: VehicleTypeChoice choice }) return;
        _activeTypeKey = choice.Key;
        if (SearchBox.Text.Length > 0) SearchBox.Clear();
        else RefreshView();
    }

    private void LevelButton_OnClick(object sender, RoutedEventArgs eventArgs)
    {
        if (sender is not Button { DataContext: VehicleLevelChoice choice }) return;
        if (string.Equals(_selectedId, choice.Item.Id, StringComparison.Ordinal)) return;
        _selectedId = choice.Item.Id;
        _activeTypeKey = choice.Item.TypeKey;
        RefreshSelection();
        SelectedItemChanged?.Invoke(this, EventArgs.Empty);
    }

    private void RefreshView()
    {
        string query = SearchBox.Text.Trim();
        bool searching = query.Length > 0;
        string typeKey = searching ? AllTypesKey : _activeTypeKey;
        IEnumerable<CatalogPickerItem> candidates = _allItems;
        if (!searching && !string.Equals(typeKey, AllTypesKey, StringComparison.Ordinal))
        {
            candidates = candidates.Where(item => string.Equals(item.TypeKey, typeKey, StringComparison.Ordinal));
        }
        if (searching) candidates = candidates.Where(item => item.Matches(query));

        _series.Clear();
        foreach (IGrouping<string, CatalogPickerItem> family in candidates
                     .GroupBy(item => item.FamilyKey, StringComparer.Ordinal)
                     .OrderBy(group => group.Min(item => item.TypeOrder))
                     .ThenBy(group => group.Min(item => item.FamilyOrder))
                     .ThenBy(group => group.Key, StringComparer.Ordinal))
        {
            CatalogPickerItem[] levels = family
                .OrderBy(item => item.ItemOrder)
                .ThenBy(item => item.EnumName, StringComparer.Ordinal)
                .ToArray();
            _series.Add(new VehicleSeriesChoice(family.Key, levels));
        }

        foreach (VehicleTypeChoice type in _types)
        {
            type.IsSelected = !searching && string.Equals(type.Key, _activeTypeKey, StringComparison.Ordinal);
        }
        EmptyText.Visibility = _series.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        RefreshSelection();
    }

    private void RefreshSelection()
    {
        foreach (VehicleSeriesChoice series in _series)
        {
            series.UpdateSelection(_selectedId);
        }
    }
}

internal sealed class VehicleTypeChoice : INotifyPropertyChanged
{
    private bool _isSelected;

    public VehicleTypeChoice(string key, string displayName, int order)
    {
        Key = key;
        DisplayName = displayName;
        Order = order;
    }

    public event PropertyChangedEventHandler? PropertyChanged;
    public string Key { get; }
    public string DisplayName { get; }
    public int Order { get; }
    public string AutomationName => $"筛选战车类型 {DisplayName}";
    public bool IsSelected
    {
        get => _isSelected;
        set
        {
            if (_isSelected == value) return;
            _isSelected = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsSelected)));
        }
    }
}

internal sealed class VehicleSeriesChoice : INotifyPropertyChanged
{
    private bool _isSelected;

    public VehicleSeriesChoice(string familyKey, IReadOnlyList<CatalogPickerItem> items)
    {
        FamilyKey = familyKey;
        CatalogPickerItem representative = items[0];
        DisplayName = representative.DisplayName;
        Icon = representative.Icon;
        Levels = new ObservableCollection<VehicleLevelChoice>(items.Select(item => new VehicleLevelChoice(item)));
    }

    public event PropertyChangedEventHandler? PropertyChanged;
    public string FamilyKey { get; }
    public string DisplayName { get; }
    public ImageSource? Icon { get; }
    public ObservableCollection<VehicleLevelChoice> Levels { get; }
    public bool IsSelected
    {
        get => _isSelected;
        private set
        {
            if (_isSelected == value) return;
            _isSelected = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsSelected)));
        }
    }

    public void UpdateSelection(string selectedId)
    {
        foreach (VehicleLevelChoice level in Levels)
        {
            level.IsSelected = string.Equals(level.Item.Id, selectedId, StringComparison.Ordinal);
        }
        IsSelected = Levels.Any(level => level.IsSelected);
    }
}

internal sealed class VehicleLevelChoice : INotifyPropertyChanged
{
    private bool _isSelected;

    public VehicleLevelChoice(CatalogPickerItem item) => Item = item;

    public event PropertyChangedEventHandler? PropertyChanged;
    public CatalogPickerItem Item { get; }
    public string DisplayLevel => string.IsNullOrWhiteSpace(Item.LevelLabel) ? "默认" : Item.LevelLabel;
    public string AutomationName => $"选择 {Item.DisplayName} {DisplayLevel}";
    public bool IsSelected
    {
        get => _isSelected;
        set
        {
            if (_isSelected == value) return;
            _isSelected = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsSelected)));
        }
    }
}
