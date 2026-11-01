using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using CetaFae.Probe;

namespace CetaFae.Checks;

static class CardChecks
{
    static GuideCard New() => new(GuideOptions.Create("http://127.0.0.1:8787/v1", "deepseek-v4.1-flash"));
    static IEnumerable<(string Name, GuideCard Card)> States()
    {
        var menu = New();
        yield return ("01 Menu - cloud off", menu);
        var consent = New(); consent.ShowConsent();
        yield return ("02 Personal cloud disclosure", consent);
        var processing = New(); processing.SetCloud(true); processing.SetStatus("正在识别当前窗口，请稍候…");
        yield return ("03 Processing", processing);
        var question = New(); question.SetCloud(true); question.SetStatus("当前是浏览器的下载设置。你希望在此页面处理什么？"); question.ShowQuestion();
        yield return ("04 One clarification", question);
        var result = New(); result.SetCloud(true); result.SetStatus("你要找的开关位于右侧，当前为开启状态。这里只指出位置，不会修改。"); result.SetRegionAvailable(true);
        yield return ("05 Result and optional selection", result);
        var selection = New(); selection.SetCloud(true); selection.SetStatus("没有可靠的自动识别范围。请选择此窗口内将发送的区域，无需重新回答。"); selection.SetRegionAvailable(true);
        yield return ("06 Waiting for region", selection);
        var refusal = New(); refusal.SetCloud(true); refusal.SetStatus(GuideSession.FailureMessage(new InvalidDataException("sensitive_content")));
        yield return ("07 Privacy refusal", refusal);
        var longText = New(); longText.SetCloud(true); longText.SetStatus(string.Concat(Enumerable.Repeat("生成长文本，仅用于检查完整换行、滚动与按钮可达性。", 24))); longText.ShowQuestion();
        yield return ("08 Long generated result", longText);
        var enabled = New(); enabled.SetCloud(true);
        yield return ("09 Menu - cloud on", enabled);
        var identity = New(); identity.SetCloud(true);
        identity.SetStatus(new GuideIdentification("生成文档工具", "显示生成文档内容", "你希望在这里处理什么？")
            .ToAnswer("generated-editor").Explanation); identity.ShowQuestion();
        yield return ("10 Window identification", identity);
        var unknown = New(); unknown.SetCloud(true);
        unknown.SetStatus(new GuideIdentification(null, null, "你希望了解这个窗口的什么内容？")
            .ToAnswer("generated-editor").Explanation); unknown.ShowQuestion();
        yield return ("11 Unknown window purpose", unknown);
        var unavailable = New(); unavailable.SetCloud(true);
        unavailable.SetStatus(GuideIdentification.Unavailable("generated-editor", "识别服务不可用，请检查本机模型代理。"));
        yield return ("12 Local ownership after model failure", unavailable);
        var layered = New(); layered.SetCloud(true);
        layered.SetStatus(new GuideIdentification("生成文档工具", "显示生成文档", "你希望处理什么？")
            .ToAnswer("generated-editor", SoftwareIdentityChecks.Generated).Explanation);
        layered.ShowQuestion(); layered.SetRegionAvailable(true); layered.SetReferenceQuery(SoftwareIdentityChecks.Generated.ReferenceQuery);
        yield return ("13 Layered identity and optional lookup", layered);
        var reference = New(); reference.SetCloud(true); reference.SetStatus("已有本机和画面判断，等待可选查询确认。");
        reference.SetReferenceQuery(SoftwareIdentityChecks.Generated.ReferenceQuery); Click(Find(reference, "联网核对软件"));
        yield return ("14 Explicit public receiver disclosure", reference);
        var referenced = New(); referenced.SetCloud(true); referenced.SetStatus("原软件与页面说明保留。");
        referenced.SetReferenceResult("联网候选（未确认与本机软件相同）：\nGenerated Editor：An example software tool.\n来源：https://en.wikipedia.org/?curid=42\n不代表软件真伪或安全。");
        yield return ("15 Public reference candidate result", referenced);
        var bubble = New(); bubble.SetCloud(true); bubble.SetStatus("右侧高亮的是询问保存位置的开关，当前为开启状态。这里只指出位置，没有修改设置。"); bubble.SetPoint(true);
        yield return ("16 Contextual droplet bubble", bubble);
        var collapsed = New(); collapsed.SetStatus("原说明保留，重新展开不发请求。"); Click(Find(collapsed, "收起"));
        yield return ("17 Attached reopen tab", collapsed);
        var error = New(); error.SetCloud(true);
        error.SetStatus(GuideAnswer.Parse("""{"mode":"error","observedText":"无法连接到服务器，错误代码0x80072EE7。","hypotheses":["可能存在地址解析或连接问题，尚未确认。"],"nextCheck":"请确认提示是在执行什么操作时出现，不必提供私人地址。","missingEvidence":"仅凭一条提示不能判断具体根因。","candidateId":null}""", [],
            new CetaFae.Plugins.InterpretationPolicy(["zh-CN"], false, true)).Explanation);
        yield return ("18 Bounded visible-error explanation", error);
    }

    static void Layout(GuideCard card, double width = GuideTheme.CardWidth)
    {
        card.Measure(new Size(width, GuideTheme.CardMaxHeight));
        card.Arrange(new Rect(0, 0, width, Math.Min(card.DesiredSize.Height, GuideTheme.CardMaxHeight)));
        card.UpdateLayout();
    }

    static Button Find(GuideCard card, string label) => card.Actions.Children.Cast<Button>().Single(b => Equals(b.Content, label));
    static void Click(Button button) => button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));

    internal static void Run(Action<bool, string> check)
    {
        var card = New();
        var consent = 0; var regions = 0; var submitted = 0;
        card.CloudToggled += () => consent++;
        card.RegionRequested += () => regions++;
        card.AnswerSubmitted += _ => submitted++;
        Click(Find(card, "同意并启用云端"));
        check(consent == 0, "hidden or stale consent callback cannot enable cloud before disclosure");
        Click(Find(card, "查看云端授权"));
        check(card.View == GuideCardView.Consent && consent == 0, "opening cloud disclosure alone never grants consent");
        Click(Find(card, "同意并启用云端"));
        check(consent == 1, "only an explicit action in the disclosure requests cloud consent");
        card.SetCloud(true);
        card.SetStatus("Generated question"); card.ShowQuestion(); card.SetRegionAvailable(true);
        check(Find(card, "菜单").Visibility == Visibility.Collapsed &&
              Find(card, "关闭云端").Visibility == Visibility.Collapsed,
            "attached session feedback leaves management exclusively in the tray and whale menu");
        card.Answer.Text = "生成回答";
        Click(Find(card, "收起"));
        check(card.IsCollapsed && card.FullText == "Generated question" && card.Answer.Text == "生成回答" &&
              submitted == 0 && regions == 0 && card.HasSession, "collapse retains session, explanation and pending answer without dispatching anything");
        Click(Find(card, "展开说明  ﹀"));
        check(!card.IsCollapsed && card.HasQuestion && card.Answer.Text == "生成回答", "attached reopen restores the existing reading surface without another request");
        card.SetPoint(true);
        check(card.IsBubble && !card.IsCollapsed && card.FullText == "Generated question", "context bubble preserves the full actual explanation rather than inventing a summary");
        Click(Find(card, "展开详情"));
        check(!card.IsBubble && card.HasQuestion && card.FullText == "Generated question", "bubble details switch the same surface to its full attached reader");
        card.ShowMenu();
        check(!card.HasQuestion && !card.HasRegion && card.HasSession, "menu hides session actions without submitting or clearing its pending answer");
        Click(Find(card, "选择识别区域"));
        check(regions == 0, "stale hidden region action cannot start selection from the menu");
        Click(Find(card, "返回"));
        check(card.HasQuestion && card.HasRegion && card.Answer.Text == "生成回答" && submitted == 0,
            "returning from menu retains the current question without dispatching it");
        card.Reset();
        check(!card.HasQuestion && !card.HasRegion && !card.HasSession && card.Answer.Text.Length == 0,
            "ending a card removes old answer, region and reusable session state");
        var queries = new List<string>(); card.ReferenceRequested += queries.Add;
        card.SetCloud(true); card.SetStatus("Generated current result"); card.ShowQuestion();
        card.SetReferenceQuery("Generated Editor software");
        Click(Find(card, "确认本次联网"));
        check(queries.Count == 0, "a hidden confirmation cannot authorize a public reference request");
        Click(Find(card, "联网核对软件"));
        check(queries.Count == 0 && card.View == GuideCardView.ReferenceConsent && !card.HasQuestion,
            "opening reference disclosure does not send or submit the pending answer");
        Click(Find(card, "返回"));
        check(card.HasQuestion && queries.Count == 0, "return cancels the disclosure without losing the current question");
        Click(Find(card, "联网核对软件")); Click(Find(card, "确认本次联网")); Click(Find(card, "确认本次联网"));
        check(queries.SequenceEqual(new[] { "Generated Editor software" }) && card.View == GuideCardView.Session,
            "explicit query confirmation is consumed once and returns to the existing result");
        card.SetReferenceQuery("Generated Editor software"); Click(Find(card, "联网核对软件")); card.SetCloud(false);
        Click(Find(card, "确认本次联网"));
        check(queries.Count == 1, "cloud revocation invalidates a visible reference consent callback");
        card.SetCloud(true); card.SetReferenceQuery("Generated Editor software"); Click(Find(card, "联网核对软件")); card.Reset();
        Click(Find(card, "确认本次联网"));
        check(queries.Count == 1 && !card.HasSession, "reset clears query, confirmation and public result state");
        check(!card.IsBubble && !card.IsCollapsed && card.FullText.Length == 0,
            "ending feedback removes compact state and old explanation");
        var work = new Rect(-1280, 0, 1280, 720);
        var entry = new Rect(-760, 0, 288, 36);
        var control = new Rect(-160, 240, 50, 24);
        var attached = FeedbackLayout.Attached(entry, work, new Size(288, 260));
        check(work.Contains(attached) && attached.Left == entry.Left && attached.Top == entry.Bottom + 1,
            "attached reader joins directly below the real input body on negative-coordinate work areas");
        var bubbleBounds = FeedbackLayout.Bubble(control, entry, work, new Size(258, 210));
        check(bubbleBounds is { } safe && work.Contains(safe) && !safe.IntersectsWith(entry) && !safe.IntersectsWith(control),
            "control bubble stays in the work area without covering its target or input");
        check(FeedbackLayout.Bubble(new Rect(0, 40, 100, 30), new Rect(0, 0, 100, 36),
            new Rect(0, 0, 100, 100), new Size(258, 210)) is null,
            "cramped control placement requires the attached-reader fallback");
        check(work.Contains(FeedbackLayout.Attached(new Rect(-96, 632, 88, 88), work, new Size(288, 320))),
            "failed-session feedback stays visible beside the bottom-edge home whale");
        check(!BallWindow.IsDrag(new Point(10, 10), new Point(11, 11), 1) &&
              BallWindow.IsDrag(new Point(10, 10), new Point(30, 10), 1) &&
              !BallWindow.IsDrag(new Point(10, 10), new Point(12, 12), 2),
            "menu clicks tolerate pointer jitter and real dragging uses scaled system thresholds");
        var targetFrame = new OverlayWindow();
        var controlFrame = new OverlayWindow(control: true);
        try
        {
            var targetBorder = (Border)targetFrame.Content;
            var controlBorder = (Border)controlFrame.Content;
            check(targetBorder.BorderBrush == Brushes.Transparent && targetBorder.BorderThickness == new Thickness(0) &&
                  controlBorder.BorderBrush == GuideTheme.Accent &&
                  targetBorder.BorderThickness != controlBorder.BorderThickness && targetBorder.CornerRadius != controlBorder.CornerRadius,
                "target glow has no outline while validated-control frames retain a contrasting border");
            check(new System.Windows.Interop.WindowInteropHelper(targetFrame).Handle == IntPtr.Zero &&
                  new System.Windows.Interop.WindowInteropHelper(controlFrame).Handle == IntPtr.Zero,
                "frame style verification does not create native windows");
        }
        finally { targetFrame.Close(); controlFrame.Close(); }
        foreach (var (name, state) in States())
        {
            foreach (var width in new[] { 258d, WhalePrompt.WidthDip, 320d, GuideTheme.CardWidth })
            {
                Layout(state, width);
                var actions = state.Actions.Children.Cast<Button>().Where(b => b.Visibility == Visibility.Visible).ToArray();
                check(actions.Length > 0 && actions.All(button =>
                {
                    var bounds = button.TransformToAncestor(state).TransformBounds(new Rect(button.RenderSize));
                    return bounds.Left >= 0 && bounds.Right <= width + 1 && bounds.Top >= 0 && bounds.Bottom <= state.ActualHeight + 1;
                }), $"{name}: all required actions fit outside scroll content at width{width}");
                check(state.IsCollapsed || state.View == GuideCardView.Menu || state.BodyScroll.ViewportWidth > 0 && state.BodyScroll.ViewportHeight > 0 &&
                      state.BodyScroll.ExtentWidth <= state.BodyScroll.ViewportWidth + 1,
                    $"{name}: visible text has a positive viewport without horizontal clipping at width{width}");
            }
        }
        var longCard = States().Single(s => s.Name.StartsWith("08", StringComparison.Ordinal)).Card;
        Layout(longCard);
        check(longCard.BodyScroll.ExtentHeight > longCard.BodyScroll.ViewportHeight,
            "long generated text scrolls while the footer remains outside its viewport");
    }

    internal static void Render()
    {
        var states = States().ToArray();
        var height = (int)Math.Ceiling(states.Length / 3d) * 506;
        var visual = new DrawingVisual();
        using (var draw = visual.RenderOpen())
        {
            draw.DrawRectangle(new SolidColorBrush(Color.FromRgb(231, 241, 247)), null, new Rect(0, 0, 1140, height));
            var index = 0;
            foreach (var (name, card) in states)
            {
                Layout(card);
                var point = new Point(index % 3 * 380 + 10, index / 3 * 506 + 38);
                draw.DrawText(new FormattedText(name, CultureInfo.InvariantCulture, FlowDirection.LeftToRight,
                    new Typeface("Segoe UI"), 13, Brushes.Black, 1), new Point(point.X, point.Y - 24));
                // Render the arranged viewport, not VisualBrush's descendant bounds (which include scroll overflow).
                var cardImage = new RenderTargetBitmap((int)Math.Ceiling(card.ActualWidth),
                    (int)Math.Ceiling(card.ActualHeight), 96, 96, PixelFormats.Pbgra32);
                cardImage.Render(card);
                draw.DrawImage(cardImage, new Rect(point, card.RenderSize));
                index++;
            }
        }
        var bitmap = new RenderTargetBitmap(1140, height, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(visual);
        var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap));
        var path = Path.GetFullPath(".artifacts/checks/cards-layered-identity-20261030.png");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        using var output = File.Create(path); encoder.Save(output);
        Console.WriteLine("Generated offscreen card preview: " + path + "; no native window, capture or network");
    }
}
