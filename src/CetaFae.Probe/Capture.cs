using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace CetaFae.Probe;

/// <summary>Capture only the selected window, in memory, without a screen-pixel fallback.</summary>
public static class Capture
{
    /// <summary>Guide capture never reads screen pixels or persists image content.</summary>
    public static byte[] ReadTarget(IntPtr hwnd, Rect? region = null) => ReadTarget(hwnd, region, out _);

    internal static byte[] ReadTarget(IntPtr hwnd, Rect? region, out Rect? caret, CancellationToken token = default)
    {
        token.ThrowIfCancellationRequested();
        caret = null;
        if (!Native.IsWindow(hwnd) || !Native.IsWindowVisible(hwnd) || Native.IsIconic(hwnd))
            throw new InvalidDataException("capture_target_unavailable");
        if (!Native.GetWindowRect(hwnd, out var rect)) throw new InvalidDataException("capture_dimensions");
        int w = rect.Right - rect.Left, h = rect.Bottom - rect.Top;
        if (w <= 0 || h <= 0 || (long)w * h > 16_000_000)
            throw new InvalidDataException("capture_dimensions");
        if (Native.GetWindowDisplayAffinity(hwnd, out var affinity) && affinity != 0)
            throw new InvalidDataException("capture_protected");
        var before = Native.CaretRect(hwnd);
        var captured = WithWindowFallback(() =>
        {
            var frame = ReadPixels(hwnd, w, h);
            if (!frame.Ok || frame.NonBlackPct <= 1) throw new InvalidDataException("capture_unusable");
            var image = BitmapSource.Create(w, h, 96, 96, PixelFormats.Bgra32, null, frame.Pixels, w * 4);
            ProbeLog.Log("guide_capture_backend", ("backend", "print_window"));
            return new WindowCapture.Frame(image, new Rect(rect.Left, rect.Top, w, h));
        }, () =>
        {
            token.ThrowIfCancellationRequested();
            if (!OperatingSystem.IsWindowsVersionAtLeast(10, 0, 18362))
                throw new InvalidDataException("capture_backend_unavailable");
            var modern = WindowCapture.ReadAsync(hwnd, token).GetAwaiter().GetResult();
            if (!Usable(modern.Image)) throw new InvalidDataException("capture_unusable");
            ProbeLog.Log("guide_capture_backend", ("backend", "window_graphics"));
            return modern;
        });
        var after = Native.CaretRect(hwnd);
        if (before is not null && before == after) caret = before;
        token.ThrowIfCancellationRequested();
        return EncodeRegion(captured.Image, captured.Bounds, region ?? GuideObservation.WindowRect(hwnd));
    }

    internal static T WithWindowFallback<T>(Func<T> preferred, Func<T> fallback)
    {
        try { return preferred(); }
        catch (Exception ex) when (ex is UnauthorizedAccessException || ex.HResult == unchecked((int)0x80070005))
        { throw new InvalidDataException("capture_protected"); }
        catch (Exception ex) when (ex is System.Runtime.InteropServices.COMException or TypeLoadException or
            EntryPointNotFoundException or PlatformNotSupportedException || ex is InvalidDataException
            { Message: "capture_backend_unavailable" or "capture_dimensions" or "capture_unusable" })
        { return fallback(); }
    }

    static bool Usable(BitmapSource image)
    {
        var pixels = new byte[checked(image.PixelWidth * image.PixelHeight * 4)];
        image.CopyPixels(pixels, image.PixelWidth * 4, 0);
        var nonBlack = 0;
        for (var i = 0; i < pixels.Length; i += 4)
            if (pixels[i] + pixels[i + 1] + pixels[i + 2] > 24) nonBlack++;
        return nonBlack * 100.0 / (image.PixelWidth * image.PixelHeight) > 1;
    }

    internal static byte[] EncodeRegion(BitmapSource img, Rect sourceFrame, Rect region)
    {
        if (!GuideScope.Valid(sourceFrame) || !GuideScope.Valid(region) || !sourceFrame.Contains(region) ||
            img.PixelWidth != sourceFrame.Width || img.PixelHeight != sourceFrame.Height ||
            new[] { region.X - sourceFrame.X, region.Y - sourceFrame.Y, region.Width, region.Height }.Any(v => v != Math.Truncate(v)))
            throw new InvalidDataException("scope_unavailable");
        BitmapSource output = new CroppedBitmap(img, new Int32Rect((int)(region.X - sourceFrame.X),
            (int)(region.Y - sourceFrame.Y), (int)region.Width, (int)region.Height));
        if (Math.Max(output.PixelWidth, output.PixelHeight) > 1600)
        {
            var scale = 1600.0 / Math.Max(output.PixelWidth, output.PixelHeight);
            output = new TransformedBitmap(output, new ScaleTransform(scale, scale));
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
