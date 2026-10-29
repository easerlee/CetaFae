using System.IO;
using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using CetaFae.Plugins;
using CetaFae.Probe;

namespace CetaFae.Checks;

static class GeneralGuideChecks
{
    internal const string Package = """
        {"schemaVersion":3,"kind":"general","id":"example.visual","version":"0.1.0","displayName":"Generated visual guide",
         "targets":[],"uiaView":"control","taskIds":["guide.visual"],"blockedPoints":[],"observationScopes":[]}
        """;

    public static void Run(Action<bool, string> check)
    {
        void Reject(Action action, string name)
        {
            try { action(); }
            catch (InvalidDataException) { check(true, name); return; }
            throw new InvalidOperationException(name);
        }
        var general = PluginCatalog.ParsePlugin(Package);
        foreach (var code in new[] { "capture_target_unavailable", "capture_dimensions", "capture_unusable", "worker_request_invalid" })
            check(ObservationWorker.SafeError(code) == code,
                "fixed collector failure codes survive IPC without exposing provider details");
        foreach (var intent in new string?[] { null, "Locate the generated switch without changing it." })
        foreach (var selection in new Rect?[] { new Rect(-380, 100, 140, 80), null })
        {
            var request = new ObservationWorker.Request(123, true, general, intent, selection);
            var decoded = JsonSerializer.Deserialize<ObservationWorker.Request>(
                ObservationWorker.SerializeRequest(request), ObservationWorker.Json);
            check(decoded is not null && decoded.Window == request.Window && decoded.IncludeImage == request.IncludeImage &&
                  decoded.Intent == intent && decoded.Selection == selection &&
                  JsonSerializer.Serialize(decoded.Plugin) == JsonSerializer.Serialize(general),
                "the production collector request round-trips target, plugin, intent and nullable selection");
        }
        var app = PluginCatalog.ParsePlugin(ScopeChecks.Package);
        var catalog = new PluginCatalog([general, app]);
        check(catalog.Resolve("unadapted-editor", "Generated canvas").Id == general.Id,
            "an explicitly enabled general guide serves an application without a dedicated adapter");
        check(catalog.Resolve("generated-scope", "Generated page").Id == app.Id,
            "a unique application adapter enhances the general path without a catalog conflict");
        check(new PluginCatalog([general]).Resolve("generated-scope", "Generated page").Id == general.Id,
            "disabling an application adapter retains the explicitly enabled general capability");
        Reject(() => new PluginCatalog([app]).Resolve("unadapted-editor", ""),
            "disabling the general package removes unknown-application support");
        Reject(() => new PluginCatalog([]).Resolve("unadapted-editor", ""),
            "an empty catalog never recreates a hidden generic first-party feature");
        var conflict = PluginCatalog.ParsePlugin(ScopeChecks.Package.Replace("example.scope", "example.conflict"));
        Reject(() => new PluginCatalog([general, app, conflict]).Resolve("generated-scope", ""),
            "conflicting application adapters do not silently fall back to the general guide");
        Reject(() => new PluginCatalog([general, PluginCatalog.ParsePlugin(Package.Replace("example.visual", "example.other"))]),
            "multiple enabled general packages reject instead of choosing by load order");
        Reject(() => PluginCatalog.ParsePlugin(Package.Replace("\"targets\":[]", "\"targets\":[{\"processNames\":[\"x\"]}]")),
            "a general package cannot hide application-specific identity rules");
        Reject(() => PluginCatalog.ParsePlugin(Package.Replace("\"kind\":\"general\"", "\"kind\":\"native-code\"")),
            "a package kind cannot grant executable plugin authority");
        check(general.Matches("unadapted-editor", "Generated canvas") && !general.UsesRawView,
            "generic target admission remains an explicit immutable package policy");
        var selected = JsonSerializer.Deserialize<GuideScope>(
            """{"Region":[10,20,90,50],"Anchor":null,"UserSelected":true}""", ObservationWorker.Json)!;
        var observed = new GuideObservation(new IntPtr(1), 1, new Rect(0, 0, 200, 100), [], null,
            PluginId: general.Id, Scope: selected, ScopeIntent: "Explain this generated area");
        check(GuidePayload.From(observed).VisibleContext is null && GuidePayload.From(observed).Candidates.Count == 0,
            "a user-selected image region needs no software label or actionable UIA candidate");
        var frame = new Rect(-400, 80, 400, 200);
        var region = GuideScope.FromUserSelection(frame, new Rect(-350, 100, 120, 80));
        check(region.UserSelected && region.Anchor is null && region.Region.Width == 120,
            "user selection is independent of application labels on a negative-origin display");
        check(GuideScope.FromUserSelection(frame, frame).Region == frame,
            "only deliberate user selection may include the entire target rather than automatic scope expansion");
        foreach (var invalid in new[] { new Rect(-401, 100, 50, 50), new Rect(-350, 100, 1, 50),
                     new Rect(-350, 100, double.PositiveInfinity, 50), Rect.Empty })
            Reject(() => GuideScope.FromUserSelection(frame, invalid), "invalid or out-of-target user regions refuse");
        foreach (var (start, end) in new[] { (new Point(10, 10), new Point(80, 50)), (new Point(80, 50), new Point(10, 10)),
                     (new Point(10, 50), new Point(80, 10)), (new Point(80, 10), new Point(10, 50)) })
            check(RegionPicker.Selection(frame, start, end, 2) == new Rect(-380, 100, 140, 80),
                "all selection drag directions use physical pixels and the bound target origin");
        check(RegionPicker.Selection(frame, new Point(-20, -20), new Point(800, 800), 1) == frame,
            "captured pointer movement is clamped to the chosen target, never another window");
        var selectionObservation = observed with { Frame = frame, Scope = region, VisualHash = "old-image" };
        Reject(() => selectionObservation.ValidateContext(selectionObservation with { VisualHash = "new-image" }, null),
            "pixel changes invalidate zero-control explanations despite an unchanged empty UIA list");
        Reject(() => selectionObservation.ValidateContext(selectionObservation with { VisualHash = null }, null),
            "a missing visual recheck cannot substitute for a pixel-bound explanation");
        Reject(() => selectionObservation.ValidateContext(selectionObservation with { Scope = region with { Region = frame } }, null),
            "a changed user region invalidates the previous observation");
        Reject(() => selectionObservation.ValidateContext(selectionObservation with { ScopeIntent = "A different generated task" }, null),
            "a selected image cannot be reused for a different bound user intent");
        selectionObservation.ValidateContext(selectionObservation, null);
        check(true, "unchanged zero-control visual evidence remains explainable");
        Reject(() => selectionObservation.ValidateTargetContext(selectionObservation with { Frame = new Rect(-395, 80, 400, 200) }),
            "a target moving between selection-source recheck and new collection invalidates the requested absolute region");
        Reject(() => selectionObservation.ValidateTargetContext(selectionObservation with { ProcessId = 2 }),
            "a reused handle in another process cannot inherit the user's selected region");
        selectionObservation.ValidateTargetContext(selectionObservation with { Scope = null, ScopeIntent = null });
        check(true, "a deliberate scope change keeps the same bound target and candidate context");
        ObservationWorker.ValidateScopeReply(observed, general, observed.ScopeIntent, selected.Region, false);
        check(true, "worker IPC accepts the exact host-authorized user region without a label");
        Reject(() => ObservationWorker.ValidateScopeReply(observed, general, observed.ScopeIntent, null, false),
            "a collector cannot invent user selection when the host did not request one");
        Reject(() => ObservationWorker.ValidateScopeReply(observed, general, observed.ScopeIntent, new Rect(10, 20, 80, 50), false),
            "a collector cannot enlarge or replace the requested user region");
        var hash = GuideObservation.Fingerprint([1, 2, 3]);
        check(hash != GuideObservation.Fingerprint([1, 2, 4]), "visual evidence distinguishes generated changed pixels");
        Reject(() => ObservationWorker.ValidateScopeReply(observed with { Png = [1, 2, 3], VisualHash = "forged" },
                general, observed.ScopeIntent, selected.Region, true), "IPC visual evidence is recomputed from the returned bytes");
        var picker = new RegionPicker(frame, 1);
        try { check(new System.Windows.Interop.WindowInteropHelper(picker).Handle == IntPtr.Zero,
            "region geometry checks do not create or show a desktop picker window"); }
        finally { picker.Abort(); }
    }

    public static async Task RunLiveAsync(Action<bool, string> check)
    {
        // Pixels are generated in memory; no desktop, UIA content or saved image is involved.
        var frame = new Rect(0, 0, 720, 320);
        var selection = new Rect(40, 100, 640, 120);
        var visual = new DrawingVisual();
        using (var draw = visual.RenderOpen())
        {
            draw.DrawRectangle(Brushes.White, null, frame);
            draw.DrawText(new FormattedText("GENERATED_OUTSIDE_REGION", CultureInfo.InvariantCulture,
                FlowDirection.LeftToRight, new Typeface("Segoe UI"), 18, Brushes.Black, 1), new Point(40, 30));
            draw.DrawRectangle(Brushes.AliceBlue, new Pen(Brushes.SteelBlue, 1), selection);
            draw.DrawText(new FormattedText("生成任务：已暂停，直到2026/10/29为止。", CultureInfo.GetCultureInfo("zh-CN"),
                FlowDirection.LeftToRight, new Typeface("Microsoft YaHei"), 24, Brushes.Black, 1), new Point(55, 145));
        }
        var bitmap = new RenderTargetBitmap(720, 320, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(visual);
        var png = Capture.EncodeRegion(bitmap, frame, selection);
        var observation = new GuideObservation(new IntPtr(1), 1, frame, [], png, PluginId: "cetafae.visual-guide",
            Scope: GuideScope.FromUserSelection(frame, selection));
        var payload = GuidePayload.From(observation);
        using var model = new GuideModel(GuideOptions.Create("http://127.0.0.1:8787/v1", "deepseek-v4.1-flash", timeoutSeconds: 45));
        var clock = Stopwatch.StartNew();
        var question = (await model.AskAsync(GuideObservation.TaskDescription, payload.Candidates, payload.Png,
            CancellationToken.None, payload.VisibleContext)).ForIntent(null);
        check(question.Mode == "ask" && question.CandidateId is null && question.Explanation.Contains("暂停"),
            $"LIVE generated generic zero-UIA question: elapsedMs={clock.ElapsedMilliseconds}; native acceptance NOT implied");
        const string intent = "只解释图中生成任务当前的状态和截止日期，不执行。";
        clock.Restart();
        var answer = (await model.AskAsync(intent, payload.Candidates, payload.Png,
            CancellationToken.None, payload.VisibleContext)).ForIntent(intent);
        check(answer.Mode == "explain" && answer.CandidateId is null && answer.Explanation.Contains("暂停") &&
              answer.Explanation.Contains("2026") && answer.Explanation.Contains("29") &&
              !answer.Explanation.Contains("GENERATED_OUTSIDE_REGION"),
            $"LIVE generated generic image-only selected-region explanation: pngBytes={png.Length}, elapsedMs={clock.ElapsedMilliseconds}; native acceptance NOT implied");
    }
}
