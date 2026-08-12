using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;

namespace Loopstructor.AutoPlayer.Manager.UI;

internal sealed partial class CatalogActionGridControl : UserControl
{
    private readonly ObservableCollection<CatalogActionChoice> _items = new();
    private readonly ICollectionView _view;

    public CatalogActionGridControl()
    {
        InitializeComponent();
        _view = CollectionViewSource.GetDefaultView(_items);
        _view.Filter = Matches;
        ItemsHost.ItemsSource = _view;
    }

    public event EventHandler<CatalogActionInvokedEventArgs>? ItemInvoked;

    public int ItemCount => _items.Count;

    public bool IsActionEnabled { get; set; } = true;

    public string InteractionHint
    {
        get => InteractionText.Text;
        set => InteractionText.Text = value ?? string.Empty;
    }

    public void SetItems(IEnumerable<CatalogPickerItem> items)
    {
        Dictionary<string, int> counts = _items.ToDictionary(item => item.Item.Id, item => item.OwnedCount, StringComparer.Ordinal);
        _items.Clear();
        foreach (CatalogPickerItem item in items
                     .OrderBy(item => item.GroupOrder)
                     .ThenBy(item => item.ItemOrder)
                     .ThenBy(item => item.EnumName, StringComparer.Ordinal))
        {
            _items.Add(new CatalogActionChoice(item, counts.TryGetValue(item.Id, out int count) ? count : 0));
        }
        _view.Refresh();
    }

    public void SetOwnedCounts(IReadOnlyDictionary<string, int> counts)
    {
        foreach (CatalogActionChoice choice in _items)
        {
            choice.OwnedCount = counts.TryGetValue(choice.Item.EnumName, out int enumCount)
                ? enumCount
                : counts.TryGetValue(choice.Item.Id, out int idCount) ? idCount : 0;
        }
    }

    public void ClearItems()
    {
        _items.Clear();
        SearchBox.Clear();
    }

    private void SearchBox_OnTextChanged(object sender, TextChangedEventArgs eventArgs)
    {
        SearchHint.Visibility = string.IsNullOrEmpty(SearchBox.Text) ? Visibility.Visible : Visibility.Collapsed;
        _view.Refresh();
    }

    private bool Matches(object value) => value is CatalogActionChoice choice && choice.Item.Matches(SearchBox.Text);

    private void Choice_OnPreviewMouseDown(object sender, MouseButtonEventArgs eventArgs)
    {
        if (!IsEnabled || !IsActionEnabled || sender is not Border { DataContext: CatalogActionChoice choice }) return;
        if (eventArgs.ChangedButton is not (MouseButton.Left or MouseButton.Right)) return;
        ItemInvoked?.Invoke(this, new CatalogActionInvokedEventArgs(choice.Item, choice.OwnedCount, eventArgs.ChangedButton));
        eventArgs.Handled = true;
    }
}

internal sealed class CatalogActionChoice : INotifyPropertyChanged
{
    private int _ownedCount;

    public CatalogActionChoice(CatalogPickerItem item, int ownedCount)
    {
        Item = item;
        _ownedCount = Math.Max(0, ownedCount);
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    public CatalogPickerItem Item { get; }

    public int OwnedCount
    {
        get => _ownedCount;
        set
        {
            int next = Math.Max(0, value);
            if (_ownedCount == next) return;
            _ownedCount = next;
            OnPropertyChanged();
            OnPropertyChanged(nameof(IsActive));
            OnPropertyChanged(nameof(CountVisibility));
            OnPropertyChanged(nameof(StateLabel));
            OnPropertyChanged(nameof(AutomationName));
        }
    }

    public bool IsActive => OwnedCount > 0;
    public Visibility CountVisibility => IsActive ? Visibility.Visible : Visibility.Collapsed;
    public string StateLabel => IsActive ? $"已持有 {OwnedCount}" : string.Empty;
    public string AutomationName => $"{Item.DisplayName}，{Item.EnumName}，持有 {OwnedCount}";

    private void OnPropertyChanged([CallerMemberName] string? name = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}

internal sealed class CatalogActionInvokedEventArgs : EventArgs
{
    public CatalogActionInvokedEventArgs(CatalogPickerItem item, int ownedCount, MouseButton button)
    {
        Item = item;
        OwnedCount = Math.Max(0, ownedCount);
        Button = button;
    }

    public CatalogPickerItem Item { get; }
    public int OwnedCount { get; }
    public MouseButton Button { get; }
}
