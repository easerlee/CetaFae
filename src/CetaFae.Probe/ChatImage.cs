using System.IO;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace CetaFae.Probe;

internal sealed class ChatImage
{
    internal const int MaxFileBytes = 10 * 1024 * 1024;
    const int MaxPayloadBytes = 4 * 1024 * 1024;
    const int MaxEdge = 2048;
    readonly byte[] _bytes;
    readonly string _mime;
    internal BitmapSource Preview { get; }

    ChatImage(BitmapSource bitmap, byte[] bytes, string mime)
    {
        var small = Resize(bitmap, 64);
        Preview = CopyPixels(small);
        Preview.Freeze();
        _bytes = bytes;
        _mime = mime;
    }

    internal string DataUrl() => $"data:{_mime};base64,{Convert.ToBase64String(_bytes)}";

    internal static ChatImage FromFile(string path)
    {
        using var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        var length = file.Length;
        if (length > MaxFileBytes) throw new InvalidDataException("chat_image_size");
        var bytes = new byte[(int)length];
        file.ReadExactly(bytes);
        var png = bytes.AsSpan().StartsWith(new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 });
        var jpeg = bytes.AsSpan().StartsWith(new byte[] { 255, 216, 255 });
        if (!png && !jpeg && !bytes.AsSpan().StartsWith("BM"u8))
            throw new InvalidDataException("chat_image_format");

        using var stream = new MemoryStream(bytes, writable: false);
        var frame = BitmapFrame.Create(stream, BitmapCreateOptions.IgnoreColorProfile | BitmapCreateOptions.DelayCreation, BitmapCacheOption.None);
        CheckDimensions(frame);
        var orientation = jpeg && frame.Metadata is BitmapMetadata metadata &&
            metadata.GetQuery("/app1/ifd/{ushort=274}") is ushort value ? value : 1;
        stream.Position = 0;
        var decoded = new BitmapImage();
        decoded.BeginInit();
        decoded.StreamSource = stream;
        decoded.CacheOption = BitmapCacheOption.OnLoad;
        decoded.CreateOptions = BitmapCreateOptions.IgnoreColorProfile;
        if (frame.PixelWidth >= frame.PixelHeight && frame.PixelWidth > MaxEdge) decoded.DecodePixelWidth = MaxEdge;
        if (frame.PixelHeight > frame.PixelWidth && frame.PixelHeight > MaxEdge) decoded.DecodePixelHeight = MaxEdge;
        decoded.EndInit();
        return Encode(Orient(decoded, orientation), jpeg);
    }

    internal static ChatImage FromBitmap(BitmapSource bitmap)
    {
        CheckDimensions(bitmap);
        return Encode(Resize(bitmap, MaxEdge), jpeg: false);
    }

    static void CheckDimensions(BitmapSource bitmap)
    {
        var width = bitmap.PixelWidth;
        var height = bitmap.PixelHeight;
        if (width is <= 0 or > 16384 || height is <= 0 or > 16384 || (long)width * height > 20_000_000)
            throw new InvalidDataException("chat_image_size");
    }

    static BitmapSource Resize(BitmapSource bitmap, int limit)
    {
        var scale = (double)limit / Math.Max(bitmap.PixelWidth, bitmap.PixelHeight);
        if (scale >= 1) return bitmap;
        return new TransformedBitmap(bitmap, new ScaleTransform(Math.Max(scale, 1d / bitmap.PixelWidth),
            Math.Max(scale, 1d / bitmap.PixelHeight)));
    }

    static BitmapSource Orient(BitmapSource bitmap, int orientation)
    {
        if (orientation is < 2 or > 8) return bitmap;
        var transform = new TransformGroup();
        if (orientation is 2 or 4 or 5 or 7) transform.Children.Add(new ScaleTransform(-1, 1));
        var angle = orientation switch { 3 or 4 => 180, 5 or 8 => 270, 6 or 7 => 90, _ => 0 };
        if (angle != 0) transform.Children.Add(new RotateTransform(angle));
        return new TransformedBitmap(bitmap, transform);
    }

    static BitmapSource CopyPixels(BitmapSource bitmap)
    {
        var source = new FormatConvertedBitmap(bitmap, PixelFormats.Bgra32, null, 0);
        var stride = source.PixelWidth * 4;
        var pixels = new byte[stride * source.PixelHeight];
        source.CopyPixels(pixels, stride, 0);
        // Flatten transparency so encoded pixels and the preview cannot reveal hidden color data.
        for (var index = 0; index < pixels.Length; index += 4)
        {
            var alpha = pixels[index + 3];
            if (alpha == 255) continue;
            for (var channel = 0; channel < 3; channel++)
                pixels[index + channel] = (byte)((pixels[index + channel] * alpha + 255 * (255 - alpha) + 127) / 255);
            pixels[index + 3] = 255;
        }
        return BitmapSource.Create(source.PixelWidth, source.PixelHeight, 96, 96, PixelFormats.Bgra32, null, pixels, stride);
    }

    static ChatImage Encode(BitmapSource bitmap, bool jpeg)
    {
        // A fresh pixel source prevents the encoder from inheriting metadata.
        var clean = CopyPixels(bitmap);
        using var stream = new MemoryStream();
        if (!jpeg)
        {
            Write(new PngBitmapEncoder());
            if (stream.Length <= MaxPayloadBytes) return new ChatImage(clean, stream.ToArray(), "image/png");
            stream.SetLength(0);
        }
        Write(new JpegBitmapEncoder { QualityLevel = 88 });
        if (stream.Length > MaxPayloadBytes) throw new InvalidDataException("chat_image_size");
        return new ChatImage(clean, stream.ToArray(), "image/jpeg");

        void Write(BitmapEncoder encoder)
        {
            encoder.Frames.Add(BitmapFrame.Create(clean));
            encoder.Save(stream);
        }
    }
}
