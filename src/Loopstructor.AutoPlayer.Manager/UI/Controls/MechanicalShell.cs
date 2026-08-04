using System.Windows;
using System.Windows.Controls;

namespace Loopstructor.AutoPlayer.Manager.UI.Controls;

internal sealed class MechanicalShell : ContentControl
{
    public static readonly DependencyProperty TitleProperty = DependencyProperty.Register(
        nameof(Title),
        typeof(string),
        typeof(MechanicalShell),
        new PropertyMetadata(string.Empty));

    public static readonly DependencyProperty SubtitleProperty = DependencyProperty.Register(
        nameof(Subtitle),
        typeof(string),
        typeof(MechanicalShell),
        new PropertyMetadata(string.Empty));

    public string Title
    {
        get => (string)GetValue(TitleProperty);
        set => SetValue(TitleProperty, value);
    }

    public string Subtitle
    {
        get => (string)GetValue(SubtitleProperty);
        set => SetValue(SubtitleProperty, value);
    }
}
