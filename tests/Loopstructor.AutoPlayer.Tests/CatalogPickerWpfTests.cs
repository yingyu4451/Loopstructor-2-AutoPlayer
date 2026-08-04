using System.Runtime.ExceptionServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Threading;
using Loopstructor.AutoPlayer.Manager.UI;

namespace Loopstructor.AutoPlayer.Tests;

[CollectionDefinition("WPF UI", DisableParallelization = true)]
public sealed class WpfUiCollection
{
}

[Collection("WPF UI")]
public sealed class CatalogPickerWpfTests
{
    [Fact]
    public void SearchInput_OpensFilteredLevelledResults()
    {
        RunSta(() =>
        {
            Window? window = null;
            try
            {
                CatalogPickerControl picker = new();
                picker.SetItems(new[]
                {
                    Item("Link_ElectricFork_L1", "雷叉"),
                    Item("Link_ElectricFork_L2", "雷叉"),
                    Item("Link_ElectricFork_L3", "雷叉"),
                    Item("Shell_DoubleShell_L4", "双发重炮")
                });
                window = new Window
                {
                    Width = 520,
                    Height = 240,
                    Content = picker,
                    WindowStyle = WindowStyle.None,
                    ShowInTaskbar = false
                };
                window.Show();
                window.Activate();

                TextBox search = Assert.IsType<TextBox>(picker.FindName("SearchBox"));
                Popup popup = Assert.IsType<Popup>(picker.FindName("ResultsPopup"));
                ListBox results = Assert.IsType<ListBox>(picker.FindName("ResultsList"));
                Assert.True(search.Focus());
                search.Text = "雷叉";
                Dispatcher.CurrentDispatcher.Invoke(() => { }, DispatcherPriority.ApplicationIdle);

                Assert.True(popup.IsOpen);
                Assert.True(popup.StaysOpen);
                Border frame = Assert.IsType<Border>(picker.FindName("PickerFrame"));
                Assert.True(frame.IsKeyboardFocusWithin);
                Assert.Equal(2, frame.BorderThickness.Left);
                CatalogPickerItem[] visible = results.Items.Cast<CatalogPickerItem>().ToArray();
                Assert.Equal(3, visible.Length);
                Assert.Equal(new[] { "Lv.1", "Lv.2", "Lv.3" }, visible.Select(item => item.LevelLabel));
            }
            finally
            {
                window?.Close();
            }
        });
    }

    [Fact]
    public void Popup_RemainsOpenAcrossPickerFocus_AndClosesAfterWholePickerLosesFocus()
    {
        RunSta(() =>
        {
            CatalogPickerControl picker = new();
            picker.SetItems(new[]
            {
                Item("Link_ElectricFork_L1", "雷叉"),
                Item("Link_ElectricFork_L2", "雷叉")
            });
            Button outside = new() { Content = "外部控件" };
            StackPanel root = new();
            root.Children.Add(picker);
            root.Children.Add(outside);
            Window window = new()
            {
                Width = 520,
                Height = 240,
                Content = root,
                WindowStyle = WindowStyle.None,
                ShowInTaskbar = false
            };

            try
            {
                window.Show();
                window.Activate();
                TextBox search = Assert.IsType<TextBox>(picker.FindName("SearchBox"));
                Popup popup = Assert.IsType<Popup>(picker.FindName("ResultsPopup"));
                ListBox results = Assert.IsType<ListBox>(picker.FindName("ResultsList"));

                Assert.True(search.Focus());
                PumpDispatcher();
                Assert.True(popup.IsOpen);

                Assert.True(results.Focus());
                PumpDispatcher();
                Assert.True(popup.IsOpen);

                Assert.True(outside.Focus());
                PumpDispatcher();
                Assert.False(popup.IsOpen);
                Assert.Equal("Link_ElectricFork_L1", picker.SelectedId);
            }
            finally
            {
                window.Close();
            }
        });
    }

    [Fact]
    public void SelectedItem_RendersItsIconNameIdAndLevel()
    {
        RunSta(() =>
        {
            DrawingImage icon = new(new GeometryDrawing(
                Brushes.Gold,
                new Pen(Brushes.SaddleBrown, 1),
                Geometry.Parse("M 1 1 L 9 1 L 9 9 L 1 9 Z")));
            CatalogPickerControl picker = new();
            picker.SetItems(new[]
            {
                new CatalogPickerItem(
                    "Link_ElectricFork_L12",
                    "雷叉",
                    string.Empty,
                    icon,
                    new[] { "战车" })
            });

            TextBox search = Assert.IsType<TextBox>(picker.FindName("SearchBox"));
            Image selectedIcon = Assert.IsType<Image>(picker.FindName("SelectedIcon"));
            Assert.Same(icon, selectedIcon.Source);
            Assert.Equal("雷叉 · Link_ElectricFork_L12 · Lv.12", search.Text);
            Assert.Equal("Link_ElectricFork_L12", picker.SelectedCatalogItem?.Id);
        });
    }

    [Fact]
    public void EnumName_IsVisibleAndSearchable_WhenDifferentFromProtocolId()
    {
        RunSta(() =>
        {
            CatalogPickerControl picker = new();
            picker.SetItems(new[]
            {
                new CatalogPickerItem(
                    "vehicle-42",
                    "雷叉",
                    string.Empty,
                    null,
                    new[] { "战车" },
                    enumName: "Link_ElectricFork_L2")
            });

            TextBox search = Assert.IsType<TextBox>(picker.FindName("SearchBox"));
            Assert.Contains("Link_ElectricFork_L2", search.Text, StringComparison.Ordinal);
            search.Text = "ElectricFork";
            Assert.Equal("vehicle-42", picker.FindItem("Link_ElectricFork_L2")?.Id);
            ListBox results = Assert.IsType<ListBox>(picker.FindName("ResultsList"));
            CatalogPickerItem match = Assert.IsType<CatalogPickerItem>(Assert.Single(results.Items));
            Assert.Equal("枚举 Link_ElectricFork_L2 · ID vehicle-42", match.TechnicalLabel);
        });
    }

    [Fact]
    public void SearchInput_UsesReadableSelectionHighlight()
    {
        RunSta(() =>
        {
            CatalogPickerControl picker = new();
            TextBox search = Assert.IsType<TextBox>(picker.FindName("SearchBox"));
            SolidColorBrush selectionBrush = Assert.IsType<SolidColorBrush>(search.SelectionBrush);

            Assert.Equal(Color.FromRgb(0x2D, 0x78, 0x9C), selectionBrush.Color);
            Assert.Equal(0.92, search.SelectionOpacity, precision: 2);
            Assert.True(search.IsInactiveSelectionHighlightEnabled);
        });
    }

    private static void RunSta(Action action)
    {
        Exception? failure = null;
        Thread thread = new(() =>
        {
            try
            {
                action();
            }
            catch (Exception exception)
            {
                failure = exception;
            }
            finally
            {
                Dispatcher.CurrentDispatcher.InvokeShutdown();
            }
        });
        thread.IsBackground = true;
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();

        Assert.True(thread.Join(TimeSpan.FromSeconds(10)), "WPF 搜索弹层测试超时。");
        if (failure != null) ExceptionDispatchInfo.Capture(failure).Throw();
    }

    private static void PumpDispatcher() =>
        Dispatcher.CurrentDispatcher.Invoke(() => { }, DispatcherPriority.ApplicationIdle);

    private static CatalogPickerItem Item(string id, string name) => new(
        id,
        name,
        string.Empty,
        null,
        new[] { "战车" });
}
