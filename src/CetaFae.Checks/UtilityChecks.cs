using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using CetaFae.Plugins;
using CetaFae.Probe;

namespace CetaFae.Checks;

internal static class UtilityChecks
{
    internal static async Task RunAsync(Action<bool, string> check, bool render = false)
    {
        var provider = GuideFeature.DefaultCatalog().UtilityProvider!;
        var menu = await GuideSession.UtilityMenuAsync(provider);
        check(menu.Count == 5 && menu.Select(m => m.Id).Distinct().Count() == 5, "utility actions come from the enabled TS provider");
        check(provider.Matches("generated-editor", "Generated data"), "generic utility capture accepts a selected window without application adapters");
        check((await GuideSession.UtilityMenuAsync(provider, text: true)).Select(a => a.Id)
            .SequenceEqual(new[] { "translate", "summarize", "rewrite", "explain" }), "text action labels and processing policy come from the TS provider");
        var receiver = new TextReceiver();
        using (var model = new GuideModel(GuideOptions.Create("http://127.0.0.1:8787/v1", "generated-utility"), receiver))
        {
            check(await model.ProcessTextAsync(provider, new string('\\', 15900), "summarize", default) == "整理后的测试文字。" &&
                receiver.Body?.Contains("image_url", StringComparison.Ordinal) == false,
                "explicit text processing sends bounded escaped text without window images through controlled transport");
            try { await model.ProcessTextAsync(provider, "容量 16 GB", "translate", default); check(false, "translation facts preserved"); }
            catch (InvalidDataException ex) when (ex.Message == "translation_literals")
            { check(true, "text translation rejects missing numeric facts and leaves the original for retry"); }
            receiver.Reply = "Capacity 16 GB";
            check(await model.ProcessTextAsync(provider, "容量 16 GB", "translate", default) == "Capacity 16 GB",
                "text translation admits the generated reply with preserved numeric facts");
        }
        var catalog = GuideFeature.DefaultCatalog();
        check((GuidePreferences.Default with { DisabledPlugins = [provider.Id] }).Filter(catalog).UtilityProvider is null,
            "disabled utility provider has no privileged fallback");
        foreach (var q in new[] { "检查内存", "我想了解这个软件", "截图" })
        {
            using var doc = JsonDocument.Parse(await GuideFeature.InvokeAsync(provider, "utility-intent", new { question = q }));
            check(doc.RootElement.GetProperty("action").ValueKind == (q.Contains("软件", StringComparison.Ordinal) ? JsonValueKind.Null : JsonValueKind.String),
                "utility routing does not hijack an unrelated software question");
        }
        var config = new MonitorSettings(true);
        var saved = GuidePreferences.Default with { Monitoring = config };
        check(GuidePreferences.Parse(saved.Serialize()).Monitor == config && !GuidePreferences.Default.Monitor.Enabled,
            "monitoring persists only through explicit settings and defaults off");
        foreach (var bad in new[] { config with { Low = 86 }, config with { High = 100 }, config with { SustainedSeconds = 0 }, config with { CooldownMinutes = 0 } })
        {
            try { bad.Validate(); check(false, "invalid monitor parameters rejected"); }
            catch (ArgumentException) { check(true, "invalid monitor parameters rejected"); }
        }
        var v2 = JsonSerializer.SerializeToUtf8Bytes(new { version = 2, baseUrl = GuidePreferences.Default.BaseUrl,
            model = GuidePreferences.Default.Model, includeImage = true, disabledPlugins = Array.Empty<string>(), installedPlugins = Array.Empty<object>() });
        check(!GuidePreferences.Parse(v2).Monitor.Enabled, "v2 preferences never enable a new background monitor on migration");
        var state = new MemoryPolicyState(); var now = 100000d; var notices = 0;
        async Task Sample(double percent, bool suppressed = false, double ignore = 0)
        {
            var result = await MemoryMonitor.EvaluateAsync(provider, config, state, now, percent, suppressed, ignore);
            state = result.State; if (result.Remind) notices++; now += 10000;
        }
        for (var i = 0; i < 7; i++) await Sample(85);
        check(notices == 0, "threshold equality is not sustained excess");
        for (var i = 0; i < 6; i++) await Sample(90);
        check(notices == 0, "short memory pressure does not notify");
        await Sample(90);
        check(notices == 1, "continuous excess triggers one reminder at the duration boundary");
        for (var i = 0; i < 7; i++) await Sample(90);
        check(notices == 1, "one high-pressure episode does not repeat reminders");
        for (var i = 0; i < 7; i++) await Sample(70);
        check(state.Armed, "sustained recovery rearms the next episode");
        for (var i = 0; i < 7; i++) await Sample(90);
        check(notices == 1, "cooldown prevents a newly rearmed early reminder");
        now += 1800000;
        await Sample(90);
        check(state.HighSince == now - 10000 && notices == 1, "sleep gap starts a new continuous-pressure interval");
        for (var i = 0; i < 7; i++) await Sample(90, suppressed: true);
        check(notices == 1, "fullscreen or active help suppresses presentation without consuming the episode");
        await Sample(90, ignore: now + 7200000);
        check(notices == 1, "explicit ignore suppresses an otherwise ready notice");
        await Sample(90);
        check(notices == 2, "still-high pressure can notify once after suppression ends");
        var disabled = await MemoryMonitor.EvaluateAsync(provider, new(), state, now, 99, false, 0);
        check(!disabled.Remind && disabled.State.LastSample is null, "disable clears pending policy state");
        var pixels = new byte[420 * 180 * 4];
        for (var i = 0; i < pixels.Length; i += 4) { pixels[i] = 255; pixels[i + 1] = 255; pixels[i + 2] = 255; pixels[i + 3] = 255; }
        var bitmap = BitmapSource.Create(420, 180, 96, 96, PixelFormats.Bgra32, null, pixels, 420 * 4);
        var editor = new UtilityImageView(bitmap);
        editor.AddMark("mask", new Point(20, 20), new Point(70, 80));
        editor.AddMark("rectangle", new Point(100, 20), new Point(150, 80));
        editor.AddMark("arrow", new Point(200, 20), new Point(260, 80));
        var exported = editor.Export(); var sample = new byte[4]; exported.CopyPixels(new Int32Rect(40, 40, 1, 1), sample, 4, 0);
        check(sample[0] == 0 && sample[1] == 0 && sample[2] == 0 && sample[3] == 255,
            "opaque redaction is burned into exported pixels rather than a removable overlay");
        using var encoded = new MemoryStream(); var png = new PngBitmapEncoder(); png.Frames.Add(BitmapFrame.Create(exported)); png.Save(encoded);
        check(UtilityUi.Decode(encoded.ToArray()).PixelWidth == 420, "annotated exported PNG independently decodes at the expected dimensions");
        var textImage = new DrawingVisual();
        using (var c = textImage.RenderOpen())
        {
            c.DrawRectangle(Brushes.White, null, new Rect(0, 0, 640, 160));
            c.DrawText(new FormattedText("HELLO SAMPLE\nHELLO SAMPLE", CultureInfo.GetCultureInfo("en-US"), FlowDirection.LeftToRight,
                new Typeface("Arial"), 30, Brushes.Black, 1), new Point(20, 20));
        }
        var textBitmap = new RenderTargetBitmap(640, 160, 96, 96, PixelFormats.Pbgra32); textBitmap.Render(textImage);
        using var textBytes = new MemoryStream(); var textPng = new PngBitmapEncoder(); textPng.Frames.Add(BitmapFrame.Create(textBitmap)); textPng.Save(textBytes);
        var extracted = await GuideTextRegions.ExtractAsync(textBytes.ToArray(), default);
        check(extracted.Split('\n').Count(line => line.Contains("HELLO", StringComparison.OrdinalIgnoreCase)) == 2,
            "local OCR extraction preserves repeated lines in reading order");
        encoded.Position = 0;
        var compressed = ImageCompression.Encode(encoded, 65536, default);
        check(compressed.Bytes.Length <= 65536 && UtilityUi.Decode(compressed.Bytes).PixelHeight == compressed.Height,
            "compression enforces size and validates actual JPEG dimensions");
        using var oriented = new MemoryStream();
        var metadata = new BitmapMetadata("jpg"); metadata.SetQuery("/app1/ifd/{ushort=274}", (ushort)6);
        var jpeg = new JpegBitmapEncoder(); jpeg.Frames.Add(BitmapFrame.Create(bitmap, null, metadata, null)); jpeg.Save(oriented); oriented.Position = 0;
        var rotated = ImageCompression.Encode(oriented, 65536, default);
        check(rotated.Width == 180 && rotated.Height == 420, "compression preserves JPEG displayed orientation before removing metadata");
        using (var stop = new CancellationTokenSource())
        {
            stop.Cancel(); encoded.Position = 0;
            try { ImageCompression.Encode(encoded, 65536, stop.Token); check(false, "cancelled compression stops"); }
            catch (OperationCanceledException) { check(true, "cancelled compression stops before output admission"); }
        }
        var root = Path.GetFullPath(Path.Combine(".artifacts", "checks", "daily-" + Guid.NewGuid().ToString("N")));
        Directory.CreateDirectory(root);
        var noise = new byte[640 * 480 * 3]; new Random(7).NextBytes(noise);
        var photo = BitmapSource.Create(640, 480, 96, 96, PixelFormats.Bgr24, null, noise, 640 * 3);
        var sourceFile = Path.Combine(root, "generated-photo.png");
        using (var stream = File.Create(sourceFile)) { var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(photo)); encoder.Save(stream); }
        var originalHash = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(sourceFile)));
        var recordStore = new TaskRecordStore(Path.Combine(root, "records")); var decisions = 0;
        var compressTask = new TaskCoordinator(recordStore, new((_, _, _) => { decisions++; return Task.FromResult(true); },
            (_, _) => Task.FromResult<string?>(sourceFile), _ => { }), catalog);
        var compression = await compressTask.StepAsync(new("file.compress", MaxBytes: 65536), catalog.ChatProvider!, default);
        var outputFile = Directory.GetFiles(root, "*.jpg").Single();
        check(compression.Status == "verified" && decisions == 1 && new FileInfo(outputFile).Length <= 65536 &&
            Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(sourceFile))) == originalHash && compression.Receipt?.RecoveryId is not null,
            "real compression task previews once, verifies a bounded new copy, preserves source and retains recovery evidence");
        var rejectedCompression = false;
        try { TaskProposal.Parse(JsonSerializer.SerializeToElement(new { operation = "file.compress", maxBytes = 1 })); }
        catch (InvalidDataException) { rejectedCompression = true; }
        check(rejectedCompression, "compression task cannot authorize an out-of-range target");
        var dispatched = "";
        var utilityTask = new TaskCoordinator(recordStore, new((_, _, _) => Task.FromResult(true), (_, _) => Task.FromResult<string?>(null), _ => { }),
            catalog, action => dispatched = action);
        var toolResult = await utilityTask.StepAsync(new("utility.run", Action: "memory.inspect"), catalog.ChatProvider!, default);
        check(dispatched == "memory.inspect" && toolResult.Status == "observed", "conversation task dispatches the enabled generic utility without granting process mutation");
        dispatched = "";
        var noUtility = new TaskCoordinator(recordStore, new((_, _, _) => Task.FromResult(true), (_, _) => Task.FromResult<string?>(null), _ => { }),
            new(catalog.Plugins.Where(p => p.Kind != "utility")), action => dispatched = action);
        check((await noUtility.StepAsync(new("utility.run", Action: "memory.inspect"), catalog.ChatProvider!, default)).Status == "denied" && dispatched.Length == 0,
            "conversation cannot silently restore a disabled utility provider");
        var data = Encoding.UTF8.GetBytes("generated dependency, never executable");
        var hash = Convert.ToHexString(SHA256.HashData(data));
        async Task Fetch(HttpMessageHandler handler, string expectedHash, string name)
        {
            using var http = new HttpClient(handler);
            await ToolDownload.DownloadAsync(http, new Uri("https://github.com/generated/archive"), Path.Combine(root, name), data.Length, expectedHash, null, default);
        }
        await Fetch(new Handler(_ => new(HttpStatusCode.OK) { Content = new ByteArrayContent(data) }), hash, "ok");
        check(File.ReadAllBytes(Path.Combine(root, "ok")).SequenceEqual(data), "download stream validates exact generated bytes without model transport");
        async Task Reject(Func<Task> action, string label)
        { try { await action(); check(false, label); } catch (Exception ex) when (ex is InvalidDataException or HttpRequestException) { check(true, label); } }
        await Reject(() => Fetch(new Handler(_ => new(HttpStatusCode.OK) { Content = new ByteArrayContent(data) }), new string('0', 64), "bad"),
            "substituted dependency fails fixed hash verification");
        await Reject(() => Fetch(new Handler(_ => { var r = new HttpResponseMessage(HttpStatusCode.Redirect); r.Headers.Location = new Uri("http://127.0.0.1/private"); return r; }), hash, "redirect"),
            "dependency redirect cannot reach loopback or downgrade HTTPS");
        await Reject(() => Fetch(new Handler(_ => new(HttpStatusCode.OK) { Content = new ByteArrayContent([1]) }), hash, "truncated"),
            "truncated dependency is rejected before installation");
        await Reject(() => Fetch(new Handler(_ => new(HttpStatusCode.Redirect) { Headers = { Location = new Uri("https://github.com/again") } }), hash, "loop"),
            "redirect loops stop at the host limit");
        using (var stopped = new CancellationTokenSource())
        {
            stopped.Cancel(); using var http = new HttpClient(new Handler(_ => new(HttpStatusCode.OK) { Content = new ByteArrayContent(data) }));
            try { await ToolDownload.DownloadAsync(http, new Uri("https://github.com/generated/cancel"), Path.Combine(root, "cancel"), data.Length, hash, null, stopped.Token);
                check(false, "cancelled download stops"); }
            catch (OperationCanceledException) { check(!File.Exists(Path.Combine(root, "cancel")), "cancelled download cannot admit a partial component"); }
        }
        check(!ToolDownload.AllowedUri(new Uri("https://github.com.evil.invalid/x")) && !ToolDownload.AllowedUri(new Uri("https://user@github.com/x")),
            "download allowlist rejects lookalike authorities and user info");
        var scope = new PlatformScope { Operations = ["process.trim"], ExpiresAt = DateTimeOffset.UtcNow.AddMinutes(1) };
        var denied = await new PlatformSession(scope, Path.Combine(root, "journal")).ExecuteAsync(TaskCoordinator.Call("process.trim", new { processId = Environment.ProcessId }));
        check(denied.Status == "denied" && denied.Code == "resource_denied", "working-set release never gets ambient process authority");
        foreach (var size in new[] { new Size(800, 580), new Size(500, 420) })
        {
            editor.Measure(size); editor.Arrange(new Rect(size)); editor.UpdateLayout();
            check(editor.ActualWidth == size.Width && editor.ActualHeight == size.Height, "local image tools arrange without a native window");
            if (render)
            {
                var image = new RenderTargetBitmap((int)size.Width, (int)size.Height, 96, 96, PixelFormats.Pbgra32); image.Render(editor);
                using var file = File.Create(Path.Combine(root, $"tools-{size.Width}.png"));
                var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(image)); encoder.Save(file);
            }
        }
        if (render) Console.WriteLine("UTILITY_RENDER: " + root);
    }
    sealed class Handler(Func<HttpRequestMessage, HttpResponseMessage> reply) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token) => Task.FromResult(reply(request));
    }
    sealed class TextReceiver : HttpMessageHandler
    {
        internal string Reply = "整理后的测试文字。";
        internal string? Body;
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        {
            Body = await request.Content!.ReadAsStringAsync(token);
            return new(HttpStatusCode.OK) { Content = new StringContent(JsonSerializer.Serialize(new {
                choices = new[] { new { finish_reason = "stop", message = new { content = JsonSerializer.Serialize(new { reply = Reply }) } } } }
            ), Encoding.UTF8, "application/json") };
        }
    }
}
