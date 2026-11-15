using System.IO;
using System.Net;
using System.Net.Http;
using System.Text.Json;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using CetaFae.Plugins;
using CetaFae.Probe;

namespace CetaFae.Checks;

internal static class ConversionChecks
{
    internal static async Task RunAsync(Action<bool, string> check, bool requireTool)
    {
        var catalog = GuideFeature.DefaultCatalog();
        var plugin = catalog.ChatProvider!;
        var convert = new TaskProposal("file.convert", Purpose: "compatible");
        var tool = FixedTool.ImageConverter;
        foreach (var (proposal, sourceFormat, resultFormat, needsTool) in new[]
        {
            (convert, "heic", "jpg", true),
            (new TaskProposal("file.convert", Purpose: "transparent"), "webp", "png", true),
            (new TaskProposal("file.convert", Purpose: "print"), "png", "pdf", true),
            (new TaskProposal("file.convert", Purpose: "print", Format: "webp"), "png", "webp", true),
            (convert, "jpeg", "jpg", false)
        })
        {
            var plan = await TaskConversion.PlanAsync(catalog, proposal, sourceFormat, default);
            check(plan is not null && plan.Format == resultFormat && (plan.Tool is not null) == needsTool,
                $"conversion plugin plans {sourceFormat} to {resultFormat} from purpose or explicit format");
        }
        check(await TaskConversion.PlanAsync(catalog, convert, "docx", default) is null &&
            await TaskConversion.PlanAsync(catalog, new("file.convert", Format: "docx"), "png", default) is null,
            "unsupported format combinations make no conversion recommendation");

        async Task Reject(Func<Task> attempt, string name)
        {
            try { await attempt(); }
            catch (Exception ex) when (ex is InvalidDataException or JsonException or OperationCanceledException)
            { check(true, name); return; }
            check(false, name);
        }
        foreach (var json in new[]
        {
            """{"operation":"file.convert","path":"C:/outside.png","format":"jpg"}""",
            """{"operation":"file.convert","format":"--overwrite"}""",
            """{"operation":"file.convert","purpose":"delete"}""",
            """{"operation":"file.convert"}""",
            """{"operation":"file.info","format":null}"""
        })
            await Reject(() => Task.FromResult(TaskProposal.Parse(JsonSerializer.Deserialize<JsonElement>(json))),
                "conversion rejects malformed intent or native authority");

        GuidePlugin Provider(string response) => new(8, "example.conversion", "1.0", "Generated converter",
            [], "control", [], [], kind: "conversion",
            program: new PluginProgram(1, "function invoke() { return " + JsonSerializer.Serialize(response) + "; }"));
        var independent = Provider(JsonSerializer.Serialize(new { conversion = new { format = "jpg", reason = "Generated only", recipe = tool.Id } }));
        check((await TaskConversion.PlanAsync(new([independent]), convert, "png", default))?.Tool == tool,
            "independent TS provider uses the same reviewed tool contract");
        await Reject(() => TaskConversion.PlanAsync(new(catalog.Plugins.Append(independent)), convert, "png", default),
            "two enabled providers cannot silently select conflicting authority");
        foreach (var response in new[]
        {
            """{"conversion":{"format":"jpg","reason":"generated","recipe":"powershell"}}""",
            """{"conversion":{"format":"jpg","reason":"generated","recipe":null}}""",
            """{"conversion":{"format":"jpg","reason":"generated","recipe":null,"args":["--overwrite"]}}""",
            """{"conversion":{"format":"jpg","reason":"generated","format":"png","recipe":null}}"""
        })
            await Reject(() => TaskConversion.PlanAsync(new([Provider(response)]), convert, "png", default),
                "untrusted conversion plan cannot add a native tool, switches or duplicate fields");

        var root = Path.GetFullPath(Path.Combine(".artifacts", "checks", "conversion-" + Guid.NewGuid().ToString("N")));
        Directory.CreateDirectory(root);
        var source = Path.Combine(root, "生成 图片.png");
        var png = new PngBitmapEncoder();
        png.Frames.Add(BitmapFrame.Create(BitmapSource.Create(2, 2, 96, 96, PixelFormats.Bgra32, null,
            new byte[] { 255, 0, 0, 255, 30, 120, 200, 128, 0, 255, 0, 255, 0, 0, 255, 0 }, 8)));
        using (var stream = File.Create(source)) png.Save(stream);
        var original = PlatformFiles.State(source);
        var store = new TaskRecordStore(Path.Combine(root, "records"));
        var picks = 0;
        var approvals = 0;
        TaskCoordinator Coordinator(string file, PluginCatalog? available = null, Func<string, bool>? approve = null) =>
            new(store, new((preview, action, _) =>
            {
                approvals++;
                check(action?.StartsWith("转换为 ", StringComparison.Ordinal) == true &&
                    preview.Contains("保留原文件") && preview.Contains("这台电脑"),
                    "one local decision previews format, destination and original preservation");
                return Task.FromResult(approve?.Invoke(preview) ?? true);
            }, (kind, _) =>
            {
                picks++;
                check(kind == "conversion-file", "conversion uses one explicit local file selection");
                return Task.FromResult<string?>(file);
            }, _ => { }), available ?? catalog);

        var off = new PluginCatalog([plugin]);
        check((await Coordinator(source, off).StepAsync(convert, plugin, default)).Status == "denied" && picks == 0,
            "disabled conversion providers cannot open a picker or queue a tool");
        var modelCalls = 0;
        var options = GuideOptions.Create("http://127.0.0.1:8787/v1", "generated-conversion");
        using var model = new GuideModel(options, new Handler(async request =>
        {
            modelCalls++;
            var body = await request.Content!.ReadAsStringAsync();
            using var json = JsonDocument.Parse(body);
            var system = json.RootElement.GetProperty("messages")[0].GetProperty("content").GetString()!;
            check(system.Contains("generic upload failure") && system.Contains("another plugin") &&
                !body.Contains("generated.png") && !body.Contains("filee-cli.exe"),
                "chat advertises the capability and leaves ambiguity unresolved without receiving tool or file details");
        }), plugins: catalog);
        var proposalReply = await model.ConverseAsync([], "这张照片发给别人，别人打不开，帮我转一下。", true, default, taskAvailable: true);
        check(proposalReply.Task == convert, "model-shaped intent reaches the same task entry from a bound window");
        var result = await TaskWorkflow.RunAsync(Coordinator(source), plugin, new(null, new("file.convert", Format: "png")),
            (_, _) => throw new InvalidOperationException("conversion evidence must stay local"), default);
        check(result.Contains("无需转换") && approvals == 0 && modelCalls == 1,
            "already suitable input asks for no write confirmation and no extra model turn");
        if (!tool.Installed)
        {
            check(!requireTool, "pinned official Filee dependency required for live conversion checks");
            check((await Coordinator(source).StepAsync(convert, plugin, default)).Message.Contains("缺少转换工具") &&
                approvals == 0 && store.Read().Length == 0, "missing dependency has a specific handoff and no side effects");
            return;
        }
        if (!requireTool) return;

        check(FixedTool.DirectoryHash(tool.InstallDirectory, default) == tool.TreeHash, "live dependency matches the pinned upstream native bundle");
        var refused = await Coordinator(source, approve: _ => false).StepAsync(convert, plugin, default);
        check(refused.Status == "denied" && PlatformFiles.State(source) == original && store.Read().Length == 0,
            "declining conversion neither runs a tool nor creates a conversion receipt");
        var jpg = Path.ChangeExtension(source, "jpg");
        File.WriteAllText(jpg, "existing");
        var occupied = await Coordinator(source, approve: _ =>
        {
            File.WriteAllText(Path.Combine(root, "生成 图片 (2).jpg"), "appeared");
            return true;
        }).StepAsync(convert, plugin, default);
        check(occupied.Status == "denied" && File.ReadAllText(jpg) == "existing" && store.Read().Length == 0,
            "existing and newly occupied destinations are not overwritten");
        var jpgCopy = Path.Combine(root, "生成 图片 (3).jpg");
        var first = await TaskWorkflow.RunAsync(Coordinator(source), plugin, proposalReply,
            (_, _) => throw new InvalidOperationException("conversion output must stay local"), default);
        check(first.Contains("已转换为 JPG") && TaskConversion.VerifyOutput(jpgCopy, "jpg").Length == 64 &&
            PlatformFiles.State(source) == original && File.ReadAllText(jpg) == "existing" && modelCalls == 1,
            "chat→conversion TS→official Filee→verified copy produces a real JPG without touching original or sending results");
        foreach (var format in new[] { "webp", "pdf" })
        {
            var conversion = await Coordinator(source).StepAsync(new("file.convert", Format: format), plugin, default);
            check(conversion.Status == "verified" && TaskConversion.VerifyOutput(Path.ChangeExtension(source, format), format).Length == 64,
                $"actual Filee creates a verified {format} from generated PNG");
        }
        var pngCopy = await Coordinator(Path.ChangeExtension(source, "webp")).StepAsync(new("file.convert", Purpose: "transparent"), plugin, default);
        check(pngCopy.Status == "verified" && TaskConversion.VerifyOutput(Path.Combine(root, "生成 图片 (2).png"), "png").Length == 64,
            "actual Filee creates a decodable PNG from generated WebP");
        var saved = store.Read().Single(r => r.Resources.Contains(jpgCopy));
        check(saved.State == "verified" && saved.RecoveryId is not null, "verified copy retains the encrypted recovery receipt");
        var restore = new TaskCoordinator(store, new((_, _, _) => Task.FromResult(true), (_, _) => Task.FromResult<string?>(null), _ => { }));
        check((await restore.RestoreAsync(saved, default)).Contains("完成") && !File.Exists(jpgCopy) && PlatformFiles.State(source) == original,
            "conversion recovery removes only the derived file");
        var corrupted = Path.Combine(root, "corrupted.png");
        File.WriteAllText(corrupted, "generated invalid");
        await Reject(() => Coordinator(corrupted).StepAsync(convert, plugin, default), "corrupt input never reaches Filee");
        using var stop = new CancellationTokenSource();
        await Reject(() => Coordinator(source, approve: _ => { stop.Cancel(); return true; }).StepAsync(convert, plugin, stop.Token),
            "cancellation at the conversion decision stops before staging");
        check(!Directory.EnumerateDirectories(store.Root, "conversion-*").Any() && PlatformFiles.State(source) == original,
            "successful, denied and cancelled runs leave no staged copies in the task store");
    }

    sealed class Handler(Func<HttpRequestMessage, Task> observe) : HttpMessageHandler
    {
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        {
            await observe(request);
            return new(HttpStatusCode.OK) { Content = new StringContent(JsonSerializer.Serialize(new
            {
                choices = new[] { new { finish_reason = "stop", message = new
                { content = """{"task":{"operation":"file.convert","purpose":"compatible"}}""" } } }
            })) };
        }
    }
}
