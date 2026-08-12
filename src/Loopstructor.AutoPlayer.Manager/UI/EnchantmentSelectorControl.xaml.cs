using System.Collections.ObjectModel;
using System.Collections;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Media;

namespace Loopstructor.AutoPlayer.Manager.UI;

internal sealed partial class EnchantmentSelectorControl : UserControl
{
    private readonly ObservableCollection<EnchantmentChoice> _items = new();
    private readonly ObservableCollection<EnchantmentChoice> _selectedItems = new();
    private readonly ICollectionView _view;

    public EnchantmentSelectorControl()
    {
        InitializeComponent();
        _view = CollectionViewSource.GetDefaultView(_items);
        _view.GroupDescriptions.Add(new PropertyGroupDescription(nameof(EnchantmentChoice.GroupName)));
        _view.Filter = Matches;
        ItemsHost.ItemsSource = _view;
    }

    public event EventHandler? SelectionChanged;

    public IReadOnlyList<CheatEnchantmentSelection> Selections => _items
        .Where(item => item.Level > 0)
        .Select(item => new CheatEnchantmentSelection(item.Item, item.Level))
        .ToArray();

    public int ItemCount => _items.Count;

    public IEnumerable SelectedChoices => _selectedItems;

    public void SetItems(IEnumerable<CatalogPickerItem> items)
    {
        Dictionary<string, int> selected = _items
            .Where(item => item.Level > 0)
            .ToDictionary(item => item.Item.Id, item => item.Level, StringComparer.Ordinal);
        _items.Clear();
        foreach (CatalogPickerItem item in items
                     .OrderBy(item => item.GroupOrder)
                     .ThenBy(item => item.ItemOrder)
                     .ThenBy(item => item.EnumName, StringComparer.Ordinal))
        {
            _items.Add(new EnchantmentChoice(item, selected.TryGetValue(item.Id, out int level) ? level : 0));
        }
        RefreshSummary();
    }

    public void ClearItems()
    {
        _items.Clear();
        SearchBox.Clear();
        RefreshSummary();
    }

    public void ClearSelections()
    {
        foreach (EnchantmentChoice item in _items) item.Level = 0;
        RefreshSummary();
        SelectionChanged?.Invoke(this, EventArgs.Empty);
    }

    public void CloseDetails()
    {
        CloseDetails(ItemsHost);
    }

    private void SearchBox_OnTextChanged(object sender, TextChangedEventArgs e)
    {
        SearchHint.Visibility = string.IsNullOrEmpty(SearchBox.Text) ? Visibility.Visible : Visibility.Collapsed;
        _view.Refresh();
    }

    private bool Matches(object value) => value is EnchantmentChoice choice && choice.Item.Matches(SearchBox.Text);

    private void Choice_OnPreviewMouseDown(object sender, MouseButtonEventArgs e)
    {
        if (!IsEnabled || sender is not Border { DataContext: EnchantmentChoice choice }) return;
        CloseDetails();
        if (e.ChangedButton == MouseButton.Left)
        {
            choice.Level = choice.Level == int.MaxValue ? int.MaxValue : choice.Level + 1;
        }
        else if (e.ChangedButton == MouseButton.Right)
        {
            choice.Level = Math.Max(0, choice.Level - 1);
        }
        else return;
        RefreshSummary();
        SelectionChanged?.Invoke(this, EventArgs.Empty);
        e.Handled = true;
    }

    private void ItemsScrollViewer_OnScrollChanged(object sender, ScrollChangedEventArgs eventArgs)
    {
        if (eventArgs.VerticalChange == 0 && eventArgs.HorizontalChange == 0) return;
        CloseDetails();
    }

    private static void CloseDetails(DependencyObject root)
    {
        if (root is FrameworkElement { ToolTip: ToolTip toolTip }) toolTip.IsOpen = false;
        for (int index = 0; index < VisualTreeHelper.GetChildrenCount(root); index++)
        {
            CloseDetails(VisualTreeHelper.GetChild(root, index));
        }
    }

    private void RefreshSummary()
    {
        EnchantmentChoice[] selected = _items.Where(item => item.Level > 0).ToArray();
        _selectedItems.Clear();
        foreach (EnchantmentChoice item in selected) _selectedItems.Add(item);
        long layers = selected.Aggregate<EnchantmentChoice, long>(0, (total, item) => total + item.Level);
        SummaryText.Text = $"已选 {selected.Length} 种附魔 · 共 {layers} 层";
    }
}

internal sealed class EnchantmentChoice : INotifyPropertyChanged
{
    private int _level;

    public EnchantmentChoice(CatalogPickerItem item, int level)
    {
        Item = item;
        _level = Math.Max(0, level);
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    public CatalogPickerItem Item { get; }
    public string GroupName => Item.GroupName;
    public int Level
    {
        get => _level;
        set
        {
            int next = Math.Max(0, value);
            if (_level == next) return;
            _level = next;
            OnPropertyChanged();
            OnPropertyChanged(nameof(IsSelected));
            OnPropertyChanged(nameof(BadgeVisibility));
            OnPropertyChanged(nameof(AutomationName));
        }
    }
    public bool IsSelected => Level > 0;
    public Visibility BadgeVisibility => IsSelected ? Visibility.Visible : Visibility.Collapsed;
    public string AutomationName => $"{Item.DisplayName}，{Item.EnumName}，当前 {Level} 层；左键增加，右键减少";

    private void OnPropertyChanged([CallerMemberName] string? name = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}
