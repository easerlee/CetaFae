using System.Windows;
using System.Windows.Media;

namespace CetaFae.Probe;

/// <summary>A transparent blue sweep precedes the perimeter without a full-window layered surface.</summary>
internal static class WindowWash
{
    internal const double Band = 192;
    internal const double Duration = 1.4;
    internal const double SweepEnd = 1.24 / Duration;
    internal const double TotalDuration = WhaleArt.DropDuration + Duration;
    internal static double Progress(double seconds) => seconds <= WhaleArt.DropDuration ? -1 :
        Math.Clamp((seconds - WhaleArt.DropDuration) / Duration, 0, 1);
    internal static double Front(Size size, double progress) => (size.Height + Band + 24) * progress / SweepEnd - 12;
    internal static double Wave(double u, double progress) =>
        8 * Math.Exp(-Math.Pow((u - .22 - .025 * Math.Sin(progress * Duration * 3)) / .18, 2)) -
        6 * Math.Exp(-Math.Pow((u - .62) / .24, 2)) + 4 * Math.Exp(-Math.Pow((u - .89) / .11, 2));
    internal static Rect Region(Size size, double progress)
    {
        if (progress <= 0 || progress >= SweepEnd) return Rect.Empty;
        var front = Front(size, progress);
        var top = Math.Clamp(Math.Floor(front - Band - 12), 0, size.Height);
        var bottom = Math.Clamp(Math.Ceiling(front + 12), 0, size.Height);
        return bottom > top ? new Rect(0, top, size.Width, bottom - top) : Rect.Empty;
    }

    static readonly Brush Water = CreateWater();
    static Brush CreateWater()
    {
        var blue = ((SolidColorBrush)GuideTheme.WhaleBlue).Color;
        var brush = new LinearGradientBrush
        {
            StartPoint = new(0, 0), EndPoint = new(0, 1),
            GradientStops = { new(Color.FromArgb(0, 142, 205, 250), 0),
                new(Color.FromArgb(12, 110, 185, 246), .35),
                new(Color.FromArgb(35, 65, 160, 240), .7),
                new(Color.FromArgb(60, blue.R, blue.G, blue.B), .94),
                new(Color.FromArgb(0, blue.R, blue.G, blue.B), 1) }
        };
        brush.Freeze(); return brush;
    }

    internal static void Draw(DrawingContext draw, Size size, double progress)
    {
        if (Region(size, progress).IsEmpty) return;
        var front = Front(size, progress);
        var shape = new StreamGeometry();
        using (var path = shape.Open())
        {
            path.BeginFigure(new Point(0, front - Band - 12), true, true);
            path.LineTo(new Point(size.Width, front - Band - 12), true, false);
            for (var i = 64; i >= 0; i--)
            {
                var u = i / 64d;
                path.LineTo(new Point(u * size.Width, front + Wave(u, progress)), true, false);
            }
        }
        shape.Freeze(); draw.DrawGeometry(Water, null, shape);
    }
}
