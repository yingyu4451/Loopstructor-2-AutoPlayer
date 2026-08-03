using System.Drawing.Drawing2D;

namespace Loopstructor.AutoPlayer.Manager.UI;

internal sealed class CatalogPickerControl : UserControl
{
    private readonly ComboBox _combo;
    private Font _primaryFont;
    private Font _idFont;

    public CatalogPickerControl()
    {
        AutoScaleMode = AutoScaleMode.Dpi;
        BackColor = Color.Transparent;
        Height = 46;
        MinimumSize = new Size(150, 46);
        Margin = Padding.Empty;

        _primaryFont = Theme.Body(9f, FontStyle.Bold);
        _idFont = Theme.Data(7.5f);
        _combo = new ComboBox
        {
            Dock = DockStyle.Fill,
            DrawMode = DrawMode.OwnerDrawFixed,
            DropDownStyle = ComboBoxStyle.DropDownList,
            IntegralHeight = false,
            MaxDropDownItems = 9,
            Font = _primaryFont
        };
        _combo.DrawItem += DrawCatalogItem;
        _combo.SelectedIndexChanged += (_, _) => SelectedItemChanged?.Invoke(this, EventArgs.Empty);
        Controls.Add(_combo);
        UpdateDpiMetrics();
    }

    public event EventHandler? SelectedItemChanged;

    public CatalogPickerItem? SelectedCatalogItem => _combo.SelectedItem as CatalogPickerItem;

    public string SelectedId => SelectedCatalogItem?.Id ?? string.Empty;

    public int ItemCount => _combo.Items.Count;

    public void SetItems(IEnumerable<CatalogPickerItem> items)
    {
        string selectedId = SelectedId;
        _combo.BeginUpdate();
        try
        {
            _combo.Items.Clear();
            foreach (CatalogPickerItem item in items) _combo.Items.Add(item);

            int matchingIndex = _combo.Items.Cast<CatalogPickerItem>()
                .Select((item, index) => new { item, index })
                .FirstOrDefault(pair => string.Equals(pair.item.Id, selectedId, StringComparison.Ordinal))
                ?.index ?? -1;
            _combo.SelectedIndex = matchingIndex >= 0
                ? matchingIndex
                : _combo.Items.Count > 0 ? 0 : -1;
        }
        finally
        {
            _combo.EndUpdate();
        }
    }

    public void ClearItems()
    {
        _combo.Items.Clear();
        _combo.SelectedIndex = -1;
    }

    protected override void OnDpiChangedAfterParent(EventArgs eventArgs)
    {
        base.OnDpiChangedAfterParent(eventArgs);
        UpdateDpiMetrics();
    }

    protected override void OnEnabledChanged(EventArgs eventArgs)
    {
        base.OnEnabledChanged(eventArgs);
        _combo.Invalidate();
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _combo.DrawItem -= DrawCatalogItem;
            _primaryFont.Dispose();
            _idFont.Dispose();
        }

        base.Dispose(disposing);
    }

    private void UpdateDpiMetrics()
    {
        int itemHeight = ScaleLogical(42);
        Height = ScaleLogical(46);
        MinimumSize = new Size(ScaleLogical(150), Height);
        _combo.ItemHeight = itemHeight;
        _combo.DropDownHeight = (itemHeight * 8) + ScaleLogical(2);
    }

    private void DrawCatalogItem(object? sender, DrawItemEventArgs eventArgs)
    {
        if (eventArgs.Index < 0 || eventArgs.Index >= _combo.Items.Count)
        {
            eventArgs.DrawBackground();
            return;
        }

        if (_combo.Items[eventArgs.Index] is not CatalogPickerItem item)
        {
            eventArgs.DrawBackground();
            return;
        }
        bool selected = (eventArgs.State & DrawItemState.Selected) != 0;
        bool disabled = (eventArgs.State & DrawItemState.Disabled) != 0 || !Enabled;
        Color background = disabled
            ? Color.FromArgb(242, 244, 245)
            : selected ? Color.FromArgb(213, 234, 234) : Color.White;
        Color primary = disabled ? Theme.Muted : Theme.Ink;
        Color secondary = disabled ? Color.FromArgb(143, 151, 156) : selected ? Theme.TealDark : Theme.Muted;
        using (SolidBrush brush = new(background)) eventArgs.Graphics.FillRectangle(brush, eventArgs.Bounds);

        int padding = ScaleLogical(6);
        int iconSize = ScaleLogical(32);
        Rectangle iconBounds = new(
            eventArgs.Bounds.Left + padding,
            eventArgs.Bounds.Top + Math.Max(0, (eventArgs.Bounds.Height - iconSize) / 2),
            iconSize,
            iconSize);
        DrawIcon(eventArgs.Graphics, item, iconBounds);

        int textLeft = iconBounds.Right + ScaleLogical(8);
        int textRightPadding = ScaleLogical(22);
        int textWidth = Math.Max(1, eventArgs.Bounds.Right - textLeft - textRightPadding);
        int primaryHeight = ScaleLogical(20);
        Rectangle primaryBounds = new(textLeft, eventArgs.Bounds.Top + ScaleLogical(2), textWidth, primaryHeight);
        Rectangle idBounds = new(
            textLeft,
            primaryBounds.Bottom - ScaleLogical(1),
            textWidth,
            Math.Max(1, eventArgs.Bounds.Bottom - primaryBounds.Bottom));

        TextRenderer.DrawText(
            eventArgs.Graphics,
            item.DisplayName,
            _primaryFont,
            primaryBounds,
            primary,
            TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix);
        TextRenderer.DrawText(
            eventArgs.Graphics,
            item.Id,
            _idFont,
            idBounds,
            secondary,
            TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix);

        eventArgs.DrawFocusRectangle();
    }

    private void DrawIcon(Graphics graphics, CatalogPickerItem item, Rectangle bounds)
    {
        using (SolidBrush background = new(Color.FromArgb(239, 243, 244))) graphics.FillRectangle(background, bounds);
        if (item.Icon != null)
        {
            graphics.InterpolationMode = InterpolationMode.HighQualityBicubic;
            graphics.PixelOffsetMode = PixelOffsetMode.HighQuality;
            Rectangle destination = FitInside(item.Icon.Size, bounds);
            graphics.DrawImage(item.Icon, destination);
        }
        else
        {
            string marker = item.DisplayName.Length == 0 ? "?" : item.DisplayName[..1];
            TextRenderer.DrawText(
                graphics,
                marker,
                _primaryFont,
                bounds,
                Theme.Muted,
                TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPrefix);
        }

        using Pen border = new(Theme.Line);
        graphics.DrawRectangle(border, bounds.X, bounds.Y, Math.Max(0, bounds.Width - 1), Math.Max(0, bounds.Height - 1));
    }

    private static Rectangle FitInside(Size source, Rectangle target)
    {
        if (source.Width <= 0 || source.Height <= 0) return target;
        float scale = Math.Min((float)target.Width / source.Width, (float)target.Height / source.Height);
        int width = Math.Max(1, (int)Math.Round(source.Width * scale));
        int height = Math.Max(1, (int)Math.Round(source.Height * scale));
        return new Rectangle(
            target.Left + ((target.Width - width) / 2),
            target.Top + ((target.Height - height) / 2),
            width,
            height);
    }

    private int ScaleLogical(int value) => Math.Max(1, (int)Math.Round(value * (DeviceDpi / 96f)));
}

internal sealed class CatalogPickerItem
{
    public CatalogPickerItem(
        string id,
        string name,
        string fallbackName,
        Image? icon,
        IReadOnlyList<string> tags)
    {
        Id = id;
        Name = name;
        FallbackName = fallbackName;
        Icon = icon;
        Tags = tags;
    }

    public string Id { get; }

    public string Name { get; }

    public string FallbackName { get; }

    public Image? Icon { get; }

    public IReadOnlyList<string> Tags { get; }

    public string DisplayName => !string.IsNullOrWhiteSpace(Name)
        ? Name
        : !string.IsNullOrWhiteSpace(FallbackName) ? FallbackName : Id;

    public override string ToString() => DisplayName;
}
