using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;

namespace CetaFae.Probe;

public sealed class OverlayWindow : Window
{
    public OverlayWindow(bool control = false)
    {
        WindowStyle = WindowStyle.None;
        AllowsTransparency = true;
        Background = Brushes.Transparent;
        Topmost = true;
        ShowInTaskbar = false;
        ShowActivated = false;
        ResizeMode = ResizeMode.NoResize;
        IsHitTestVisible = false;
        WindowStartupLocation = WindowStartupLocation.Manual;
        Left = -2000;
        Top = -2000;
        Width = 1;
        Height = 1;

        Content = new Border
        {
            BorderBrush = control ? GuideTheme.Accent : GuideTheme.Outline,
            BorderThickness = new Thickness(control ? 3 : 2),
            CornerRadius = new CornerRadius(control ? 5 : 0),
            Background = Brushes.Transparent
        };

        SourceInitialized += (_, _) =>
        {
            var hwnd = new WindowInteropHelper(this).Handle;
            var ex = Native.GetWindowLong(hwnd, Native.GWL_EXSTYLE);
            Native.SetWindowLong(hwnd, Native.GWL_EXSTYLE,
                ex | Native.WS_EX_NOACTIVATE | Native.WS_EX_TOOLWINDOW | Native.WS_EX_TRANSPARENT);
        };
    }

    public void HideAndReset()
    {
        Hide();
        // Keep a tiny idle surface; native sizing can round one unit up to two.
        // Callers restore the target bounds before Show.
        Width = 2;
        Height = 2;
    }
}
