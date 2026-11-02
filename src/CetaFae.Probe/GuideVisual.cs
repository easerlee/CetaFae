using System.IO;
using System.Security.Cryptography;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace CetaFae.Probe;

/// <summary>Local pixel evidence; no rectangle is erased from the captured image.</summary>
public sealed record GuideVisual(string PixelHash, Rect? CaretBounds, string? BlinkHash,
    string? SolidCaretHash = null, string? LeadingCaretHash = null)
{
    internal static GuideVisual Read(byte[] png, Rect region, Rect? caret)
    {
        using var stream = new MemoryStream(png, false);
        var image = BitmapDecoder.Create(stream, BitmapCreateOptions.None, BitmapCacheOption.OnLoad).Frames[0];
        if (!GuideScope.Valid(region) || (long)image.PixelWidth * image.PixelHeight > 16_000_000)
            throw new InvalidDataException("target_changed");
        var bitmap = new FormatConvertedBitmap(image, PixelFormats.Bgra32, null, 0);
        var pixels = new byte[checked(bitmap.PixelWidth * bitmap.PixelHeight * 4)];
        bitmap.CopyPixels(pixels, bitmap.PixelWidth * 4, 0);
        string Hash() => $"{bitmap.PixelWidth}x{bitmap.PixelHeight}:{Convert.ToHexString(SHA256.HashData(pixels))}";
        var exact = Hash();
        if (caret is not { } bounds || !GuideScope.Valid(bounds) || bounds.Width > 2 || bounds.Height > 64 ||
            !region.Contains(bounds) || image.PixelWidth != region.Width || image.PixelHeight != region.Height ||
            new[] { bounds.X - region.X, bounds.Y - region.Y, bounds.Width, bounds.Height }.Any(v => v != Math.Truncate(v)))
            return new GuideVisual(exact, null, null);
        var top = (int)(bounds.Top - region.Top);
        var bottom = (int)(bounds.Bottom - region.Top);
        var column = (int)(bounds.Left - region.Left);
        // A proven solid stroke can be reconstructed, not an arbitrary ignored rectangle.
        var solid = bounds.Width == 1 ? BackgroundHash(column) : null;
        var leading = bounds.Width == 1 ? BackgroundHash(column - 1) : null;
        for (var y = (int)(bounds.Top - region.Top); y < bounds.Bottom - region.Top; y++)
        for (var x = (int)(bounds.Left - region.Left); x < bounds.Right - region.Left; x++)
        {
            var offset = (y * bitmap.PixelWidth + x) * 4;
            for (var channel = 0; channel < 3; channel++) pixels[offset + channel] ^= 255;
        }
        return new GuideVisual(exact, bounds, Hash(), solid, leading);

        string? BackgroundHash(int x)
        {
            if (x < 1 || x + 1 >= bitmap.PixelWidth) return null;
            var first = (top * bitmap.PixelWidth + x) * 4;
            var stroke = pixels.AsSpan(first, 4).ToArray();
            var background = pixels.AsSpan(first - 4, 4).ToArray();
            if (stroke.AsSpan().SequenceEqual(background) || stroke[3] != background[3]) return null;
            for (var y = top; y < bottom; y++)
            {
                var offset = (y * bitmap.PixelWidth + x) * 4;
                if (!pixels.AsSpan(offset, 4).SequenceEqual(stroke) ||
                    !pixels.AsSpan(offset - 4, 4).SequenceEqual(background) ||
                    !pixels.AsSpan(offset + 4, 4).SequenceEqual(background)) return null;
            }
            for (var y = top; y < bottom; y++)
                background.CopyTo(pixels, (y * bitmap.PixelWidth + x) * 4);
            var restored = Hash();
            for (var y = top; y < bottom; y++)
                stroke.CopyTo(pixels, (y * bitmap.PixelWidth + x) * 4);
            return restored;
        }
    }

    internal bool Matches(GuideVisual current)
    {
        if (PixelHash == current.PixelHash) return true;
        if (CaretBounds is { } previous && current.CaretBounds is { } next && previous != next) return false;
        // Each exception must reconstruct the other complete image exactly.
        return CaretBounds is not null && BlinkHash == current.PixelHash ||
               current.CaretBounds is not null && current.BlinkHash == PixelHash ||
               CaretBounds is not null && current.CaretBounds == CaretBounds &&
               (SolidCaretHash == current.PixelHash || LeadingCaretHash == current.PixelHash ||
                current.SolidCaretHash == PixelHash || current.LeadingCaretHash == PixelHash);
    }
}
