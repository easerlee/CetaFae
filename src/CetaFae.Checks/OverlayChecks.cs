using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using CetaFae.Probe;

namespace CetaFae.Checks;

internal static class OverlayChecks
{
    internal static void Run(Action<bool, string> check)
    {
        var cornerSize = new Size(600, 400);
        var cornerPixels = Render(cornerSize, new Rect(cornerSize), 0);
        int Alpha(int x, int y) => cornerPixels[(y * 600 + x) * 4 + 3];
        int NeighborAlpha(Point p)
        {
            var slope = (p.X < 300) == (p.Y < 200) ? -1 : 1;
            return Math.Min(Alpha((int)p.X - 1, (int)p.Y - slope), Alpha((int)p.X + 1, (int)p.Y + slope));
        }
        var crackedCorners = Enumerable.Range(2, 78).SelectMany(i =>
            new[] { new Point(i, i), new Point(599 - i, i), new Point(i, 399 - i), new Point(599 - i, 399 - i) }
                .Where(p => Alpha((int)p.X, (int)p.Y) < NeighborAlpha(p) - 3)).ToArray();
        if (crackedCorners.Length > 0) Console.WriteLine(string.Join("; ", crackedCorners.Take(8).Select(p =>
            $"corner {p}: {Alpha((int)p.X, (int)p.Y)}/{NeighborAlpha(p)}")));
        check(crackedCorners.Length == 0,
            "all four water corners have no diagonal antialias crack below neighboring alpha");
        foreach (var size in new[] { new Size(3440, 1392), new Size(3840, 2160), new Size(2560, 1440),
                     new Size(600, 320), new Size(8, 8), new Size(2, 2) })
        {
            var regions = TargetOverlay.Regions(size);
            check(regions.Length == 4 && regions.All(region => new Rect(size).Contains(region) &&
                    Math.Min(region.Width, region.Height) <= GuideFrameVisual.WaterDepth),
                "normal maximized scaled and tiny glow surfaces are bounded perimeter strips");
            check(Enumerable.Range(0, 4).All(i => Enumerable.Range(i + 1, 3 - i)
                    .All(j => Rect.Intersect(regions[i], regions[j]).IsEmpty ||
                        Rect.Intersect(regions[i], regions[j]).Width * Rect.Intersect(regions[i], regions[j]).Height == 0)),
                "perimeter surfaces do not double paint corners or allocate the target center");
            if (size.Width >= 2560)
                check(regions.All(region => !region.Contains(new Point(size.Width / 2, size.Height / 2))) &&
                    regions.Sum(region => region.Width * region.Height) < size.Width * size.Height,
                    "wider large-target perimeter still excludes all center allocation");
        }
        var overlay = new TargetOverlay();
        try
        {
            var target = new Rect(-120, 40, 3440, 1392);
            overlay.Place(target);
            check(overlay.Bounds == target && overlay.Edges.All(edge => edge.ShowActivated == false &&
                    !edge.IsHitTestVisible && new WindowInteropHelper(edge).Handle == IntPtr.Zero),
                "all four offset edges retain noactivate clickthrough policy without native windows");
            overlay.SetWaterProgress(-1);
            overlay.SetWaterProgress(.5);
            check(overlay.Wash.Width == target.Width && overlay.Wash.Height <= WindowWash.Band + 26 &&
                !overlay.Wash.IsHitTestVisible && !overlay.Wash.ShowActivated &&
                new WindowInteropHelper(overlay.Wash).Handle == IntPtr.Zero,
                "the traveling wash shares clickthrough noactivate policy and stays one bounded strip without native launch");
            foreach (var edge in overlay.Edges)
                ((GuideFrameVisual)((Border)edge.Content).Child).SetMotion(true);
            overlay.SetWaterProgress(.5); overlay.HideAndReset();
            check(overlay.Wash.Width == 2 && overlay.Wash.Height == 2 && overlay.Edges.All(edge => edge.Width == 2 && edge.Height == 2 &&
                    !((GuideFrameVisual)((Border)edge.Content).Child).HasAnimatedProperties) && !overlay.IsVisible,
                "clearing a tiled overlay stops every edge clock and shrinks every surface");
            overlay.Place(target);
            check(overlay.Edges[0].Width == target.Width && overlay.Edges[0].Height == GuideFrameVisual.WaterDepth,
                "redropping on the same target after clear restores full edge strips rather than tiny idle surfaces");
            overlay.Place(new Rect(0, 0, 600, 320));
            check(overlay.Edges.Sum(edge => edge.Width * edge.Height) ==
                    TargetOverlay.Regions(new Size(600, 320)).Sum(region => region.Width * region.Height),
                "placing after clear restores every edge size without showing or activating windows");
        }
        finally { overlay.Close(); }
        var sizeForPixels = new Size(600, 320);
        foreach (var phase in new[] { 0d, 1.7, 4.2 })
        {
            var full = Render(sizeForPixels, new Rect(sizeForPixels), phase);
            var stitched = new byte[full.Length];
            foreach (var region in TargetOverlay.Regions(sizeForPixels))
            {
                var pixels = Render(sizeForPixels, region, phase);
                for (var row = 0; row < (int)region.Height; row++)
                    Array.Copy(pixels, row * (int)region.Width * 4, stitched,
                        (((int)region.Y + row) * (int)sizeForPixels.Width + (int)region.X) * 4, (int)region.Width * 4);
            }
            check(full.SequenceEqual(stitched), "tiled water exactly matches composite pixels including corners and changing phase");
        }
    }

    static byte[] Render(Size target, Rect region, double phase)
    {
        var visual = new GuideFrameVisual { Width = region.Width, Height = region.Height, Phase = phase };
        visual.SetViewport(target, region);
        visual.Measure(region.Size); visual.Arrange(new Rect(region.Size)); visual.UpdateLayout();
        var bitmap = new RenderTargetBitmap((int)region.Width, (int)region.Height, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(visual);
        var pixels = new byte[(int)region.Width * (int)region.Height * 4];
        bitmap.CopyPixels(pixels, (int)region.Width * 4, 0); return pixels;
    }
}
