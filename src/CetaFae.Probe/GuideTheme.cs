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
    internal const double CardOutline = 2;
    internal const double CardHeaderHeight = GuideFrameVisual.WaterDepth;
    internal static readonly Brush Accent = Frozen("#087FA6");
    internal static readonly Brush Outline = Frozen("#279FC4");
    internal static readonly Brush WhaleBlue = Frozen("#258FEC");
    internal static readonly Brush CardHeader = WaterGradient(CardHeaderHeight);
    internal static readonly Brush ReaderBackground = OpaqueReaderBackground();
    internal static readonly Brush Ink = Frozen("#143747");
    internal static readonly Brush Muted = Frozen("#536F7C");
    internal static readonly Brush Surface = Frozen("#F8FCFF");
    internal static readonly Brush ChatBubble = Frozen("#E4F2FF");
    internal static readonly Thickness Padding = new(16);
    internal static readonly CornerRadius Radius = new(12);

    internal static Brush WaterGradient(double depth)
    {
        var blue = ((SolidColorBrush)WhaleBlue).Color;
        var solid = GuideFrameVisual.SolidDepth / GuideFrameVisual.WaterDepth;
        var brush = new LinearGradientBrush
        {
            MappingMode = BrushMappingMode.Absolute,
            StartPoint = new Point(0, 0), EndPoint = new Point(0, depth),
            GradientStops = { new(blue, 0), new(blue, solid),
                new(Color.FromArgb(90, blue.R, blue.G, blue.B), solid + (1 - solid) * .28),
                new(Color.FromArgb(15, blue.R, blue.G, blue.B), solid + (1 - solid) * .7),
                new(Color.FromArgb(0, blue.R, blue.G, blue.B), 1) }
        };
        brush.Freeze(); return brush;
    }

    static Brush OpaqueReaderBackground()
    {
        // Paint through the outline so its antialiased edge cannot pick up white at the join.
        var brush = ((LinearGradientBrush)CardHeader).Clone();
        brush.StartPoint += new Vector(0, CardOutline);
        brush.EndPoint += new Vector(0, CardOutline);
        foreach (var stop in brush.GradientStops)
        {
            var color = stop.Color;
            byte OverWhite(byte channel) => (byte)(255 - (255 - channel) * color.A / 255);
            stop.Color = Color.FromRgb(OverWhite(color.R), OverWhite(color.G), OverWhite(color.B));
        }
        brush.Freeze();
        return brush;
    }

    static Brush Frozen(string value)
    {
        var brush = new SolidColorBrush((Color)ColorConverter.ConvertFromString(value));
        brush.Freeze();
        return brush;
    }
}
