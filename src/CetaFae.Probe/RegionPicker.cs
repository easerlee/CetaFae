using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Shapes;

namespace CetaFae.Probe;

/// <summary>A target-frame-only input overlay. It never captures screen pixels.</summary>
internal sealed class RegionPicker : Window
{
    readonly Rect _frame;
    readonly double _scale;
    readonly Canvas _canvas = new() { Background = new SolidColorBrush(Color.FromArgb(36, 20, 60, 120)) };
    readonly Rectangle _outline = new() { Stroke = Brushes.DodgerBlue, StrokeThickness = 2, IsHitTestVisible = false };
    Point? _start;
    bool _finished;
    public event Action<Rect?>? Selected;
    public IntPtr Handle => new WindowInteropHelper(this).EnsureHandle();

    public RegionPicker(Rect frame, double scale)
    {
        if (!GuideScope.Valid(frame) || !double.IsFinite(scale) || scale <= 0) throw new InvalidDataException("scope_unavailable");
        _frame = frame; _scale = scale;
        WindowStyle = WindowStyle.None; ResizeMode = ResizeMode.NoResize;
        AllowsTransparency = true; Background = Brushes.Transparent;
        ShowInTaskbar = false; Topmost = true;
        Left = frame.Left / scale; Top = frame.Top / scale;
        Width = frame.Width / scale; Height = frame.Height / scale;
        Content = _canvas;
        _canvas.Children.Add(_outline);
        _canvas.Children.Add(new TextBlock { Text = "框选将发送的识别区域；Esc 取消", Foreground = Brushes.White,
            Background = Brushes.DarkSlateBlue, Padding = new Thickness(8), IsHitTestVisible = false });
        MouseLeftButtonDown += (_, e) => { _start = e.GetPosition(_canvas); CaptureMouse(); e.Handled = true; };
        MouseMove += (_, e) =>
        {
            if (_start is not { } start) return;
            var rect = new Rect(start, e.GetPosition(_canvas));
            Canvas.SetLeft(_outline, rect.Left); Canvas.SetTop(_outline, rect.Top);
            _outline.Width = rect.Width; _outline.Height = rect.Height;
        };
        MouseLeftButtonUp += (_, e) =>
        {
            if (_start is not { } start) return;
            try { Finish(Selection(_frame, start, e.GetPosition(_canvas), _scale)); }
            catch (InvalidDataException) { _start = null; ReleaseMouseCapture(); }
            e.Handled = true;
        };
        PreviewKeyDown += (_, e) => { if (e.Key == Key.Escape) { Finish(null); e.Handled = true; } };
        Deactivated += (_, _) => { if (IsVisible) Finish(null); };
        Closed += (_, _) => Finish(null);
    }

    internal static Rect Selection(Rect frame, Point start, Point end, double scale)
    {
        if (!double.IsFinite(scale) || scale <= 0 ||
            new[] { start.X, start.Y, end.X, end.Y }.Any(v => !double.IsFinite(v)))
            throw new InvalidDataException("scope_unavailable");
        var a = new Point(frame.X + Math.Clamp(start.X * scale, 0, frame.Width), frame.Y + Math.Clamp(start.Y * scale, 0, frame.Height));
        var b = new Point(frame.X + Math.Clamp(end.X * scale, 0, frame.Width), frame.Y + Math.Clamp(end.Y * scale, 0, frame.Height));
        return GuideScope.FromUserSelection(frame, new Rect(a, b)).Region;
    }

    void Finish(Rect? region)
    {
        if (_finished) return;
        _finished = true;
        ReleaseMouseCapture();
        Selected?.Invoke(region);
    }

    public void Abort() { _finished = true; Close(); }
}
