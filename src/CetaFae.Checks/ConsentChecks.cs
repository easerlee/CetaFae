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
                "disabled state survives restart without forgetting the unchanged grant");
            check(store.Forget() && !store.Read().Granted && !File.Exists(path),
                "explicit configuration reset removes the old grant so returning to a prior receiver cannot revive it");
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
            var toggles = 0; var disclosures = 0; var clears = 0;
            using var menu = new GuideMenu(ball, () => toggles++, () => disclosures++, () => clears++, registerTray: false);
            var items = menu.Menu.Items.OfType<MenuItem>().ToArray();
            check(items.Select(i => i.Header).SequenceEqual(new[] { "启用云端", "查看云端授权", "清除标记", "退出鲸汐" }),
                "shared tray/whale menu exposes one consistent set of management commands");
            foreach (var item in items.Take(3)) item.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
            check(toggles == 1 && disclosures == 1 && clears == 1, "shared commands dispatch once without opening a native menu");
            menu.SetCloud(true);
            check(Equals(items[0].Header, "关闭云端") && !menu.OwnsForeground(IntPtr.Zero),
                "menu reflects cloud state and does not authorize arbitrary foreground handles while closed");
            var requests = 0; ball.MenuRequested += () => requests++;
            ball.RaiseEvent(new System.Windows.Input.MouseButtonEventArgs(System.Windows.Input.Mouse.PrimaryDevice, 0,
                System.Windows.Input.MouseButton.Right) { RoutedEvent = System.Windows.Input.Mouse.MouseUpEvent });
            check(requests == 1, "whale right button routes to the shared management entry without desktop input");
            var settingsRequests = 0;
            using var withSettings = new GuideMenu(ball, () => { }, () => { }, () => { }, registerTray: false,
                settings: () => settingsRequests++);
            withSettings.Menu.Items.OfType<MenuItem>().Single(item => Equals(item.Header, "鲸汐设置"))
                .RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
            check(settingsRequests == 1, "settings is dispatched through the same tray/whale menu without an additional entry surface");
        }
        finally { ball.Close(); }
    }
}
