using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace CetaFae.Probe;

internal readonly record struct WhalePose(int Frame, double Y = 0, double ScaleX = 1, double ScaleY = 1,
    double WaterScale = 1, double Angle = 0, double Alpha = 1, double Progress = 0, int Spout = -1, bool Submerge = false);

/// <summary>Project-owned atlas reader; decoded surfaces are bounded independently of session length.</summary>
internal sealed class WhaleArt
{
    internal const int Cell = 96;
    internal const int FramesPerPage = 32;
    internal const int CacheLimit = 6;
    readonly Dictionary<string, BitmapSource> _pages = new();
    readonly Queue<string> _order = new();
    static readonly Lazy<WhalePose[]> Dock = new(ReadMotion);
    static readonly Lazy<bool[]> Contact = new(() =>
    {
        using var source = Resource("motion.json");
        using var json = JsonDocument.Parse(source);
        var contact = json.RootElement.GetProperty("contact").EnumerateArray().Select(v => v.GetBoolean()).ToArray();
        return contact.Length == 320 ? contact : throw new InvalidDataException("whale_contact_invalid");
    });
    internal int CachedPages => _pages.Count;

    static Stream Resource(string file) => Application.GetResourceStream(
        new Uri($"/CetaFae.Probe;component/Art/Whale/{file}", UriKind.Relative))?.Stream
        ?? throw new InvalidDataException("whale_art_missing");

    static WhalePose[] ReadMotion()
    {
        using var source = Resource("motion.json");
        using var document = JsonDocument.Parse(source);
        var root = document.RootElement;
        if (root.GetProperty("source").GetString() != "sequence-v2" || root.GetProperty("fps").GetInt32() != 25 ||
            root.GetProperty("cell").GetInt32() != Cell || root.GetProperty("frames").GetInt32() != 320)
            throw new InvalidDataException("whale_motion_invalid");
        var result = root.GetProperty("dock").EnumerateArray().Select((p, i) => new WhalePose(240 + i,
            p.GetProperty("y").GetDouble(), p.GetProperty("scaleX").GetDouble(), p.GetProperty("scaleY").GetDouble(),
            p.GetProperty("waterScale").GetDouble(), p.GetProperty("angle").GetDouble(),
            p.GetProperty("alpha").GetDouble(), p.GetProperty("progress").GetDouble(), i)).ToArray();
        if (result.Length != 80 || result.Any(p => !double.IsFinite(p.Y) || Math.Abs(p.Y) > .1 ||
            !double.IsFinite(p.Angle) || Math.Abs(p.Angle) > 10 || p.ScaleX is < .5 or > 1.1 ||
            p.ScaleY is < .5 or > 1.1 || p.WaterScale is < .5 or > 1.1 ||
            p.Alpha is < 0 or > 1 || p.Progress is < 0 or > 1)) throw new InvalidDataException("whale_motion_invalid");
        return result;
    }

    internal static WhalePose DockPose(double seconds, bool reduced = false) =>
        Dock.Value[reduced ? 79 : Math.Clamp((int)(Math.Max(0, seconds) * 25), 0, 79)];
    internal const double DropDuration = .8;
    internal static WhalePose DropPose(double seconds, bool reduced = false)
    {
        var t = reduced ? 1 : Math.Clamp(seconds / DropDuration, 0, 1);
        var progress = t * t * (3 - 2 * t);
        // The water hides a descending whale; shrinking/fading would read as a replacement rather than immersion.
        return new WhalePose(319, Y: .12 * progress, Alpha: t >= 1 ? 0 : 1, Progress: progress,
            Spout: t >= 1 ? -1 : 44 + (int)(t * 19), Submerge: true);
    }
    internal static bool IsImmersed(int frame) => frame is >= 0 and < 320 && Contact.Value[frame];

    internal BitmapSource Frame(string layer, int frame)
    {
        if (layer is not ("water-rear" or "whale-visible" or "water-front" or "spout") ||
            frame < 0 || frame >= (layer == "spout" ? 80 : 320)) throw new ArgumentOutOfRangeException(nameof(frame));
        var key = $"{layer}-{frame / FramesPerPage:00}.png";
        if (!_pages.TryGetValue(key, out var page))
        {
            using var source = Resource(key);
            page = BitmapFrame.Create(source, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad);
            if (page.PixelWidth != 768 || page.PixelHeight != 384) throw new InvalidDataException("whale_atlas_invalid");
            page.Freeze();
            while (_pages.Count >= CacheLimit) _pages.Remove(_order.Dequeue());
            _pages.Add(key, page); _order.Enqueue(key);
        }
        var cell = frame % FramesPerPage;
        var image = new CroppedBitmap(page, new Int32Rect(cell % 8 * Cell, cell / 8 * Cell, Cell, Cell));
        image.Freeze();
        return image;
    }

    internal void Clear() { _pages.Clear(); _order.Clear(); }
}

/// <summary>Only draws supplied art and a native input-morph shape; never reads another window.</summary>
internal sealed class WhaleVisual : FrameworkElement
{
    internal const double CanvasSize = 88;
    readonly WhaleArt _art = new();
    internal WhalePose Pose { get; private set; } = new(109);
    internal int Facing { get; private set; } = -1;
    internal void SetPose(WhalePose pose, int facing = -1)
    { Pose = pose; Facing = facing; InvalidateVisual(); }
    internal void Release() { _art.Clear(); SetPose(new WhalePose(109)); }

    protected override void OnRender(DrawingContext draw)
    {
        base.OnRender(draw);
        var center = new Point(ActualWidth / 2, ActualHeight / 2);
        if (Pose.Submerge) { DrawSubmerging(draw, center); return; }
        void Layer(string layer, double scaleX, double scaleY, double y, double angle, int frame)
        {
            draw.PushOpacity(Pose.Alpha);
            draw.PushTransform(new TranslateTransform(center.X, center.Y + y * CanvasSize));
            draw.PushTransform(new RotateTransform(angle));
            draw.PushTransform(new ScaleTransform(Facing * scaleX, scaleY));
            draw.DrawImage(_art.Frame(layer, frame), new Rect(-CanvasSize / 2, -CanvasSize / 2, CanvasSize, CanvasSize));
            draw.Pop(); draw.Pop(); draw.Pop(); draw.Pop();
        }
        if (Pose.Alpha > 0)
        {
            Layer("water-rear", Pose.WaterScale, Pose.WaterScale, 0, 0, Pose.Frame);
            Layer("whale-visible", Pose.ScaleX, Pose.ScaleY, Pose.Y, Pose.Angle, Pose.Frame);
            Layer("water-front", Pose.WaterScale, Pose.WaterScale, 0, 0, Pose.Frame);
            if (Pose.Spout >= 0) Layer("spout", 1, 1, 0, 0, Pose.Spout);
        }
        if (Pose.Progress > 0)
        {
            var width = 24 + (ActualWidth - 24) * Pose.Progress;
            var height = 24 + 26 * Pose.Progress;
            draw.PushOpacity(Pose.Progress);
            draw.PushTransform(new TranslateTransform(center.X - width / 2,
                center.Y + 7 * Pose.Progress - height / 2));
            if (width >= 60 && height >= 32) draw.DrawGeometry(GuideTheme.WhaleBlue, null, PromptShell.Shape(width, height));
            else draw.DrawRoundedRectangle(GuideTheme.WhaleBlue, null, new Rect(0, 0, width, height), 12, 12);
            draw.Pop();
            draw.Pop();
        }
    }

    internal static Rect WaterBody(double progress, double width)
    {
        var reveal = .12 + .88 * progress;
        var w = 88 + (width - 88) * reveal; var h = 8 + 42 * reveal;
        return new Rect((width - w) / 2, WhalePrompt.DockFoot - h, w, h);
    }

    void DrawSubmerging(DrawingContext draw, Point center)
    {
        var body = WaterBody(Pose.Progress, ActualWidth);
        var amplitude = 2.4 * (1 - Pose.Progress); var phase = Pose.Progress * Math.PI * 3;
        draw.PushTransform(new TranslateTransform(body.X, body.Y));
        if (body.Height >= 32) draw.DrawGeometry(GuideTheme.WhaleBlue, null,
            PromptShell.Shape(body.Width, body.Height, amplitude, phase));
        else draw.DrawRoundedRectangle(GuideTheme.WhaleBlue, null, new Rect(body.Size), body.Height / 2, body.Height / 2);
        if (body.Height > 26)
        {
            var inner = new Rect(18, 6, Math.Max(0, body.Width - 66), body.Height - 14);
            draw.DrawRoundedRectangle(Brushes.White, null, inner, 12, 12);
        }
        draw.Pop();
        if (Pose.Alpha <= 0) return;
        var above = new StreamGeometry();
        using (var path = above.Open())
        {
            path.BeginFigure(new Point(0, 0), true, true); path.LineTo(new Point(ActualWidth, 0), true, false);
            var steps = Math.Max(1, (int)Math.Ceiling(ActualWidth / 8));
            for (var i = steps; i >= 0; i--)
            {
                var x = ActualWidth * i / steps;
                var surface = body.Y + PromptShell.SurfaceY(x - body.X, body.Width, amplitude, phase);
                path.LineTo(new Point(x, surface), true, false);
            }
        }
        above.Freeze(); draw.PushClip(above);
        draw.PushTransform(new TranslateTransform(center.X, center.Y + Pose.Y * CanvasSize));
        draw.PushTransform(new ScaleTransform(Facing, 1));
        var cell = new Rect(-CanvasSize / 2, -CanvasSize / 2, CanvasSize, CanvasSize);
        draw.DrawImage(_art.Frame("whale-visible", Pose.Frame), cell);
        if (Pose.Spout >= 0) draw.DrawImage(_art.Frame("spout", Pose.Spout), cell);
        draw.Pop(); draw.Pop(); draw.Pop();
    }
}
