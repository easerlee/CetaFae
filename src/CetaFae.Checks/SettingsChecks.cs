using System.IO;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using CetaFae.Plugins;
using CetaFae.Probe;

namespace CetaFae.Checks;

internal static class SettingsChecks
{
    internal static void Layout(Action<bool, string> check)
    {
        var catalog = PluginCatalog.Load(Path.Combine(AppContext.BaseDirectory, "plugins"));
        foreach (var size in new[] { new Size(640, 600), new Size(500, 420) })
        for (var page = 0; page < 4; page++)
        {
            var view = new GuideSettingsView(GuidePreferences.Default, catalog, false);
            view.SelectPage(page); Arrange(view, size);
            var save = view.SaveButton.TransformToAncestor(view).TransformBounds(new Rect(view.SaveButton.RenderSize));
            check(new Rect(size).Contains(save) && save.Height >= 34, "settings save remains reachable outside the scroll area");
            var scroll = view.BodyScroll;
            check(scroll.ExtentWidth <= scroll.ViewportWidth + 1, "settings page has no horizontal overflow at supported width");
            check(PresentationSource.FromVisual(view) is null, "settings layout creates no native handle");
        }
        var edit = new GuideSettingsView(GuidePreferences.Default, catalog, false);
        var privacyAction = ((Grid)edit.BodyScroll.Content).Children.OfType<Panel>()
            .SelectMany(page => page.Children.OfType<Button>()).Single(button => Equals(button.Content, "查看使用协议"));
        var consentRequests = 0; edit.CloudRequested += () => consentRequests++;
        privacyAction.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        check(consentRequests == 1, "privacy settings requests disclosure without granting permission");
        edit.SetCloud(true, GuidePreferences.Default.Options);
        check(Equals(privacyAction.Content, "撤回同意"), "withdrawal lives in privacy settings, not the everyday menu");
        privacyAction.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        check(consentRequests == 2, "settings dispatches one explicit withdrawal action without a native window");
        var submits = 0; edit.SaveRequested += _ => submits++;
        edit.Endpoint.Text = "http://remote.invalid/v1";
        edit.SaveButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        check(submits == 0, "invalid settings cannot dispatch an apply operation");
        edit.Endpoint.Text = "https://example.invalid/v1";
        edit.SaveButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        check(submits == 1, "valid user save dispatches once without reading any target");
    }

    internal static async Task RunAsync(Action<bool, string> check)
    {
        var original = GuidePreferences.Default.Validate();
        check(original.SameAs(GuidePreferences.Parse(original.Serialize())), "non-secret settings round trip without collection authority");
        foreach (var invalid in new[] { "http://example.invalid/v1", "https://user:pass@example.invalid/v1", "https://example.invalid/v1?key=generated", "https://example.invalid/#x" })
        {
            var rejected = false;
            try { (original with { BaseUrl = invalid }).Validate(); } catch (ArgumentException) { rejected = true; }
            check(rejected, "settings reject unsafe receiver forms before persistence");
        }
        foreach (var json in new[] {
            Encoding.UTF8.GetString(original.Serialize()).Replace("\"version\":1", "\"version\":1,\"version\":1"),
            Encoding.UTF8.GetString(original.Serialize()).Replace("\"version\":1", "\"version\":1,\"key\":\"generated\""),
            Encoding.UTF8.GetString(original.Serialize()).Replace("\"version\":1", "\"version\":2") })
        {
            var rejected = false;
            try { GuidePreferences.Parse(Encoding.UTF8.GetBytes(json)); } catch (InvalidDataException) { rejected = true; }
            check(rejected, "duplicate, unknown and versioned settings cannot silently replace reviewed configuration");
        }
        var directory = Path.GetFullPath(Path.Combine(".artifacts", "checks", "settings-" + Guid.NewGuid().ToString("N")));
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, "settings.json");
        try
        {
            var store = new GuidePreferencesStore(path);
            check(store.Read().Status == PreferencesStatus.Missing, "missing preferences are distinguished from damaged configuration");
            check(store.Save(original) && store.Read().Status == PreferencesStatus.Loaded, "atomic preference save can be read");
            var bytes = File.ReadAllBytes(path);
            using (var locked = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read))
                check(!store.Save(original with { Model = "other" }), "failed atomic replacement does not report success");
            check(File.ReadAllBytes(path).SequenceEqual(bytes), "failed preference save preserves the previous complete file");
            check(Directory.GetFiles(directory).Length == 1, "failed save removes its owned temporary file");
            File.WriteAllText(path, "{broken}");
            check(store.Read().Status == PreferencesStatus.Invalid, "corrupt settings require a cloud-disabled recovery path");
            File.WriteAllBytes(path, new byte[17 * 1024]);
            check(store.Read().Status == PreferencesStatus.Invalid, "oversized settings are refused before parsing");
        }
        finally { Directory.Delete(directory, recursive: true); }
        var shipped = PluginCatalog.Load(Path.Combine(AppContext.BaseDirectory, "plugins"));
        var identity = shipped.IdentityProvider!;
        check((original with { DisabledPlugins = new[] { identity.Id } }).Filter(shipped).Plugins.Any(p => p.Kind == "general"),
            "disabling identity leaves generic interpretation independently available");
        check((original with { DisabledPlugins = shipped.Plugins.Select(p => p.Id).ToArray() }).Filter(shipped).Plugins.Count == 0,
            "all shipped plugins can be disabled without a privileged fallback");
        check((original with { Model = "another-model" }).RequiresNewConsent(original) &&
            (original with { IncludeImage = false }).RequiresNewConsent(original) &&
            (original with { BaseUrl = "https://example.invalid/v1" }).RequiresNewConsent(original),
            "every changed disclosure requires explicit grant reset, not just a different URL");
        check(!(original with { DisabledPlugins = new[] { identity.Id } }).RequiresNewConsent(original),
            "plugin switches cannot mint or broaden cloud authority");
        using (var diagnostics = JsonDocument.Parse(GuideDiagnostics.Create(shipped, original.Filter(shipped), false, ConnectionStatus.Authentication)))
        {
            var fields = diagnostics.RootElement.EnumerateObject().Select(p => p.Name).Order().ToArray();
            check(fields.SequenceEqual(new[] { "cloudEnabled", "connection", "plugins", "productVersion", "runtimeVersion", "schemaVersion" }.Order()),
                "diagnostics contains only fixed metadata, no receiver/model/key/path/log or conversation fields");
            check(diagnostics.RootElement.GetProperty("plugins").EnumerateArray().All(p =>
                    p.EnumerateObject().Select(v => v.Name).Order().SequenceEqual(new[] { "enabled", "id", "version" })),
                "exported plugin records cannot include application match rules or user content");
        }
        foreach (var status in new[] { HttpStatusCode.Unauthorized, HttpStatusCode.PaymentRequired, HttpStatusCode.TooManyRequests,
                     HttpStatusCode.InternalServerError, HttpStatusCode.Redirect })
        {
            var result = await GuideConnection.TestAsync(original.Options, CancellationToken.None, new Handler(status, "{}"));
            check(result == status switch { HttpStatusCode.Unauthorized => ConnectionStatus.Authentication,
                HttpStatusCode.PaymentRequired => ConnectionStatus.Quota, HttpStatusCode.TooManyRequests => ConnectionStatus.Limited,
                HttpStatusCode.InternalServerError => ConnectionStatus.Unavailable, _ => ConnectionStatus.InvalidResponse },
                "connection diagnostics classify fixed HTTP status without echoing provider content");
        }
        foreach (var (body, expected) in new[] {
            ("{\"data\":[{\"id\":\"deepseek-v4.1-flash\"}]}", ConnectionStatus.Reachable),
            ("{\"data\":[{\"id\":\"other\"}]}", ConnectionStatus.ModelMissing),
            ("{bad}", ConnectionStatus.InvalidResponse), (new string('x', 33 * 1024), ConnectionStatus.InvalidResponse) })
            check(await GuideConnection.TestAsync(original.Options, CancellationToken.None, new Handler(HttpStatusCode.OK, body)) == expected,
                "model-list diagnosis is bounded and does not claim inference support");
        using var cancelled = new CancellationTokenSource(); cancelled.Cancel();
        var unused = new Handler(HttpStatusCode.OK, "{}");
        check(await GuideConnection.TestAsync(original.Options, cancelled.Token, unused) == ConnectionStatus.Cancelled && unused.Requests == 0,
            "cancelled connection test sends no new request");
    }

    sealed class Handler(HttpStatusCode code, string body) : HttpMessageHandler
    {
        internal int Requests;
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests++;
            if (request.Method != HttpMethod.Get || request.RequestUri!.AbsolutePath != "/v1/models" || request.Content is not null)
                throw new InvalidDataException("unexpected_diagnostic_request");
            return Task.FromResult(new HttpResponseMessage(code) { Content = new StringContent(body) });
        }
    }
    static void Arrange(FrameworkElement view, Size size)
    { view.Measure(size); view.Arrange(new Rect(size)); view.UpdateLayout(); }

    internal static void Render()
    {
        var catalog = PluginCatalog.Load(Path.Combine(AppContext.BaseDirectory, "plugins"));
        RenderBoard(catalog, new Size(640, 600), "settings");
        RenderBoard(catalog, new Size(500, 420), "settings-narrow");
        Console.WriteLine("Rendered four production settings pages offscreen; no native window, screen capture or network.");
    }
    static void RenderBoard(PluginCatalog catalog, Size size, string name)
    {
        var width = (int)size.Width * 2; var height = (int)size.Height * 2;
        var board = new DrawingVisual();
        using (var draw = board.RenderOpen())
        {
            draw.DrawRectangle(GuideTheme.Surface, null, new Rect(0, 0, width, height));
            for (var page = 0; page < 4; page++)
            {
                var view = new GuideSettingsView(GuidePreferences.Default, catalog, false, page == 0);
                view.SelectPage(page); Arrange(view, size);
                var image = new RenderTargetBitmap((int)size.Width, (int)size.Height, 96, 96, PixelFormats.Pbgra32); image.Render(view);
                draw.DrawImage(image, new Rect(new Point(page % 2 * size.Width, page / 2 * size.Height), size));
            }
        }
        var clean = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32); clean.Render(board);
        Save(clean, name + "-clean.png");
        var grid = new DrawingVisual();
        using (var draw = grid.RenderOpen())
        {
            draw.DrawImage(clean, new Rect(0, 0, width, height));
            var pen = new Pen(new SolidColorBrush(Color.FromArgb(45, 40, 100, 155)), .5);
            for (var x = 0; x < width; x += 20) draw.DrawLine(pen, new Point(x, 0), new Point(x, height));
            for (var y = 0; y < height; y += 20) draw.DrawLine(pen, new Point(0, y), new Point(width, y));
        }
        var overlay = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32); overlay.Render(grid);
        Save(overlay, name + "-grid.png");
    }
    static void Save(BitmapSource source, string name)
    {
        var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(source));
        Directory.CreateDirectory(".artifacts/checks"); using var file = File.Create(Path.Combine(".artifacts/checks", name)); encoder.Save(file);
    }
}
