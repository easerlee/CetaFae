using System.IO;
using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using CetaFae.Plugins;
using CetaFae.Probe;

namespace CetaFae.Checks;

static class ScopeChecks
{
    internal const string Package = """
        {"schemaVersion":2,"id":"example.scope","version":"0.2.0","displayName":"Generated scoped app",
         "targets":[{"processNames":["generated-scope"]}],"uiaView":"control","taskIds":["example.locate"],"blockedPoints":[],
         "observationScopes":[{"intentContainsAny":["find generated"],"anchorNameContainsAny":["Generated option"],
          "anchorControlTypes":["ControlType.CheckBox"],"padding":16}]}
        """;

    public static void Run(Action<bool, string> check)
    {
        void Reject(Action action, string name)
        {
            try { action(); }
            catch (Exception ex) when (ex is InvalidDataException or JsonException or ArgumentException)
            { check(true, name); return; }
            throw new InvalidOperationException(name);
        }
        var plugin = PluginCatalog.ParsePlugin(Package);
        var installedWinrar = PluginCatalog.Load(Path.Combine(AppContext.BaseDirectory, "plugins")).Get("cetafae.winrar");
        var destinationRule = installedWinrar.ScopeFor("在哪里选择解压到哪个文件夹？只指出位置，不修改。");
        const string destinationLabel = "目的地路径（如果不存在，将创建）(P)";
        var destinationFrame = new Rect(2135, 617, 534, 463);
        GuideScopeAnchor[] destinationControls =
        [
            new(destinationLabel, "ControlType.Text", new Rect(2155, 694, 387, 14), [1]),
            new(destinationLabel, "ControlType.ComboBox", new Rect(2155, 710, 387, 20), [2]),
            new(destinationLabel, "ControlType.Edit", new Rect(2158, 713, 364, 14), [3])
        ];
        var destinationScope = GuideScope.Resolve(destinationFrame, destinationRule, destinationControls);
        check(destinationScope.Anchor!.RuntimeId.SequenceEqual(new[] { 2 }) &&
              destinationScope.Region.Contains(destinationControls[2].Bounds),
            "the observed destination label resolves one ComboBox scope with its complete edit child, excluding duplicate label roles");
        check(installedWinrar.ScopeFor("目的地路径在哪里？").MatchesAnchor(destinationLabel, "ControlType.ComboBox") &&
              destinationRule.MatchesAnchor("目的路径", "ControlType.ComboBox") &&
              destinationRule.MatchesAnchor("目标路径", "ControlType.ComboBox") &&
              destinationRule.MatchesAnchor("Destination path", "ControlType.ComboBox"),
            "the bounded destination label alias preserves previously declared labels and explicit intent matching");
        Reject(() => GuideScope.Resolve(destinationFrame, destinationRule,
                [destinationControls[1] with { Name = "其他路径" }]),
            "a generic unrelated path label cannot inherit the extraction destination scope");
        Reject(() => GuideScope.Resolve(destinationFrame, destinationRule,
                [destinationControls[1], destinationControls[1] with { RuntimeId = [4] }]),
            "two destination ComboBoxes remain ambiguous despite the new label alias");
        check(plugin.SchemaVersion == 2, "a data-only v2 package declares a bounded outbound scope");
        Reject(() => new GuidePlugin(2, plugin.Id, "0.2.0", "Generated", plugin.Targets, "control", ["test"], []),
            "v2 cannot omit its task scope definitions");
        Reject(() => new GuidePlugin(1, plugin.Id, "0.1.0", "Generated", plugin.Targets, "control", ["test"], [])
            .ScopeFor("find generated"), "legacy v1 explicit tasks do not bypass outbound scope policy");
        var rule = plugin.ScopeFor("Please find generated option");
        Reject(() => plugin.ScopeFor("an unrelated goal"), "unmatched explicit intent cannot silently send a whole window");
        Reject(() => new GuidePlugin(2, "example.duplicate", "0.2.0", "Generated", plugin.Targets, "control", ["test"], [],
                [rule, rule]).ScopeFor("find generated"), "ambiguous scope rules refuse instead of choosing by order");
        foreach (var (json, name) in new[]
        {
            (Package.Replace("\"padding\":16", "\"padding\":65"), "excessive scope padding"),
            (Package.Replace("\"padding\":16", "\"padding\":-1"), "negative scope padding"),
            (Package.Replace("ControlType.CheckBox", "ControlType.Window"), "unbounded window anchor role"),
            (Package.Replace("[\"find generated\"]", "[]"), "empty scope intent"),
            (Package.Replace("[\"Generated option\"]", "[]"), "empty scope anchor"),
            (Package.Replace("\"padding\":16", "\"bypassPrivacy\":true,\"padding\":16"), "scope privacy bypass field"),
            (Package.Replace("\"schemaVersion\":2", "\"schemaVersion\":1"), "v2 scope hidden in a v1 package")
        }) Reject(() => PluginCatalog.ParsePlugin(json), $"plugin scope contract rejects {name}");
        var phrases = new List<string> { "find generated" };
        var frozen = new ObservationScopeRule(phrases, ["Generated option"], ["ControlType.CheckBox"], 16);
        phrases[0] = "changed";
        check(frozen.MatchesIntent("find generated") && !frozen.MatchesIntent("changed"),
            "validated scope phrases are immutable snapshots");
        var transferred = JsonSerializer.Deserialize<GuidePlugin>(JsonSerializer.Serialize(plugin, ObservationWorker.Json), ObservationWorker.Json)!;
        check(transferred.ScopeFor("find generated").Padding == 16, "collector IPC preserves the reviewed scope contract");
        var frame = new Rect(-100, 80, 600, 400);
        foreach (var scale in new[] { 1d, 1.25, 1.5, 2 })
        {
            var selected = RegionPicker.Selection(frame, new Point(40 / scale, 60 / scale),
                new Point(240 / scale, 160 / scale), scale);
            check(selected == new Rect(-60, 140, 200, 100),
                "region selection preserves physical coordinates at 100/125/150/200 percent scaling");
            var reversed = RegionPicker.Selection(frame, new Point(240 / scale, 160 / scale),
                new Point(40 / scale, 60 / scale), scale);
            check(reversed == selected,
                "reverse-direction selection uses the same bounded physical region at every supported scale");
        }
        var anchor = new GuideScopeAnchor("Generated option", "ControlType.CheckBox", new Rect(0, 180, 100, 30), [7]);
        var scope = GuideScope.Resolve(frame, rule, [anchor]);
        check(scope.Region == new Rect(-16, 164, 132, 62), "unique scope is local to its anchor on a negative-origin desktop");
        var fractional = anchor with { Bounds = new Rect(.5, 180.25, 100.25, 30.5) };
        var zeroPadding = new ObservationScopeRule(["find generated"], ["Generated option"], ["ControlType.CheckBox"], 0);
        check(GuideScope.Resolve(frame, zeroPadding, [fractional]).Region == new Rect(0, 180, 101, 31),
            "fractional UIA geometry rounds outward to retain the complete anchor with zero padding");
        Reject(() => GuideScope.Resolve(frame, rule, []), "missing scope anchor refuses without whole-window fallback");
        Reject(() => GuideScope.Resolve(frame, rule, [anchor, anchor with { RuntimeId = [8] }]), "duplicate visible anchors refuse");
        Reject(() => GuideScope.Resolve(frame, rule, [anchor with { Bounds = new Rect(-110, 180, 100, 30) }]),
            "partially out-of-window anchors refuse");
        Reject(() => GuideScope.Resolve(frame, rule, [anchor with { Bounds = frame }]), "near-whole-window scope refuses");
        Reject(() => GuideScope.Resolve(frame, rule, [anchor with { Bounds = new Rect(double.NaN, 0, 10, 10) }]),
            "non-finite scope geometry refuses");
        Reject(() => GuideScope.Resolve(frame, rule, [anchor with { Name = "Generated option password=generated-secret" }]),
            "sensitive anchor text cannot become outbound context");
        var inside = new GuideCandidate(7, anchor.Name, "local-only-id", anchor.ControlType, anchor.Bounds, [7], "On");
        var outside = new GuideCandidate(8, "UNRELATED_LOCAL_ONLY", "local-id", "ControlType.Button", new Rect(250, 100, 80, 30), [8]);
        var partial = outside with { Id = 9, Bounds = new Rect(100, 180, 80, 30) };
        var observation = new GuideObservation(new IntPtr(1), 1, frame, [inside, outside, partial], null,
            PluginId: plugin.Id, Scope: scope, ScopeIntent: "find generated");
        var payload = GuidePayload.From(observation);
        check(payload.Candidates.Count == 1 && payload.Candidates[0].Id == 7 && payload.Candidates[0].Bounds == new Rect(16, 16, 100, 30),
            "outbound candidates exclude outside and partial controls and use crop-relative geometry");
        check(payload.Candidates[0].AutomationId == "" && payload.Candidates[0].RuntimeId.Length == 0 &&
              payload.VisibleContext == anchor.Name && observation.Candidates.Count == 3 && observation.Candidates[0].AutomationId == "local-only-id",
            "outbound IDs and text are reduced without mutating local validation context");
        Reject(() => GuideAnswer.Parse("""{"mode":"point","candidateId":8,"explanation":"Generated"}""", payload.Candidates),
            "a model cannot point to a valid local control omitted from the outbound scope");
        var same = observation with { Scope = scope with { Anchor = anchor with { RuntimeId = [7] } } };
        check(observation.ValidateContext(same, 7) == anchor.Bounds, "scope-relative model ID maps to unchanged original desktop bounds");
        foreach (var changed in new[]
        {
            scope with { Anchor = anchor with { Name = "Generated option changed" } },
            scope with { Anchor = anchor with { RuntimeId = [9] } },
            scope with { Region = new Rect(-15, 164, 132, 62) }
        }) Reject(() => observation.ValidateContext(observation with { Scope = changed }, 7),
            "scope changes invalidate a result even with unchanged actionable controls");
        Reject(() => observation.ValidateContext(observation with { Candidates = [inside] }, 7),
            "a change outside the crop still invalidates full local context");
        Reject(() => GuidePayload.From(observation with { Scope = null }), "explicit intent with a missing scope cannot reach the model");
        var status = GuidePayload.From(observation with { Candidates = [] });
        check(GuideAnswer.Parse("""{"mode":"explain","candidateId":null,"explanation":"Generated status"}""", status.Candidates).Mode == "explain" &&
              status.VisibleContext == anchor.Name, "a read-only status anchor can explain without an invented actionable control");

        var pixels = Enumerable.Repeat((byte)255, 200 * 100 * 4).ToArray();
        for (var y = 20; y < 50; y++)
            for (var x = 40; x < 80; x++)
            { var offset = (y * 200 + x) * 4; pixels[offset] = 0; pixels[offset + 1] = 0; pixels[offset + 2] = 255; }
        var image = BitmapSource.Create(200, 100, 96, 96, PixelFormats.Bgra32, null, pixels, 800);
        var png = Capture.EncodeRegion(image, new Rect(-200, -100, 200, 100), new Rect(-160, -80, 40, 30));
        using var stream = new MemoryStream(png);
        var decoded = BitmapDecoder.Create(stream, BitmapCreateOptions.None, BitmapCacheOption.OnLoad).Frames[0];
        var cropped = new byte[40 * 30 * 4];
        decoded.CopyPixels(cropped, 160, 0);
        check(decoded.PixelWidth == 40 && decoded.PixelHeight == 30 && Enumerable.Range(0, 40 * 30)
            .All(i => cropped[i * 4] == 0 && cropped[i * 4 + 1] == 0 && cropped[i * 4 + 2] == 255),
            "encoded PNG contains only the generated target region and no outside white pixels");
        Reject(() => Capture.EncodeRegion(image, new Rect(-200, -100, 200, 100), new Rect(-201, -100, 40, 30)),
            "image crop cannot extend outside the selected window bitmap");
        var scaled = GuidePayload.From(observation with { Png = png });
        check(Math.Abs(scaled.Candidates[0].Bounds.Width - 100d * 40 / 132) < .001 &&
              Math.Abs(scaled.Candidates[0].Bounds.Y - 16d * 30 / 62) < .001,
            "outbound candidate geometry follows the actual encoded image scale");
        check(ObservationWorker.SafeError("scope_unavailable") == "scope_unavailable" &&
              GuideSession.FailureMessage(new InvalidDataException("scope_unavailable")).Contains("不会改发整个窗口"),
            "scope refusal survives worker IPC with explicit no-fallback disclosure");
    }

    public static async Task CheckTransportAsync(Action<bool, string> check)
    {
        var rule = PluginCatalog.ParsePlugin(Package).ScopeFor("find generated");
        var anchor = new GuideScopeAnchor("Generated option", "ControlType.CheckBox", new Rect(100, 100, 80, 30), [7]);
        var frame = new Rect(0, 0, 800, 500);
        var scope = GuideScope.Resolve(frame, rule, [anchor]);
        var local = new GuideObservation(new IntPtr(1), 1, frame,
            [new(7, anchor.Name, "LOCAL_AUTOMATION_SECRET_MARKER", anchor.ControlType, anchor.Bounds, [7], "On"),
             new(8, "UNRELATED_LOCAL_ONLY", "other", "ControlType.Button", new Rect(500, 100, 50, 30), [8])],
            null, Scope: scope, ScopeIntent: "find generated");
        var payload = GuidePayload.From(local);
        using var model = new GuideModel(GuideOptions.Create("http://127.0.0.1:8787/v1", "generated", false), new Handler(async request =>
        {
            var body = await request.Content!.ReadAsStringAsync();
            using var parsed = JsonDocument.Parse(body);
            using var context = JsonDocument.Parse(parsed.RootElement.GetProperty("messages")[1].GetProperty("content")[0].GetProperty("text").GetString()!);
            check(!body.Contains("UNRELATED_LOCAL_ONLY") && !body.Contains("LOCAL_AUTOMATION_SECRET_MARKER") &&
                  !body.Contains("automationId") && context.RootElement.GetProperty("candidates").GetArrayLength() == 1 &&
                  context.RootElement.GetProperty("visibleContext").GetString() == anchor.Name,
                "intercepted HTTP request contains only scoped control text and safe visible context");
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(
                """{"choices":[{"finish_reason":"stop","message":{"content":"{\"mode\":\"point\",\"candidateId\":8,\"explanation\":\"Generated\"}"}}]}""", Encoding.UTF8, "application/json") };
        }));
        try { await model.AskAsync("find generated", payload.Candidates, payload.Png, CancellationToken.None, payload.VisibleContext); }
        catch (InvalidDataException)
        { check(true, "transport rejects a model response selecting an excluded local ID"); return; }
        throw new InvalidOperationException("transport accepted an excluded candidate ID");
    }

    public static async Task RunLiveAsync(Action<bool, string> check)
    {
        var catalog = PluginCatalog.Load(Path.Combine(AppContext.BaseDirectory, "plugins"));
        var fixtures = new[]
        {
            (Id: "cetafae.edge", Intent: "下载前询问的开关在哪里？只指出位置，不修改。",
                Name: "在下载之前询问每个文件的保存位置", Role: "ControlType.CheckBox", State: "On", Expected: "point"),
            (Id: "cetafae.winrar", Intent: "在哪里选择解压到哪个文件夹？只指出位置，不修改。",
                Name: "目的地路径（如果不存在，将创建）(P)", Role: "ControlType.ComboBox", State: (string?)null, Expected: "point"),
            (Id: "cetafae.windows-settings", Intent: "我想暂停更新，现在是什么状态？只解释，不修改。",
                Name: "更新已暂停，直到2026/10/29为止", Role: "ControlType.Text", State: (string?)null, Expected: "explain")
        };
        using var model = new GuideModel(GuideOptions.Create("http://127.0.0.1:8787/v1", "deepseek-v4.1-flash", timeoutSeconds: 45));
        foreach (var fixture in fixtures)
        {
            var plugin = catalog.Get(fixture.Id);
            var frame = new Rect(0, 0, 800, 500);
            var anchor = new GuideScopeAnchor(fixture.Name, fixture.Role, new Rect(160, 180, 460, 42), [7]);
            var scope = GuideScope.Resolve(frame, plugin.ScopeFor(fixture.Intent), [anchor]);
            var visual = new DrawingVisual();
            using (var draw = visual.RenderOpen())
            {
                draw.DrawRectangle(Brushes.White, null, frame);
                draw.DrawText(new FormattedText("UNRELATED_GENERATED_OUTSIDE", CultureInfo.InvariantCulture,
                    FlowDirection.LeftToRight, new Typeface("Segoe UI"), 16, Brushes.Black, 1), new Point(20, 20));
                draw.DrawRectangle(Brushes.AliceBlue, new Pen(Brushes.Gray, 1), anchor.Bounds);
                draw.DrawText(new FormattedText(fixture.Name, CultureInfo.GetCultureInfo("zh-CN"), FlowDirection.LeftToRight,
                    new Typeface("Microsoft YaHei"), 16, Brushes.Black, 1), new Point(166, 189));
            }
            var bitmap = new RenderTargetBitmap(800, 500, 96, 96, PixelFormats.Pbgra32);
            bitmap.Render(visual);
            bitmap.Freeze();
            var png = Capture.EncodeRegion(bitmap, frame, scope.Region);
            IReadOnlyList<GuideCandidate> candidates = fixture.Expected == "point"
                ? [new(7, fixture.Name, "local-generated", fixture.Role, anchor.Bounds, [7], fixture.State)] : [];
            var observation = new GuideObservation(new IntPtr(1), 1, frame, candidates, png,
                PluginId: plugin.Id, Scope: scope, ScopeIntent: fixture.Intent);
            var payload = GuidePayload.From(observation);
            var timer = Stopwatch.StartNew();
            var answer = (await model.AskAsync("用户明确提出的目标：" + fixture.Intent, payload.Candidates, payload.Png,
                CancellationToken.None, payload.VisibleContext)).ForIntent(fixture.Intent);
            check(answer.Mode == fixture.Expected && (fixture.Expected == "point" ? answer.CandidateId == 7 :
                    answer.CandidateId is null && answer.Explanation.Contains("2026") && answer.Explanation.Contains("暂停")) &&
                  !answer.Explanation.Contains("UNRELATED_GENERATED_OUTSIDE"),
                $"LIVE generated scoped {fixture.Id}: mode={answer.Mode}, pngBytes={png.Length}, elapsedMs={timer.ElapsedMilliseconds}; native acceptance NOT implied");
            if (fixture.Id == "cetafae.edge")
            {
                var initial = GuidePayload.From(observation with { Scope = null, ScopeIntent = null,
                    Png = Capture.EncodeRegion(bitmap, frame, frame) });
                var question = (await model.AskAsync(GuideObservation.TaskDescription, initial.Candidates, initial.Png,
                    CancellationToken.None, initial.VisibleContext)).ForIntent(null);
                check(question.Mode == "ask" && question.CandidateId is null && question.Explanation.Contains("下载"),
                    "LIVE generated initial unknown-goal whole-window exception still requests intent without a marker");
            }
        }
    }

    sealed class Handler(Func<HttpRequestMessage, Task<HttpResponseMessage>> send) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token) => send(request);
    }
}
