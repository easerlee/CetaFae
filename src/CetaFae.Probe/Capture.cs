using System.Diagnostics;
using System.IO;
using System.Security.Cryptography;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace CetaFae.Probe;

/// <summary>目标窗口截图：PrintWindow 优先，屏幕区域兜底。每次记录是否黑图、耗时、图像哈希。</summary>
public static class Capture
{
    public static void Shoot(IntPtr hwnd, string outPath, int shots, int intervalMs)
    {
        if (!Native.IsWindow(hwnd))
        {
            ProbeLog.Log("capture_window_gone", ("window", Native.Hex(hwnd)));
            return;
        }

        var rect = Native.Frame(hwnd);
        int w = rect.Right - rect.Left, h = rect.Bottom - rect.Top;
        if (w <= 0 || h <= 0)
        {
            ProbeLog.Log("capture_bad_rect", ("window", Native.Hex(hwnd)), ("rect", $"{w}x{h}"));
            return;
        }

        for (var i = 1; i <= shots; i++)
        {
            var stamp = $"logs/cap-{DateTime.Now:HHmmss}-{i}";
            var print = Grab(hwnd, rect, w, h, viaPrintWindow: true, $"{stamp}-print.png");
            var screen = Grab(hwnd, rect, w, h, viaPrintWindow: false, $"{stamp}-screen.png");

            ProbeLog.Log("capture",
                ("n", i),
                ("window", Native.Hex(hwnd)),
                ("title", Native.Title(hwnd)),
                ("rect", $"{w}x{h}"),
                ("printOk", print.Ok), ("printMs", print.ElapsedMs), ("printNonBlack", print.NonBlackPct), ("printHash", print.Hash),
                ("screenOk", screen.Ok), ("screenMs", screen.ElapsedMs), ("screenNonBlack", screen.NonBlackPct), ("screenHash", screen.Hash),
                ("sameImage", print.Hash.Length > 0 && print.Hash == screen.Hash));

            if (i == 1 && outPath.Length > 0)
            {
                var pick = print.Ok && print.NonBlackPct > 1 ? print.Path : screen.Path;
                if (File.Exists(pick)) File.Copy(pick, outPath, overwrite: true);
            }

            if (i < shots) Thread.Sleep(intervalMs);
        }
    }

    sealed record Shot(bool Ok, long ElapsedMs, double NonBlackPct, string Hash, string Path);

    static Shot Grab(IntPtr hwnd, Native.RECT rect, int w, int h, bool viaPrintWindow, string path)
    {
        var clock = Stopwatch.StartNew();
        var screenDc = Native.GetDC(IntPtr.Zero);
        var memDc = Native.CreateCompatibleDC(screenDc);
        var bmp = Native.CreateCompatibleBitmap(screenDc, w, h);
        var old = Native.SelectObject(memDc, bmp);

        var ok = viaPrintWindow
            ? Native.PrintWindow(hwnd, memDc, 2)
            : Native.BitBlt(memDc, 0, 0, w, h, screenDc, rect.Left, rect.Top, Native.SRCCOPY);

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

        var elapsed = clock.ElapsedMilliseconds;
        var pct = 100.0 * nonBlack / (w * h);
        var hash = Convert.ToHexString(SHA256.HashData(pixels))[..16];

        try
        {
            var img = BitmapSource.Create(w, h, 96, 96, PixelFormats.Bgra32, null, pixels, w * 4);
            var encoder = new PngBitmapEncoder();
            encoder.Frames.Add(BitmapFrame.Create(img));
            using var fs = File.Create(path);
            encoder.Save(fs);
        }
        catch
        {
            ok = false;
        }

        return new Shot(ok, elapsed, Math.Round(pct, 1), hash, path);
    }
}
