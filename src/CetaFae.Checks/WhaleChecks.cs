using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using CetaFae.Probe;

namespace CetaFae.Checks;

internal static class WhaleChecks
{
    internal static void Run(Action<bool, string> check)
    {
        var art = new WhaleArt();
        var pixels = new byte[96 * 96 * 4];
        var nonempty = 0;
        for (var frame = 0; frame < 320; frame++)
        {
            foreach (var layer in new[] { "water-rear", "whale-visible", "water-front" })
            {
                var image = art.Frame(layer, frame);
                if (image.PixelWidth != 96 || image.PixelHeight != 96 || !image.IsFrozen)
                    throw new InvalidOperationException("whale_layer_export");
                if (layer == "whale-visible")
                {
                    new FormatConvertedBitmap(image, PixelFormats.Bgra32, null, 0).CopyPixels(pixels, 96 * 4, 0);
                    if (Enumerable.Range(0, 96 * 96).Any(i => pixels[i * 4 + 3] != 0)) nonempty++;
                    if (pixels[3] != 0 || pixels[^1] != 0) throw new InvalidOperationException("whale_background_not_transparent");
                }
            }
        }
        check(nonempty == 320, "all320 normalized whale poses are nonempty transparent frozen runtime cells");
        var seamEqual = true;
        foreach (var layer in new[] { "water-rear", "whale-visible", "water-front" })
        {
            var left = new byte[96 * 96 * 4]; var right = new byte[left.Length];
            new FormatConvertedBitmap(art.Frame(layer, 109), PixelFormats.Bgra32, null, 0).CopyPixels(left, 384, 0);
            new FormatConvertedBitmap(art.Frame(layer, 110), PixelFormats.Bgra32, null, 0).CopyPixels(right, 384, 0);
            seamEqual &= left.SequenceEqual(right);
        }
        check(seamEqual, "reduced atlases preserve the shared idle-to-swim seam without a water crossfade");
        check(Enumerable.Range(0, 110).All(f => !WhaleArt.IsImmersed(f)) &&
            Enumerable.Range(140, 100).Any(WhaleArt.IsImmersed),
            "trail deposits use the exported contact mask and never appear before swimming contact");
        for (var frame = 0; frame < 80; frame++) _ = art.Frame("spout", frame);
        check(art.CachedPages <= WhaleArt.CacheLimit, "all sprite sequences retain at most six decoded atlas pages");
        art.Clear(); check(art.CachedPages == 0, "clearing art releases the bounded atlas cache");
        var before = WhaleArt.DockPose(2.08);
        var morph = WhaleArt.DockPose(2.56);
        var end = WhaleArt.DockPose(3.2);
        check(Math.Abs(before.Y) > 0 && before.WaterScale == 1 && before.Progress == 0,
            "pre-spout whale motion leaves the resting water transform independent");
        check(morph.Progress > 0 && morph.Alpha < 1 && morph.WaterScale == morph.ScaleX,
            "water coverage and whale input morph share the exported progress");
        check(end.Progress == 1 && end.Alpha == 0 && WhaleArt.DockPose(0, true) == end,
            "completed and reduced-motion docking leave no whale behind the native input");
        var work = new Rect(0, 0, 1920, 1040);
        check(BallWindow.ConstrainPosition(new Point(1910, 1030), work, new Size(88, 88)) == new Point(1832, 952) &&
            BallWindow.ConstrainPosition(new Point(1910, 1030), work, new Size(280, 88)) == new Point(1640, 952),
            "actual whale and inline surface sizes share the same visible work-area constraint");
        var normal = BallWindow.TopDockPosition(new Rect(100, 180, 900, 700), work);
        var full = BallWindow.TopDockPosition(new Rect(0, 0, 1920, 1040), work);
        check(normal.Y + 68 == 180 && full.Y == 0,
            "normal top docking stays outside the title bar and fullscreen docking stays inside the work area");
        foreach (var target in new[] { new Rect(-500, -200, 900, 700), new Rect(1800, 900, 800, 700), new Rect(0, 0, 80, 60) })
            check(work.Contains(new Rect(BallWindow.TopDockPosition(target, work), new Size(280, 88))),
                "top input remains fully reachable for shifted or small targets");
        var prompt = new WhalePrompt(); var submissions = new List<string>(); var focus = 0;
        prompt.Submitted += submissions.Add; prompt.FocusRequested += () => focus++;
        var row = (DockPanel)prompt.Child;
        var send = row.Children.OfType<Button>().Single(b => Equals(b.Content, "→"));
        prompt.SetQuestion(true); prompt.Answer.Text = "生成中文：只解释，不修改。";
        send.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        send.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        check(submissions.SequenceEqual(new[] { "生成中文：只解释，不修改。" }) && !prompt.CanAnswer && prompt.Answer.Text == "",
            "native inline answer consumes Chinese text once and clears the editable value before dispatch");
        check(focus == 0, "showing or submitting a generated inline question never requests focus automatically");
        prompt.SetQuestion(true); prompt.Answer.Text = "   "; send.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        check(prompt.CanAnswer && submissions.Count == 1 && !prompt.Answer.AcceptsReturn && prompt.Answer.MaxLength == 300,
            "inline clarification preserves blank rejection and the single-line300-character contract");
        var ball = new BallWindow();
        try
        {
            ball.ShowQuestion();
            check(ball.HasInlineInput && ball.Prompt.CanAnswer && !ball.ArtAnimating,
                "a ready question immediately replaces the whale without waiting for animation");
            ball.ResetArt();
            check(!ball.HasInlineInput && !ball.Prompt.CanAnswer && !ball.ArtAnimating && ball.Width == 88,
                "art reset removes input and animation and restores the reusable whale entry");
            check(new WindowInteropHelper(ball).Handle == IntPtr.Zero,
                "inline lifecycle checks create no native window or desktop surface");
        }
        finally { ball.Close(); }
    }

    internal static void Render()
    {
        var sheet = new DrawingVisual();
        using (var draw = sheet.RenderOpen())
        {
            draw.DrawRectangle(new SolidColorBrush(Color.FromRgb(230, 240, 247)), null, new Rect(0, 0, 1080, 660));
            var poses = new[] { new WhalePose(109), new WhalePose(170), WhaleArt.DockPose(1.6),
                WhaleArt.DockPose(1.84), WhaleArt.DockPose(2.56), WhaleArt.DockPose(3.2) };
            var labels = new[] { "Idle (static)", "Drag / mirrored", "Top dock / settle", "Small spout", "Coverage + input morph", "Native input (no autofocus)" };
            for (var i = 0; i < poses.Length; i++)
            {
                var x = i % 3 * 360d + 20; var y = i / 3 * 210d + 44;
                draw.DrawText(new FormattedText(labels[i], CultureInfo.InvariantCulture, FlowDirection.LeftToRight,
                    new Typeface("Segoe UI"), 14, GuideTheme.Ink, 1), new Point(x, y - 22));
                draw.DrawRoundedRectangle(Brushes.White, new Pen(GuideTheme.Outline, 1), new Rect(x, y + 88, 320, 80), 4, 4);
                var view = new WhaleVisual(); view.SetPose(poses[i], i == 1 ? 1 : -1);
                view.Measure(new Size(280, 88)); view.Arrange(new Rect(0, 0, 280, 88));
                var bitmap = new RenderTargetBitmap(280, 88, 96, 96, PixelFormats.Pbgra32); bitmap.Render(view);
                draw.DrawImage(bitmap, new Rect(x + 20, y, 280, 88));
                if (i == 5)
                {
                    var input = new WhalePrompt(); input.SetQuestion(true);
                    input.Measure(new Size(280, 88)); input.Arrange(new Rect(0, 0, 280, 88));
                    var prompt = new RenderTargetBitmap(280, 88, 96, 96, PixelFormats.Pbgra32); prompt.Render(input);
                    draw.DrawImage(prompt, new Rect(x + 20, y, 280, 88));
                }
            }
            for (var i = 0; i < 3; i++)
            {
                var size = new[] { 44, 66, 88 }[i];
                var view = new WhaleVisual(); view.SetPose(new WhalePose(109));
                view.LayoutTransform = new ScaleTransform(size / 88d, size / 88d);
                view.Measure(new Size(size, size)); view.Arrange(new Rect(0, 0, size, size));
                var image = new RenderTargetBitmap(size, size, 96, 96, PixelFormats.Pbgra32); image.Render(view);
                draw.DrawImage(image, new Rect(30 + i * 130, 510, size, size));
            }
        }
        var result = new RenderTargetBitmap(1080, 660, 96, 96, PixelFormats.Pbgra32); result.Render(sheet);
        var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(result));
        var file = Path.GetFullPath(".artifacts/checks/whale-native-states.png"); Directory.CreateDirectory(Path.GetDirectoryName(file)!);
        using var output = File.Create(file); encoder.Save(output);
        Console.WriteLine("Offscreen whale states: " + file + "; no native window, capture or network");
    }
}
