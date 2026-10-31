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
    public OverlayWindow(bool control = false)
    {
        _frame = new GuideFrameVisual(control);
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
            _frame.SetMotion(!control && IsVisible && SystemParameters.ClientAreaAnimation);
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
internal sealed class GuideFrameVisual(bool control = false) : FrameworkElement
{
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
    internal void SetProgress(double progress) { _progress = Math.Clamp(progress, 0, 1); InvalidateVisual(); }
    internal static StreamGeometry WaveContour(Size size, double depth, double phase = 0)
    {
        var inset = Math.Min(depth, Math.Min(size.Width, size.Height) / 4);
        var left = inset; var top = inset; var right = size.Width - inset; var bottom = size.Height - inset;
        var wave = new StreamGeometry();
        using (var path = wave.Open())
        {
            path.BeginFigure(new Point(left, top), true, true);
            void Side(Point start, Point end, Vector normal)
            {
                var length = (end - start).Length;
                var steps = Math.Max(1, (int)Math.Ceiling(length / 12));
                for (var i = 1; i <= steps; i++)
                {
                    var t = (double)i / steps;
                    var offset = Math.Min(7, inset / 3) * Math.Sin(t * Math.PI) * Math.Sin(t * length / 160 * Math.PI * 2 + phase);
                    path.LineTo(start + (end - start) * t + normal * offset, true, false);
                }
            }
            Side(new(left, top), new(right, top), new(0, 1));
            Side(new(right, top), new(right, bottom), new(-1, 0));
            Side(new(right, bottom), new(left, bottom), new(0, -1));
            Side(new(left, bottom), new(left, top), new(1, 0));
        }
        wave.Freeze(); return wave;
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
            // Soft nested fills fade to zero; no pen can turn the inner ripple into a contour line.
            var depth = Math.Min(56, Math.Min(ActualWidth, ActualHeight) / 4);
            for (var i = 1; i <= 24; i++)
            {
                var t = i / 24d;
                var inner = WaveContour(RenderSize, depth * t, Phase);
                var ring = new CombinedGeometry(GeometryCombineMode.Exclude, new RectangleGeometry(area), inner);
                draw.PushOpacity(.045 * (1 - t)); draw.DrawGeometry(GuideTheme.Outline, null, ring); draw.Pop();
            }
        }
        if (!control && _progress > 0 && _progress < 1)
        {
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
            draw.Pop();
        }
        draw.Pop();
    }
}
