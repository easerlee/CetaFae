using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace CetaFae.Probe;

internal static class ImageCompression
{
    internal sealed record Output(byte[] Bytes, int Width, int Height, int Quality);
    internal static Output Encode(Stream source, int maximum, CancellationToken token)
    {
        if (maximum is < 65536 or > 4194304) throw new InvalidDataException("compression_size");
        var decoded = BitmapDecoder.Create(source, BitmapCreateOptions.DelayCreation, BitmapCacheOption.None);
        if (decoded.Frames.Count != 1 || (long)decoded.Frames[0].PixelWidth * decoded.Frames[0].PixelHeight > 16000000)
            throw new InvalidDataException("compression_image");
        BitmapSource original = decoded.Frames[0];
        // Preserve the displayed orientation before stripping source metadata from the new copy.
        if (original.Metadata is BitmapMetadata metadata && metadata.ContainsQuery("/app1/ifd/{ushort=274}"))
        {
            if (metadata.GetQuery("/app1/ifd/{ushort=274}") is ushort orientation)
                original = ChatImage.Orient(original, orientation);
        }
        foreach (var scale in new[] { 1d, .85, .7, .5 })
        foreach (var quality in new[] { 90, 80, 70, 60, 50 })
        {
            token.ThrowIfCancellationRequested();
            var w = Math.Max(1, (int)(original.PixelWidth * scale)); var h = Math.Max(1, (int)(original.PixelHeight * scale));
            var drawing = new DrawingVisual();
            using (var c = drawing.RenderOpen()) { c.DrawRectangle(Brushes.White, null, new Rect(0, 0, w, h)); c.DrawImage(original, new Rect(0, 0, w, h)); }
            var image = new RenderTargetBitmap(w, h, 96, 96, PixelFormats.Pbgra32); image.Render(drawing);
            var encoder = new JpegBitmapEncoder { QualityLevel = quality }; encoder.Frames.Add(BitmapFrame.Create(image));
            using var result = new MemoryStream(); encoder.Save(result);
            if (result.Length > maximum) continue;
            var bytes = result.ToArray();
            var verified = UtilityUi.Decode(bytes);
            if (verified.PixelWidth != w || verified.PixelHeight != h) throw new InvalidDataException("compression_output");
            return new(bytes, w, h, quality);
        }
        throw new InvalidDataException("compression_target_unavailable");
    }
}
