using System.Runtime.ExceptionServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;
using Loopstructor.AutoPlayer.Manager.UI;
using Newtonsoft.Json.Linq;

namespace Loopstructor.AutoPlayer.Tests;

[Collection("WPF UI")]
public sealed class EnchantmentSelectorWpfTests
{
    [Fact]
    public void GroupedGrid_KeepsFamilyOrder_AndMouseButtonsChangeUnboundedLayers()
    {
        RunSta(() =>
        {
            EnchantmentSelectorControl selector = new();
            selector.SetItems(new[]
            {
                Item("Poison_Train", "毒·列车", "Poison", 10, 1),
                Item("Poison", "毒", "Poison", 10, 0),
                Item("Freeze_Domain", "冰·领域", "Freeze", 20, 3),
                Item("Freeze", "冰", "Freeze", 20, 0),
                Item("Poison_Railway", "毒·轨道", "Poison", 10, 2)
            });
            Window window = new()
            {
                Width = 720,
                Height = 520,
                Content = selector,
                WindowStyle = WindowStyle.None,
                ShowInTaskbar = false
            };

            try
            {
                window.Show();
                PumpDispatcher();
                ItemsControl itemsHost = Assert.IsType<ItemsControl>(selector.FindName("ItemsHost"));
                string[] order = itemsHost.Items.Cast<EnchantmentChoice>().Select(item => item.Item.EnumName).ToArray();
                Assert.Equal(new[] { "Poison", "Poison_Train", "Poison_Railway", "Freeze", "Freeze_Domain" }, order);

                EnchantmentChoice poison = itemsHost.Items.Cast<EnchantmentChoice>().First();
                Border tile = FindVisualChild<Border>(itemsHost.ItemContainerGenerator.ContainerFromItem(poison));
                tile.RaiseEvent(new MouseButtonEventArgs(Mouse.PrimaryDevice, 0, MouseButton.Left)
                {
                    RoutedEvent = UIElement.PreviewMouseDownEvent,
                    Source = tile
                });
                tile.RaiseEvent(new MouseButtonEventArgs(Mouse.PrimaryDevice, 0, MouseButton.Left)
                {
                    RoutedEvent = UIElement.PreviewMouseDownEvent,
                    Source = tile
                });
                Assert.Equal(2, Assert.Single(selector.Selections).Level);

                tile.RaiseEvent(new MouseButtonEventArgs(Mouse.PrimaryDevice, 0, MouseButton.Right)
                {
                    RoutedEvent = UIElement.PreviewMouseDownEvent,
                    Source = tile
                });
                tile.RaiseEvent(new MouseButtonEventArgs(Mouse.PrimaryDevice, 0, MouseButton.Right)
                {
                    RoutedEvent = UIElement.PreviewMouseDownEvent,
                    Source = tile
                });
                Assert.Empty(selector.Selections);
            }
            finally
            {
                window.Close();
            }
        });
    }

    [Fact]
    public void Search_DoesNotReorderFamilyItems()
    {
        RunSta(() =>
        {
            EnchantmentSelectorControl selector = new();
            selector.SetItems(new[]
            {
                Item("Link_Railway", "连锁轨道", "Link", 5, 2),
                Item("Link", "连锁", "Link", 5, 0),
                Item("Link_Train", "连锁列车", "Link", 5, 1)
            });
            TextBox search = Assert.IsType<TextBox>(selector.FindName("SearchBox"));
            ItemsControl host = Assert.IsType<ItemsControl>(selector.FindName("ItemsHost"));
            search.Text = "Link";
            PumpDispatcher();

            Assert.Equal(
                new[] { "Link", "Link_Train", "Link_Railway" },
                host.Items.Cast<EnchantmentChoice>().Select(item => item.Item.EnumName));
        });
    }

    private static CatalogPickerItem Item(string id, string name, string group, int groupOrder, int itemOrder) =>
        new(
            id,
            name,
            string.Empty,
            null,
            new[] { "附魔", group },
            new JObject
            {
                ["groupKey"] = "enchantment:" + group,
                ["groupName"] = group,
                ["groupOrder"] = groupOrder,
                ["itemOrder"] = itemOrder
            },
            id);

    private static T FindVisualChild<T>(DependencyObject? parent) where T : DependencyObject
    {
        Assert.NotNull(parent);
        int count = System.Windows.Media.VisualTreeHelper.GetChildrenCount(parent);
        for (int index = 0; index < count; index++)
        {
            DependencyObject child = System.Windows.Media.VisualTreeHelper.GetChild(parent, index);
            if (child is T result) return result;
            if (System.Windows.Media.VisualTreeHelper.GetChildrenCount(child) > 0)
            {
                try { return FindVisualChild<T>(child); }
                catch (Xunit.Sdk.XunitException) { }
            }
        }
        throw new Xunit.Sdk.XunitException($"找不到 {typeof(T).Name} 子元素。");
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
        Assert.True(thread.Join(TimeSpan.FromSeconds(10)), "WPF 附魔选择测试超时。");
        if (failure != null) ExceptionDispatchInfo.Capture(failure).Throw();
    }

    private static void PumpDispatcher() =>
        Dispatcher.CurrentDispatcher.Invoke(() => { }, DispatcherPriority.ApplicationIdle);
}
