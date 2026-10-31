using System.Windows;

namespace CetaFae.Probe;

// Four narrow layered windows avoid uploading a full desktop-sized transparent bitmap per ripple.
internal sealed class TargetOverlay
{
    readonly OverlayWindow[] _edges = Enumerable.Range(0, 4).Select(_ => new OverlayWindow()).ToArray();
    readonly OverlayWindow _wash = new(wash: true);
    internal Rect Bounds { get; private set; }
    internal bool IsVisible => _edges.Any(edge => edge.IsVisible) || _wash.IsVisible;
    internal IReadOnlyList<OverlayWindow> Edges => _edges;
    internal OverlayWindow Wash => _wash;

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

    internal void Show()
    {
        SetWaterProgress(SystemParameters.ClientAreaAnimation ? -1 : 0);
        foreach (var edge in _edges) edge.Show();
    }
    internal void SetWaterProgress(double progress, IntPtr prompt = default)
    {
        foreach (var edge in _edges) edge.SetWaterProgress(progress >= 1 ? 0 : progress);
        var region = Bounds.IsEmpty ? Rect.Empty : WindowWash.Region(Bounds.Size, progress);
        if (region.IsEmpty) { if (_wash.IsVisible) _wash.HideAndReset(); return; }
        _wash.Left = Bounds.Left; _wash.Top = Bounds.Top + region.Top;
        _wash.Width = Math.Max(2, region.Width); _wash.Height = Math.Max(2, region.Height);
        _wash.SetViewport(Bounds.Size, region); _wash.SetWaterProgress(progress);
        if (_edges.Any(edge => edge.IsVisible) && !_wash.IsVisible)
        { _wash.Show(); Native.RaiseWithoutActivation(prompt); }
    }
    internal void HideAndReset()
    {
        foreach (var edge in _edges) edge.HideAndReset();
        _wash.HideAndReset();
        Bounds = Rect.Empty;
    }
    internal void Close() { foreach (var edge in _edges) edge.Close(); _wash.Close(); }
}
