using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Text.Json;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using CetaFae.Probe;

namespace CetaFae.Checks;

public static class Program
{
    static int _passed;
    static readonly GuideCandidate[] Candidates =
    [
        new(1, "左侧选项", "left", "ControlType.Button", new Rect(10, 10, 60, 60), [1]),
        new(2, "右侧选项", "right", "ControlType.Button", new Rect(100, 10, 60, 60), [2])
    ];

    [STAThread]
    public static int Main(string[] args)
    {
        if (args.Length == 2 && args[0] == "--worker-fixture") return LifetimeChecks.Fixture(args[1]);
        if (args.Contains("--capture-desktop")) return CaptureChecks.Run();
        if (args.Contains("--scroll-desktop")) return ScrollChecks.Run();
        if (args.Contains("--privacy-desktop")) return PrivacyDesktopChecks.Run();
        Console.WriteLine($"BUILD: {typeof(GuideSession).Module.ModuleVersionId:N}");
        if (args.Contains("--render-cards"))
        {
            _ = Dispatcher.CurrentDispatcher;
            CardChecks.Run(Check);
            CardChecks.Render();
            return 0;
        }
        if (args.Contains("--transport-allocation"))
        {
            TransportChecks.MeasureAsync().GetAwaiter().GetResult();
            return 0;
        }
        try { RunAsync(args).GetAwaiter().GetResult(); return 0; }
        catch (Exception ex) { Console.Error.WriteLine($"FAIL: {ex.GetType().Name}: {ex.Message}"); return 1; }
    }

    static void Check(bool value, string name)
    {
        if (!value) throw new InvalidOperationException(name);
        _passed++;
        Console.WriteLine($"PASS: {name}");
    }

    static void Reject(Action action, string name)
    {
        try { action(); }
        catch (Exception ex) when (ex is JsonException or InvalidDataException or InvalidOperationException or
                                      ArgumentException or FormatException or KeyNotFoundException)
        { Check(true, name); return; }
        throw new InvalidOperationException(name);
    }

    static async Task RejectAsync(Func<Task> action, string name)
    {
        try { await action(); }
        catch (Exception ex) when (ex is InvalidDataException or JsonException or HttpRequestException or OperationCanceledException)
        { Check(true, name); return; }
        throw new InvalidOperationException(name);
    }

    static async Task RunAsync(string[] args)
    {
        Check(CultureInfo.GetCultureInfo(2052).Name == "zh-CN", "Chinese Windows input locale is available");
        // A console entry point has no WPF dispatcher until explicitly initialized.
        _ = Dispatcher.CurrentDispatcher;
        Check(!string.IsNullOrEmpty(InputLanguageManager.Current.CurrentInputLanguage.Name),
            "WPF current input-language lookup succeeds without opening a window");
        using (var runtime = JsonDocument.Parse(File.ReadAllText(
                   Path.ChangeExtension(typeof(GuideOptions).Assembly.Location, ".runtimeconfig.json"))))
            Check(!runtime.RootElement.GetProperty("runtimeOptions").GetProperty("configProperties")
                    .GetProperty("System.Globalization.Invariant").GetBoolean(),
                "application runtime retains culture data for editable WPF controls");
        PrivacyChecks.CheckDisclosureLayout(Check);
        await PrivacyChecks.RunAsync(Check);
        LogRetentionChecks.Run(Check);
        PluginChecks.Run(Check);
        GeneralGuideChecks.Run(Check);
        await IdentificationChecks.RunAsync(Check);
        CaretChecks.Run(Check);
        ScopeChecks.Run(Check);
        await ScopeChecks.CheckTransportAsync(Check);
        await TransportChecks.RunAsync(Check);
        TaskChecks.Run(Check);
        CardChecks.Run(Check);
        Check(GuideSession.FailureMessage(new InvalidDataException("target_not_foreground"))
                  .Contains("请先点击目标窗口", StringComparison.Ordinal),
            "foreground rejection gives an actionable target-focus instruction");
        Check(!GuideSession.FailureMessage(new InvalidDataException("private provider text"))
                  .Contains("private provider text", StringComparison.Ordinal),
            "unrecognized failure details are not echoed into the panel");
        Check(GuideSession.FailureMessage(new InvalidDataException("target_activation_failed"))
                  .Contains("系统未允许激活目标窗口", StringComparison.Ordinal),
            "OS activation refusal is reported separately from a model failure");
        var toggled = Candidates[0] with { ToggleState = "On" };
        Check(!GuideObservation.HasSameContext([toggled], [toggled with { ToggleState = "Off" }]),
            "toggle state change invalidates an old answer");
        var clarification = new GuideRunGate();
        clarification.SetCloud(true);
        var questionVersion = clarification.Begin();
        clarification.AwaitClarification(questionVersion);
        Check(clarification.CanClarify(questionVersion), "active question permits deliberate input");
        Reject(() => clarification.TakeClarification(questionVersion, "\n"), "empty multiline clarification rejected");
        Reject(() => clarification.TakeClarification(questionVersion, "first\nsecond"), "multiline intent rejected");
        Reject(() => clarification.TakeClarification(questionVersion, new string('x', 301)), "overlong intent rejected");
        Check(clarification.CanClarify(questionVersion), "invalid input preserves the pending question");
        Check(clarification.TakeClarification(questionVersion, "下载前先询问") == "下载前先询问",
            "clarification preserves user intent");
        Reject(() => clarification.TakeClarification(questionVersion, "again"), "duplicate clarification rejected");
        var staleQuestion = clarification.Begin();
        clarification.AwaitClarification(staleQuestion);
        clarification.Cancel();
        Check(!clarification.CanClarify(staleQuestion), "cancellation discards clarification");
        Reject(() => clarification.TakeClarification(staleQuestion, "old"), "cancelled clarification cannot restart a request");
        var replacedQuestion = clarification.Begin();
        clarification.AwaitClarification(replacedQuestion);
        clarification.Begin();
        Check(!clarification.CanClarify(replacedQuestion), "a new drag discards the previous question");
        var revokedQuestion = clarification.Begin();
        clarification.AwaitClarification(revokedQuestion);
        clarification.SetCloud(false);
        Check(!clarification.CanClarify(revokedQuestion), "revocation discards the pending question");
        var speculative = new GuideAnswer("point", "click", 1).ForIntent(null);
        Check(speculative.Mode == "ask" && speculative.CandidateId is null,
            "an unspecified settings goal cannot produce a speculative marker");
        var explanation = new GuideAnswer("explain", "当前是下载设置。", null).ForIntent(null);
        Check(explanation.Mode == "ask" && explanation.Explanation.StartsWith("当前是下载设置。"),
            "initial explanation retains page context before a short question");
        Check(new GuideAnswer("point", "click", 1).ForIntent("show the control").CandidateId == 1,
            "explicit intent permits candidate guidance");
        var generatedQuestion = GuideAnswer.Parse(
            """{"mode":"ask","explanation":"Which generated control do you mean?","candidateId":null}""", Candidates);
        Check(ReferenceEquals(generatedQuestion.ForIntent(null), generatedQuestion),
            "an initial model question remains available once");
        var questionLimit = new GuideRunGate();
        questionLimit.SetCloud(true);
        var firstQuestion = questionLimit.Begin();
        questionLimit.AwaitClarification(firstQuestion);
        var submittedIntent = questionLimit.TakeClarification(firstQuestion, "Locate the generated destination control");
        var followupVersion = questionLimit.Begin();
        var unresolved = generatedQuestion.ForIntent(submittedIntent);
        Check(unresolved.Mode == "explain" && unresolved.CandidateId is null,
            "a model ask after the single user answer terminates without a speculative marker");
        Check(!unresolved.Explanation.Contains(generatedQuestion.Explanation, StringComparison.Ordinal),
            "the terminal uncertainty message does not repeat the model's follow-up question");
        if (unresolved.Mode == "ask") questionLimit.AwaitClarification(followupVersion);
        Check(!questionLimit.CanClarify(followupVersion),
            "the follow-up result cannot reopen the question gate across request versions");
        Reject(() => questionLimit.TakeClarification(followupVersion, "another answer"),
            "a terminal uncertainty result cannot send another clarification");
        var freshDrop = questionLimit.Begin();
        questionLimit.AwaitClarification(freshDrop);
        Check(questionLimit.CanClarify(freshDrop), "a fresh deliberate drop can ask its own question");
        var satisfied = new GuideAnswer("explain", "The generated goal is already satisfied.", null);
        Check(ReferenceEquals(satisfied.ForIntent(submittedIntent), satisfied),
            "a valid post-clarification explanation remains unchanged");
        Check(BallWindow.DragPosition(new Point(3163, 696), new Point(25, 20), 1) == new Point(3138, 676),
            "delayed mouse-down handling retains the original grab point");
        Check(BallWindow.DragPosition(new Point(1000, 500), new Point(10, 30), 1) == new Point(990, 470),
            "a later drag uses its new grab point");
        var home = new GuideCandidate[]
        {
            new(1, "Windows 更新", "nav", "ControlType.ListItem", new Rect(10, 10, 100, 30), [1]),
            new(2, "主页项目", "home", "ControlType.Button", new Rect(120, 10, 100, 30), [2])
        };
        var update = new[] { home[0], home[1] with { Name = "继续更新", AutomationId = "resume", RuntimeId = [3] } };
        Check(!GuideObservation.HasSameContext(home, update),
            "page change rejects an old answer even when its navigation candidate survives");
        Check(GuideObservation.HasSameContext(home, home.Select(c => c with { RuntimeId = c.RuntimeId.ToArray() }).ToArray()),
            "unchanged candidate values preserve context across fresh UIA objects");
        Check(!GuideObservation.HasSameContext(home, [home[0]]), "removed page controls invalidate context");
        Check(!GuideObservation.HasSameContext(home, [home[0], home[1] with { Bounds = new Rect(150, 10, 100, 30) }]),
            "moved page controls invalidate context");
        var contextGate = new GuideRunGate();
        contextGate.SetCloud(true);
        var contextVersion = contextGate.Begin();
        Check(!contextGate.CancelIfContextChanged(contextVersion, home, home) && contextGate.Accepts(contextVersion),
            "an unchanged page event preserves the active answer");
        Check(contextGate.CancelIfContextChanged(contextVersion, home, update) && !contextGate.Accepts(contextVersion),
            "a changed page event invalidates the active answer");
        var newerContextVersion = contextGate.Begin();
        Check(!contextGate.CancelIfContextChanged(contextVersion, home, update) && contextGate.Accepts(newerContextVersion),
            "a late event check cannot cancel a newer request");
        var work = new Rect(0, 0, 3440, 1392);
        Check(BallWindow.ConstrainPosition(new Point(1720, -44), work) == new Point(1720, 0),
            "top-edge snap keeps the whole ball onscreen");
        foreach (var (position, expected) in new[]
                 {
                     (new Point(-44, -44), new Point(0, 0)),
                     (new Point(3440, -44), new Point(3396, 0)),
                     (new Point(-44, 1392), new Point(0, 1348)),
                     (new Point(3440, 1392), new Point(3396, 1348)),
                     (new Point(100, 200), new Point(100, 200))
                 })
            Check(BallWindow.ConstrainPosition(position, work) == expected, $"ball position {position} remains reachable");
        Check(BallWindow.ConstrainPosition(new Point(-44, 0), new Rect(100, 50, 800, 600)) == new Point(100, 50),
            "ball visibility respects the working-area origin");
        var leftImage = Image(redOnLeft: true);
        var rightImage = Image(redOnLeft: false);
        Check(GuideAnswer.Parse("{\"mode\":\"point\",\"explanation\":\"点这里\",\"candidateId\":1}", Candidates).CandidateId == 1,
            "known candidate accepted");
        Check(GuideAnswer.Parse("{\"mode\":\"ask\",\"explanation\":\"你想做什么？\",\"candidateId\":null}", Candidates).Mode == "ask",
            "uncertainty preserves clarification");
        foreach (var json in new[]
        {
            "{\"mode\":\"point\",\"explanation\":\"x\",\"candidateId\":99}",
            "{\"mode\":\"point\",\"explanation\":\"x\",\"candidateId\":null}",
            "{\"mode\":\"ask\",\"explanation\":\"x\",\"candidateId\":1}",
            "{\"mode\":\"execute\",\"explanation\":\"x\",\"candidateId\":1}",
            "{\"mode\":\"point\",\"explanation\":\"x\",\"candidateId\":1,\"x\":42}",
            "{\"mode\":\"explain\",\"explanation\":\"\",\"candidateId\":null}",
            "{\"mode\":\"ask\",\"mode\":\"point\",\"candidateId\":1}",
            "[]", "not json",
            JsonSerializer.Serialize(new { mode = "explain", explanation = new string('x', 601), candidateId = (int?)null })
        }) Reject(() => GuideAnswer.Parse(json, Candidates), $"invalid answer {_passed}");

        foreach (var endpoint in new[] { "http://example.com/v1", "https://user:password@example.com/v1",
                     "https://example.com/v1?key=secret", "file:///C:/model", "http://127.0.0.1.example.com/v1" })
            Reject(() => GuideOptions.Create(endpoint, "deepseek-v4.1-flash"), "unsafe endpoint rejected");
        var options = GuideOptions.Create("http://127.0.0.1:8787/v1", "deepseek-v4.1-flash");
        Check(options.BaseUrl.AbsoluteUri == "http://127.0.0.1:8787/v1/", "loopback endpoint normalized");
        Check(GuideOptions.Create("https://example.com/v1", "test").BaseUrl.Scheme == "https", "TLS endpoint accepted");

        var gate = new GuideRunGate();
        Check(!gate.Accepts(gate.Begin()), "consent required");
        gate.SetCloud(true);
        var first = gate.Begin();
        Check(gate.Accepts(first), "active result accepted");
        var second = gate.Begin();
        Check(!gate.Accepts(first) && gate.Accepts(second), "late result rejected");
        gate.Cancel();
        Check(!gate.Accepts(second), "cancel invalidates result");
        var third = gate.Begin();
        gate.SetCloud(false);
        Check(!gate.Accepts(third), "revocation invalidates result");
        Check(GuideObservation.UsableBounds(new Rect(10, 10, 20, 20), new Rect(0, 0, 100, 100)), "visible bounds accepted");
        Check(!GuideObservation.UsableBounds(Rect.Empty, new Rect(0, 0, 100, 100)) &&
              !GuideObservation.UsableBounds(new Rect(90, 90, 20, 20), new Rect(0, 0, 100, 100)), "invalid or clipped bounds rejected");

        using (var model = new GuideModel(options, new Handler(async (request, token) =>
               {
                   using var body = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(token));
                   Check(request.RequestUri!.AbsolutePath == "/v1/chat/completions", "HTTP endpoint path");
                   Check(body.RootElement.GetProperty("model").GetString() == "deepseek-v4.1-flash", "selected model preserved");
                   Check(body.RootElement.GetProperty("messages")[1].GetProperty("content")[1].GetProperty("type").GetString() == "image_url",
                       "image supplied in actual client request");
                   using var context = JsonDocument.Parse(body.RootElement.GetProperty("messages")[1]
                       .GetProperty("content")[0].GetProperty("text").GetString()!);
                   Check(context.RootElement.GetProperty("candidates")[0].GetProperty("toggleState").GetString() == "On" &&
                         context.RootElement.GetProperty("candidates")[1].GetProperty("toggleState").ValueKind == JsonValueKind.Null,
                       "HTTP preserves a known toggle state and unknown state separately");
                   return Completion("{\"mode\":\"point\",\"explanation\":\"左边\",\"candidateId\":1}");
               })))
            Check((await model.AskAsync("test", [toggled, Candidates[1]], leftImage, CancellationToken.None)).CandidateId == 1, "HTTP success parsed");

        var responses = new[]
        {
            new HttpResponseMessage(HttpStatusCode.ServiceUnavailable),
            new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("not json") },
            new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(new string('x', 33000)) },
            Completion("{\"mode\":\"point\",\"explanation\":\"bad\",\"candidateId\":500}"),
            new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("{\"choices\":[{\"finish_reason\":\"length\"}]}") }
        };
        foreach (var response in responses)
        {
            using var model = new GuideModel(options, new Handler((_, _) => Task.FromResult(response)));
            await RejectAsync(() => model.AskAsync("test", Candidates, leftImage, CancellationToken.None), "HTTP or model failure stays failure");
        }
        using (var model = new GuideModel(options, new Handler((_, _) => throw new InvalidOperationException("must not send"))))
            await RejectAsync(() => model.AskAsync("test", Candidates, null, CancellationToken.None), "missing image never silently downgrades");
        using (var model = new GuideModel(options, new Handler(async (_, token) =>
               { await Task.Delay(Timeout.Infinite, token); return Completion(""); })))
        {
            using var cancel = new CancellationTokenSource(30);
            await RejectAsync(() => model.AskAsync("test", Candidates, leftImage, cancel.Token), "request cancellation propagates");
        }
        using (var model = new GuideModel(options with { Timeout = TimeSpan.FromMilliseconds(30) },
                   new Handler(async (_, token) => { await Task.Delay(Timeout.Infinite, token); return Completion(""); })))
            await RejectAsync(() => model.AskAsync("test", Candidates, leftImage, CancellationToken.None), "request timeout propagates");

        var targetWindow = new IntPtr(42);
        var foregroundSamples = 0;
        Check(await GuideSession.WaitForForegroundAsync(targetWindow,
                () => ++foregroundSamples == 1 ? IntPtr.Zero : targetWindow, CancellationToken.None),
            "accepted activation can settle from zero to target before recognition");
        foregroundSamples = 0;
        Check(await GuideSession.WaitForForegroundAsync(targetWindow,
                () => { foregroundSamples++; return targetWindow; }, CancellationToken.None) && foregroundSamples == 1,
            "already-confirmed foreground completes without waiting");
        Check(!await GuideSession.WaitForForegroundAsync(targetWindow, () => IntPtr.Zero, CancellationToken.None),
            "unconfirmed activation has a bounded wait and fails closed");
        using (var activationCancel = new CancellationTokenSource(30))
            await RejectAsync(async () => { await GuideSession.WaitForForegroundAsync(targetWindow,
                    () => IntPtr.Zero, activationCancel.Token); },
                "activation confirmation can be cancelled without observing content");

        await LifetimeChecks.RunAsync(Check);

        var delayedResponse = new TaskCompletionSource<HttpResponseMessage>(TaskCreationOptions.RunContinuationsAsynchronously);
        using (var model = new GuideModel(options, new Handler((_, _) => delayedResponse.Task)))
        using (var deadline = new GuideDeadline(TimeSpan.FromMilliseconds(40), CancellationToken.None))
        {
            var lateGate = new GuideRunGate();
            lateGate.SetCloud(true);
            var oldVersion = lateGate.Begin();
            Task<GuideAnswer>? oldRequest = null;
            var expired = false;
            try
            {
                await deadline.RunAsync(t => oldRequest = model.AskAsync("generated", Candidates, leftImage, t));
            }
            catch (OperationCanceledException) { expired = true; lateGate.Cancel(); }
            var newVersion = lateGate.Begin();
            delayedResponse.SetResult(Completion("{\"mode\":\"point\",\"explanation\":\"late generated response\",\"candidateId\":1}"));
            try { await oldRequest!; } catch (OperationCanceledException) { }
            Check(expired && !lateGate.Accepts(oldVersion) && lateGate.Accepts(newVersion),
                "HTTP handler ignoring cancellation cannot revive an expired version or invalidate its replacement");
        }

        await IdentificationChecks.CheckLateReplyAsync(Check);
        if (args.Contains("--live-scopes")) await ScopeChecks.RunLiveAsync(Check);
        if (args.Contains("--live-general")) await GeneralGuideChecks.RunLiveAsync(Check);
        if (args.Contains("--live-identification")) await IdentificationChecks.RunLiveAsync(Check);
        if (args.Contains("--live-tasks")) await TaskChecks.RunLiveAsync(Check,
            args.SingleOrDefault(a => a.StartsWith("--task-case=", StringComparison.Ordinal))?[12..]);
        if (args.Contains("--live") || args.Contains("--live-winrar"))
        {
            using var live = new GuideModel(options with { Timeout = TimeSpan.FromSeconds(45) });
            var extractionCandidates = new GuideCandidate[]
            {
                new(1, "浏览", "browse", "ControlType.Button", new Rect(530, 65, 90, 35), [1]),
                new(2, "目标路径", "destination", "ControlType.ComboBox", new Rect(30, 65, 480, 35), [2]),
                new(3, "确定", "confirm", "ControlType.Button", new Rect(430, 160, 90, 35), [3]),
                new(4, "取消", "cancel", "ControlType.Button", new Rect(530, 160, 90, 35), [4])
            };
            var extractionImage = ExtractionImage();
            var extractionQuestion = (await live.AskAsync(GuideObservation.TaskDescription, extractionCandidates,
                extractionImage, CancellationToken.None)).ForIntent(null);
            Check(extractionQuestion.Mode == "ask" && extractionQuestion.CandidateId is null,
                "LIVE generated extraction dialog requests intent without a speculative marker");
            var extractionClock = Stopwatch.StartNew();
            var extractionLocation = await live.AskAsync(
                "用户明确提出的目标：在哪里选择解压到哪个文件夹？只指出位置，不修改。只解释和指路，不执行任何操作。",
                extractionCandidates, extractionImage, CancellationToken.None);
            Check(extractionLocation.Mode == "point" && extractionLocation.CandidateId is 1 or 2,
                $"LIVE generated folder selection identifies an observed path or browse control, never submit; mode={extractionLocation.Mode}, candidate={extractionLocation.CandidateId}, {extractionClock.ElapsedMilliseconds}ms");
            var pathLocation = await live.AskAsync(
                "用户明确提出的目标：目标路径输入框在哪里？我要找填写解压后文件夹路径的输入位置，只指出位置，不修改。只解释和指路，不执行任何操作。",
                extractionCandidates, extractionImage, CancellationToken.None);
            Check(pathLocation.Mode == "point" && pathLocation.CandidateId == 2,
                $"LIVE generated explicit path-field request selects the path instead of browse or submit; mode={pathLocation.Mode}, candidate={pathLocation.CandidateId}");
        }
        if (args.Contains("--live"))
        {
            using var live = new GuideModel(options with { Timeout = TimeSpan.FromSeconds(45) });
            var downloadCandidates = new GuideCandidate[]
            {
                new(1, "更改", "change-location", "ControlType.Button", new Rect(580, 90, 80, 30), [1]),
                new(2, "在下载之前询问每个文件的保存位置", "save-location", "ControlType.CheckBox",
                    new Rect(30, 150, 630, 35), [2], "On")
            };
            var downloadImage = DownloadImage();
            for (var attempt = 1; attempt <= 3; attempt++)
            {
                var location = await live.AskAsync(
                    "用户明确提出的目标：下载前询问的开关在哪里？只指出位置，不修改。。只解释和指路，不执行任何操作；目标已满足时说明现状，不指向相反动作。",
                    downloadCandidates, downloadImage, CancellationToken.None);
                Check(location.Mode == "point" && location.CandidateId == 2,
                    $"LIVE location request points to an already-enabled control ({attempt}); mode={location.Mode}");
            }
            for (var attempt = 1; attempt <= 2; attempt++)
            {
                var clock = Stopwatch.StartNew();
                var question = await live.AskAsync(GuideObservation.TaskDescription, downloadCandidates,
                    downloadImage, CancellationToken.None);
                Check(question.Mode == "ask" && question.CandidateId is null &&
                      question.Explanation.Contains("下载", StringComparison.Ordinal) &&
                      !new[] { "Windows", "更新", "系统设置", "操作系统" }.Any(s =>
                          question.Explanation.Contains(s, StringComparison.OrdinalIgnoreCase)),
                    $"LIVE generated Downloads clarification stays on the selected page ({attempt}); {clock.ElapsedMilliseconds}ms");
            }
            foreach (var (image, expected) in new[] { (leftImage, 1), (rightImage, 2) })
            {
                var clock = Stopwatch.StartNew();
                var answer = await live.AskAsync("这是生成的测试图，没有真实用户数据。请指出图中红色方块所在的左侧或右侧选项。",
                    Candidates, image, CancellationToken.None);
                Check(answer.Mode == "point" && answer.CandidateId == expected, $"LIVE image-only red-square placement {expected}; {clock.ElapsedMilliseconds}ms");
            }
            var ambiguous = await live.AskAsync("帮我弄一下。没有给出任何具体目标。", Candidates, leftImage, CancellationToken.None);
            Check(ambiguous.Mode == "ask" && ambiguous.CandidateId is null, "LIVE ambiguity returns clarification");
            var downloadSwitch = new GuideCandidate(1, "每次下载时询问我该做些什么", "download-prompt",
                "ControlType.CheckBox", new Rect(10, 10, 60, 60), [1], "On");
            foreach (var state in new[] { "On", "Off" })
            {
                var answer = await live.AskAsync("生成的下载设置测试，无个人数据。请告诉我怎样开启每次下载前询问。" +
                    "候选的 toggleState 是明确的当前状态，图片仅是生成的颜色占位图。",
                    [downloadSwitch with { ToggleState = state }], leftImage, CancellationToken.None);
                Check(state == "On" ? answer.Mode == "explain" && answer.CandidateId is null :
                    answer.Mode == "point" && answer.CandidateId == 1,
                    $"LIVE explicit download intent handles state {state}");
            }
        }
        Console.WriteLine($"RESULT: {_passed} checks passed. Desktop observation: NOT RUN.");
    }

    static HttpResponseMessage Completion(string answer) => new(HttpStatusCode.OK)
    {
        Content = new StringContent(JsonSerializer.Serialize(new
        {
            choices = new[] { new { finish_reason = "stop", message = new { role = "assistant", content = answer } } }
        }))
    };

    static byte[] Image(bool redOnLeft)
    {
        const int width = 180, height = 80;
        var pixels = Enumerable.Repeat((byte)255, width * height * 4).ToArray();
        for (var y = 10; y < 70; y++)
        for (var x = 10; x < 160; x++)
        {
            if (x is >= 70 and < 100) continue;
            var red = x < 70 == redOnLeft;
            var at = (y * width + x) * 4;
            pixels[at] = red ? (byte)0 : (byte)255;
            pixels[at + 1] = 0;
            pixels[at + 2] = red ? (byte)255 : (byte)0;
        }
        var bitmap = BitmapSource.Create(width, height, 96, 96, PixelFormats.Bgra32, null, pixels, width * 4);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var stream = new MemoryStream();
        encoder.Save(stream);
        return stream.ToArray();
    }

    static byte[] ExtractionImage()
    {
        var visual = new DrawingVisual();
        using (var draw = visual.RenderOpen())
        {
            draw.DrawRectangle(Brushes.White, null, new Rect(0, 0, 660, 220));
            draw.DrawText(new FormattedText("WinRAR 解压路径和选项（生成测试）\n目标路径",
                CultureInfo.GetCultureInfo("zh-CN"), FlowDirection.LeftToRight, new Typeface("Microsoft YaHei"),
                16, Brushes.Black, 1), new Point(30, 10));
            draw.DrawRectangle(Brushes.White, new Pen(Brushes.Gray, 1), new Rect(30, 65, 480, 35));
            draw.DrawText(new FormattedText("C:\\GeneratedOutput", CultureInfo.InvariantCulture,
                FlowDirection.LeftToRight, new Typeface("Segoe UI"), 16, Brushes.Black, 1), new Point(40, 72));
            foreach (var (text, rect) in new[] { ("浏览", new Rect(530, 65, 90, 35)),
                         ("确定", new Rect(430, 160, 90, 35)), ("取消", new Rect(530, 160, 90, 35)) })
            {
                draw.DrawRectangle(Brushes.LightGray, new Pen(Brushes.Gray, 1), rect);
                draw.DrawText(new FormattedText(text, CultureInfo.GetCultureInfo("zh-CN"),
                    FlowDirection.LeftToRight, new Typeface("Microsoft YaHei"), 16, Brushes.Black, 1),
                    new Point(rect.X + 20, rect.Y + 7));
            }
        }
        var bitmap = new RenderTargetBitmap(660, 220, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(visual);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var stream = new MemoryStream();
        encoder.Save(stream);
        return stream.ToArray();
    }

    static byte[] DownloadImage()
    {
        var visual = new DrawingVisual();
        using (var draw = visual.RenderOpen())
        {
            draw.DrawRectangle(Brushes.White, null, new Rect(0, 0, 700, 240));
            draw.DrawText(new FormattedText("Microsoft Edge 设置\n\n下载\n\n位置：C:\\GeneratedDownloads     更改\n\n在下载之前询问每个文件的保存位置：开启",
                CultureInfo.GetCultureInfo("zh-CN"), FlowDirection.LeftToRight, new Typeface("Microsoft YaHei"),
                18, Brushes.Black, 1), new Point(30, 15));
        }
        var bitmap = new RenderTargetBitmap(700, 240, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(visual);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var stream = new MemoryStream();
        encoder.Save(stream);
        return stream.ToArray();
    }

    sealed class Handler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> send) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            send(request, cancellationToken);
    }
}
