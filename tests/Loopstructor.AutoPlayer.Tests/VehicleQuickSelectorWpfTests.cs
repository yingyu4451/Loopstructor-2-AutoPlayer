using System.Reflection;
using System.Runtime.ExceptionServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using Loopstructor.AutoPlayer.Manager.UI;
using Newtonsoft.Json.Linq;

namespace Loopstructor.AutoPlayer.Tests;

[Collection("WPF UI")]
public sealed class VehicleQuickSelectorWpfTests
{
    [Fact]
    public void Selector_GroupsByTypeAndFamily_AndOrdersLevelsAscending()
    {
        RunSta(() =>
        {
            VehicleQuickSelectorControl selector = new();
            Assert.Null(selector.ToolTip);
            Assert.Null(Assert.IsType<TextBox>(selector.FindName("SearchBox")).ToolTip);
            selector.SetItems(new[]
            {
                Vehicle("Link_Beta_L2", "Link", 1, "Link_Beta", 20, 2),
                Vehicle("Shell_Alpha_L3", "Shell", 0, "Shell_Alpha", 10, 3),
                Vehicle("Link_Beta_L1", "Link", 1, "Link_Beta", 20, 1),
                Vehicle("Shell_Alpha_L1", "Shell", 0, "Shell_Alpha", 10, 1),
                Vehicle("Future_Gamma_L1", "Future", 2, "Future_Gamma", 30, 1)
            });

            Assert.Equal(new[] { "全部", "Shell", "Link", "Future" }, Types(selector).Select(type => type.DisplayName));
            VehicleSeriesChoice shell = Assert.Single(Series(selector));
            Assert.Equal("Shell_Alpha", shell.FamilyKey);
            Assert.Equal(new[] { "Shell_Alpha_L1", "Shell_Alpha_L3" }, shell.Levels.Select(level => level.Item.Id));
            Assert.Equal("Shell_Alpha_L1", selector.SelectedId);
        });
    }

    [Fact]
    public void CurrentCheatRoster_ShowsFourTypes_FourSeriesEach_AndOnlyL1ToL3()
    {
        RunSta(() =>
        {
            VehicleQuickSelectorControl selector = new();
            CatalogPickerItem[] roster = CurrentCheatRoster().ToArray();

            selector.SetItems(roster);

            Assert.Equal(48, selector.ItemCount);
            Assert.Equal(
                new[] { "全部", "Shell", "Link", "Penetrate", "Missile" },
                Types(selector).Select(type => type.DisplayName));
            foreach (string type in new[] { "Shell", "Link", "Penetrate", "Missile" })
            {
                CatalogPickerItem first = roster.First(item => item.TypeKey == type);
                Select(selector, first.Id);
                VehicleSeriesChoice[] series = Series(selector).ToArray();
                Assert.Equal(4, series.Length);
                Assert.All(series, family =>
                {
                    Assert.Equal(3, family.Levels.Count);
                    Assert.Equal(new[] { "Lv.1", "Lv.2", "Lv.3" }, family.Levels.Select(level => level.DisplayLevel));
                    Assert.DoesNotContain(family.Levels, level => level.Item.Id.EndsWith("_L4", StringComparison.Ordinal));
                });
            }
        });
    }

    [Fact]
    public void SearchCrossesTypes_AndClearingRestoresPreviousType()
    {
        RunSta(() =>
        {
            VehicleQuickSelectorControl selector = new();
            selector.SetItems(new[]
            {
                Vehicle("Shell_Alpha_L1", "Shell", 0, "Shell_Alpha", 10, 1),
                Vehicle("Link_Beta_L1", "Link", 1, "Link_Beta", 20, 1)
            });
            TextBox search = Assert.IsType<TextBox>(selector.FindName("SearchBox"));

            search.Text = "Link_Beta";
            PumpDispatcher();
            Assert.Equal("Link_Beta", Assert.Single(Series(selector)).FamilyKey);

            search.Clear();
            PumpDispatcher();
            Assert.Equal("Shell_Alpha", Assert.Single(Series(selector)).FamilyKey);
        });
    }

    [Fact]
    public void RefreshPreservesSelectedVehicleWhenStillPresent()
    {
        RunSta(() =>
        {
            VehicleQuickSelectorControl selector = new();
            CatalogPickerItem shell = Vehicle("Shell_Alpha_L1", "Shell", 0, "Shell_Alpha", 10, 1);
            CatalogPickerItem link = Vehicle("Link_Beta_L1", "Link", 1, "Link_Beta", 20, 1);
            selector.SetItems(new[] { shell, link });
            Select(selector, link.Id);

            selector.SetItems(new[] { shell, link, Vehicle("Future_Gamma_L1", "Future", 2, "Future_Gamma", 30, 1) });

            Assert.Equal(link.Id, selector.SelectedId);
            Assert.Equal(link.Id, selector.SelectedCatalogItem?.Id);
        });
    }

    [Fact]
    public void DisabledTypeAndLevelButtons_KeepDarkMechanicalFace()
    {
        RunSta(() =>
        {
            VehicleQuickSelectorControl selector = new();
            selector.SetItems(new[]
            {
                Vehicle("Shell_Alpha_L1", "Shell", 0, "Shell_Alpha", 10, 1),
                Vehicle("Shell_Alpha_L2", "Shell", 0, "Shell_Alpha", 10, 2)
            });
            selector.IsEnabled = false;
            Window window = new()
            {
                Width = 520,
                Height = 360,
                Content = selector,
                ShowInTaskbar = false,
                WindowStyle = WindowStyle.None
            };

            try
            {
                window.Show();
                PumpDispatcher();

                Button[] buttons = VisualDescendants<Button>(selector).ToArray();
                Assert.NotEmpty(buttons);
                Assert.All(buttons, button =>
                {
                    Assert.False(button.IsEnabled);
                    button.ApplyTemplate();
                    Border body = Assert.IsType<Border>(button.Template.FindName("Body", button));
                    SolidColorBrush background = Assert.IsType<SolidColorBrush>(body.Background);
                    Assert.Equal(Color.FromRgb(31, 26, 21), background.Color);
                    Assert.True(background.Color.R < 80 && background.Color.G < 80 && background.Color.B < 80);
                });
            }
            finally
            {
                window.Close();
            }
        });
    }

    private static CatalogPickerItem Vehicle(string id, string type, int typeOrder, string family, int familyOrder, int level) => new(
        id,
        id,
        id,
        null,
        new[] { type, family, id },
        new JObject
        {
            ["typeKey"] = type,
            ["typeOrder"] = typeOrder,
            ["familyKey"] = family,
            ["familyOrder"] = familyOrder,
            ["groupKey"] = family,
            ["groupName"] = family,
            ["groupOrder"] = familyOrder,
            ["itemOrder"] = level,
            ["level"] = level
        },
        id);

    private static IEnumerable<CatalogPickerItem> CurrentCheatRoster()
    {
        (string Type, string[] Families)[] roster =
        {
            ("Shell", new[] { "ShadowRift", "SoulChaser", "Projectile", "Burst" }),
            ("Link", new[] { "IceRipple", "ForkLightning", "ElectricGun", "Station" }),
            ("Penetrate", new[] { "SplitArrow", "ReturnToOrigin", "WindPiercer", "Crossbow" }),
            ("Missile", new[] { "Long", "SelfGuided", "InterceptBlock", "RandomCatapult" })
        };
        int familyOrder = 0;
        for (int typeOrder = 0; typeOrder < roster.Length; typeOrder++)
        {
            (string type, string[] families) = roster[typeOrder];
            foreach (string familyName in families)
            {
                string family = type + "_" + familyName;
                for (int level = 1; level <= 3; level++)
                {
                    yield return Vehicle(family + "_L" + level, type, typeOrder, family, familyOrder, level);
                }
                familyOrder++;
            }
        }
    }

    private static IReadOnlyList<VehicleTypeChoice> Types(VehicleQuickSelectorControl selector) =>
        ((System.Collections.IEnumerable)typeof(VehicleQuickSelectorControl)
            .GetField("_types", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(selector)!)
        .Cast<VehicleTypeChoice>().ToArray();

    private static IReadOnlyList<VehicleSeriesChoice> Series(VehicleQuickSelectorControl selector) =>
        ((System.Collections.IEnumerable)typeof(VehicleQuickSelectorControl)
            .GetField("_series", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(selector)!)
        .Cast<VehicleSeriesChoice>().ToArray();

    private static void Select(VehicleQuickSelectorControl selector, string id)
    {
        CatalogPickerItem item = selector.FindItem(id)!;
        VehicleTypeChoice type = Types(selector).Single(choice => choice.Key == item.TypeKey);
        Button typeButton = new() { DataContext = type };
        typeof(VehicleQuickSelectorControl).GetMethod("TypeButton_OnClick", BindingFlags.NonPublic | BindingFlags.Instance)!
            .Invoke(selector, new object[] { typeButton, new RoutedEventArgs() });
        VehicleLevelChoice choice = Series(selector).SelectMany(series => series.Levels).Single(level => level.Item.Id == id);
        Button button = new() { DataContext = choice };
        typeof(VehicleQuickSelectorControl).GetMethod("LevelButton_OnClick", BindingFlags.NonPublic | BindingFlags.Instance)!
            .Invoke(selector, new object[] { button, new RoutedEventArgs() });
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
        Assert.True(thread.Join(TimeSpan.FromSeconds(10)), "WPF 战车快速选择测试超时。");
        if (failure != null) ExceptionDispatchInfo.Capture(failure).Throw();
    }

    private static void PumpDispatcher() =>
        Dispatcher.CurrentDispatcher.Invoke(() => { }, DispatcherPriority.ApplicationIdle);

    private static IEnumerable<T> VisualDescendants<T>(DependencyObject root)
        where T : DependencyObject
    {
        for (int index = 0; index < VisualTreeHelper.GetChildrenCount(root); index++)
        {
            DependencyObject child = VisualTreeHelper.GetChild(root, index);
            if (child is T typed)
            {
                yield return typed;
            }

            foreach (T descendant in VisualDescendants<T>(child))
            {
                yield return descendant;
            }
        }
    }
}
