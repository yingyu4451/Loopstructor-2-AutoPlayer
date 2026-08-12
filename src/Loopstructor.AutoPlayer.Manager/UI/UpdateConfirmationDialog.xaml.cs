using System.Windows;
using System.Windows.Input;

namespace Loopstructor.AutoPlayer.Manager.UI;

internal sealed partial class UpdateConfirmationDialog : Window
{
    public UpdateConfirmationDialog(string currentVersion, string latestVersion)
    {
        InitializeComponent();
        CurrentVersionText.Text = "v" + Normalize(currentVersion);
        LatestVersionText.Text = "v" + Normalize(latestVersion);
    }

    private void ApplyButtonOnClick(object sender, RoutedEventArgs eventArgs)
    {
        DialogResult = true;
    }

    private void LaterButtonOnClick(object sender, RoutedEventArgs eventArgs)
    {
        DialogResult = false;
    }

    private void DialogOnPreviewKeyDown(object sender, KeyEventArgs eventArgs)
    {
        if (eventArgs.Key != Key.Escape) return;
        DialogResult = false;
        eventArgs.Handled = true;
    }

    private static string Normalize(string? value) =>
        string.IsNullOrWhiteSpace(value) ? "-" : value.Trim().TrimStart('v', 'V');
}
