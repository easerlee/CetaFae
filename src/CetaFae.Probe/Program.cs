using System.Windows;
using System.Reflection;
using CetaFae.Plugins;

namespace CetaFae.Probe;

public static class Program
{
    internal static string VersionLabel => typeof(Program).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? "unknown";
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
        ProbeLog.Log("guide_runtime", ("buildId", typeof(Program).Module.ModuleVersionId.ToString("N")));
        var switches = new[] { "--guide", "--guide-preview", "--base-url", "--model", "--text-only", "--log", "--exit-after" };
        if (args.Any(a => a.StartsWith("--", StringComparison.Ordinal) && !switches.Contains(a)))
        {
            ProbeLog.Log("invalid_argument");
            Environment.ExitCode = 2;
            return;
        }
        var store = new GuidePreferencesStore();
        var stored = store.Read();
        GuidePreferences preferences;
        try
        {
            preferences = (stored.Value with { BaseUrl = ArgValue(args, "--base-url") ?? stored.Value.BaseUrl,
                Model = ArgValue(args, "--model") ?? stored.Value.Model,
                IncludeImage = !args.Contains("--text-only") && stored.Value.IncludeImage }).Validate();
        }
        catch (ArgumentException)
        {
            ProbeLog.Log("guide_configuration_invalid");
            Environment.ExitCode = 2;
            return;
        }

        PluginCatalog shipped;
        try { shipped = PluginCatalog.Load(System.IO.Path.Combine(AppContext.BaseDirectory, "plugins")); }
        catch (Exception ex) when (ex is System.IO.IOException or System.IO.InvalidDataException or UnauthorizedAccessException or
                                      System.Text.Json.JsonException or ArgumentException)
        {
            ProbeLog.Log("plugin_catalog_invalid");
            Environment.ExitCode = 2;
            return;
        }
        System.Windows.Media.RenderOptions.ProcessRenderMode = System.Windows.Interop.RenderMode.SoftwareOnly;
        var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        var ball = new BallWindow();
        ball.Show();
        var preview = args.Contains("--guide-preview");
        var invalidPreferences = stored.Status == PreferencesStatus.Invalid;
        if (invalidPreferences && !preview) new CloudConsentStore(preferences.Options).Forget();
        var plugins = preferences.Filter(shipped);
        GuideSettingsWindow? settings = null;
        GuideSession session = null!;
        void OpenSettings()
        {
            session.EndForSettings();
            if (settings is not null) { settings.Activate(); return; }
            settings = new GuideSettingsWindow(preferences, shipped, () => session.CloudEnabled, Apply,
                () => session.ManageCloud(), status => GuideDiagnostics.Create(shipped, plugins, session.CloudEnabled, status), invalidPreferences);
            settings.Closed += (_, _) => settings = null;
            settings.Show();
        }
        string? Apply(GuidePreferences next)
        {
            if (preview) return "静态预览不会更改配置。";
            next = next.Validate();
            var nextPlugins = next.Filter(shipped);
            var revoke = invalidPreferences || next.RequiresNewConsent(preferences);
            if (revoke)
            {
                // Forget before saving a new recipient, including changes back to a previously granted one.
                session.RevokeForConfiguration();
                if (!new CloudConsentStore(preferences.Options).Forget())
                    return "云端已在本次运行中关闭，但无法清除旧授权记录。新配置尚未保存；请修复用户配置目录权限后重试。";
            }
            if (!store.Save(next)) return revoke
                ? "无法保存设置，仍使用旧连接配置，云端已关闭。请检查本机用户配置目录权限后重试。"
                : "无法保存设置，旧配置保持有效。请检查本机用户配置目录权限后重试。";
            if (!preferences.SameAs(next) || invalidPreferences)
            {
                session.EndForSettings();
                session.Dispose();
                preferences = next; plugins = nextPlugins;
                // A changed disclosure cannot reuse the previous grant; the store checks its exact hash.
                session = new GuideSession(ball, next.Options, preview, plugins, OpenSettings, restoreConsent: !invalidPreferences);
            }
            invalidPreferences = false;
            return null;
        }
        session = new GuideSession(ball, preferences.Options, preview, plugins, OpenSettings, restoreConsent: !invalidPreferences);
        if (invalidPreferences) ball.Dispatcher.InvokeAsync(session.ShowConfigurationWarning);
        app.Exit += (_, _) => { settings?.Close(); session.Dispose(); ball.Cleanup(); ProbeLog.Close(); };
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
