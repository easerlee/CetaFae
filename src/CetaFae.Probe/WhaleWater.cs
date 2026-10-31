using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace CetaFae.Probe;

/// <summary>Morphs the embedded resting water itself; no second puddle or external pixels.</summary>
internal static class WhaleWater
{
    static readonly Lazy<(Point[] Top, Point[] Bottom)> Source = new(ReadSource);

    static (Point[], Point[]) ReadSource()
    {
        using var source = WhaleArt.Resource("rest-water.png");
        var bitmap = new FormatConvertedBitmap(BitmapFrame.Create(source, BitmapCreateOptions.None, BitmapCacheOption.OnLoad), PixelFormats.Bgra32, null, 0);
        var size = bitmap.PixelWidth;
        var pixels = new byte[size * size * 4]; bitmap.CopyPixels(pixels, size * 4, 0);
        var top = new List<Point>(); var bottom = new List<Point>();
        for (var x = 0; x < size; x++)
        {
            var rows = Enumerable.Range(0, size).Where(y => pixels[(y * size + x) * 4 + 3] >= 128).ToArray();
            if (rows.Length == 0) continue;
            top.Add(new Point((x + .5) * 96 / size, rows[0] * 96d / size));
            bottom.Add(new Point((x + .5) * 96 / size, (rows[^1] + 1) * 96d / size));
        }
        if (top.Count < 2) throw new InvalidDataException("whale_water_outline_invalid");
        return (top.ToArray(), bottom.ToArray());
    }

    static Point At(double u, bool bottom, int facing, double width)
    {
        var points = bottom ? Source.Value.Bottom : Source.Value.Top;
        var index = Math.Clamp(facing < 0 ? 1 - u : u, 0, 1) * (points.Length - 1);
        var left = (int)index; var right = Math.Min(left + 1, points.Length - 1);
        var point = points[left] + (points[right] - points[left]) * (index - left);
        return new Point(width / 2 + facing * (point.X - WhaleArt.Cell / 2d) * WhaleVisual.CanvasSize / WhaleArt.Cell,
            point.Y * WhaleVisual.CanvasSize / WhaleArt.Cell);
    }

    internal static StreamGeometry Shape(double progress, double width, int facing = -1, bool above = false, bool topAttached = false, double spread = -1)
    {
        progress = Math.Clamp(progress, 0, 1);
        var height = WhalePrompt.BodyHeight;
        var target = PromptShell.Shape(width, height).GetFlattenedPathGeometry(.15, ToleranceType.Absolute).Figures[0];
        var rim = new List<Point> { target.StartPoint }; var previous = target.StartPoint;
        void Append(Point next)
        {
            var steps = Math.Max(1, (int)Math.Ceiling((next - previous).Length / 4));
            for (var i = 1; i <= steps; i++)
            {
                rim.Add(previous + (next - previous) * (i / (double)steps));
            }
            previous = next;
        }
        foreach (var segment in target.Segments)
        {
            if (segment is PolyLineSegment poly) foreach (var point in poly.Points)
                { if (previous.X >= width) break; Append(point); }
            else if (segment is LineSegment line) Append(line.Point);
            if (previous.X >= width) break;
        }
        var straight = Enumerable.Range(0, 49).Select(i => new Point(width * i / 48d, 0)).ToArray();
        Point Morph(Point point, bool bottom)
        {
            var source = At(point.X / width, bottom, facing, width);
            // Gain depth before spanning the width: this is rising water, not a horizontal wipe.
            return new Point(source.X + (point.X - source.X) * (spread >= 0 ? spread : progress * progress),
                source.Y + (point.Y - source.Y) * progress);
        }
        var upper = (topAttached ? straight : rim.Select(p => new Point(p.X, p.Y + WhalePrompt.DockFoot - height)))
            .Select(p => Morph(p, false)).ToArray();
        var lower = (topAttached ? rim.AsEnumerable().Reverse().Select(p => new Point(p.X, height - p.Y)) :
            straight.Reverse().Select(p => new Point(p.X, WhalePrompt.DockFoot))).Select(p => Morph(p, true));
        var shape = new StreamGeometry();
        using (var path = shape.Open())
        {
            if (above)
            {
                path.BeginFigure(new Point(0, 0), true, true); path.LineTo(new Point(width, 0), true, false);
                for (var i = upper.Length - 1; i >= 0; i--) path.LineTo(upper[i], true, false);
            }
            else
            {
                path.BeginFigure(upper[0], true, true);
                foreach (var point in upper.Skip(1)) path.LineTo(point, true, false);
                foreach (var point in lower) path.LineTo(point, true, false);
            }
        }
        shape.Freeze(); return shape;
    }
}
