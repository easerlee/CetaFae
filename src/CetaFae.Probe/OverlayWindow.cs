using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;

namespace CetaFae.Probe;

public sealed class OverlayWindow : Window
{
    readonly WaterWash _wash = new();
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
            Background = Brushes.Transparent,
            Child = _wash
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
        SetWaterProgress(0);
        Hide();
        // Keep a tiny idle surface; native sizing can round one unit up to two.
        // Callers restore the target bounds before Show.
        Width = 2;
        Height = 2;
    }

    internal void SetWaterProgress(double progress) => _wash.SetProgress(progress);

    sealed class WaterWash : FrameworkElement
    {
        double _progress;
        internal void SetProgress(double progress) { _progress = Math.Clamp(progress, 0, 1); InvalidateVisual(); }
        protected override void OnRender(DrawingContext draw)
        {
            if (_progress <= 0 || _progress >= 1) return;
            draw.PushClip(new RectangleGeometry(new Rect(RenderSize)));
            // Target-local vector wash adapts to arbitrary windows; it never captures pixels.
            draw.PushOpacity(.13 * Math.Sin(_progress * Math.PI));
            draw.DrawRectangle(GuideTheme.Outline, null, new Rect(0, 0, ActualWidth, ActualHeight * _progress));
            draw.Pop();
            draw.PushOpacity(.35 * Math.Sin(_progress * Math.PI));
            var wave = new StreamGeometry();
            using (var path = wave.Open())
            {
                var y = ActualHeight * _progress;
                path.BeginFigure(new Point(0, y), false, false);
                path.BezierTo(new Point(ActualWidth * .33, y - 7), new Point(ActualWidth * .67, y + 7),
                    new Point(ActualWidth, y), true, false);
            }
            draw.DrawGeometry(null, new Pen(GuideTheme.Outline, 2), wave);
            draw.Pop(); draw.Pop();
        }
    }
}
