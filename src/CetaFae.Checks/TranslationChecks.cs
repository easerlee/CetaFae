using System.IO;
using System.Net;
using System.Net.Http;
using System.Text.Json;
using System.Diagnostics;
using System.Globalization;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using CetaFae.Plugins;
using CetaFae.Probe;

namespace CetaFae.Checks;

internal static class TranslationChecks
{
    internal static async Task RunAsync(Action<bool, string> check)
    {
        var shipped = PluginCatalog.Load(Path.Combine(AppContext.BaseDirectory, "plugins"));
        var general = shipped.GeneralProvider!;
        var policy = general.InterpretationPolicy!;
        check(general.SchemaVersion == 5 && policy.TranslateSelectedText && policy.Languages.SequenceEqual(new[] { "zh-CN", "en" }),
            "translation is an enabled data-only general capability, not an application-name branch");
        check(new PluginCatalog(shipped.Plugins.Where(p => p != general)).GeneralProvider is null &&
              PluginCatalog.ParsePlugin(GeneralGuideChecks.Package).InterpretationPolicy is null,
            "disabled or legacy general plugins have no implicit translation policy");
        var languages = new List<string> { "en" }; var frozen = new InterpretationPolicy(languages, true); languages[0] = "other";
        check(frozen.Languages.Single() == "en", "general language policy takes an immutable validated snapshot");
        void Reject(Action action, string name)
        {
            try { action(); }
            catch (Exception ex) when (ex is InvalidDataException or JsonException or ArgumentException)
            { check(true, name); return; }
            throw new InvalidOperationException(name);
        }
        Reject(() => new InterpretationPolicy(["unknown"], true), "unknown language codes cannot broaden interpretation policy");
        var package = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "plugins", general.Id, "plugin.json"));
        Reject(() => PluginCatalog.ParsePlugin(package.Replace("\"schemaVersion\": 5", "\"schemaVersion\": 3")),
            "legacy schema cannot silently acquire a new interpretation capability");
        Reject(() => PluginCatalog.ParsePlugin(package.Replace("\"kind\": \"general\"", "\"kind\": \"application\"")),
            "application packages cannot impersonate the general interpretation policy");
        var transferred = JsonSerializer.Deserialize<GuidePlugin>(JsonSerializer.Serialize(general, ObservationWorker.Json), ObservationWorker.Json)!;
        check(transferred.InterpretationPolicy!.Languages.SequenceEqual(policy.Languages), "collector IPC retains the reviewed general policy");
        var source = "Error 0x80070005 ERROR_ACCESS_DENIED; 2026/10/29; 1024MB; -10℃";
        var translated = "错误 0x80070005 ERROR_ACCESS_DENIED；2026/10/29；1024MB；-10℃。";
        string Answer(string text, string language = "zh-CN", int? id = null) => JsonSerializer.Serialize(new
            { mode = "translate", sourceText = source, translation = text, language, candidateId = id });
        var result = GuideAnswer.Parse(Answer(translated), [], policy);
        check(result.Mode == "explain" && result.CandidateId is null && result.Explanation.Contains(source) && result.Explanation.Contains(translated),
            "translation preserves the full recognized source and translation without a control marker");
        Reject(() => GuideAnswer.Parse(Answer(translated), []), "translation output without an enabled general policy is refused");
        Reject(() => GuideAnswer.Parse(Answer(translated, "other"), [], policy), "model cannot invent an allowed translation language");
        Reject(() => GuideAnswer.Parse(Answer(translated, id: 1), [], policy), "translation cannot request a control highlight");
        foreach (var altered in new[] { translated.Replace("80070005", "80070006"), translated.Replace("1024MB", "1024GB"),
                     translated.Replace("2026/10/29", "2026/10/30"), translated.Replace("-10", "+10"),
                     translated.Replace("ERROR_ACCESS_DENIED", "ERROR_UNKNOWN"), translated + "99MB" })
            Reject(() => GuideAnswer.Parse(Answer(altered), [], policy), "changed numeric code date unit or signed value refuses the translation");
        Reject(() => GuideAnswer.Parse(Answer(translated).Replace("\"mode\":", "\"extra\":true,\"mode\":"), [], policy),
            "translation rejects unreviewed fields rather than relaxing the normal answer schema");
        Reject(() => GuideAnswer.Parse(Answer(translated).Replace("\"mode\":", "\"mode\":\"translate\",\"mode\":"), [], policy),
            "duplicate translation fields cannot replace reviewed output");
        Reject(() => GuideAnswer.Parse(Answer(translated + "\nother"), [], policy), "translation strings remain bounded plain single-line data");
        Reject(() => GuideAnswer.Parse(Answer(new string('x', 601)), [], policy), "oversized translation is refused before display");
        var unreadable = GuideAnswer.Parse("""{"mode":"explain","explanation":"部分文字无法可靠识读，本次不补写。","candidateId":null}""", [], policy);
        check(unreadable.Mode == "explain" && unreadable.CandidateId is null, "unreadable content retains an honest explanation-only response");
        using var model = new GuideModel(GuideOptions.Create("http://127.0.0.1:8787/v1", "generated", false), new Handler(async request =>
        {
            using var body = JsonDocument.Parse(await request.Content!.ReadAsByteArrayAsync());
            var instructions = body.RootElement.GetProperty("messages")[0].GetProperty("content").GetString()!;
            check(instructions.Contains("Enabled general interpretation policy") && instructions.Contains("sourceText") && instructions.Contains("unreadable"),
                "only the enabled policy augments the existing bounded model request with translation rules");
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(JsonSerializer.Serialize(new
                { choices = new[] { new { finish_reason = "stop", message = new { content = Answer(translated) } } } })) };
        }));
        var answer = await model.AskAsync("Translate this generated text into Chinese.", [], null, CancellationToken.None, source, policy);
        check(answer == result, "controlled transport parses the structured translation through the production model API");
    }

    internal static async Task RunLiveAsync(Action<bool, string> check)
    {
        var policy = PluginCatalog.Load(Path.Combine(AppContext.BaseDirectory, "plugins")).GeneralProvider!.InterpretationPolicy!;
        var cases = new (string Id, string Source, string Goal, Func<GuideAnswer, bool> Accept)[]
        {
            ("english-error", "Request failed: 0x80070005. Retry in 15ms.", "请把区域内的英文测试文字翻译成中文，保留原文、错误码、数值和单位，不判断真实系统。",
                answer => answer.Explanation.StartsWith("原文（画面识读）：") && answer.Explanation.Contains("中文翻译：") &&
                    answer.Explanation.Contains("0x80070005") && answer.Explanation.Contains("15ms") && answer.Explanation.Contains("失败")),
            ("chinese-state", "状态：已暂停；截止日期：2026/10/29；可用空间：1024MB。", "请把区域内的中文生成文字翻译成英文，保留原文、日期、数值和单位，不判断真实系统。",
                answer => answer.Explanation.StartsWith("原文（画面识读）：") && answer.Explanation.Contains("英文翻译：") &&
                    answer.Explanation.Contains("2026/10/29") && answer.Explanation.Contains("1024MB") &&
                    (answer.Explanation.Contains("paused", StringComparison.OrdinalIgnoreCase) || answer.Explanation.Contains("suspended", StringComparison.OrdinalIgnoreCase))),
            ("blank-region", "", "请翻译区域里能看见的错误文字；没有文字或看不清时明确说明，不要补写。",
                answer => !answer.Explanation.StartsWith("原文（画面识读）：") &&
                    new[] { "没有", "未显示", "空白", "无法", "未见", "未发现" }.Any(answer.Explanation.Contains))
        };
        using var model = new GuideModel(GuideOptions.Create("http://127.0.0.1:8787/v1", "deepseek-v4.1-flash", timeoutSeconds: 45));
        foreach (var item in cases)
        {
            var frame = new Rect(0, 0, 1000, 180); var visual = new DrawingVisual();
            using (var draw = visual.RenderOpen())
            {
                draw.DrawRectangle(Brushes.White, null, frame);
                if (item.Source.Length > 0) draw.DrawText(new FormattedText(item.Source, CultureInfo.GetCultureInfo("zh-CN"),
                    FlowDirection.LeftToRight, new Typeface("Microsoft YaHei"), 24, Brushes.Black, 1), new Point(24, 60));
            }
            var image = new RenderTargetBitmap(1000, 180, 96, 96, PixelFormats.Pbgra32); image.Render(visual);
            var png = Capture.EncodeRegion(image, frame, frame);
            var clock = Stopwatch.StartNew();
            var result = await model.AskAsync(item.Goal, [], png, CancellationToken.None, interpretationPolicy: policy);
            Console.WriteLine(JsonSerializer.Serialize(new { generatedTranslation = item.Id, result.Explanation, elapsedMs = clock.ElapsedMilliseconds }));
            check(result.Mode == "explain" && result.CandidateId is null && item.Accept(result),
                $"LIVE translation {item.Id}: elapsedMs={clock.ElapsedMilliseconds}; generated region only, no native or timing acceptance");
        }
    }

    sealed class Handler(Func<HttpRequestMessage, Task<HttpResponseMessage>> respond) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) => respond(request);
    }
}
