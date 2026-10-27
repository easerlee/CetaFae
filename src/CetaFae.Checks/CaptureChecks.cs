using System.IO;
using System.Windows;
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
                target.Background = Brushes.Black;
                await Settle();
                Reject(() => Capture.ReadTarget(handle), "capture_unusable", "black target rejected");
                target.Close();
                Reject(() => Capture.ReadTarget(handle), "capture_target_unavailable", "closed target rejected");
                Console.WriteLine("RESULT: 7 desktop capture checks passed; generated fixture only; no files or network.");
                result = 0;
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"FAIL: desktop capture: {ex.GetType().Name}: {ex.Message}");
            }
            finally
            {
                cover.Close();
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
}
