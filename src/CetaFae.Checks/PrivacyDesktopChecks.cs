using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Interop;
using CetaFae.Plugins;
using CetaFae.Probe;

namespace CetaFae.Checks;

/// <summary>Opt-in native coverage using only this process's generated window; no model requests.</summary>
static class PrivacyDesktopChecks
{
    public static int Run()
    {
        var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        var action = new Button { Content = "Generated ordinary action", Height = 40 };
        var label = new TextBlock { Text = "Generated privacy fixture. No real credentials or network requests.",
            TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 8, 0, 8) };
        var password = new PasswordBox { Password = "Generated fixture text", Height = 30, Visibility = Visibility.Collapsed };
        var panel = new StackPanel { Margin = new Thickness(20) };
        panel.Children.Add(label);
        panel.Children.Add(action);
        panel.Children.Add(password);
        var window = new Window { Title = "CetaFae generated privacy fixture", Width = 480, Height = 250,
            Content = panel, WindowStartupLocation = WindowStartupLocation.CenterScreen, Topmost = true };
        var plugin = new GuidePlugin(1, "generated.privacy", "0.1.0", "Generated privacy fixture",
            [new PluginTarget([Process.GetCurrentProcess().ProcessName], [])], "control", ["generated.privacy"], []);
        var result = 1;
        var running = false;
        window.Deactivated += (_, _) => Console.WriteLine("DIAGNOSTIC: owned fixture deactivated");
        window.Loaded += async (_, _) =>
        {
            if (running) return;
            running = true;
            var handle = new WindowInteropHelper(window).Handle;
            using var deadline = new CancellationTokenSource(TimeSpan.FromMinutes(3));
            try
            {
                await Task.Delay(180, deadline.Token);
                var activated = Native.SetForegroundWindow(handle);
                Console.WriteLine("DIAGNOSTIC: owned fixture activation accepted=" + activated);
                if (!await GuideSession.WaitForForegroundAsync(handle, Native.GetForegroundWindow, deadline.Token))
                {
                    label.Text = "Please click this generated test window once. Checks will run and close it automatically.";
                    Console.WriteLine("WAIT: click the owned generated fixture once to start the checks");
                    var focus = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                    void OnActivated(object? sender, EventArgs args) => focus.TrySetResult();
                    window.Activated += OnActivated;
                    try
                    {
                        if (Native.GetForegroundWindow() == handle) focus.TrySetResult();
                        await focus.Task.WaitAsync(TimeSpan.FromMinutes(2), deadline.Token);
                    }
                    finally { window.Activated -= OnActivated; }
                    label.Text = "Generated privacy fixture. No real credentials or network requests.";
                    await Task.Delay(180, deadline.Token);
                }
                async Task ReadOrdinary(string name)
                {
                    var initialFrame = GuideObservation.WindowRect(handle);
                    GuideObservation observation;
                    try { observation = await ObservationWorker.ReadAsync(handle, true, plugin, deadline.Token); }
                    catch
                    {
                        Console.WriteLine("DIAGNOSTIC: owned fixture foreground=" + (Native.GetForegroundWindow() == handle) +
                            " visible=" + Native.IsWindowVisible(handle) + " minimized=" + Native.IsIconic(handle) +
                            " frameUnchanged=" + (GuideObservation.WindowRect(handle) == initialFrame));
                        throw;
                    }
                    if (observation.Png is not { Length: > 0 } || !observation.Candidates.Any(c => c.Name == "Generated ordinary action"))
                        throw new InvalidOperationException("fixture_observation_incomplete");
                    Console.WriteLine("PASS: " + name);
                }
                async Task Refuse(string name)
                {
                    var refused = false;
                    try { await ObservationWorker.ReadAsync(handle, true, plugin, deadline.Token); }
                    catch (InvalidDataException ex) when (ex.Message == "sensitive_content") { refused = true; }
                    if (!refused) throw new InvalidOperationException("fixture_privacy_refusal_missing");
                    Console.WriteLine("PASS: " + name);
                }
                await ReadOrdinary("native ordinary controls pass privacy preflight and target-only capture");
                password.Visibility = Visibility.Visible;
                await Task.Delay(180, deadline.Token);
                await Refuse("native visible password element refuses the complete observation");
                password.IsEnabled = false;
                await Task.Delay(180, deadline.Token);
                await Refuse("native disabled visible password element still refuses the observation");
                password.Visibility = Visibility.Collapsed;
                await Task.Delay(180, deadline.Token);
                await ReadOrdinary("an offscreen password element does not prevent ordinary recognition");
                label.Text = "api_key=generated-value";
                await Task.Delay(180, deadline.Token);
                await Refuse("native noninteractive visible credential text refuses the observation");
                label.Text = "Generated ordinary page restored.";
                await Task.Delay(180, deadline.Token);
                await ReadOrdinary("recognition recovers after generated sensitive content is removed");
                label.Text = "API key";
                await Task.Delay(180, deadline.Token);
                await Refuse("a native credential field label refuses pixels without reading a value pattern");
                Console.WriteLine("RESULT: 7 native privacy checks passed. Owned generated window only; no model, saved image or external target.");
                result = 0;
            }
            catch (Exception ex)
            {
                // Never echo provider exception text, even in an opt-in native fixture.
                Console.Error.WriteLine("FAIL: native privacy fixture: " +
                    (ex is InvalidDataException ? ObservationWorker.SafeError(ex.Message) : ex.GetType().Name));
                if (ex is InvalidDataException { Message: "privacy_unavailable" })
                {
                    try { await Task.Run(() => DescribeMetadata(handle)).WaitAsync(TimeSpan.FromSeconds(2)); }
                    catch { Console.WriteLine("DIAGNOSTIC: generated metadata inspection unavailable"); }
                }
            }
            finally { window.Close(); app.Shutdown(); }
        };
        window.Show();
        app.Run();
        return result;
    }

    static void DescribeMetadata(IntPtr handle)
    {
        var root = AutomationElement.FromHandle(handle);
        var cache = new CacheRequest { TreeFilter = Automation.RawViewCondition };
        cache.Add(AutomationElement.IsOffscreenProperty);
        cache.Add(AutomationElement.IsPasswordProperty);
        cache.Add(AutomationElement.ControlTypeProperty);
        AutomationElementCollection elements;
        using (cache.Activate()) elements = root.FindAll(TreeScope.Element | TreeScope.Descendants, System.Windows.Automation.Condition.TrueCondition);
        foreach (var element in elements.Cast<AutomationElement>().Take(24))
        {
            static string Flag(object value) => value is bool flag ? flag.ToString() : "unsupported";
            Console.WriteLine("DIAGNOSTIC: " + element.Cached.ControlType.ProgrammaticName +
                " offscreen=" + Flag(element.GetCachedPropertyValue(AutomationElement.IsOffscreenProperty, true)) +
                " password=" + Flag(element.GetCachedPropertyValue(AutomationElement.IsPasswordProperty, true)));
        }
    }
}
