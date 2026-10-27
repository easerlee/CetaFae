using System.Windows;
using CetaFae.Plugins;

namespace CetaFae.Probe;

public static class Program
{
    [STAThread]
    public static void Main(string[] args)
    {
        if (args.SequenceEqual(new[] { "--observe-worker" }))
        {
            // UIA runs on an MTA thread without creating a WPF application or owning a window.
            Task.Run(ObservationWorker.Run).GetAwaiter().GetResult();
            return;
        }
        ProbeLog.Open(ArgValue(args, "--log") ?? System.IO.Path.Combine(".artifacts", "logs"));
        var switches = new[] { "--guide", "--guide-preview", "--base-url", "--model", "--text-only", "--log", "--exit-after" };
        if (args.Any(a => a.StartsWith("--", StringComparison.Ordinal) && !switches.Contains(a)))
        {
            ProbeLog.Log("invalid_argument");
            Environment.ExitCode = 2;
            return;
        }
        GuideOptions options;
        try
        {
            options = GuideOptions.Create(ArgValue(args, "--base-url") ?? "http://127.0.0.1:8787/v1",
                ArgValue(args, "--model") ?? "deepseek-v4.1-flash", !args.Contains("--text-only"));
        }
        catch (ArgumentException)
        {
            ProbeLog.Log("guide_configuration_invalid");
            Environment.ExitCode = 2;
            return;
        }

        PluginCatalog plugins;
        try { plugins = PluginCatalog.Load(System.IO.Path.Combine(AppContext.BaseDirectory, "plugins")); }
        catch (Exception ex) when (ex is System.IO.IOException or System.IO.InvalidDataException or UnauthorizedAccessException or
                                      System.Text.Json.JsonException or ArgumentException)
        {
            ProbeLog.Log("plugin_catalog_invalid");
            Environment.ExitCode = 2;
            return;
        }
        var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        var ball = new BallWindow();
        ball.Show();
        var session = new GuideSession(ball, options, args.Contains("--guide-preview"), plugins);
        app.Exit += (_, _) => { session.Dispose(); ball.Cleanup(); };
        if (int.TryParse(ArgValue(args, "--exit-after"), out var exitAfter) && exitAfter > 0)
            app.Dispatcher.InvokeAsync(async () =>
            {
                await Task.Delay(exitAfter);
                ProbeLog.Log("exit_timer");
                app.Shutdown();
            });
        app.Run();
    }

    static string? ArgValue(string[] args, string name)
    {
        for (var i = 0; i < args.Length - 1; i++)
            if (string.Equals(args[i], name, StringComparison.OrdinalIgnoreCase)) return args[i + 1];
        return null;
    }
}
