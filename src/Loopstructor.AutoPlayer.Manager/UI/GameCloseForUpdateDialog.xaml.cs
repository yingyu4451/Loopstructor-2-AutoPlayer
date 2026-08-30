using System.Windows;
using System.Windows.Input;

namespace Loopstructor.AutoPlayer.Manager.UI;

internal sealed partial class GameCloseForUpdateDialog : Window
{
    public GameCloseForUpdateDialog(IReadOnlyCollection<int> processIds)
    {
        ArgumentNullException.ThrowIfNull(processIds);
        InitializeComponent();
        int[] ids = processIds.Distinct().OrderBy(id => id).ToArray();
        ProcessText.Text = ids.Length == 1
            ? $"Skyspine · PID {ids[0]}"
            : $"Skyspine · {ids.Length} 个进程 · PID {string.Join("、", ids)}";
    }

    private void CloseAndUpdateButtonOnClick(object sender, RoutedEventArgs eventArgs)
    {
        DialogResult = true;
    }

    private void CancelButtonOnClick(object sender, RoutedEventArgs eventArgs)
    {
        DialogResult = false;
    }

    private void DialogOnPreviewKeyDown(object sender, KeyEventArgs eventArgs)
    {
        if (eventArgs.Key != Key.Escape) return;
        DialogResult = false;
        eventArgs.Handled = true;
    }
}
