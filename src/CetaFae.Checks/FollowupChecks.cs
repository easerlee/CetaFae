using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Text.Json;
using System.Diagnostics;
using System.Net;
using System.Net.Http;
using System.Globalization;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using CetaFae.Probe;
using CetaFae.Plugins;

namespace CetaFae.Checks;

internal static class FollowupChecks
{
    internal static void Run(Action<bool, string> check)
    {
        var card = new GuideCard(GuideOptions.Create("http://127.0.0.1:8787/v1", "generated"));
        card.SetCloud(true); card.SetStatus("生成结果");
        check(card.Actions.Children.OfType<Button>().Any(button => Equals(button.Content, "继续提问")),
            "completed guidance can expose an explicit follow-up action in the existing attached reader");
        var followups = 0; card.FollowupRequested += () => followups++;
        var button = card.Actions.Children.OfType<Button>().Single(button => Equals(button.Content, "继续提问"));
        void Click() => button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Click(); check(followups == 0, "unavailable follow-up ignores stale UI callbacks");
        card.SetFollowupAvailable(true); Click();
        check(followups == 1 && button.Visibility == Visibility.Visible, "explicit available follow-up dispatches without an automatic request");
        card.ShowQuestion(); Click(); check(followups == 1, "pending clarification cannot be mistaken for a new user request");
        card.ClearQuestion(); card.SetCloud(false); Click(); check(followups == 1, "revoked cloud cannot start a follow-up");
        card.Reset(); Click(); check(followups == 1 && !card.HasFollowup, "end session removes follow-up authority and content");

        void Reject(Action action, string name)
        {
            try { action(); }
            catch (InvalidDataException) { check(true, name); return; }
            throw new InvalidOperationException(name);
        }
        var flow = new GuideFollowup();
        var target = new GuideFlowTarget(new IntPtr(123), 456, 789, "generated.plugin");
        Reject(() => flow.Begin(), "no result means no follow-up can be armed");
        flow.Complete(target, "生成目标：说明三步流程", answer: new GuideAnswer("point", "先核对范围，再查看预览。", 11));
        check(flow.Step == 1 && flow.CanOffer && !flow.WaitingForUser, "a completed goal seeds a bounded user-only flow");
        check(flow.Begin() == target && flow.WaitingForUser && !flow.CanOffer, "arming retains only target binding and user goal");
        foreach (var invalid in new[] { "", "one\ntwo", new string('x', 301) })
            Reject(() => flow.Take(invalid), "invalid continuation does not consume the armed input");
        var second = flow.Take("我已手动完成，下一步看哪里？");
        using var requestData = JsonDocument.Parse(second.ModelTask[second.ModelTask.IndexOf('{')..]);
        check(second.Step == 2 && second.Goal == "生成目标：说明三步流程" && !flow.WaitingForUser &&
              requestData.RootElement.GetProperty("currentUserRequest").GetString() == second.Request &&
              requestData.RootElement.EnumerateObject().Count() == 6 &&
              requestData.RootElement.GetProperty("initialAssistantSuggestion").ValueKind == JsonValueKind.Null && second.ModelTask.Contains("当前画面") &&
              requestData.RootElement.GetProperty("recentSteps")[0].GetProperty("Explanation").GetString() == "先核对范围，再查看预览。",
            "fresh request retains bounded task text while explicitly requiring current visual evidence");
        Reject(() => flow.Take("duplicate"), "duplicate submission cannot create an extra step");
        foreach (var changed in new[] { target with { Window = new IntPtr(124) }, target with { ProcessId = 457 },
                     target with { StartedAt = 790 }, target with { PluginId = "other.plugin" } })
            Reject(() => second.ValidateTarget(changed), "follow-up cannot cross HWND process lifetime or plugin binding");
        second.ValidateTarget(target); check(true, "same bound process lifetime remains eligible for fresh collection");
        var longClarified = second with { Request = new string('a', 300), Clarification = new string('b', 300) };
        using (var full = JsonDocument.Parse(longClarified.ModelTask[longClarified.ModelTask.IndexOf('{')..]))
            check(longClarified.ScopeIntent.Length == 300 && full.RootElement.GetProperty("userClarification").GetString()!.Length == 300,
                "two valid300-character inputs stay separate; collector intent remains bounded and selected-region retry cannot loop on oversized intent");
        flow.Complete(target, second.Request);
        for (var step = 3; step <= GuideFollowup.MaxSteps; step++)
        {
            flow.Begin(); var request = flow.Take("继续核对，不执行操作");
            check(request.Step == step, "each of at least three steps requires another deliberate user request");
            flow.Complete(target, request.Request);
        }
        check(!flow.CanOffer, "bounded flow stops offering continuation after eight user-led steps");
        Reject(() => flow.Begin(), "step cap cannot be bypassed by a stale continue button");
        flow.Clear(); check(flow.Goal is null && flow.Step == 0 && !flow.CanOffer && !flow.WaitingForUser,
            "cancellation drops the original goal and the target binding");
        Reject(() => flow.Take("late"), "cancelled armed input cannot restart acquisition");
        flow.Complete(target, "生成原目标"); flow.SuspendOffer();
        check(!flow.CanOffer && flow.Goal == "生成原目标", "another explicit recognition suspends stale continue authority while retaining only the user goal");
        Reject(() => flow.Begin(), "an in-flight scope recheck cannot start a concurrent continuation");
        var ask = new GuideAnswer("ask", "请补充目标", null);
        check(ask.ForIntent("new request", allowQuestion: true).Mode == "ask" && ask.ForIntent("clarification answer").Mode == "explain",
            "one question on a new request is distinct from an already-consumed clarification");
        var policy = PluginCatalog.Load(Path.Combine(AppContext.BaseDirectory, "plugins")).GeneralProvider!.InterpretationPolicy!;
        check(policy.UserLedFollowup && !new InterpretationPolicy(["zh-CN"], true).UserLedFollowup,
            "follow-up is an enabled general plugin capability, off for older policies");
        const string doneJson = "{\"mode\":\"complete\",\"explanation\":\"已完成这次核对。\",\"candidateId\":null,\"evidence\":\"当前结果显示范围和预览已核对。\"}";
        var done = GuideAnswer.Parse(doneJson, [], policy);
        check(done is { Mode: "complete", CandidateId: null, CompletionEvidence: not null },
            "structured completion requires enabled task capability and a separate current-evidence statement");
        Reject(() => GuideAnswer.Parse(doneJson, []), "older policies cannot emit a new completion outcome");
        foreach (var invalid in new[] { doneJson.Replace("\"evidence\":\"当前结果显示范围和预览已核对。\"", "\"evidence\":\"\""),
                     doneJson.Replace("\"candidateId\":null", "\"candidateId\":11"),
                     doneJson.Replace(",\"evidence\":\"当前结果显示范围和预览已核对。\"", "") })
            Reject(() => GuideAnswer.Parse(invalid, [], policy), "completion without valid evidence or with a marker is refused");
        check(done.ForIntent(null).Mode == "ask", "recognition without a user goal cannot declare task completion");
        var retained = new GuideFollowup();
        retained.Prepare(target, "核对生成流程");
        retained.Restore();
        check(retained.CanOffer && retained.Goal == "核对生成流程" && retained.Step == 0,
            "a first-step failure retains the goal for an explicit retry");
        retained.Begin(); var retry = retained.Take("重试当前一步"); retained.Restore();
        retained.Begin(); var retried = retained.Take("重新核对");
        check(retried.Step == retry.Step && retained.Goal == retry.Goal,
            "a failed pending step does not exhaust the completed-step limit");
        retained.Complete(target, retried.Request, answer: new GuideAnswer("explain", "我已完成只是用户的说法，现在仍缺少结果。", null));
        check(!retained.Completed && retained.CanOffer, "completion words inside ordinary explanations do not end the task");
        retained.Complete(target, "核对结果", answer: done);
        check(retained.Completed && retained.ActiveGoal is null && !retained.CanOffer,
            "validated completion ends the active goal without forcing another operation");
        Reject(() => retained.Begin(), "an already completed task cannot continue through an old callback");
        retained.Clear();
        retained.Complete(target, "新目标", answer: new GuideAnswer("explain", "新说明", null));
        for (var index = 0; index < GuideFollowup.MaxHistory + 1; index++)
        {
            retained.Begin(); var next = retained.Take("第" + index + "次提问");
            retained.Complete(target, next.Request, answer: new GuideAnswer("explain", "第" + index + "次说明", null));
        }
        retained.Restore(); retained.Begin();
        var bounded = retained.Take("继续");
        check(bounded.History is { Count: GuideFollowup.MaxHistory } && bounded.History[0].Request == "第1次提问" &&
              bounded.History.All(turn => !turn.Explanation.Contains("新说明")),
            "only the recent four completed text steps survive; no frame or marker enters the context");
        retained.Clear();
        retained.Complete(target, "另一任务"); retained.Begin();
        check(retained.Take("继续").History!.Count == 0, "ending or rebinding clears every earlier task step");
        check(Decision(new GuideAnswer("explain", "范围、预览和最终结果均已核对完成，本次核对任务已结束。", null), null) &&
              Decision(done, null) && !Decision(done with { CompletionEvidence = null }, null) &&
              !Decision(new GuideAnswer("explain", "仍无法确认是否已完成。", null), null),
            "completion oracle accepts equivalent explicit completion wording but refuses uncertainty");
    }

    internal static async Task RunControlledAsync(Action<bool, string> check)
    {
        var policy = PluginCatalog.Load(Path.Combine(AppContext.BaseDirectory, "plugins")).GeneralProvider!.InterpretationPolicy!;
        var flow = new GuideFollowup();
        var target = new GuideFlowTarget(new IntPtr(123), 456, 789, "generated.wizard");
        var calls = 0;
        var frames = new[] { "当前：范围尚未核对", "当前：范围已核对，预览待确认", "当前：范围已核对，预览待确认", "当前：范围和预览均已核对完成" };
        var replies = new[]
        {
            "{\"mode\":\"point\",\"explanation\":\"先核对范围。\",\"candidateId\":11}",
            "{\"mode\":\"ask\",\"explanation\":\"你要核对哪份预览？\",\"candidateId\":null}",
            "{\"mode\":\"point\",\"explanation\":\"接着核对这份预览。\",\"candidateId\":22}",
            "{\"mode\":\"complete\",\"explanation\":\"范围和预览都已核对，本次任务完成。\",\"candidateId\":null,\"evidence\":\"当前结果明确显示范围和预览均已核对完成。\"}"
        };
        using var model = new GuideModel(GuideOptions.Create("http://127.0.0.1:8787/v1", "generated", false),
            new Handler(async request =>
            {
                using var body = JsonDocument.Parse(await request.Content!.ReadAsStringAsync());
                var messages = body.RootElement.GetProperty("messages");
                var contract = messages[0].GetProperty("content").GetString()!;
                using var input = JsonDocument.Parse(messages[1].GetProperty("content")[0].GetProperty("text").GetString()!);
                check(input.RootElement.GetProperty("visibleContext").GetString() == frames[calls] &&
                      contract.Contains("Only the current supplied observation") && contract.Contains("evidence"),
                    "controlled guidance sends the new observation and an explicit evidence-based completion contract");
                var task = input.RootElement.GetProperty("task").GetString()!;
                if (calls > 0)
                {
                    using var step = JsonDocument.Parse(task[task.IndexOf('{')..]);
                    check(step.RootElement.GetProperty("originalUserGoal").GetString() == "核对范围和预览" &&
                          step.RootElement.GetProperty("recentSteps").GetArrayLength() == (calls == 3 ? 2 : 1),
                        "controlled continuation preserves its goal and completed steps across page changes and clarification");
                }
                return Completion(replies[calls++]);
            }));
        GuideCandidate[] candidates = [new(11, "核对范围", "", "ControlType.Button", new Rect(10, 10, 80, 30), [11]),
            new(22, "核对预览", "", "ControlType.Button", new Rect(10, 60, 80, 30), [22])];
        const string goal = "核对范围和预览";
        flow.Prepare(target, goal);
        var first = await model.AskAsync(goal, candidates, null, default, frames[0], policy);
        flow.Complete(target, goal, answer: first);
        flow.Restore(); flow.Begin();
        var next = flow.Take("我操作完了，下一步呢？");
        var clarification = (await model.AskAsync(next.ModelTask, candidates, null, default, frames[1], policy)).ForIntent(next.Request, allowQuestion: true);
        check(clarification.Mode == "ask" && flow.Step == 1, "an ambiguous new step can ask once without recording an uncompleted step");
        next = next with { Clarification = "当前这一份" };
        var second = (await model.AskAsync(next.ModelTask, candidates, null, default, frames[2], policy)).ForIntent(next.Request);
        flow.Complete(target, next.Request + "；" + next.Clarification, answer: second);
        flow.Restore(); flow.Begin(); next = flow.Take("现在完成了吗？");
        var done = await model.AskAsync(next.ModelTask, candidates, null, default, frames[3], policy);
        flow.Complete(target, next.Request, answer: done);
        check(calls == 4 && first.CandidateId == 11 && second.CandidateId == 22 && done.CandidateId is null &&
              flow.Completed && !flow.CanOffer && flow.Step == 3,
            "controlled three-step loop handles a clarification then stops on current completion without a marker or automatic action");

        using var route = new GuideModel(GuideOptions.Create("http://127.0.0.1:8787/v1", "generated", false),
            new Handler(async request =>
            {
                using var body = JsonDocument.Parse(await request.Content!.ReadAsStringAsync());
                var messages = body.RootElement.GetProperty("messages");
                check(messages[0].GetProperty("content").GetString()!.Contains("completion check for that goal requires a fresh window observation") &&
                      messages[1].GetProperty("content").GetString()!.Contains("activeUserGoal"),
                    "text routing receives the active goal and must hand current-state checks to fresh window guidance");
                return Completion("{\"reply\":null}");
            }));
        check(await route.RouteQuestionAsync([], "做完了，下一步？", default, goal) is null,
            "controlled progress reports hand off instead of treating prior text as completion evidence");

        // A completed task must not contaminate the next goal on the same bound window.
        flow.Clear();
        flow.Prepare(target, "检查另一份结果");
        var recoveryCalls = 0;
        using var recovery = new GuideModel(GuideOptions.Create("http://127.0.0.1:8787/v1", "generated", false),
            new Handler(async request =>
            {
                recoveryCalls++;
                if (recoveryCalls == 1) throw new HttpRequestException("generated_failure");
                using var body = JsonDocument.Parse(await request.Content!.ReadAsStringAsync());
                using var input = JsonDocument.Parse(body.RootElement.GetProperty("messages")[1]
                    .GetProperty("content")[0].GetProperty("text").GetString()!);
                check(input.RootElement.GetProperty("visibleContext").GetString() == "当前：另一份结果仍待确认" &&
                      !input.RootElement.GetProperty("task").GetString()!.Contains("范围和预览"),
                    "retry for a new goal sends fresh evidence without the completed earlier task");
                return Completion("{\"mode\":\"explain\",\"explanation\":\"另一份结果尚未确认，请先核对它。\",\"candidateId\":null}");
            }));
        flow.Restore(); flow.Begin(); var failed = flow.Take("检查当前结果");
        try
        {
            await recovery.AskAsync(failed.ModelTask, [], null, default, "旧画面", policy);
            throw new InvalidOperationException("generated transport did not fail");
        }
        catch (HttpRequestException) { flow.Restore(); }
        check(flow.Step == 0 && flow.CanOffer && !flow.Completed,
            "a failed first request retains the new goal without marking a step complete");
        flow.Begin(); var retried = flow.Take("重新检查当前结果");
        var retryAnswer = await recovery.AskAsync(retried.ModelTask, [], null, default, "当前：另一份结果仍待确认", policy);
        flow.Complete(target, retried.Request, answer: retryAnswer);
        check(flow.Step == 1 && flow.ActiveGoal == "检查另一份结果" && !flow.Completed && flow.CanOffer && recoveryCalls == 2,
            "explicit retry advances once on the actual result and leaves an unfinished new goal available");
        flow.Clear();
        check(flow.Goal is null && !flow.CanOffer, "ending the controlled new task removes its goal and continuation authority");
    }

    static HttpResponseMessage Completion(string answer) => new(HttpStatusCode.OK)
    {
        Content = new StringContent(JsonSerializer.Serialize(new { choices = new[] { new { finish_reason = "stop", message = new { content = answer } } } }))
    };

    sealed class Handler(Func<HttpRequestMessage, Task<HttpResponseMessage>> respond) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) => respond(request);
    }

    internal static async Task RunLiveAsync(Action<bool, string> check, int? onlyStep = null)
    {
        if (onlyStep is < 1 or > 3) throw new ArgumentException("unknown_generated_followup_step");
        var policy = PluginCatalog.Load(Path.Combine(AppContext.BaseDirectory, "plugins")).GeneralProvider!.InterpretationPolicy!;
        var target = new GuideFlowTarget(new IntPtr(123), 456, 789, "generated.wizard");
        var flow = new GuideFollowup();
        const string goal = "帮我核对这个生成向导的范围、预览和最终结果，每次只指当前一步，不替我操作。";
        var cases = new (string State, string Label, int? Id)[]
        {
            ("生成核对向导：步骤1/3，范围尚未核对。", "核对范围", 11),
            ("生成核对向导：步骤2/3，预览尚未核对，整体任务没有完成。", "核对预览", 22),
            ("生成核对向导：步骤3/3，范围、预览和最终结果均已核对完成，本次核对任务完成。", "", null)
        };
        using var model = new GuideModel(GuideOptions.Create("http://127.0.0.1:8787/v1", "deepseek-v4.1-flash", timeoutSeconds: 45));
        for (var i = 0; i < cases.Length; i++)
        {
            var item = cases[i]; var frame = new Rect(0, 0, 900, 240);
            var bounds = new Rect(40, 145, 300, 50);
            var candidates = item.Id is { } id ? new[] { new GuideCandidate(id, item.Label, "", "ControlType.Button", bounds, [id]) } : [];
            var visual = new DrawingVisual();
            using (var draw = visual.RenderOpen())
            {
                draw.DrawRectangle(Brushes.White, null, frame);
                void Text(string value, Point point) => draw.DrawText(new FormattedText(value, CultureInfo.GetCultureInfo("zh-CN"),
                    FlowDirection.LeftToRight, new Typeface("Microsoft YaHei"), 22, Brushes.Black, 1) { MaxTextWidth = 820 }, point);
                Text(item.State, new Point(35, 40));
                if (item.Id is not null) { draw.DrawRectangle(Brushes.AliceBlue, new Pen(Brushes.SteelBlue, 1), bounds); Text(item.Label, new Point(55, 155)); }
            }
            var bitmap = new RenderTargetBitmap(900, 240, 96, 96, PixelFormats.Pbgra32); bitmap.Render(visual);
            var task = goal;
            if (i > 0) { flow.Begin(); task = flow.Take("我说我完成了，下一步呢？请以当前画面为准。").ModelTask; }
            if (onlyStep is not null && onlyStep != i + 1) { flow.Complete(target, goal); continue; }
            var clock = Stopwatch.StartNew();
            var answer = await model.AskAsync(task, candidates, Capture.EncodeRegion(bitmap, frame, frame), CancellationToken.None,
                interpretationPolicy: policy);
            Console.WriteLine(JsonSerializer.Serialize(new { generatedFollowup = i + 1, answer.Mode, answer.CandidateId, answer.Explanation, elapsedMs = clock.ElapsedMilliseconds }));
            check(Decision(answer, item.Id),
                $"LIVE follow-up step{i + 1}: decision agrees with current generated state; elapsedMs={clock.ElapsedMilliseconds}; no native acceptance");
            flow.Complete(target, goal, answer: answer);
        }
    }

    static bool Decision(GuideAnswer answer, int? id) => answer.CandidateId == id &&
        (id is not null ? answer.Mode == "point" :
            answer.Mode == "complete" ? !string.IsNullOrWhiteSpace(answer.CompletionEvidence) :
            answer.Mode == "explain" && new[] { "已完成", "已核对完成", "任务已结束" }.Any(answer.Explanation.Contains) &&
            !new[] { "无法确认", "尚未完成", "没有完成", "不能确认" }.Any(answer.Explanation.Contains));

    internal static void ReplayThirdStep(Action<bool, string> check)
    {
        // Only this generated-data diagnostic is replayed; preserve the original failed check and avoid another paid request.
        var line = File.ReadLines(".artifacts/checks/followup-final-release.log")
            .Single(value => value.StartsWith("{\"generatedFollowup\":3,", StringComparison.Ordinal));
        using var recorded = JsonDocument.Parse(line);
        var root = recorded.RootElement;
        var answer = GuideAnswer.Parse(JsonSerializer.Serialize(new { mode = root.GetProperty("Mode").GetString(),
            explanation = root.GetProperty("Explanation").GetString(), candidateId = (int?)null }), []);
        check(root.GetProperty("CandidateId").ValueKind == JsonValueKind.Null && Decision(answer, null),
            "REPLAY generated step3: recorded5240ms response explains visible completion without a marker; no new model request or native acceptance");
    }
}
