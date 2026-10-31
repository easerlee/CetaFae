using System.Windows;
using System.Windows.Media;

namespace CetaFae.Probe;

/// <summary>Native UI defaults; independent of the separately produced whale artwork.</summary>
internal static class GuideTheme
{
    internal const double CardWidth = 360;
    internal const double CardMaxHeight = 460;
    internal static readonly Brush Accent = Frozen("#087FA6");
    internal static readonly Brush Outline = Frozen("#279FC4");
    internal static readonly Brush WhaleBlue = Frozen("#258FEC");
    internal static readonly Brush Ink = Frozen("#143747");
    internal static readonly Brush Muted = Frozen("#536F7C");
    internal static readonly Brush Surface = Frozen("#F8FCFF");
    internal static readonly Thickness Padding = new(16);
    internal static readonly CornerRadius Radius = new(12);

    static Brush Frozen(string value)
    {
        var brush = new SolidColorBrush((Color)ColorConverter.ConvertFromString(value));
        brush.Freeze();
        return brush;
    }
}
