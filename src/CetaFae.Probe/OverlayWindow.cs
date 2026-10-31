using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;

namespace CetaFae.Probe;

public sealed class OverlayWindow : Window
{
    readonly GuideFrameVisual _frame;
    readonly Border _border;
    public OverlayWindow(bool control = false, bool wash = false)
    {
        _frame = new GuideFrameVisual(control, wash);
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

        Content = _border = new Border
        {
            BorderBrush = control ? GuideTheme.Accent : Brushes.Transparent,
            BorderThickness = new Thickness(control ? 4 : 0),
            CornerRadius = new CornerRadius(control ? 8 : 0),
            Background = Brushes.Transparent,
            Child = _frame
        };
        IsVisibleChanged += (_, _) =>
        {
            _border.BeginAnimation(OpacityProperty, null);
            _frame.SetMotion(!control && !wash && IsVisible && SystemParameters.ClientAreaAnimation);
            if (control && IsVisible && SystemParameters.ClientAreaAnimation)
                _border.BeginAnimation(OpacityProperty, CreateBlink());
        };
        Closed += (_, _) => { _border.BeginAnimation(OpacityProperty, null); _frame.SetMotion(false); };

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
        _border.BeginAnimation(OpacityProperty, null);
        _frame.SetMotion(false);
        SetWaterProgress(0);
        Hide();
        // Keep a tiny idle surface; native sizing can round one unit up to two.
        // Callers restore the target bounds before Show.
        Width = 2;
        Height = 2;
    }

    internal void SetWaterProgress(double progress) => _frame.SetProgress(progress);
    internal void SetViewport(Size target, Rect region) => _frame.SetViewport(target, region);

    internal static DoubleAnimationUsingKeyFrames CreateBlink() => new()
    {
        Duration = TimeSpan.FromSeconds(1.2), RepeatBehavior = RepeatBehavior.Forever,
        KeyFrames = { new DiscreteDoubleKeyFrame(1, KeyTime.FromTimeSpan(TimeSpan.Zero)),
            new DiscreteDoubleKeyFrame(0, KeyTime.FromTimeSpan(TimeSpan.FromSeconds(.6))),
            new DiscreteDoubleKeyFrame(1, KeyTime.FromTimeSpan(TimeSpan.FromSeconds(1.2))) }
    };

    internal static Rect ControlBounds(Rect control, Rect target, Rect work, double scale)
    {
        control.Inflate(8 * scale, 8 * scale);
        control.Intersect(target); control.Intersect(work);
        return control;
    }

    internal bool PlaceControl(Rect control, Rect target, double scale)
    {
        var work = SystemParameters.WorkArea;
        var bounds = ControlBounds(control, target,
            new Rect(work.X * scale, work.Y * scale, work.Width * scale, work.Height * scale), scale);
        if (bounds.IsEmpty || bounds.Width <= 0 || bounds.Height <= 0) return false;
        Left = bounds.Left / scale; Top = bounds.Top / scale;
        Width = bounds.Width / scale; Height = bounds.Height / scale;
        return true;
    }
}

/// <summary>Target-local vector feedback; the center remains transparent and no pixels are collected.</summary>
internal sealed class GuideFrameVisual(bool control = false, bool wash = false) : FrameworkElement
{
    internal const double WaterDepth = 96;
    internal const double SolidDepth = 16;
    internal static double Depth(Size size) => Math.Min(WaterDepth, Math.Min(size.Width, size.Height) / 4);
    Size? _targetSize;
    Rect _region;
    internal void SetViewport(Size target, Rect region)
    {
        if (_targetSize == target && _region == region) return;
        _targetSize = target; _region = region; InvalidateVisual();
    }
    internal static readonly DependencyProperty PhaseProperty = DependencyProperty.Register(nameof(Phase), typeof(double),
        typeof(GuideFrameVisual), new FrameworkPropertyMetadata(0d, FrameworkPropertyMetadataOptions.AffectsRender));
    internal double Phase { get => (double)GetValue(PhaseProperty); set => SetValue(PhaseProperty, value); }
    internal void SetMotion(bool enabled)
    {
        BeginAnimation(PhaseProperty, null); Phase = 0;
        if (!enabled || control) return;
        var wave = new DoubleAnimation(0, Math.PI * 2, TimeSpan.FromSeconds(4)) { RepeatBehavior = RepeatBehavior.Forever };
        Timeline.SetDesiredFrameRate(wave, 15);
        BeginAnimation(PhaseProperty, wave);
    }
    double _progress;
    internal void SetProgress(double progress)
    {
        var value = Math.Clamp(progress, -1, 1);
        if (_progress == value) return;
        _progress = value; InvalidateVisual();
    }

    protected override void OnRender(DrawingContext draw)
    {
        if (ActualWidth <= 0 || ActualHeight <= 0) return;
        var area = new Rect(RenderSize);
        draw.PushClip(new RectangleGeometry(area));
        if (control)
        {
            draw.PushOpacity(.08); draw.DrawRoundedRectangle(GuideTheme.Accent, null, area, 4, 4); draw.Pop();
            draw.DrawRoundedRectangle(null, new Pen(Brushes.White, 2),
                new Rect(1, 1, Math.Max(0, ActualWidth - 2), Math.Max(0, ActualHeight - 2)), 4, 4);
        }
        else
        {
            if (_targetSize is not null) draw.PushTransform(new TranslateTransform(-_region.X, -_region.Y));
            if (wash) WindowWash.Draw(draw, _targetSize ?? RenderSize, _progress);
            else DrawWater(draw, _targetSize ?? RenderSize, Phase, _progress);
            if (_targetSize is not null) draw.Pop();
        }
        draw.Pop();
    }

    static readonly Brush[] WaterBrushes = Enumerable.Range(0, 4).Select(side =>
    {
        var blue = ((SolidColorBrush)GuideTheme.WhaleBlue).Color;
        var brush = new LinearGradientBrush
        {
            StartPoint = side switch { 1 => new(1, 0), 2 => new(0, 1), _ => new(0, 0) },
            EndPoint = side switch { 0 => new(0, 1), 1 => new(0, 0), 2 => new(0, 0), _ => new(1, 0) },
            GradientStops = { new(blue, 0), new(blue, SolidDepth / WaterDepth),
                new(Color.FromArgb(90, blue.R, blue.G, blue.B), .4), new(Color.FromArgb(15, blue.R, blue.G, blue.B), .75),
                new(Color.FromArgb(0, blue.R, blue.G, blue.B), 1) }
        };
        brush.Freeze(); return (Brush)brush;
    }).ToArray();

    internal static void DrawWater(DrawingContext draw, Size size, double phase, double progress)
    {
        var depth = Depth(size);
        if (depth <= 0 || progress < 0) return;
        var revealing = progress > 0 && progress < WindowWash.SweepEnd;
        if (revealing) draw.PushOpacity(progress / WindowWash.SweepEnd);
        for (var side = 0; side < 4; side++)
        {
            var origin = side switch { 1 => new Point(size.Width, 0), 2 => new Point(size.Width, size.Height),
                3 => new Point(0, size.Height), _ => new Point() };
            var tangent = side switch { 0 => new Vector(1, 0), 1 => new Vector(0, 1),
                2 => new Vector(-1, 0), _ => new Vector(0, -1) };
            var normal = new Vector(-tangent.Y, tangent.X);
            var length = side % 2 == 0 ? size.Width : size.Height;
            var mesh = new StreamGeometry();
            using (var path = mesh.Open())
            {
                path.BeginFigure(origin, true, true);
                path.LineTo(origin + tangent * length, true, false);
                var steps = Math.Max(1, (int)Math.Ceiling(length / 32));
                for (var i = steps; i >= 0; i--)
                {
                    // Overlap the vector fills at corners; diagonal joins leave antialias cracks.
                    var t = (double)i / steps; var x = length * t;
                    var ripple = Math.Min(7, depth / 3) * Math.Pow(Math.Sin(t * Math.PI), 2) *
                        (.5 + .5 * Math.Sin(x / 160 * Math.PI * 2 + phase));
                    path.LineTo(origin + tangent * x + normal * (depth - ripple), true, false);
                }
            }
            mesh.Freeze(); draw.DrawGeometry(WaterBrushes[side], null, mesh);
        }
        if (revealing) draw.Pop();
    }
}
