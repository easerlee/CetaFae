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
        check(card.Header.ActualHeight == GuideFrameVisual.WaterDepth && card.Header.ActualHeight == 48 &&
            card.Heading.Text == "窗口解读", "reading header and perimeter share the reference depth and plain window-reading title");
        var surface = new RenderTargetBitmap((int)Math.Ceiling(size.Width), (int)Math.Ceiling(size.Height), 96, 96, PixelFormats.Pbgra32);
        surface.Render(card);
        var rgba = new byte[surface.PixelWidth * surface.PixelHeight * 4]; surface.CopyPixels(rgba, surface.PixelWidth * 4, 0);
        var headerBounds = card.Header.TransformToAncestor(card).TransformBounds(new Rect(card.Header.RenderSize));
        int Alpha(int y) => rgba[(y * surface.PixelWidth + surface.PixelWidth - 24) * 4 + 3];
        check(Alpha((int)headerBounds.Top + 1) == 255 && Alpha((int)headerBounds.Bottom - 1) < 5 &&
            Alpha((int)headerBounds.Bottom + 10) == 255,
            "actual card header fades to transparent pixels while the separate reading body stays opaque");
        check(card.DismissButton.Visibility == Visibility.Collapsed,
            "session reader has no duplicate close beside the input bar close");
        var gradient = (LinearGradientBrush)card.Header.Background;
        check(gradient.GradientStops[0].Color == ((SolidColorBrush)GuideTheme.WhaleBlue).Color &&
            gradient.GradientStops.Any(stop => stop.Offset > 0 && stop.Color == gradient.GradientStops[0].Color) &&
            gradient.GradientStops[^1].Color.A == 0,
            "header uses the perimeter solid-blue plateau and transparent inner fade");
        card.SetStatus(string.Concat(Enumerable.Repeat("整理材料，核对结果。", 34)));
        var mediumSize = Layout(card, new Size(900, 800));
        check(mediumSize.Width == GuideTheme.CardWidth && mediumSize.Height > 360 && card.PageCount == 1,
            $"medium feedback grows vertically before widening or paging: {mediumSize}");
        card.SetStatus(text); size = Layout(card, new Size(900, 700));
        check(size.Width == GuideTheme.CardWidth && size.Height <= GuideTheme.CardMaxHeight,
            "short feedback uses the shared baseline width and height cap");
        check(card.Heading.Text == "窗口解读" && card.Heading.Foreground == Brushes.White && card.Header.Background == GuideTheme.CardHeader &&
            !Descendants(card).OfType<TextBlock>().Any(block => block.Text.Contains("解释与指路") || block.Text.Contains("仅指路")),
            "reading header uses white title on the shared blue gradient without explanatory captions");
        card.SetPoint(true); size = Layout(card, new Size(900, 700));
        check(size.Width == GuideTheme.CardWidth && card.DismissButton.Visibility == Visibility.Collapsed &&
            card.BodyScroll.VerticalScrollBarVisibility == ScrollBarVisibility.Disabled,
            "control bubbles share the baseline card width and never trim or scroll their text");
        var longText = string.Concat(Enumerable.Repeat("生成长文：保留日期2026/10/29和错误代码0x80070002，完整呈现事实。", 18));
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
        check(scan[0] > scan[10] && scan[10] > scan[20] && scan[20] < scan[30] && scan[30] < scan[40] &&
            Math.Abs(scan[0] - scan[40]) < .0001,
            "after the entry descent processing starts upward then alternates without a second descent");
        check(WindowWash.Progress(WindowWash.TotalDuration) == 1 && WindowWash.ScanProgress(0) == WindowWash.SweepEnd &&
            WindowWash.ScanUpward(0) && WindowWash.ScanUpward(.5 * WindowWash.Duration) &&
            !WindowWash.ScanUpward(1.5 * WindowWash.Duration) && WindowWash.ScanUpward(2.5 * WindowWash.Duration),
            "completed entry connects to the bottom turnaround and alternating scan direction");
        var pixelsDown = RenderWash(new Size(580, 320), WindowWash.SweepEnd * .4, false);
        var pixelsUp = RenderWash(new Size(580, 320), WindowWash.SweepEnd * .6, true);
        var differences = Enumerable.Range(0, 320).SelectMany(y => Enumerable.Range(0, 580 * 4)
            .Select(x => Math.Abs(pixelsDown[(319 - y) * 580 * 4 + x] - pixelsUp[y * 580 * 4 + x]))).ToArray();
        Console.WriteLine($"MIRROR: max={differences.Max()}, mean={differences.Average():F5}, channelsOverOne={differences.Count(difference => difference > 1)}");
        // WPF rasterizes reflected sloped edges with subpixel coverage differences.
        check(pixelsDown.Any(value => value != 0) && differences.Average() < .02 &&
            differences.Count(difference => difference > 1) < differences.Length * .005,
            "upward scan mirrors the complete wave shape and depth gradient rather than just moving the down image");
        var target = new Size(3840, 2160);
        check(scan.SelectMany(progress => new[] { WindowWash.Region(target, progress), WindowWash.Region(target, progress, true) }).Where(region => !region.IsEmpty)
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
    static byte[] RenderWash(Size size, double progress, bool upward)
    {
        var visual = new DrawingVisual();
        using (var draw = visual.RenderOpen())
        {
            draw.PushClip(new RectangleGeometry(new Rect(size)));
            WindowWash.Draw(draw, size, progress, upward); draw.Pop();
        }
        var image = new RenderTargetBitmap((int)size.Width, (int)size.Height, 96, 96, PixelFormats.Pbgra32);
        image.Render(visual); var pixels = new byte[(int)size.Width * (int)size.Height * 4];
        image.CopyPixels(pixels, (int)size.Width * 4, 0); return pixels;
    }
    internal static void Render()
    {
        const int width = 1500; const int height = 1200;
        var visual = new DrawingVisual();
        using (var draw = visual.RenderOpen())
        {
            draw.DrawRectangle(new SolidColorBrush(Color.FromRgb(226, 239, 248)), null, new Rect(0, 0, width, height));
            var cases = new[]
            {
                ("Short explanation / one close on input bar", "文件管理器，正在查看 installer_preq_25 文件夹，其中包含 payload 文件夹和安装程序。你想在这个文件夹里做什么？", new Size(700,520)),
                ("Long explanation / height before width", string.Concat(Enumerable.Repeat("这里是下载设置，可以选择文件的保存位置。目标开关当前已开启，无需修改。", 18)), new Size(700,520)),
                ("Medium explanation / baseline width", string.Concat(Enumerable.Repeat("整理材料，核对结果。", 34)), new Size(700,520)),
                ("Constrained width / local pages", string.Concat(Enumerable.Repeat("生成长文：完整保留重要提示和日期，不省略内容，不显示滚动条。", 28)), new Size(400,360))
            };
            for (var i = 0; i < cases.Length; i++)
            {
                var item = cases[i]; var card = Create(item.Item2);
                var size = Layout(card, item.Item3);
                var bitmap = new RenderTargetBitmap((int)Math.Ceiling(size.Width), (int)Math.Ceiling(size.Height), 96, 96, PixelFormats.Pbgra32); bitmap.Render(card);
                var point = new Point(24 + i % 2 * 740, 58 + i / 2 * 580);
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
        RenderTransparency();
    }
    static void RenderTransparency()
    {
        var visual = new DrawingVisual();
        using (var draw = visual.RenderOpen())
        {
            draw.DrawRectangle(GuideTheme.Surface, null, new Rect(0, 0, 1280, 760));
            var sizes = new[] { new Size(600, 320), new Size(3440, 1392) };
            for (var i = 0; i < 2; i++)
            {
                var origin = new Point(20 + i * 630, 50);
                draw.DrawText(new FormattedText(i == 0 ? "Normal 600 x 320 / 1 DIP = 1 px" : "Maximized 3440 x 1392 / unscaled corner crop",
                    CultureInfo.InvariantCulture, FlowDirection.LeftToRight, new Typeface("Segoe UI"), 14, GuideTheme.Ink, 1), new Point(origin.X, 20));
                draw.PushTransform(new TranslateTransform(origin.X, origin.Y));
                draw.PushClip(new RectangleGeometry(new Rect(0, 0, 600, 320)));
                draw.DrawRectangle(Brushes.White, null, new Rect(0, 0, 600, 320));
                GuideFrameVisual.DrawWater(draw, sizes[i], 0, 0);
                for (var distance = 8; distance <= GuideFrameVisual.WaterDepth; distance += 8)
                {
                    draw.DrawLine(new Pen(GuideTheme.Ink, .5), new Point(230, distance), new Point(250, distance));
                    draw.DrawText(new FormattedText(distance.ToString(), CultureInfo.InvariantCulture, FlowDirection.LeftToRight,
                        new Typeface("Segoe UI"), 10, GuideTheme.Ink, 1), new Point(255, distance - 6));
                }
                draw.Pop(); draw.Pop();
                var background = new Rect(origin.X, 410, 600, 320);
                draw.DrawRectangle(i == 0 ? Brushes.White : GuideTheme.Ink, null, background);
                for (var x = 0; x < 600; x += 24) for (var y = 0; y < 320; y += 24)
                    if ((x / 24 + y / 24) % 2 == 0)
                        draw.DrawRectangle(new SolidColorBrush(Color.FromArgb(28, 100, 170, 220)), null, new Rect(origin.X + x, 410 + y, 24, 24));
                var card = Create("这是文本编辑器 Notepad3，正在显示一份待办清单。你想了解哪一部分？");
                var size = Layout(card, new Size(600, 320));
                var bitmap = new RenderTargetBitmap((int)Math.Ceiling(size.Width), (int)Math.Ceiling(size.Height), 96, 96, PixelFormats.Pbgra32); bitmap.Render(card);
                draw.DrawImage(bitmap, new Rect(origin.X + 110, 450, size.Width, size.Height));
            }
        }
        var clean = new RenderTargetBitmap(1280, 760, 96, 96, PixelFormats.Pbgra32); clean.Render(visual); Save(clean, "water-header-clean.png");
        var grid = new DrawingVisual();
        using (var draw = grid.RenderOpen())
        {
            draw.DrawImage(clean, new Rect(0, 0, 1280, 760)); var pen = new Pen(new SolidColorBrush(Color.FromArgb(45, 90, 90, 90)), .5);
            for (var x = 0; x < 1280; x += 10) draw.DrawLine(pen, new Point(x, 0), new Point(x, 760));
            for (var y = 0; y < 760; y += 10) draw.DrawLine(pen, new Point(0, y), new Point(1280, y));
        }
        var gridded = new RenderTargetBitmap(1280, 760, 96, 96, PixelFormats.Pbgra32); gridded.Render(grid); Save(gridded, "water-header-grid.png");
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
                var seconds = new[] { 1.65, 1.35, .65, .35 }[i] * WindowWash.Duration;
                WindowWash.Draw(draw, size, WindowWash.ScanProgress(seconds), WindowWash.ScanUpward(seconds));
                draw.Pop(); draw.Pop();
            }
        }
        var image = new RenderTargetBitmap(1240, 800, 96, 96, PixelFormats.Pbgra32); image.Render(visual); Save(image, "adaptive-scan-frames.png");
        for (var i = 0; i <= 84; i++)
        {
            var seconds = i / 15d; var entry = seconds < WindowWash.Duration;
            var progress = entry ? seconds / WindowWash.Duration : WindowWash.ScanProgress(seconds - WindowWash.Duration);
            var upward = !entry && WindowWash.ScanUpward(seconds - WindowWash.Duration);
            var frame = new DrawingVisual();
            using (var draw = frame.RenderOpen())
            {
                var size = new Size(600, 400);
                draw.DrawRectangle(GuideTheme.Surface, null, new Rect(size));
                draw.DrawText(new FormattedText(entry ? "ENTRY / down" : upward ? "WAIT / up" : "WAIT / down",
                    CultureInfo.InvariantCulture, FlowDirection.LeftToRight, new Typeface("Segoe UI"), 15, GuideTheme.Ink, 1), new Point(120, 140));
                draw.DrawText(new FormattedText("生成窗口 / 浅尾部 → 深色浪头", CultureInfo.InvariantCulture, FlowDirection.LeftToRight,
                    new Typeface("Microsoft YaHei UI"), 18, GuideTheme.Ink, 1), new Point(120, 180));
                draw.PushClip(new RectangleGeometry(new Rect(size)));
                GuideFrameVisual.DrawWater(draw, size, 0, entry && progress == 0 ? -1 : entry ? progress : 0);
                WindowWash.Draw(draw, size, progress, upward); draw.Pop();
            }
            var bitmap = new RenderTargetBitmap(600, 400, 96, 96, PixelFormats.Pbgra32); bitmap.Render(frame);
            Save(bitmap, $"card-scan-sequence/frame-{i:000}.png");
        }
    }
    static void Save(BitmapSource source, string name)
    {
        var path = Path.GetFullPath(Path.Combine(".artifacts/checks", name)); Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(source)); using var output = File.Create(path); encoder.Save(output);
    }
}
