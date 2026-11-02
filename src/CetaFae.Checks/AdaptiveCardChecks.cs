using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using CetaFae.Probe;

namespace CetaFae.Checks;

internal static class AdaptiveCardChecks
{
    static readonly GuideOptions Options = GuideOptions.Create("http://127.0.0.1:8787/v1", "generated-model");
    static GuideCard Create(string text)
    {
        var card = new GuideCard(Options); card.SetCloud(true); card.SetStatus(text); return card;
    }
    internal static Size Layout(GuideCard card, Size available)
    {
        var size = card.Fit(available);
        card.Measure(size); card.Arrange(new Rect(size)); card.UpdateLayout(); return size;
    }
    static Button PageButton(GuideCard card, string content) => Descendants(card).OfType<Button>()
        .Single(button => Equals(button.Content, content));
    static IEnumerable<DependencyObject> Descendants(DependencyObject parent)
    {
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(parent); i++)
        {
            var child = VisualTreeHelper.GetChild(parent, i); yield return child;
            foreach (var descendant in Descendants(child)) yield return descendant;
        }
    }
    internal static void Run(Action<bool, string> check)
    {
        var text = "这是文本编辑器 Notepad3，正在显示一份待办清单。你想了解哪一部分？";
        var card = Create(text);
        var size = Layout(card, new Size(900, 700));
        check(size.Width == GuideTheme.CardWidth && size.Height <= GuideTheme.CardMaxHeight,
            "short feedback uses the shared baseline width and height cap");
        check(card.Heading.Text == "鲸汐解读" && card.Heading.Foreground == Brushes.White && card.Header.Background == GuideTheme.CardHeader &&
            !Descendants(card).OfType<TextBlock>().Any(block => block.Text.Contains("解释与指路") || block.Text.Contains("仅指路")),
            "reading header uses white title on the shared blue gradient without explanatory captions");
        card.SetPoint(true); size = Layout(card, new Size(900, 700));
        check(size.Width == GuideTheme.CardWidth && card.BodyScroll.VerticalScrollBarVisibility == ScrollBarVisibility.Disabled,
            "control bubbles share the baseline card width and never trim or scroll their text");
        var longText = string.Concat(Enumerable.Repeat("生成长文：保留日期2026/10/29和错误代码0x80070002，完整呈现事实。", 12));
        card.SetStatus(longText); size = Layout(card, new Size(900, 700));
        check(size.Width > GuideTheme.CardWidth && size.Width <= GuideTheme.CardMaxWidth && size.Height <= GuideTheme.CardMaxHeight,
             $"long feedback grows in width before reaching the card height cap: size={size}, text={longText.Length}, pages={card.PageCount}");
        foreach (var available in new[] { new Size(400, 360), new Size(720, 360), new Size(258, 260) })
        {
            card.SetStatus(longText + "🐋e\u0301结束"); size = Layout(card, available);
            var result = ""; var pages = card.PageCount;
            check(pages < 20, "pagination packs meaningful text rather than creating character-sized pages");
            for (var page = 0; page < pages; page++)
            {
                result += card.DisplayedText;
                check(card.BodyScroll.ExtentHeight <= card.BodyScroll.ViewportHeight + 1 &&
                    card.BodyScroll.ExtentWidth <= card.BodyScroll.ViewportWidth + 1,
                    "each fitted page has a complete text viewport without scrolling or clipping");
                if (page + 1 < pages)
                { PageButton(card, "›").RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); Layout(card, available); }
            }
            check(result == card.FullText && card.PageIndex == pages - 1 && size.Width <= available.Width && size.Height <= available.Height,
                "bounded pages reconstruct all original text including grapheme clusters inside the screen");
        }
        var work = new Rect(-1366, -768, 1366, 728);
        var entry = new Rect(work.Left + 539, work.Top, 288, 36);
        var right = FeedbackLayout.Attached(entry, work, new Size(360, 180), true);
        check(right.Left == entry.Right + GuideTheme.CardGap && right.Top == entry.Top && work.Contains(right),
            "negative-coordinate top attachment preserves the right gap and screen bounds");
        var narrow = new Rect(0, 0, 640, 480); var narrowEntry = new Rect(176, 0, 288, 36);
        var below = FeedbackLayout.Attached(narrowEntry, narrow, new Size(360, 180), true);
        check(narrow.Contains(below) && !below.IntersectsWith(narrowEntry),
            "a narrow screen falls below the input instead of clipping a side card");
        var scan = Enumerable.Range(0, 81).Select(i => WindowWash.ScanProgress(i * WindowWash.Duration / 20)).ToArray();
        check(scan[0] < scan[10] && scan[10] < scan[20] && scan[20] > scan[30] && scan[30] > scan[40] &&
            Math.Abs(scan[0] - scan[40]) < .0001,
            "processing sweep reverses downward and upward smoothly in a repeatable cycle");
        var target = new Size(3840, 2160);
        check(scan.Select(progress => WindowWash.Region(target, progress)).Where(region => !region.IsEmpty)
            .All(region => new Rect(target).Contains(region) && region.Height <= WindowWash.Band + 26),
            "repeated scans remain narrow clipped strips on large targets");
        var ball = new BallWindow();
        try
        {
            ball.BeginDockMotion(); ball.SetProcessing(true); ball.AdvanceMotion(WindowWash.TotalDuration);
            check(ball.Processing && ball.HasInlineInput && (SystemParameters.ClientAreaAnimation ? ball.Scanning : !ball.ArtAnimating),
                "pending recognition continues scanning after the existing dock transition with reduced-motion support");
            ball.SetResult();
            check(!ball.Processing && !ball.Scanning && !ball.ArtAnimating,
                "receiving a result stops the waiting scan without waiting for another cycle");
            ball.SetProcessing(true); ball.StopTracking("cancelled");
            check(!ball.Processing && !ball.Scanning && !ball.ArtAnimating && ball.Handle == IntPtr.Zero,
                "cancelling processing stops animation without a native window or stale scan");
        }
        finally { ball.Close(); }
        card.SetStatus(text); Layout(card, new Size(900, 700));
        check(card.PageIndex == 0 && card.PageCount == 1 && card.DisplayedText == text,
            "new results reset pagination and recover a compact complete card");
    }
    internal static void Render()
    {
        const int width = 1500; const int height = 920;
        var visual = new DrawingVisual();
        using (var draw = visual.RenderOpen())
        {
            draw.DrawRectangle(new SolidColorBrush(Color.FromRgb(226, 239, 248)), null, new Rect(0, 0, width, height));
            var cases = new[]
            {
                ("Short explanation / shared width", "这是文本编辑器 Notepad3，正在显示一份待办清单。你想了解哪一部分？", new Size(700,360)),
                ("Long explanation / width grows", string.Concat(Enumerable.Repeat("这里是下载设置，可以选择文件的保存位置。目标开关当前已开启，无需修改。", 12)), new Size(700,360)),
                ("Required selection / one action", "请圈出你想了解的区域，包含相关文字和开关即可，不用重新回答。", new Size(700,360)),
                ("Constrained width / local pages", string.Concat(Enumerable.Repeat("生成长文：完整保留重要提示和日期，不省略内容，不显示滚动条。", 28)), new Size(400,360))
            };
            for (var i = 0; i < cases.Length; i++)
            {
                var item = cases[i]; var card = Create(item.Item2);
                if (i == 2) card.SetRegionAvailable(true);
                var size = Layout(card, item.Item3);
                var bitmap = new RenderTargetBitmap((int)Math.Ceiling(size.Width), (int)Math.Ceiling(size.Height), 96, 96, PixelFormats.Pbgra32); bitmap.Render(card);
                var point = new Point(24 + i % 2 * 740, 58 + i / 2 * 440);
                draw.DrawText(new FormattedText(item.Item1, CultureInfo.InvariantCulture, FlowDirection.LeftToRight,
                    new Typeface("Segoe UI"), 14, GuideTheme.Ink, 1), new Point(point.X, point.Y - 28));
                draw.DrawImage(bitmap, new Rect(point, size));
            }
        }
        var clean = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32); clean.Render(visual); Save(clean, "adaptive-card-clean.png");
        var grid = new DrawingVisual();
        using (var draw = grid.RenderOpen())
        {
            draw.DrawImage(clean, new Rect(0, 0, width, height));
            var pen = new Pen(new SolidColorBrush(Color.FromArgb(40, 30, 90, 150)), .5);
            for (var x = 0; x < width; x += 20) draw.DrawLine(pen, new Point(x, 0), new Point(x, height));
            for (var y = 0; y < height; y += 20) draw.DrawLine(pen, new Point(0, y), new Point(width, y));
        }
        var gridded = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32); gridded.Render(grid); Save(gridded, "adaptive-card-grid.png");
    }
    internal static void RenderScan()
    {
        var visual = new DrawingVisual();
        using (var draw = visual.RenderOpen())
        {
            draw.DrawRectangle(GuideTheme.Surface, null, new Rect(0, 0, 1240, 800));
            for (var i = 0; i < 4; i++)
            {
                draw.PushTransform(new TranslateTransform(20 + i % 2 * 610, 40 + i / 2 * 380));
                var size = new Size(580, 320);
                draw.DrawRectangle(Brushes.White, new Pen(GuideTheme.Muted, .5), new Rect(size));
                draw.DrawText(new FormattedText(i < 2 ? "Scanning down / no feedback card" : "Scanning up / no feedback card",
                    CultureInfo.InvariantCulture, FlowDirection.LeftToRight, new Typeface("Segoe UI"), 14, GuideTheme.Ink, 1), new Point(12, 12));
                draw.DrawText(new FormattedText("生成窗口：正在等待识别结果", CultureInfo.InvariantCulture, FlowDirection.LeftToRight,
                    new Typeface("Microsoft YaHei UI"), 18, GuideTheme.Ink, 1), new Point(36, 135));
                GuideFrameVisual.DrawWater(draw, size, 0, 0);
                draw.PushClip(new RectangleGeometry(new Rect(size)));
                var phase = new[] { .35, .65, 1.35, 1.65 }[i];
                WindowWash.Draw(draw, size, WindowWash.ScanProgress(phase * WindowWash.Duration));
                draw.Pop(); draw.Pop();
            }
        }
        var image = new RenderTargetBitmap(1240, 800, 96, 96, PixelFormats.Pbgra32); image.Render(visual); Save(image, "adaptive-scan-frames.png");
    }
    static void Save(BitmapSource source, string name)
    {
        var path = Path.GetFullPath(Path.Combine(".artifacts/checks", name)); Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(source)); using var output = File.Create(path); encoder.Save(output);
    }
}
