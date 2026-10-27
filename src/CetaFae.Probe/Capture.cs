using System.IO;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace CetaFae.Probe;

/// <summary>Capture only the selected window, in memory, without a screen-pixel fallback.</summary>
public static class Capture
{
    /// <summary>Guide capture never reads screen pixels or persists image content.</summary>
    public static byte[] ReadTarget(IntPtr hwnd)
    {
        if (!Native.IsWindow(hwnd) || !Native.IsWindowVisible(hwnd) || Native.IsIconic(hwnd))
            throw new InvalidDataException("capture_target_unavailable");
        var rect = Native.Frame(hwnd);
        int w = rect.Right - rect.Left, h = rect.Bottom - rect.Top;
        if (w <= 0 || h <= 0 || (long)w * h > 16_000_000)
            throw new InvalidDataException("capture_dimensions");
        var frame = ReadPixels(hwnd, w, h);
        if (!frame.Ok || frame.NonBlackPct <= 1) throw new InvalidDataException("capture_unusable");
        var img = BitmapSource.Create(w, h, 96, 96, PixelFormats.Bgra32, null, frame.Pixels, w * 4);
        BitmapSource output = img;
        if (Math.Max(w, h) > 1600)
        {
            var scale = 1600.0 / Math.Max(w, h);
            output = new TransformedBitmap(img, new ScaleTransform(scale, scale));
        }
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(output));
        using var stream = new MemoryStream();
        encoder.Save(stream);
        return stream.ToArray();
    }

    sealed record PixelFrame(bool Ok, double NonBlackPct, byte[] Pixels);

    static PixelFrame ReadPixels(IntPtr hwnd, int w, int h)
    {
        var screenDc = Native.GetDC(IntPtr.Zero);
        var memDc = Native.CreateCompatibleDC(screenDc);
        var bmp = Native.CreateCompatibleBitmap(screenDc, w, h);
        var old = Native.SelectObject(memDc, bmp);
        Native.PatBlt(memDc, 0, 0, w, h, 0x00000042);

        // PrintWindow may report false while returning valid pixels; readback and content decide usability.
        Native.PrintWindow(hwnd, memDc, 2);
        var ok = true;

        var pixels = new byte[w * h * 4];
        var nonBlack = 0;
        try
        {
            var info = new Native.BITMAPINFO
            {
                bmiHeader = new Native.BITMAPINFOHEADER
                {
                    biSize = System.Runtime.InteropServices.Marshal.SizeOf<Native.BITMAPINFOHEADER>(),
                    biWidth = w,
                    biHeight = -h,
                    biPlanes = 1,
                    biBitCount = 32,
                    biCompression = 0
                }
            };
            if (Native.GetDIBits(memDc, bmp, 0, (uint)h, pixels, ref info, 0) == 0) ok = false;

            for (var i = 0; i < pixels.Length; i += 4)
                if (pixels[i] + pixels[i + 1] + pixels[i + 2] > 24) nonBlack++;
        }
        finally
        {
            Native.SelectObject(memDc, old);
            Native.DeleteObject(bmp);
            Native.DeleteDC(memDc);
            Native.ReleaseDC(IntPtr.Zero, screenDc);
        }

        var pct = 100.0 * nonBlack / (w * h);
        return new PixelFrame(ok, pct, pixels);
    }
}
