using System.Windows;

namespace CetaFae.Probe;

// Four narrow layered windows avoid uploading a full desktop-sized transparent bitmap per ripple.
internal sealed class TargetOverlay
{
    readonly OverlayWindow[] _edges = Enumerable.Range(0, 4).Select(_ => new OverlayWindow()).ToArray();
    internal Rect Bounds { get; private set; }
    internal bool IsVisible => _edges.Any(edge => edge.IsVisible);
    internal IReadOnlyList<OverlayWindow> Edges => _edges;

    internal static Rect[] Regions(Size size)
    {
        var depth = GuideFrameVisual.Depth(size);
        return [new(0, 0, size.Width, depth), new(0, size.Height - depth, size.Width, depth),
            new(0, depth, depth, size.Height - depth * 2),
            new(size.Width - depth, depth, depth, size.Height - depth * 2)];
    }

    internal void Place(Rect bounds)
    {
        if (Bounds == bounds) return;
        Bounds = bounds;
        var regions = Regions(bounds.Size);
        for (var i = 0; i < _edges.Length; i++)
        {
            var region = regions[i]; var edge = _edges[i];
            edge.Left = bounds.Left + region.Left; edge.Top = bounds.Top + region.Top;
            edge.Width = region.Width; edge.Height = region.Height;
            edge.SetViewport(bounds.Size, region);
        }
    }

    internal void Show() { foreach (var edge in _edges) edge.Show(); }
    internal void SetWaterProgress(double progress) { foreach (var edge in _edges) edge.SetWaterProgress(progress); }
    internal void HideAndReset()
    {
        foreach (var edge in _edges) edge.HideAndReset();
        Bounds = Rect.Empty;
    }
    internal void Close() { foreach (var edge in _edges) edge.Close(); }
}
