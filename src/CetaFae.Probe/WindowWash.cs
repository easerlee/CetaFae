using System.Windows;
using System.Windows.Media;

namespace CetaFae.Probe;

/// <summary>A transparent blue sweep precedes the perimeter without a full-window layered surface.</summary>
internal static class WindowWash
{
    internal const double Band = 192;
    internal const double Duration = 1;
    internal const double SweepEnd = .84;
    internal const double TotalDuration = WhaleArt.DropDuration + Duration;
    internal static double Progress(double seconds) => seconds <= WhaleArt.DropDuration ? -1 :
        Math.Clamp((seconds - WhaleArt.DropDuration) / Duration, 0, 1);
    internal static double Front(Size size, double progress) => (size.Height + Band + 16) * progress / SweepEnd - 8;
    internal static Rect Region(Size size, double progress)
    {
        if (progress <= 0 || progress >= SweepEnd) return Rect.Empty;
        var front = Front(size, progress);
        var top = Math.Clamp(Math.Floor(front - Band), 0, size.Height);
        var bottom = Math.Clamp(Math.Ceiling(front + 8), 0, size.Height);
        return bottom > top ? new Rect(0, top, size.Width, bottom - top) : Rect.Empty;
    }

    static readonly Brush Water = CreateWater();
    static Brush CreateWater()
    {
        var blue = ((SolidColorBrush)GuideTheme.WhaleBlue).Color;
        var brush = new LinearGradientBrush
        {
            StartPoint = new(0, 0), EndPoint = new(0, 1),
            GradientStops = { new(Color.FromArgb(0, blue.R, blue.G, blue.B), 0),
                new(Color.FromArgb(64, blue.R, blue.G, blue.B), .1),
                new(Color.FromArgb(64, blue.R, blue.G, blue.B), .9),
                new(Color.FromArgb(0, blue.R, blue.G, blue.B), 1) }
        };
        brush.Freeze(); return brush;
    }

    internal static void Draw(DrawingContext draw, Size size, double progress)
    {
        if (Region(size, progress).IsEmpty) return;
        var front = Front(size, progress);
        draw.DrawRectangle(Water, null, new Rect(0, front - Band, size.Width, Band));
    }
}
