using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Shell;
using Loopstructor.AutoPlayer.Manager.Models;

namespace Loopstructor.AutoPlayer.Manager.Services;

internal static class UiScaleService
{
    private sealed class WindowMetrics
    {
        public required double Width { get; init; }
        public required double Height { get; init; }
        public required double MinWidth { get; init; }
        public required double MinHeight { get; init; }
        public required double CaptionHeight { get; init; }
        public required Thickness ResizeBorder { get; init; }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeRect { public int Left, Top, Right, Bottom; }

    [StructLayout(LayoutKind.Sequential)]
    private struct MonitorInfo
    {
        public int Size;
        public NativeRect Monitor;
        public NativeRect Work;
        public uint Flags;
    }

    private static readonly ConditionalWeakTable<Window, WindowMetrics> Metrics = new();
    private static readonly List<WeakReference<Window>> Windows = new();
    private static double _scale = 1d;

    public static double CurrentScale => _scale;

    public static void Register(Window window, ManagerSettings settings)
    {
        ArgumentNullException.ThrowIfNull(window);
        if (!Metrics.TryGetValue(window, out _))
        {
            WindowChrome? chrome = WindowChrome.GetWindowChrome(window);
            Metrics.Add(window, new WindowMetrics
            {
                Width = window.Width,
                Height = window.Height,
                MinWidth = window.MinWidth,
                MinHeight = window.MinHeight,
                CaptionHeight = chrome?.CaptionHeight ?? 0d,
                ResizeBorder = chrome?.ResizeBorderThickness ?? default
            });
            Windows.Add(new WeakReference<Window>(window));
        }

        ApplyAll(settings);
    }

    public static void ApplyAll(ManagerSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        settings.CustomUiScalePercent = Math.Clamp(settings.CustomUiScalePercent, 75, 200);
        _scale = settings.UiScaleMode == UiScaleMode.Custom
            ? settings.CustomUiScalePercent / 100d
            : 1d;
        if (Application.Current != null)
        {
            Application.Current.Resources["UiScaleTransform"] = new ScaleTransform(_scale, _scale);
        }

        for (int index = Windows.Count - 1; index >= 0; index--)
        {
            if (!Windows[index].TryGetTarget(out Window? window) || !Metrics.TryGetValue(window, out WindowMetrics? metrics))
            {
                Windows.RemoveAt(index);
                continue;
            }

            Apply(window, metrics);
        }
    }

    private static void Apply(Window window, WindowMetrics metrics)
    {
        Rect work = CurrentWorkArea(window);
        if (window.Content is FrameworkElement content)
        {
            content.LayoutTransform = new ScaleTransform(_scale, _scale);
        }

        window.MinWidth = Math.Min(metrics.MinWidth * _scale, work.Width);
        window.MinHeight = Math.Min(metrics.MinHeight * _scale, work.Height);
        if (window.WindowState == WindowState.Normal)
        {
            window.Width = metrics.Width * _scale;
            window.Height = metrics.Height * _scale;
        }

        WindowChrome? chrome = WindowChrome.GetWindowChrome(window);
        if (chrome != null)
        {
            chrome.CaptionHeight = metrics.CaptionHeight * _scale;
            chrome.ResizeBorderThickness = Scale(metrics.ResizeBorder, _scale);
        }

        window.Dispatcher.BeginInvoke(() => ClampToWorkArea(window));
    }

    private static Thickness Scale(Thickness value, double scale) => new(
        value.Left * scale,
        value.Top * scale,
        value.Right * scale,
        value.Bottom * scale);

    private static void ClampToWorkArea(Window window)
    {
        if (window.WindowState != WindowState.Normal) return;
        Rect work = CurrentWorkArea(window);
        window.Width = Math.Min(window.Width, work.Width);
        window.Height = Math.Min(window.Height, work.Height);
        window.Left = Math.Max(work.Left, Math.Min(window.Left, work.Right - window.Width));
        window.Top = Math.Max(work.Top, Math.Min(window.Top, work.Bottom - window.Height));
    }

    private static Rect CurrentWorkArea(Window window)
    {
        IntPtr handle = new WindowInteropHelper(window).Handle;
        if (handle == IntPtr.Zero) return SystemParameters.WorkArea;
        IntPtr monitor = MonitorFromWindow(handle, 2);
        MonitorInfo info = new() { Size = Marshal.SizeOf<MonitorInfo>() };
        if (monitor == IntPtr.Zero || !GetMonitorInfo(monitor, ref info)) return SystemParameters.WorkArea;
        Matrix fromDevice = PresentationSource.FromVisual(window)?.CompositionTarget?.TransformFromDevice ?? Matrix.Identity;
        Point topLeft = fromDevice.Transform(new Point(info.Work.Left, info.Work.Top));
        Point bottomRight = fromDevice.Transform(new Point(info.Work.Right, info.Work.Bottom));
        return new Rect(topLeft, bottomRight);
    }

    [DllImport("user32.dll")]
    private static extern IntPtr MonitorFromWindow(IntPtr window, uint flags);

    [DllImport("user32.dll", CharSet = CharSet.Auto)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetMonitorInfo(IntPtr monitor, ref MonitorInfo info);
}
