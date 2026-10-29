using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using CetaFae.Probe;

namespace CetaFae.Checks;

static class CaptureChecks
{
    public static int Run()
    {
        var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        var initial = Color.FromRgb(20, 100, 220);
        var changed = Color.FromRgb(30, 180, 70);
        var target = Fixture("CetaFae capture fixture", initial);
        var cover = Fixture("CetaFae capture occluder", Colors.Magenta);
        var framed = Fixture("CetaFae generated framed crop fixture", Colors.Green);
        framed.WindowStyle = WindowStyle.SingleBorderWindow;
        var patch = new Border { Background = Brushes.Red, Width = 80, Height = 40,
            HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
        framed.Content = patch;
        var result = 1;
        var running = false;
        target.Loaded += async (_, _) =>
        {
            if (running) return;
            running = true;
            var handle = new WindowInteropHelper(target).Handle;
            try
            {
                await Settle();
                CheckColor(Capture.ReadTarget(handle), initial, "unobscured fixture pixels");
                cover.Show();
                await Settle();
                CheckColor(Capture.ReadTarget(handle), initial, "occluder pixels excluded");
                target.Background = new SolidColorBrush(changed);
                await Settle();
                CheckColor(Capture.ReadTarget(handle), changed, "covered content update is fresh");
                target.WindowState = WindowState.Minimized;
                await Settle();
                Reject(() => Capture.ReadTarget(handle), "capture_target_unavailable", "minimized target rejected");
                target.WindowState = WindowState.Normal;
                await Settle();
                CheckColor(Capture.ReadTarget(handle), changed, "restored target pixels");
                framed.Show();
                await Settle();
                var topLeft = patch.PointToScreen(new Point());
                var bottomRight = patch.PointToScreen(new Point(patch.ActualWidth, patch.ActualHeight));
                var region = new Rect(Math.Ceiling(topLeft.X) + 2, Math.Ceiling(topLeft.Y) + 2,
                    Math.Floor(bottomRight.X) - Math.Ceiling(topLeft.X) - 4,
                    Math.Floor(bottomRight.Y) - Math.Ceiling(topLeft.Y) - 4);
                CheckCropColor(Capture.ReadTarget(new WindowInteropHelper(framed).Handle, region), Colors.Red, region,
                    "framed window crop aligns with generated client pixels, excluding non-client borders");
                framed.Close();
                target.Background = Brushes.Black;
                await Settle();
                Reject(() => Capture.ReadTarget(handle), "capture_unusable", "black target rejected");
                target.Close();
                Reject(() => Capture.ReadTarget(handle), "capture_target_unavailable", "closed target rejected");
                Console.WriteLine("RESULT: 8 desktop capture checks passed; generated fixture only; no files or network.");
                result = 0;
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"FAIL: desktop capture: {ex.GetType().Name}: {ex.Message}");
            }
            finally
            {
                cover.Close();
                if (framed.IsVisible) framed.Close();
                if (target.IsVisible) target.Close();
                app.Shutdown();
            }
        };
        target.Show();
        app.Run();
        return result;
    }

    static Window Fixture(string title, Color color) => new()
    {
        Title = title, Width = 320, Height = 200, Left = 100, Top = 100,
        WindowStartupLocation = WindowStartupLocation.Manual,
        WindowStyle = WindowStyle.None, ResizeMode = ResizeMode.NoResize,
        ShowActivated = false, ShowInTaskbar = false, Topmost = true,
        Background = new SolidColorBrush(color)
    };

    static async Task Settle() => await Task.Delay(180);

    static void CheckColor(byte[] png, Color expected, string name)
    {
        using var stream = new MemoryStream(png);
        var decoded = new PngBitmapDecoder(stream, BitmapCreateOptions.None, BitmapCacheOption.OnLoad).Frames[0];
        var frame = new FormatConvertedBitmap(decoded, PixelFormats.Bgra32, null, 0);
        var pixel = new byte[4];
        frame.CopyPixels(new Int32Rect(frame.PixelWidth / 2, frame.PixelHeight / 2, 1, 1), pixel, 4, 0);
        if (Math.Abs(pixel[0] - expected.B) > 8 || Math.Abs(pixel[1] - expected.G) > 8 ||
            Math.Abs(pixel[2] - expected.R) > 8)
            throw new InvalidOperationException(name);
        Console.WriteLine($"PASS: {name}");
    }

    static void Reject(Action read, string category, string name)
    {
        try { read(); }
        catch (InvalidDataException ex) when (ex.Message == category)
        {
            Console.WriteLine($"PASS: {name}");
            return;
        }
        throw new InvalidOperationException(name);
    }

    static void CheckCropColor(byte[] png, Color expected, Rect region, string name)
    {
        using var stream = new MemoryStream(png);
        var decoded = new PngBitmapDecoder(stream, BitmapCreateOptions.None, BitmapCacheOption.OnLoad).Frames[0];
        var frame = new FormatConvertedBitmap(decoded, PixelFormats.Bgra32, null, 0);
        var pixels = new byte[frame.PixelWidth * frame.PixelHeight * 4];
        frame.CopyPixels(pixels, frame.PixelWidth * 4, 0);
        if (frame.PixelWidth != region.Width || frame.PixelHeight != region.Height ||
            Enumerable.Range(0, frame.PixelWidth * frame.PixelHeight).Any(i =>
                Math.Abs(pixels[i * 4] - expected.B) > 8 || Math.Abs(pixels[i * 4 + 1] - expected.G) > 8 ||
                Math.Abs(pixels[i * 4 + 2] - expected.R) > 8)) throw new InvalidOperationException(name);
        Console.WriteLine($"PASS: {name}");
    }
}
