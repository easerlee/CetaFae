using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using CetaFae.Probe;

namespace CetaFae.Checks;

internal static class FeedbackChecks
{
    static readonly GuideOptions Options = GuideOptions.Create("http://127.0.0.1:8787/v1", "deepseek-v4.1-flash");
    static void Click(GuideCard card, string label) => card.Actions.Children.Cast<Button>()
        .Single(button => Equals(button.Content, label)).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));

    internal static void Run(Action<bool, string> check, bool renderMeasured = false)
    {
        CheckMeasuredReader(check, renderMeasured);
        CheckDetailsInput(check);
        var work = SystemParameters.WorkArea;
        var entry = new Rect(work.Left + 80, work.Top, 288, 36);
        var panel = new GuidePanel(Options);
        try
        {
            var scale = 2d;
            panel.BindEntry(() => entry, () => scale, () => true);
            panel.Card.SetStatus("生成窗口说明，全部文字仅用于离屏检查。");
            panel.BeginProcessing();
            check(!panel.IsVisible && !panel.Card.HasSession && panel.Card.FullText.Length == 0 && panel.Handle == IntPtr.Zero,
                "processing clears stale feedback without showing a card or creating a native handle");
            panel.Card.SetStatus("生成窗口说明，全部文字仅用于离屏检查。");
            check(panel.Left == entry.Left && panel.Top == entry.Bottom && panel.Width == entry.Width,
                "live reader uses the input body anchor and matches the prompt width");
            check(!panel.IsVisible && !panel.ShowActivated && panel.AllowsTransparency && panel.Handle == IntPtr.Zero,
                "feedback placement and rendering need no native handle, visible launch or automatic activation");
            var topEntry = new Rect(work.Left + (work.Width - WhalePrompt.WidthDip) / 2, work.Top, WhalePrompt.WidthDip, 36);
            panel.BindEntry(() => topEntry, () => scale, () => true, () => true);
            panel.Reposition();
            check(panel.Left >= topEntry.Right + GuideTheme.CardGap && panel.Top == topEntry.Top &&
                work.Contains(new Rect(panel.Left, panel.Top, panel.Width, panel.Height)),
                "top-attached feedback sits to the input right with a gap and stays inside the screen");
            panel.BindEntry(() => entry, () => scale, () => true);
            panel.Reposition();
            var control = new Rect(work.Right - 120, work.Top + 400, 50, 24);
            panel.SetControl(new Rect(control.X * 2, control.Y * 2, control.Width * 2, control.Height * 2));
            panel.SetPoint(true);
            var bubble = new Rect(panel.Left, panel.Top, panel.Width, panel.Height);
            check(panel.Card.IsBubble && work.Contains(bubble) && !bubble.IntersectsWith(control) && !bubble.IntersectsWith(entry),
                "validated target pixels convert once into a safe DIP bubble placement");
            foreach (var currentScale in new[] { 1d, 1.25, 1.5, 2 })
            {
                scale = currentScale;
                panel.SetControl(new Rect(control.X * scale, control.Y * scale, control.Width * scale, control.Height * scale));
                check(new Rect(panel.Left, panel.Top, panel.Width, panel.Height) == bubble,
                    "same logical control preserves bubble placement at 100/125/150/200 percent without double scaling");
                var grab = new Point(18, 22); var origin = new Point(140, 90);
                var dragged = BallWindow.DragPosition(new Point((origin.X + grab.X) * scale, (origin.Y + grab.Y) * scale), grab, scale);
                check(dragged == origin,
                    "whale drag preserves pointer grab offset across 100/125/150/200 percent scaling");
            }
            var generatedWork = new Rect(0, 0, 800, 600); var generatedEntry = new Rect(100, 200, 288, 36);
            var protectedControl = new Rect(100, 250, 200, 50);
            var avoided = FeedbackLayout.Reader(protectedControl, generatedEntry, generatedWork, new Size(288, 180));
            check(avoided is { } safeReader && generatedWork.Contains(safeReader) &&
                !safeReader.IntersectsWith(protectedControl) && !safeReader.IntersectsWith(generatedEntry),
                "expanded reader changes side rather than covering the located control or input bar");
            check(FeedbackLayout.Reader(generatedWork, generatedEntry, generatedWork, new Size(288, 180)) is null,
                "a cramped reader reports unavailable placement instead of pretending the control is visible");
            Click(panel.Card, "展开详情");
            check(!panel.Card.IsBubble && panel.Width == entry.Width && panel.Top == entry.Bottom && panel.Card.FullText.Contains("生成窗口"),
                "native details replace the same bubble surface with the attached full explanation");
            panel.Card.Collapse();
            check(panel.Card.IsCollapsed && panel.Height <= 40 && panel.Card.HasSession,
                $"native collapse leaves a compact attached reopen tab and keeps the session (collapsed={panel.Card.IsCollapsed}, height={panel.Height}, session={panel.Card.HasSession})");
            entry.Offset(120, 24); panel.Reposition();
            check(panel.Left == entry.Left && panel.Top == entry.Bottom,
                "reopen tab follows changes to the existing input anchor");
            Click(panel.Card, "展开说明  ﹀");
            panel.SetControl(new Rect(work.X * scale, work.Y * scale, work.Width * scale, work.Height * scale));
            panel.SetPoint(true);
            check(panel.Card.IsCollapsed && panel.Card.HasSession && panel.Card.FullText.Contains("生成窗口"),
                "no safe reader or bubble collapses once while preserving full text and session");
            Click(panel.Card, "展开说明  ﹀");
            check(!panel.Card.IsCollapsed && panel.Card.FullText.Contains("生成窗口"),
                "explicit reader reopening cannot enter an automatic-collapse loop");
            panel.EndSession();
            panel.Card.SetStatus(GuideSession.FailureMessage(new InvalidDataException("sensitive_content")));
            panel.SetPoint(true);
            check(!panel.Card.IsBubble && !panel.Card.IsCollapsed && !panel.Card.FullText.Contains("生成窗口") && !panel.Card.HasRegion,
                "privacy refusal cannot retain a stale target bubble, selection action or prior explanation");
            check(new WindowInteropHelper(panel).Handle == IntPtr.Zero,
                "all native feedback state checks remain offscreen");
        }
        finally { panel.Close(); }
    }

    static void CheckDetailsInput(Action<bool, string> check)
    {
        var ball = new BallWindow();
        var panel = new GuidePanel(Options);
        var submitted = new List<string>();
        ball.AnswerSubmitted += submitted.Add;
        try
        {
            panel.BindEntry(() => new Rect(300, 100, WhalePrompt.WidthDip, 36), () => 1, () => true);
            ball.ShowQuestion(); ball.ClearQuestion(); ball.SetResult(canContinue: true);
            panel.Card.SetCloud(true); panel.Card.SetStatus("生成控件说明，展开后应能直接继续提问。");
            panel.SetControl(new Rect(800, 400, 45, 24)); panel.SetPoint(true);
            panel.SetFollowupAvailable(true);
            ball.Prompt.Answer.Text = "请继续说明这个开关。";
            Click(panel.Card, "展开详情");
            check(!panel.Card.IsBubble && panel.Card.FullText.Contains("生成控件说明") &&
                ball.Prompt.CanAnswer && !ball.Prompt.Answer.IsReadOnly && ball.Prompt.Answer.Focusable &&
                ball.Prompt.Answer.Text == "请继续说明这个开关。" && submitted.Count == 0,
                "expanded result retains its explanation and an editable inline continuation without target navigation");
            ball.Prompt.Send.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            ball.Prompt.Send.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            check(submitted.SequenceEqual(new[] { "请继续说明这个开关。" }) && !ball.Prompt.CanAnswer,
                "expanded-result input submits once and immediately prevents duplicate submission");
            ball.SetResult(canContinue: false);
            ball.Prompt.Answer.Text = "不可发送的生成输入";
            ball.Prompt.Send.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            check(ball.Prompt.Answer.IsReadOnly && !ball.Prompt.CanAnswer && submitted.Count == 1,
                "a result without continuation authority cannot submit through the inline input");
            ball.SetResult(canContinue: true); ball.SetProcessing(true);
            ball.Prompt.Answer.Text = "处理中不可发送";
            ball.Prompt.Send.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            check(!ball.Prompt.CanAnswer && ball.Prompt.Answer.IsReadOnly && submitted.Count == 1,
                "processing disables a previously editable result input without another request");
            check(ball.Handle == IntPtr.Zero && panel.Handle == IntPtr.Zero,
                "details-input regression never launches a native window or reads a real target");
        }
        finally { panel.Close(); ball.Close(); }
    }

    static void CheckMeasuredReader(Action<bool, string> check, bool render)
    {
        var panel = new GuidePanel(Options);
        try
        {
            panel.BindEntry(() => new Rect(100, 100, WhalePrompt.WidthDip, 36), () => 1, () => true);
            var surface = (FrameworkElement)panel.Content;
            void Arrange()
            {
                surface.Measure(new Size(panel.Width, panel.Height));
                surface.Arrange(new Rect(0, 0, panel.Width, panel.Height));
                surface.UpdateLayout();
                panel.Dispatcher.Invoke(() => { }, DispatcherPriority.ContextIdle);
                surface.Measure(new Size(panel.Width, panel.Height));
                surface.Arrange(new Rect(0, 0, panel.Width, panel.Height));
                surface.UpdateLayout();
            }
            for (var round = 0; round < 3; round++)
            {
                panel.Card.Reset();
                panel.Card.SetCloud(true);
                panel.Card.SetStatus(GuideIdentification.Processing);
                Arrange();
                panel.SetControl(null);
                panel.Card.SetStatus(new GuideIdentification("生成文档工具", string.Concat(Enumerable.Repeat("显示生成待办事项：整理材料、核对结果。", round == 0 ? 5 : round == 1 ? 40 : 1)),
                    "你希望在这个窗口里了解什么？", "regular").ToAnswer("generated-editor").Explanation);
                panel.SetPoint(false);
                panel.SetRegionAvailable(true);
                Arrange();
                var state = panel.Card.FeedbackState();
                check(state.TextPresent && state.TextVisible && state.TextInViewport && state.ViewportReady,
                    $"measured live reader keeps text visible after result/footer changes round{round}: host={panel.Height}, viewport={panel.Card.BodyScroll.ViewportHeight}, actions={panel.Card.Actions.ActualHeight}");
                var visibleActions = panel.Card.Actions.Children.Cast<Button>().Where(button => button.Visibility == Visibility.Visible);
                check(visibleActions.All(button => button.TransformToAncestor(surface).TransformBounds(new Rect(button.RenderSize)).Bottom <= panel.Height + 1),
                    "measured live reader keeps wrapped result actions inside the actual host");
                if (render)
                {
                    var image = new RenderTargetBitmap((int)Math.Ceiling(panel.Width), (int)Math.Ceiling(panel.Height), 96, 96, PixelFormats.Pbgra32);
                    image.Render(surface); Save(image, $"feedback-measured-{round}.png");
                }
                panel.Card.Collapse(); Arrange();
                check(panel.Card.IsCollapsed && panel.Height <= 40,
                    "measured reader retains intentional compact collapse after footer resizing");
                Click(panel.Card, "展开说明  ﹀"); Arrange();
                check(panel.Card.FeedbackState() is (true, true, true, true),
                    "measured reader reopening restores the current explanation viewport");
            }
            check(new WindowInteropHelper(panel).Handle == IntPtr.Zero,
                "measured-reader regression never creates a native window or reads a real target");
        }
        finally { panel.Close(); }
    }

    internal static int RunDesktop()
    {
        var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        var panel = new GuidePanel(Options) { Title = "CetaFae generated reader fixture" };
        var work = SystemParameters.WorkArea;
        panel.BindEntry(() => new Rect(work.Left + (work.Width - WhalePrompt.WidthDip) / 2,
            work.Top + 80, WhalePrompt.WidthDip, 36), () => 1, () => true);
        panel.Card.SetCloud(true);
        var result = 1;
        var running = false;
        panel.Loaded += async (_, _) =>
        {
            if (running) return;
            running = true;
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(15));
            try
            {
                void Result(int repeat)
                {
                    panel.SetStatus(string.Concat(Enumerable.Repeat("生成说明：这里只显示测试文字，不读取任何软件、不联网。", repeat)));
                    panel.SetPoint(false);
                    panel.SetRegionAvailable(true);
                }
                var stages = new (string Name, Action Change)[]
                {
                    ("processing", panel.BeginProcessing),
                    ("long-result-footer", () => Result(20)),
                    ("collapse", panel.Card.Collapse),
                    ("reopen", () => Click(panel.Card, "展开说明  ﹀")),
                    ("short-result", () => Result(1)),
                    ("reused-processing", panel.BeginProcessing),
                    ("reused-result-footer", () => Result(6))
                };
                foreach (var (name, change) in stages)
                {
                    deadline.Token.ThrowIfCancellationRequested();
                    change();
                    await panel.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ContextIdle);
                    await Task.Delay(450, deadline.Token);
                    var state = panel.Card.FeedbackState();
                    var actionsFit = panel.Card.Actions.Children.Cast<Button>().Where(button => button.Visibility == Visibility.Visible)
                        .All(button => button.TransformToAncestor(panel.Card).TransformBounds(new Rect(button.RenderSize)).Bottom <= panel.Card.ActualHeight + 1);
                    var waiting = name is "processing" or "reused-processing";
                    var passed = waiting ? !panel.IsVisible && !state.TextPresent :
                        panel.IsVisible && actionsFit && (panel.Card.IsCollapsed ? panel.ActualHeight <= 40 :
                            state is (true, true, true, true));
                    Console.WriteLine(System.Text.Json.JsonSerializer.Serialize(new { generatedReaderStage = name, passed,
                        height = panel.ActualHeight, viewportHeight = panel.Card.BodyScroll.ViewportHeight, actionsFit }));
                    if (!passed) throw new InvalidOperationException("generated_reader_layout");
                }
                Console.WriteLine("PASS: seven generated native reader states; no capture, target read, model request or consent change.");
                result = 0;
            }
            catch (Exception ex) when (ex is InvalidOperationException or OperationCanceledException)
            { Console.Error.WriteLine("FAIL: generated reader fixture " + ex.GetType().Name); }
            finally { panel.Close(); app.Shutdown(); }
        };
        panel.SetStatus("生成布局检查即将开始。");
        app.Run();
        return result;
    }

    static RenderTargetBitmap RenderElement(FrameworkElement element, double width, double height)
    {
        element.Measure(new Size(width, height));
        element.Arrange(new Rect(0, 0, width, height)); element.UpdateLayout();
        var image = new RenderTargetBitmap((int)Math.Ceiling(width), (int)Math.Ceiling(height), 96, 96, PixelFormats.Pbgra32);
        image.Render(element); return image;
    }

    static FormattedText Text(string text, double size, Brush color) => new(text, CultureInfo.InvariantCulture,
        FlowDirection.LeftToRight, new Typeface("Microsoft YaHei UI"), size, color, 1);

    internal static void Render()
    {
        var board = new DrawingVisual();
        using (var sheet = board.RenderOpen())
        {
            sheet.DrawRectangle(GuideTheme.Surface, null, new Rect(0, 0, 2300, 2640));
            var names = new[] { "Attached question / ordinary window", "Control bubble / maximized window", "Expanded long explanation", "Privacy refusal / home whale", "Region confirmation / attached", "Collapsed / marker retained", "Expanded reader / avoids highlighted control" };
            for (var i = 0; i < names.Length; i++)
            {
                var scene = new DrawingVisual();
                using (var draw = scene.RenderOpen())
                {
                    var work = new Rect(0, 0, 1120, 600);
                    var top = i is 1 or 5;
                    var target = top ? work : new Rect(16, 110, 1088, 480);
                    draw.DrawRectangle(new SolidColorBrush(Color.FromRgb(229, 240, 248)), null, work);
                    draw.DrawRoundedRectangle(Brushes.White, new Pen(GuideTheme.Muted, .5), target, 8, 8);
                    draw.DrawRectangle(new SolidColorBrush(Color.FromRgb(235, 239, 243)), null, new Rect(target.X, target.Y, target.Width, 32));
                    draw.DrawText(Text("生成软件界面                             —   □   ×", 12, GuideTheme.Muted), new Point(target.X + 16, target.Y + 8));
                    draw.DrawText(Text("下载设置（生成数据）", 20, GuideTheme.Ink), new Point(target.X + 30, target.Y + 88));
                    draw.DrawText(Text("下载前询问每个文件的保存位置", 13, GuideTheme.Ink), new Point(target.X + 30, target.Y + 192));
                    var control = new Rect(target.Right - 85, target.Top + 190, 45, 24);
                    draw.DrawRoundedRectangle(GuideTheme.WhaleBlue, null, control, 12, 12);
                    draw.DrawEllipse(Brushes.White, null, new Point(control.Right - 12, control.Y + 12), 9, 9);
                    var promptWidth = WhalePrompt.DockWidth(target.Width, work.Width);
                    var promptPosition = BallWindow.TopDockPosition(target, work, promptWidth);
                    var entry = new Rect(promptPosition.X, promptPosition.Y + WhalePrompt.BodyTop(88, top), promptWidth, WhalePrompt.BodyHeight);
                    if (i == 6)
                    {
                        control = new Rect(entry.Left + 12, entry.Bottom + 20, 150, 24);
                        draw.DrawRoundedRectangle(GuideTheme.WhaleBlue, null, control, 8, 8);
                        draw.DrawText(Text("生成目标控件", 12, Brushes.White), control.TopLeft + new Vector(10, 4));
                    }
                    if (i != 3)
                    {
                        var prompt = new WhalePrompt(); prompt.SetTopAttached(top); prompt.SetQuestion(i is 0 or 1 or 2 or 4 or 5 or 6);
                        if (i is 1 or 2 or 5 or 6) prompt.SetResult();
                        var host = new Grid(); host.Children.Add(prompt);
                        draw.DrawImage(RenderElement(host, promptWidth, 88), new Rect(promptPosition, new Size(promptWidth, 88)));
                    }
                    else
                    {
                        entry = new Rect(1024, 504, 88, 88);
                        var whale = new WhaleVisual(); whale.SetPose(new WhalePose(109), -1);
                        draw.DrawImage(RenderElement(whale, 88, 88), entry); whale.Release();
                    }
                    var card = new GuideCard(Options); card.SetCloud(true);
                    card.SetStatus(i switch
                    {
                        0 => "这是下载设置页面，可以选择保存位置和下载相关选项。你想在这里处理什么？",
                        2 => string.Concat(Enumerable.Repeat("生成说明：这里只解释界面并指出位置，不会替你修改软件。长文自动加宽，空间不足时可以翻页完整阅读。", 12)),
                        3 => GuideSession.FailureMessage(new InvalidDataException("sensitive_content")),
                        4 => "没有可靠的自动识别范围。请点击选择识别区域，在所选窗口内框住相关文字和控件；无需重新回答，确认前不会发本阶段的新请求。",
                        6 => "生成目标控件位于输入条下方。说明已换边，保留控件与输入操作空间。",
                        _ => "高亮的是下载前询问保存位置的开关，当前已开启。这里只指出位置，没有修改设置。"
                    });
                    if (i == 4) card.SetRegionAvailable(true);
                    if (i is 1 or 2 or 5 or 6) card.SetFollowupAvailable(true);
                    if (i == 1) card.SetPoint(true);
                    if (i == 5) card.Collapse();
                    var surface = new FeedbackSurface(card); surface.SetBubble(i == 1);
                    var size = surface.Fit(new Size(FeedbackLayout.AvailableWidth(entry, work, top), work.Height),
                        i == 3 ? null : entry.Width);
                    var bounds = i == 1 ? FeedbackLayout.Reader(control, entry, work, size, top)!.Value :
                        i == 6 ? FeedbackLayout.Reader(control, entry, work, size, top)!.Value : FeedbackLayout.Attached(entry, work, size, top);
                    if (i is 1 or 5 or 6)
                    {
                        var highlight = control; highlight.Inflate(6, 6);
                        draw.DrawRoundedRectangle(null, new Pen(GuideTheme.Accent, 3), highlight, 8, 8);
                    }
                    if (i == 1) surface.SetBubble(true, new Point(control.X + control.Width / 2 - bounds.X, control.Y + control.Height / 2 - bounds.Y));
                    draw.DrawImage(RenderElement(surface, size.Width, size.Height), bounds);
                }
                var image = new RenderTargetBitmap(1120, 600, 96, 96, PixelFormats.Pbgra32); image.Render(scene);
                Save(image, $"feedback-state-{i + 1}.png");
                var x = 20 + i % 2 * 1140; var y = 40 + i / 2 * 650;
                sheet.DrawText(Text(names[i], 14, GuideTheme.Ink), new Point(x, y - 25));
                sheet.DrawImage(image, new Rect(x, y, 1120, 600));
            }
        }
        var clean = new RenderTargetBitmap(2300, 2640, 96, 96, PixelFormats.Pbgra32); clean.Render(board); Save(clean, "feedback-native-clean.png");
        var grid = new DrawingVisual();
        using (var draw = grid.RenderOpen())
        {
            draw.DrawImage(clean, new Rect(0, 0, 2300, 2640));
            var pen = new Pen(new SolidColorBrush(Color.FromArgb(45, 40, 100, 155)), .5);
            for (var x = 0; x < 2300; x += 20) draw.DrawLine(pen, new Point(x, 0), new Point(x, 2640));
            for (var y = 0; y < 2640; y += 20) draw.DrawLine(pen, new Point(0, y), new Point(2300, y));
        }
        var gridded = new RenderTargetBitmap(2300, 2640, 96, 96, PixelFormats.Pbgra32); gridded.Render(grid); Save(gridded, "feedback-native-grid.png");
        Console.WriteLine("Generated native feedback states and matching clean/grid board; no desktop window, capture or network.");
    }

    static void Save(BitmapSource image, string name)
    {
        var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(image));
        var path = Path.GetFullPath(Path.Combine(".artifacts", "checks", name));
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        using var output = File.Create(path); encoder.Save(output);
    }
}
