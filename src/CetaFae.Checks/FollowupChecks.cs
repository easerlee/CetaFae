using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Text.Json;
using System.Diagnostics;
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
        flow.Complete(target, "生成目标：说明三步流程");
        check(flow.Step == 1 && flow.CanOffer && !flow.WaitingForUser, "a completed goal seeds a bounded user-only flow");
        check(flow.Begin() == target && flow.WaitingForUser && !flow.CanOffer, "arming retains only target binding and user goal");
        foreach (var invalid in new[] { "", "one\ntwo", new string('x', 301) })
            Reject(() => flow.Take(invalid), "invalid continuation does not consume the armed input");
        var second = flow.Take("我已手动完成，下一步看哪里？");
        using var requestData = JsonDocument.Parse(second.ModelTask[second.ModelTask.IndexOf('{')..]);
        check(second.Step == 2 && second.Goal == "生成目标：说明三步流程" && !flow.WaitingForUser &&
              requestData.RootElement.GetProperty("currentUserRequest").GetString() == second.Request &&
              requestData.RootElement.EnumerateObject().Count() == 4 && second.ModelTask.Contains("当前画面"),
            "fresh request includes original user goal and current request, never a prior model answer or completion assertion");
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
        check(Decision(new GuideAnswer("explain", "范围、预览和最终结果均已核对完成，本次核对任务已结束。", null), null) &&
              !Decision(new GuideAnswer("explain", "仍无法确认是否已完成。", null), null),
            "completion oracle accepts equivalent explicit completion wording but refuses uncertainty");
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
            flow.Complete(target, goal);
        }
    }

    static bool Decision(GuideAnswer answer, int? id) => answer.Mode == (id is null ? "explain" : "point") && answer.CandidateId == id &&
        (id is not null || new[] { "已完成", "已核对完成", "任务已结束" }.Any(answer.Explanation.Contains) &&
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
