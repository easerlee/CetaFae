using System.IO;
using System.Windows;

namespace CetaFae.Probe;

/// <summary>Physical capture rectangles cross into display DIPs at one explicit boundary.</summary>
internal static class GuideCoordinates
{
    internal static Rect ToDips(Rect pixels, double scale)
    {
        if (!GuideScope.Valid(pixels) || !double.IsFinite(scale) || scale <= 0)
            throw new InvalidDataException("target_changed");
        return new Rect(pixels.X / scale, pixels.Y / scale, pixels.Width / scale, pixels.Height / scale);
    }

    internal static Rect WorkspaceAt(Point physicalPoint, double scale)
    {
        var work = Native.WorkAreaAt(physicalPoint);
        // Keep the existing primary-display path only when monitor metadata is unavailable.
        return work.IsEmpty ? SystemParameters.WorkArea : ToDips(work, scale);
    }
}
