using System.IO;
using System.Windows;
using System.Windows.Media;
using Windows.Graphics.Imaging;
using Windows.Media.Ocr;
using Windows.Storage.Streams;

namespace CetaFae.Probe;

/// <summary>Local OCR evidence identifies text, never the clickable area of a nearby control.</summary>
internal static class GuideTextRegions
{
    internal const string ControlType = "Visual.Text";
    internal sealed record Line(string Text, Rect Bounds);

    internal static Rect ImageBounds(Rect bounds, int width, int height, double? angle)
    {
        if (!GuideScope.Valid(bounds) || !double.IsFinite(angle ?? 0) || Math.Abs(angle ?? 0) > 180)
            return Rect.Empty;
        // Windows OCR reports bounds on the deskewed image. Restore the original image coordinates.
        return new RotateTransform(angle ?? 0, width / 2d, height / 2d).TransformBounds(bounds);
    }

    internal static IReadOnlyList<GuideCandidate> Merge(IReadOnlyList<GuideCandidate> controls,
        IReadOnlyList<GuideCandidate> text)
    {
        var nextId = controls.Count == 0 ? 1 : controls.Max(candidate => candidate.Id) + 1;
        return controls.Concat(text.Where(line => !controls.Any(control =>
                string.Equals(line.Name, control.Name, StringComparison.OrdinalIgnoreCase) &&
                control.Bounds.IntersectsWith(line.Bounds)))
            .Take(Math.Max(0, 128 - controls.Count))
            .Select((line, index) => line with { Id = nextId + index, RuntimeId = [-1, nextId + index] })).ToArray();
    }

    internal static IReadOnlyList<GuideCandidate> Candidates(IEnumerable<Line> lines, Rect region,
        int pixelWidth, int pixelHeight)
    {
        if (!GuideScope.Valid(region) || pixelWidth < 1 || pixelHeight < 1) return [];
        var image = new Rect(0, 0, pixelWidth, pixelHeight);
        var valid = lines.Take(513).ToArray();
        if (valid.Length > 512) return [];
        foreach (var line in valid) GuidePrivacy.EnsureTextSafe(line.Text);
        var duplicates = valid.GroupBy(line => line.Text.Trim(), StringComparer.OrdinalIgnoreCase)
            .Where(group => group.Count() > 1).Select(group => group.Key).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var result = new List<GuideCandidate>();
        foreach (var line in valid)
        {
            var text = line.Text.Trim();
            if (text.Length is < 2 or > 200 || text.Any(char.IsControl) || duplicates.Contains(text) ||
                !GuideScope.Valid(line.Bounds) || !image.Contains(line.Bounds)) continue;
            var bounds = new Rect(region.Left + line.Bounds.Left * region.Width / pixelWidth,
                region.Top + line.Bounds.Top * region.Height / pixelHeight,
                line.Bounds.Width * region.Width / pixelWidth, line.Bounds.Height * region.Height / pixelHeight);
            var id = result.Count + 1;
            result.Add(new GuideCandidate(id, text, "", ControlType, bounds, [-1, id]));
            if (result.Count == 64) break;
        }
        return result;
    }

    internal static IReadOnlyList<GuideCandidate> Read(byte[] png, Rect region, CancellationToken token)
        => ReadAsync(png, region, token).GetAwaiter().GetResult();

    internal static async Task<IReadOnlyList<GuideCandidate>> ReadAsync(byte[] png, Rect region, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        // No language downloads or configuration changes; unavailable OCR stays explanation-only.
        var engine = OcrEngine.TryCreateFromUserProfileLanguages();
        if (engine is null) return [];
        using var stream = new InMemoryRandomAccessStream();
        using (var writer = new DataWriter(stream.GetOutputStreamAt(0)))
        {
            writer.WriteBytes(png);
            await writer.StoreAsync().AsTask(token);
        }
        var decoder = await Windows.Graphics.Imaging.BitmapDecoder.CreateAsync(stream).AsTask(token);
        if (decoder.PixelWidth > OcrEngine.MaxImageDimension || decoder.PixelHeight > OcrEngine.MaxImageDimension)
            return [];
        using var bitmap = await decoder.GetSoftwareBitmapAsync(BitmapPixelFormat.Bgra8, BitmapAlphaMode.Premultiplied).AsTask(token);
        var result = await engine.RecognizeAsync(bitmap).AsTask(token);
        ProbeLog.Log("guide_text_regions", ("lines", result.Lines.Count), ("angle", result.TextAngle));
        var lines = result.Lines.Select(line =>
        {
            var bounds = Rect.Empty;
            foreach (var word in line.Words)
            {
                var r = word.BoundingRect;
                bounds.Union(ImageBounds(new Rect(r.X, r.Y, r.Width, r.Height),
                    (int)decoder.PixelWidth, (int)decoder.PixelHeight, result.TextAngle));
            }
            return new Line(line.Text, bounds);
        });
        token.ThrowIfCancellationRequested();
        return Candidates(lines, region, (int)decoder.PixelWidth, (int)decoder.PixelHeight);
    }
}
