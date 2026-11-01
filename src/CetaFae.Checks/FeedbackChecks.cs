using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using CetaFae.Probe;

namespace CetaFae.Checks;

internal static class FeedbackChecks
{
    static readonly GuideOptions Options = GuideOptions.Create("http://127.0.0.1:8787/v1", "deepseek-v4.1-flash");
    static void Click(GuideCard card, string label) => card.Actions.Children.Cast<Button>()
        .Single(button => Equals(button.Content, label)).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));

    internal static void Run(Action<bool, string> check)
    {
        var work = SystemParameters.WorkArea;
        var entry = new Rect(work.Left + 80, work.Top, 288, 36);
        var panel = new GuidePanel(Options);
        try
        {
            var scale = 2d;
            panel.BindEntry(() => entry, () => scale);
            panel.Card.SetStatus("生成窗口说明，全部文字仅用于离屏检查。");
            check(panel.Left == entry.Left && panel.Top == entry.Bottom + 1 && panel.Width == 288,
                "live reader uses the input body anchor rather than the whale's transparent canvas");
            check(!panel.IsVisible && !panel.ShowActivated && panel.AllowsTransparency && panel.Handle == IntPtr.Zero,
                "feedback placement and rendering need no native handle, visible launch or automatic activation");
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
            check(!panel.Card.IsBubble && panel.Width == 288 && panel.Top == entry.Bottom + 1 && panel.Card.FullText.Contains("生成窗口"),
                "native details replace the same bubble surface with the attached full explanation");
            Click(panel.Card, "收起");
            check(panel.Card.IsCollapsed && panel.Height <= 40 && panel.Card.HasSession,
                $"native collapse leaves a compact attached reopen tab and keeps the session (collapsed={panel.Card.IsCollapsed}, height={panel.Height}, session={panel.Card.HasSession})");
            entry.Offset(120, 24); panel.Reposition();
            check(panel.Left == entry.Left && panel.Top == entry.Bottom + 1,
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
            sheet.DrawRectangle(GuideTheme.Surface, null, new Rect(0, 0, 1340, 2200));
            var names = new[] { "Attached question / ordinary window", "Control bubble / maximized window", "Expanded long explanation", "Privacy refusal / home whale", "Region confirmation / attached", "Collapsed / marker retained", "Expanded reader / avoids highlighted control" };
            for (var i = 0; i < names.Length; i++)
            {
                var scene = new DrawingVisual();
                using (var draw = scene.RenderOpen())
                {
                    var work = new Rect(0, 0, 640, 500);
                    var top = i is 1 or 5;
                    var target = top ? work : new Rect(16, 110, 608, 380);
                    draw.DrawRectangle(new SolidColorBrush(Color.FromRgb(229, 240, 248)), null, work);
                    draw.DrawRoundedRectangle(Brushes.White, new Pen(GuideTheme.Muted, .5), target, 8, 8);
                    draw.DrawRectangle(new SolidColorBrush(Color.FromRgb(235, 239, 243)), null, new Rect(target.X, target.Y, target.Width, 32));
                    draw.DrawText(Text("生成软件界面                             —   □   ×", 12, GuideTheme.Muted), new Point(target.X + 16, target.Y + 8));
                    draw.DrawText(Text("下载设置（生成数据）", 20, GuideTheme.Ink), new Point(target.X + 30, target.Y + 88));
                    draw.DrawText(Text("下载前询问每个文件的保存位置", 13, GuideTheme.Ink), new Point(target.X + 30, target.Y + 192));
                    var control = new Rect(target.Right - 85, target.Top + 190, 45, 24);
                    draw.DrawRoundedRectangle(GuideTheme.WhaleBlue, null, control, 12, 12);
                    draw.DrawEllipse(Brushes.White, null, new Point(control.Right - 12, control.Y + 12), 9, 9);
                    var promptPosition = BallWindow.TopDockPosition(target, work);
                    var entry = new Rect(promptPosition.X, promptPosition.Y + (top ? 0 : 40), 288, 36);
                    if (i == 6)
                    {
                        control = new Rect(entry.Left + 12, entry.Bottom + 20, 150, 24);
                        draw.DrawRoundedRectangle(GuideTheme.WhaleBlue, null, control, 8, 8);
                        draw.DrawText(Text("生成目标控件", 12, Brushes.White), control.TopLeft + new Vector(10, 4));
                    }
                    if (i != 3)
                    {
                        var prompt = new WhalePrompt(); prompt.SetTopAttached(top); prompt.SetQuestion(i is 0 or 4);
                        if (i is 1 or 2 or 5) prompt.SetResult();
                        var host = new Grid(); host.Children.Add(prompt);
                        draw.DrawImage(RenderElement(host, 288, 88), new Rect(promptPosition, new Size(288, 88)));
                    }
                    else
                    {
                        entry = new Rect(544, 404, 88, 88);
                        var whale = new WhaleVisual(); whale.SetPose(new WhalePose(109), -1);
                        draw.DrawImage(RenderElement(whale, 88, 88), entry); whale.Release();
                    }
                    var card = new GuideCard(Options); card.SetCloud(true);
                    card.SetStatus(i switch
                    {
                        0 => "这是下载设置页面，可以选择保存位置和下载相关选项。你想在这里处理什么？",
                        2 => string.Concat(Enumerable.Repeat("生成说明：这里只解释界面并指出位置，不会替你修改软件。完整长文可以滚动阅读，操作按钮始终保留。", 14)),
                        3 => GuideSession.FailureMessage(new InvalidDataException("sensitive_content")),
                        4 => "没有可靠的自动识别范围。请点击选择识别区域，在所选窗口内框住相关文字和控件；无需重新回答，确认前不会发本阶段的新请求。",
                        6 => "生成目标控件位于输入条下方。说明已换边，保留控件与输入操作空间。",
                        _ => "高亮的是下载前询问保存位置的开关，当前已开启。这里只指出位置，没有修改设置。"
                    });
                    if (i == 4) card.SetRegionAvailable(true);
                    if (i == 1) card.SetPoint(true);
                    if (i == 5) Click(card, "收起");
                    var surface = new FeedbackSurface(card); surface.SetBubble(i == 1);
                    var width = i == 1 ? 258 : 288;
                    surface.Measure(new Size(width, i == 1 ? 260 : 320));
                    var size = new Size(width, Math.Min(i == 1 ? 260 : 320, surface.DesiredSize.Height));
                    var bounds = i == 1 ? FeedbackLayout.Bubble(control, entry, work, size)!.Value :
                        i == 6 ? FeedbackLayout.Reader(control, entry, work, size)!.Value : FeedbackLayout.Attached(entry, work, size);
                    if (i is 1 or 5 or 6)
                    {
                        var highlight = control; highlight.Inflate(6, 6);
                        draw.DrawRoundedRectangle(null, new Pen(GuideTheme.Accent, 3), highlight, 8, 8);
                    }
                    if (i == 1) surface.SetBubble(true, new Point(control.X + control.Width / 2 - bounds.X, control.Y + control.Height / 2 - bounds.Y));
                    draw.DrawImage(RenderElement(surface, size.Width, size.Height), bounds);
                }
                var image = new RenderTargetBitmap(640, 500, 96, 96, PixelFormats.Pbgra32); image.Render(scene);
                Save(image, $"feedback-state-{i + 1}.png");
                var x = 20 + i % 2 * 660; var y = 40 + i / 2 * 550;
                sheet.DrawText(Text(names[i], 14, GuideTheme.Ink), new Point(x, y - 25));
                sheet.DrawImage(image, new Rect(x, y, 640, 500));
            }
        }
        var clean = new RenderTargetBitmap(1340, 2200, 96, 96, PixelFormats.Pbgra32); clean.Render(board); Save(clean, "feedback-native-clean.png");
        var grid = new DrawingVisual();
        using (var draw = grid.RenderOpen())
        {
            draw.DrawImage(clean, new Rect(0, 0, 1340, 2200));
            var pen = new Pen(new SolidColorBrush(Color.FromArgb(45, 40, 100, 155)), .5);
            for (var x = 0; x < 1340; x += 20) draw.DrawLine(pen, new Point(x, 0), new Point(x, 2200));
            for (var y = 0; y < 2200; y += 20) draw.DrawLine(pen, new Point(0, y), new Point(1340, y));
        }
        var gridded = new RenderTargetBitmap(1340, 2200, 96, 96, PixelFormats.Pbgra32); gridded.Render(grid); Save(gridded, "feedback-native-grid.png");
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
