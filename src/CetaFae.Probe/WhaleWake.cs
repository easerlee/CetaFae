using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;

namespace CetaFae.Probe;

/// <summary>Short deposited ripples in world coordinates; no desktop-sized animation surface.</summary>
internal sealed class WhaleWake : Window
{
    readonly List<(Point Point, double Born)> _ripples = new();
    readonly RippleView _view = new();
    double _lastDeposit = -1;
    internal WhaleWake()
    {
        WindowStyle = WindowStyle.None; AllowsTransparency = true; Background = Brushes.Transparent;
        Topmost = true; ShowInTaskbar = false; ShowActivated = false; ResizeMode = ResizeMode.NoResize;
        IsHitTestVisible = false; Width = Height = 2; Content = _view;
        SourceInitialized += (_, _) =>
        {
            var hwnd = new WindowInteropHelper(this).Handle;
            Native.SetWindowLong(hwnd, Native.GWL_EXSTYLE, Native.GetWindowLong(hwnd, Native.GWL_EXSTYLE) |
                Native.WS_EX_NOACTIVATE | Native.WS_EX_TOOLWINDOW | Native.WS_EX_TRANSPARENT);
        };
    }
    internal void Deposit(Point point, double seconds)
    {
        if (seconds - _lastDeposit < .14) return;
        if (_ripples.Count > 0 && (point - _ripples[^1].Point).Length < 8) return;
        _lastDeposit = seconds;
        _ripples.Add((point, seconds));
    }
    internal void Advance(double seconds)
    {
        _ripples.RemoveAll(r => seconds - r.Born > .52);
        while (_ripples.Count > 8) _ripples.RemoveAt(0);
        while (_ripples.Count > 1 && (Bounds().Width > 320 || Bounds().Height > 180)) _ripples.RemoveAt(0);
        if (_ripples.Count == 0) { Reset(); return; }
        var bounds = Bounds(); bounds.Intersect(SystemParameters.WorkArea);
        if (bounds.IsEmpty) { Reset(); return; }
        Left = bounds.Left; Top = bounds.Top; Width = Math.Max(2, bounds.Width); Height = Math.Max(2, bounds.Height);
        _view.Ripples = _ripples.Select(r => (new Point(r.Point.X - Left, r.Point.Y - Top), seconds - r.Born)).ToArray();
        _view.InvalidateVisual();
        if (!IsVisible) Show();
    }
    Rect Bounds()
    {
        var left = _ripples.Min(r => r.Point.X) - 20; var top = _ripples.Min(r => r.Point.Y) - 5;
        return new Rect(left, top, _ripples.Max(r => r.Point.X) - left + 20, _ripples.Max(r => r.Point.Y) - top + 8);
    }
    internal void Reset()
    {
        _ripples.Clear(); _view.Ripples = []; _view.InvalidateVisual();
        _lastDeposit = -1;
        if (IsVisible) Hide();
        Width = Height = 2;
    }
    sealed class RippleView : FrameworkElement
    {
        internal (Point Point, double Age)[] Ripples = [];
        protected override void OnRender(DrawingContext draw)
        {
            foreach (var (p, age) in Ripples)
            {
                var fade = Math.Clamp(1 - age / .52, 0, 1); var width = 8 + age * 16;
                var line = new StreamGeometry();
                using (var path = line.Open())
                {
                    path.BeginFigure(new Point(p.X - width, p.Y), false, false);
                    path.BezierTo(new Point(p.X - width / 2, p.Y + 2), new Point(p.X + width / 2, p.Y + 2),
                        new Point(p.X + width, p.Y - 1), true, false);
                }
                draw.PushOpacity(fade * .65);
                draw.DrawGeometry(null, new Pen(GuideTheme.Outline, Math.Max(.5, fade)), line);
                draw.Pop();
            }
        }
    }
}
