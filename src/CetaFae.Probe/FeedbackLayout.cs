using System.Windows;

namespace CetaFae.Probe;

/// <summary>All coordinates are DIPs; the owner converts target pixels once at the boundary.</summary>
internal static class FeedbackLayout
{
    internal static Rect Attached(Rect entry, Rect work, Size size, bool topAttached = false)
    {
        var width = Math.Min(size.Width, work.Width);
        var height = Math.Min(size.Height, work.Height);
        if (topAttached)
        {
            if (entry.Right + GuideTheme.CardGap + width <= work.Right)
                return new Rect(entry.Right + GuideTheme.CardGap, Math.Clamp(entry.Top, work.Top, work.Bottom - height), width, height);
            if (entry.Left - GuideTheme.CardGap - width >= work.Left)
                return new Rect(entry.Left - GuideTheme.CardGap - width, Math.Clamp(entry.Top, work.Top, work.Bottom - height), width, height);
        }
        var top = entry.Bottom;
        if (top + height > work.Bottom) top = entry.Top - height - 1;
        return new Rect(Math.Clamp(entry.Left, work.Left, work.Right - width),
            Math.Clamp(top, work.Top, work.Bottom - height), width, height);
    }

    internal static double AvailableWidth(Rect entry, Rect work, bool topAttached)
    {
        if (!topAttached) return work.Width;
        var right = work.Right - entry.Right - GuideTheme.CardGap;
        var left = entry.Left - work.Left - GuideTheme.CardGap;
        return right >= GuideTheme.CardWidth ? right : left >= GuideTheme.CardWidth ? left : work.Width;
    }

    internal static Rect? Bubble(Rect control, Rect entry, Rect work, Size size)
    {
        var points = new[]
        {
            new Point(control.Right - size.Width, control.Top - size.Height - 10),
            new Point(control.Right - size.Width, control.Bottom + 10),
            new Point(control.Left - size.Width - 10, control.Top),
            new Point(control.Right + 10, control.Top)
        };
        var protectedControl = control; protectedControl.Inflate(6, 6);
        foreach (var point in points)
        {
            var bounds = new Rect(point, size);
            if (work.Contains(bounds) && !bounds.IntersectsWith(protectedControl) && !bounds.IntersectsWith(entry)) return bounds;
        }
        return null;
    }

    internal static Rect? Reader(Rect control, Rect entry, Rect work, Size size, bool topAttached = false)
    {
        var width = Math.Min(size.Width, work.Width); var height = Math.Min(size.Height, work.Height);
        var preferred = Attached(entry, work, size, topAttached);
        var points = new[] { preferred.TopLeft, new Point(entry.Left, entry.Top - height - 1),
            new Point(entry.Right + 1, entry.Bottom + 1), new Point(entry.Left - width - 1, entry.Bottom + 1) };
        var protectedControl = control; protectedControl.Inflate(6, 6);
        foreach (var point in points)
        {
            var bounds = new Rect(Math.Clamp(point.X, work.Left, work.Right - width),
                Math.Clamp(point.Y, work.Top, work.Bottom - height), width, height);
            if (!bounds.IntersectsWith(protectedControl) && Rect.Intersect(bounds, entry) is not { Width: > 0, Height: > 0 })
                return bounds;
        }
        return null;
    }
}
