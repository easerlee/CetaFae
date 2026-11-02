using System.Windows;
using System.Windows.Media;

namespace CetaFae.Probe;

/// <summary>Native UI defaults; independent of the separately produced whale artwork.</summary>
internal static class GuideTheme
{
    internal const double CardWidth = 360;
    internal const double CardMaxHeight = 520;
    internal const double CardMaxWidth = 720;
    internal const double CardGap = 12;
    internal static readonly Brush Accent = Frozen("#087FA6");
    internal static readonly Brush Outline = Frozen("#279FC4");
    internal static readonly Brush WhaleBlue = Frozen("#258FEC");
    internal static readonly Brush CardHeader = WaterGradient(0, GuideFrameVisual.WaterDepth);
    internal static readonly Brush Ink = Frozen("#143747");
    internal static readonly Brush Muted = Frozen("#536F7C");
    internal static readonly Brush Surface = Frozen("#F8FCFF");
    internal static readonly Thickness Padding = new(16);
    internal static readonly CornerRadius Radius = new(12);

    internal static Brush WaterGradient(int side, double? depth = null)
    {
        var blue = ((SolidColorBrush)WhaleBlue).Color;
        var brush = new LinearGradientBrush
        {
            StartPoint = side switch { 1 => new(1, 0), 2 => new(0, 1), _ => new(0, 0) },
            EndPoint = side switch { 0 => new(0, 1), 1 => new(0, 0), 2 => new(0, 0), _ => new(1, 0) },
            GradientStops = { new(blue, 0), new(blue, GuideFrameVisual.SolidDepth / GuideFrameVisual.WaterDepth),
                new(Color.FromArgb(90, blue.R, blue.G, blue.B), .4), new(Color.FromArgb(15, blue.R, blue.G, blue.B), .75),
                new(Color.FromArgb(0, blue.R, blue.G, blue.B), 1) }
        };
        if (depth is { } height)
        {
            // A short header samples the same physical fade as the perimeter, keeping white text readable.
            brush.MappingMode = BrushMappingMode.Absolute;
            brush.EndPoint = new Point(0, height);
        }
        brush.Freeze(); return brush;
    }

    static Brush Frozen(string value)
    {
        var brush = new SolidColorBrush((Color)ColorConverter.ConvertFromString(value));
        brush.Freeze();
        return brush;
    }
}
