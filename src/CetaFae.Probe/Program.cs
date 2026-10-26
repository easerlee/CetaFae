using System.Windows;

namespace CetaFae.Probe;

public static class Program
{
    [STAThread]
    public static void Main(string[] args)
    {
        ProbeLog.Open(ArgValue(args, "--log") ?? "logs");

        var captureTitle = ArgValue(args, "--capture");
        if (captureTitle is { Length: > 0 })
        {
            var hwnd = AutoRunner.Find(captureTitle);
            if (hwnd == IntPtr.Zero)
            {
                ProbeLog.Log("capture_abort", ("reason", "not_found"), ("title", captureTitle));
                return;
            }
            var shots = int.TryParse(ArgValue(args, "--shots"), out var s) ? s : 2;
            var interval = int.TryParse(ArgValue(args, "--interval"), out var itv) ? itv : 400;
            Capture.Shoot(hwnd, ArgValue(args, "--out") ?? "", shots, interval);
            return;
        }

        var uiaTitle = ArgValue(args, "--uia");
        var uiaHwndText = ArgValue(args, "--uia-hwnd");
        if (uiaTitle is { Length: > 0 } || uiaHwndText is { Length: > 0 })
        {
            var hwnd = uiaHwndText is { Length: > 0 }
                ? new IntPtr(Convert.ToInt64(uiaHwndText, 16))
                : AutoRunner.Find(uiaTitle!);
            if (hwnd == IntPtr.Zero)
            {
                ProbeLog.Log("uia_abort", ("reason", "not_found"), ("title", uiaTitle));
                return;
            }
            var outPath = ArgValue(args, "--dump") ?? $"logs/uia-{DateTime.Now:HHmmss}.jsonl";
            var raw = args.Any(a => string.Equals(a, "--uia-raw", StringComparison.OrdinalIgnoreCase));
            UiaProbe.Dump(hwnd, outPath, 14, 5000, raw);
            return;
        }

        var autoTitle = ArgValue(args, "--auto");
        var trackTitle = ArgValue(args, "--track");
        var trials = int.TryParse(ArgValue(args, "--trials"), out var n) ? n : 3;
        var dwell = int.TryParse(ArgValue(args, "--dwell"), out var d) ? d : 700;
        var exitAfter = int.TryParse(ArgValue(args, "--exit-after"), out var ea) ? ea : 0;

        var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        var ball = new BallWindow();
        ball.Show();
        app.Exit += (_, _) => ball.Cleanup();

        if (autoTitle is { Length: > 0 })
        {
            app.Dispatcher.InvokeAsync(async () =>
            {
                await Task.Delay(500);
                await AutoRunner.RunAsync(ball, autoTitle, trials, dwell);
                app.Shutdown();
            });
        }
        else if (trackTitle is { Length: > 0 })
        {
            app.Dispatcher.InvokeAsync(async () =>
            {
                await Task.Delay(400);
                var target = AutoRunner.Find(trackTitle);
                if (target == IntPtr.Zero)
                {
                    ProbeLog.Log("track_abort", ("reason", "not_found"), ("title", trackTitle));
                }
                else
                {
                    ball.TrackWindow(target);
                    await Task.Delay(300);
                    await AutoRunner.FollowOnly(ball, target);
                }
                app.Shutdown();
            });
        }
        else if (exitAfter > 0)
        {
            app.Dispatcher.InvokeAsync(async () =>
            {
                await Task.Delay(exitAfter);
                ProbeLog.Log("exit_timer");
                app.Shutdown();
            });
        }

        app.Run();
    }

    static string? ArgValue(string[] args, string name)
    {
        for (var i = 0; i < args.Length - 1; i++)
            if (string.Equals(args[i], name, StringComparison.OrdinalIgnoreCase))
                return args[i + 1];
        return null;
    }
}
