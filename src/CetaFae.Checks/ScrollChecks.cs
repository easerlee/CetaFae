using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Interop;
using System.Windows.Media;
using CetaFae.Probe;

namespace CetaFae.Checks;

static class ScrollChecks
{
    public static int Run()
    {
        var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        var action = new Button { Content = "Task action", Width = 120, Height = 40 };
        var scrollbar = new ScrollBar { Orientation = Orientation.Vertical, Minimum = 0, Maximum = 100,
            Value = 50, Width = 24, Height = 200, Visibility = Visibility.Hidden };
        var panel = new StackPanel { Orientation = Orientation.Horizontal };
        panel.Children.Add(action);
        panel.Children.Add(scrollbar);
        var window = new Window { Title = "CetaFae scroll fixture", Width = 300, Height = 260,
            Content = panel, ShowActivated = false, ShowInTaskbar = false, Topmost = true };
        var result = 1;
        var running = false;
        window.Loaded += async (_, _) =>
        {
            if (running) return;
            running = true;
            var handle = new WindowInteropHelper(window).Handle;
            try
            {
                await Task.Delay(150);
                var before = await Task.Run(() => GuideObservation.ReadCandidates(handle, CancellationToken.None));
                if (!before.Any(c => c.Name == "Task action")) throw new InvalidOperationException("fixture task is missing");
                scrollbar.Visibility = Visibility.Visible;
                scrollbar.ApplyTemplate();
                NameScrollButtons(scrollbar);
                await Task.Delay(150);
                var rawScrollButtons = await Task.Run(() =>
                {
                    var root = AutomationElement.FromHandle(handle);
                    var bar = root.FindFirst(TreeScope.Descendants,
                        new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.ScrollBar));
                    var buttons = bar?.FindAll(TreeScope.Descendants,
                        new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Button));
                    if (buttons is null) return 0;
                    return buttons.Cast<AutomationElement>().Count(b => b.Current.IsEnabled &&
                        !b.Current.IsOffscreen && !string.IsNullOrWhiteSpace(b.Current.Name));
                });
                if (rawScrollButtons == 0) throw new InvalidOperationException("fixture has no scroll buttons; not a regression test");
                var visible = await Task.Run(() => GuideObservation.ReadCandidates(handle, CancellationToken.None));
                if (visible.Any(c => c.Name.StartsWith("Scroll part", StringComparison.Ordinal)))
                    throw new InvalidOperationException("named scrollbar buttons entered task candidates");
                if (!GuideObservation.HasSameContext(before, visible))
                    throw new InvalidOperationException("scroll chrome visibility invalidates unchanged task context");
                Console.WriteLine($"PASS: {rawScrollButtons} real scrollbar buttons excluded; task context preserved");
                action.Content = "Changed task action";
                var changed = await Task.Run(() => GuideObservation.ReadCandidates(handle, CancellationToken.None));
                if (GuideObservation.HasSameContext(before, changed)) throw new InvalidOperationException("task change was ignored");
                Console.WriteLine("PASS: real content change still invalidates context");
                result = 0;
            }
            catch (Exception ex) { Console.Error.WriteLine($"FAIL: {ex.GetType().Name}: {ex.Message}"); }
            finally { window.Close(); app.Shutdown(); }
        };
        window.Show();
        app.Run();
        return result;
    }

    static void NameScrollButtons(DependencyObject parent)
    {
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(parent); i++)
        {
            var child = VisualTreeHelper.GetChild(parent, i);
            if (child is RepeatButton button)
            {
                // Standalone scroll commands otherwise disable buttons without a ScrollViewer owner.
                button.Command = null;
                button.IsEnabled = true;
                AutomationProperties.SetName(button, $"Scroll part {i}");
            }
            NameScrollButtons(child);
        }
    }
}
