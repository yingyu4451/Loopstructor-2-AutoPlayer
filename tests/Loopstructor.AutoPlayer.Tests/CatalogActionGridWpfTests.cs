using System.Runtime.ExceptionServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using Loopstructor.AutoPlayer.Manager.UI;
using Newtonsoft.Json.Linq;

namespace Loopstructor.AutoPlayer.Tests;

[Collection("WPF UI")]
public sealed class CatalogActionGridWpfTests
{
    [Fact]
    public void IconGrid_ShowsSearchCue_AndReportsOwnedCountWithMouseAction()
    {
        RunSta(() =>
        {
            CatalogActionGridControl grid = new();
            grid.SetItems(new[]
            {
                Item("Relic_B", "遗物乙", 1),
                Item("Relic_A", "遗物甲", 0)
            });
            grid.SetOwnedCounts(new Dictionary<string, int>(StringComparer.Ordinal)
            {
                ["Relic_A"] = 1
            });
            Window window = Host(grid);
            CatalogActionInvokedEventArgs? invoked = null;
            grid.ItemInvoked += (_, eventArgs) => invoked = eventArgs;

            try
            {
                window.Show();
                PumpDispatcher();

                TextBlock hint = Assert.IsType<TextBlock>(grid.FindName("SearchHint"));
                Assert.Contains("搜索", hint.Text, StringComparison.Ordinal);
                ItemsControl host = Assert.IsType<ItemsControl>(grid.FindName("ItemsHost"));
                CatalogActionChoice active = Assert.IsType<CatalogActionChoice>(host.Items[0]);
                Assert.Equal("Relic_A", active.Item.EnumName);
                Assert.Equal(1, active.OwnedCount);

                Border tile = FindVisualChild<Border>(
                    host.ItemContainerGenerator.ContainerFromItem(active),
                    border => ReferenceEquals(border.DataContext, active));
                RaiseMouse(tile, MouseButton.Right);
                Assert.NotNull(invoked);
                Assert.Equal(MouseButton.Right, invoked.Button);
                Assert.Equal(1, invoked.OwnedCount);

                invoked = null;
                grid.IsActionEnabled = false;
                RaiseMouse(tile, MouseButton.Left);
                Assert.Null(invoked);
                Assert.True(grid.IsEnabled);
            }
            finally
            {
                window.Close();
            }
        });
    }

    [Fact]
    public void NumericStepper_UsesDimDisabledStateAtBothBounds()
    {
        RunSta(() =>
        {
            CheatNumericInput input = new() { Minimum = 1, Maximum = 2, Value = 1 };
            Window window = Host(input);
            try
            {
                window.Show();
                PumpDispatcher();
                RepeatButton[] steppers = VisualDescendants<RepeatButton>(input).ToArray();
                Assert.Equal(2, steppers.Length);
                RepeatButton disabled = Assert.Single(steppers, button => !button.IsEnabled);
                Assert.True(disabled.Opacity <= 0.3);
                Assert.Equal(Color.FromRgb(31, 26, 21), ((SolidColorBrush)disabled.Background).Color);

                input.Value = 2;
                PumpDispatcher();
                disabled = Assert.Single(steppers, button => !button.IsEnabled);
                Assert.True(disabled.Opacity <= 0.3);
            }
            finally
            {
                window.Close();
            }
        });
    }

    private static CatalogPickerItem Item(string id, string name, int itemOrder) =>
        new(
            id,
            name,
            string.Empty,
            null,
            new[] { "遗物", id },
            new JObject
            {
                ["groupKey"] = "relic",
                ["groupName"] = "遗物",
                ["groupOrder"] = 0,
                ["itemOrder"] = itemOrder
            },
            id);

    private static Window Host(UIElement content) => new()
    {
        Width = 720,
        Height = 520,
        Content = content,
        WindowStyle = WindowStyle.None,
        ShowInTaskbar = false
    };

    private static void RaiseMouse(Border tile, MouseButton button) =>
        tile.RaiseEvent(new MouseButtonEventArgs(Mouse.PrimaryDevice, 0, button)
        {
            RoutedEvent = UIElement.PreviewMouseDownEvent,
            Source = tile
        });

    private static T FindVisualChild<T>(DependencyObject? parent, Func<T, bool> predicate)
        where T : DependencyObject
    {
        Assert.NotNull(parent);
        foreach (T child in VisualDescendants<T>(parent!))
        {
            if (predicate(child)) return child;
        }
        throw new Xunit.Sdk.XunitException($"找不到 {typeof(T).Name} 子元素。");
    }

    private static IEnumerable<T> VisualDescendants<T>(DependencyObject root) where T : DependencyObject
    {
        for (int index = 0; index < VisualTreeHelper.GetChildrenCount(root); index++)
        {
            DependencyObject child = VisualTreeHelper.GetChild(root, index);
            if (child is T match) yield return match;
            foreach (T descendant in VisualDescendants<T>(child)) yield return descendant;
        }
    }

    private static void RunSta(Action action)
    {
        Exception? failure = null;
        Thread thread = new(() =>
        {
            try { action(); }
            catch (Exception exception) { failure = exception; }
            finally { Dispatcher.CurrentDispatcher.InvokeShutdown(); }
        });
        thread.IsBackground = true;
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        Assert.True(thread.Join(TimeSpan.FromSeconds(15)), "资源图标交互测试超时。");
        if (failure != null) ExceptionDispatchInfo.Capture(failure).Throw();
    }

    private static void PumpDispatcher() =>
        Dispatcher.CurrentDispatcher.Invoke(() => { }, DispatcherPriority.ApplicationIdle);
}
