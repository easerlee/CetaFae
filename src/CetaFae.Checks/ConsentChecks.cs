using System.IO;
using System.Windows;
using System.Windows.Controls;
using CetaFae.Probe;

namespace CetaFae.Checks;

static class ConsentChecks
{
    internal static void Run(Action<bool, string> check)
    {
        var options = GuideOptions.Create("http://127.0.0.1:8787/v1", "generated");
        var directory = Path.GetFullPath(Path.Combine(".artifacts", "checks", "consent-" + Guid.NewGuid().ToString("N")));
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, "consent.dat");
        try
        {
            var store = new CloudConsentStore(options, path);
            check(!store.Read().Granted, "missing consent never grants cloud permission");
            check(store.Save(true), "explicit consent can save a current-user protected grant");
            var restored = new CloudConsentStore(options, path).Read();
            check(restored.Granted && restored.Enabled, "same-scope startup restores an explicitly saved enabled grant");
            check(!File.ReadAllText(path).Contains("generated"), "persisted consent contains no plaintext model or session data");
            foreach (var changed in new[] { options with { Model = "other" }, options with { IncludeImage = false },
                         options with { BaseUrl = new Uri("https://example.invalid/v1/") } })
                check(!new CloudConsentStore(changed, path).Read().Granted, "changed recipient/model/scope cannot reuse consent");
            check(store.Save(false) && new CloudConsentStore(options, path).Read() is { Granted: true, Enabled: false },
                "legacy disabled records remain disabled and never silently upgrade to permission");
            check(store.Save(true), "explicit fresh confirmation can restore a generated grant");
            using (var locked = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read))
                check(!store.Revoke(), "locked withdrawal persistence reports failure rather than pretending the grant was removed");
            check(store.Revoke() && !store.Read().Granted && !File.Exists(path),
                "settings withdrawal removes the old grant; restarting requires fresh personal confirmation");
            check(store.Save(true) && store.Read().Enabled && store.Revoke() && !store.Read().Enabled,
                "reconfirmation then withdrawal survives the generated store cycle without a routine toggle");
            check(store.Save(false), "fresh personal consent can be recorded after configuration reset");
            var bytes = File.ReadAllBytes(path); bytes[^1] ^= 0xff; File.WriteAllBytes(path, bytes);
            check(!store.Read().Granted, "tampered protected consent fails closed");
            File.WriteAllText(path, "{\"enabled\":true}");
            check(!store.Read().Granted, "a handwritten enabled flag cannot grant consent");
            File.WriteAllBytes(path, new byte[20_000]);
            check(!store.Read().Granted, "oversized consent fails closed before decrypting");
            File.Delete(path); Directory.CreateDirectory(path);
            check(!store.Save(true) && !store.Read().Granted, "unwritable consent cannot silently enable remembered cloud");
        }
        finally { Directory.Delete(directory, recursive: true); }
        var ball = new BallWindow();
        try
        {
            var clears = 0;
            using var menu = new GuideMenu(ball, () => clears++, registerTray: false);
            var items = menu.Menu.Items.OfType<MenuItem>().ToArray();
            check(items.Select(i => i.Header).SequenceEqual(new[] { "清除标记", "退出鲸汐" }),
                "shared tray/whale menu has no routine cloud toggle or separate consent command");
            items[0].RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
            check(clears == 1, "ordinary menu command clears once without a consent operation");
            menu.SetCloud(true);
            check(items.Select(i => i.Header).SequenceEqual(new[] { "清除标记", "退出鲸汐" }) && !menu.OwnsForeground(IntPtr.Zero),
                "remembered consent never adds a routine switch or authorizes arbitrary foreground handles");
            var requests = 0; ball.MenuRequested += () => requests++;
            ball.RaiseEvent(new System.Windows.Input.MouseButtonEventArgs(System.Windows.Input.Mouse.PrimaryDevice, 0,
                System.Windows.Input.MouseButton.Right) { RoutedEvent = System.Windows.Input.Mouse.MouseUpEvent });
            check(requests == 1, "whale right button routes to the shared management entry without desktop input");
            var settingsRequests = 0;
            using var withSettings = new GuideMenu(ball, () => { }, registerTray: false,
                settings: () => settingsRequests++);
            withSettings.Menu.Items.OfType<MenuItem>().Single(item => Equals(item.Header, "鲸汐设置"))
                .RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
            check(settingsRequests == 1, "settings is dispatched through the same tray/whale menu without an additional entry surface");
        }
        finally { ball.Close(); }
    }
}
