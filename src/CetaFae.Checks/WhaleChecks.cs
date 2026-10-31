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
        var drop = WhaleArt.DropPose(0);
        var early = WhaleArt.DropPose(.04);
        var complete = WhaleArt.DropPose(WhaleArt.DropDuration);
        check(drop.Submerge && drop.Spout >= 0 && early.Progress > 0 && early.Y > drop.Y &&
            early.Alpha == 1 && early.ScaleX == 1 && early.ScaleY == 1 && early.WaterScale == 1,
            "drop starts spray and a water shell while the constant-scale opaque whale descends into occlusion");
        check(complete.Progress == 1 && complete.Alpha == 0 && complete.Spout == -1 &&
            WhaleArt.DropPose(0, true) == complete && WhaleArt.DropPose(-1) == drop,
            "drop completes in800ms and reduced motion skips spray and intermediate surfaces");
        var regionTarget = new Rect(100, 100, 300, 200);
        check(OverlayWindow.ControlBounds(new Rect(130, 130, 40, 20), regionTarget, regionTarget, 1) == new Rect(122, 122, 56, 36) &&
            OverlayWindow.ControlBounds(new Rect(100, 100, 4, 4), regionTarget, regionTarget, 1) == new Rect(100, 100, 12, 12) &&
            OverlayWindow.ControlBounds(new Rect(130, 130, 40, 20), regionTarget, regionTarget, 1.5) == new Rect(118, 118, 64, 44),
            "control padding grows inDIP and clips to target and work bounds including tiny edge controls");
        var pulse = OverlayWindow.CreateBlink();
        check(pulse.RepeatBehavior == System.Windows.Media.Animation.RepeatBehavior.Forever &&
            pulse.Duration.TimeSpan == TimeSpan.FromSeconds(1.2) && pulse.KeyFrames[1].Value == 0 &&
            pulse.KeyFrames[1].KeyTime.TimeSpan == TimeSpan.FromSeconds(.6),
            "control frame keeps a low-frequency visible-hidden blink for the instruction lifetime");
        var clock = pulse.CreateClock(); clock.Controller!.Begin();
        foreach (var (seconds, expected) in new[] { (.3, 1d), (.9, 0d), (9.9, 1d), (10.5, 0d) })
        {
            clock.Controller.SeekAlignedToLastTick(TimeSpan.FromSeconds(seconds), System.Windows.Media.Animation.TimeSeekOrigin.BeginTime);
            check(pulse.GetCurrentValue(1, 1, clock) == expected, "native blink clock remains on-off after repeated cycles");
        }
        clock.Controller.Remove();
        var marker = new OverlayWindow(control: true);
        try
        {
            var border = (Border)marker.Content;
            border.BeginAnimation(UIElement.OpacityProperty, OverlayWindow.CreateBlink());
            marker.HideAndReset();
            check(!border.HasAnimatedProperties && border.Opacity == 1 && marker.Width == 2 && marker.Height == 2 &&
                new WindowInteropHelper(marker).Handle == IntPtr.Zero,
                "marker clear removes the pulse clock and restores a tiny idle surface without opening a native window");
        }
        finally { marker.Close(); }
        var targetGlow = new OverlayWindow();
        try
        {
            var glow = (GuideFrameVisual)((Border)targetGlow.Content).Child;
            glow.SetMotion(true);
            check(glow.HasAnimatedProperties, "visible target ripple can animate without a desktop window");
            targetGlow.HideAndReset();
            check(!glow.HasAnimatedProperties && glow.Phase == 0 && new WindowInteropHelper(targetGlow).Handle == IntPtr.Zero,
                "clearing target glow removes its animation clock and resets the ripple phase");
            glow.SetMotion(false);
            check(!glow.HasAnimatedProperties, "reduced-motion target glow stays static");
        }
        finally { targetGlow.Close(); }
        var steady = RenderFrame(false, 0);
        var wash = RenderFrame(false, .5);
        var cleared = RenderFrame(false, 0);
        var rgba = new byte[320 * 180 * 4]; steady.CopyPixels(rgba, 320 * 4, 0);
        var washPixels = new byte[rgba.Length]; wash.CopyPixels(washPixels, 320 * 4, 0);
        var clearPixels = new byte[rgba.Length]; cleared.CopyPixels(clearPixels, 320 * 4, 0);
        check(rgba[(90 * 320 + 160) * 4 + 3] == 0 && rgba[(6 * 320 + 160) * 4 + 3] > 0 &&
            !rgba.SequenceEqual(washPixels) && rgba.SequenceEqual(clearPixels),
            "wide steady target band leaves content clear and wash reset restores the same surface without a window");
        byte Alpha(int y) => rgba[(y * 320 + 160) * 4 + 3];
        var blue = ((SolidColorBrush)GuideTheme.WhaleBlue).Color;
        check(Alpha(0) == 255 && rgba[160 * 4] == blue.B && rgba[160 * 4 + 1] == blue.G && rgba[160 * 4 + 2] == blue.R,
            "outer water edge uses the exact opaque prompt blue before fading inward");
        var movedPixels = new byte[rgba.Length]; RenderFrame(false, 0, phase: 1.7).CopyPixels(movedPixels, 320 * 4, 0);
        check(Alpha(1) == Alpha(6) && Alpha(6) > Alpha(12) && Alpha(12) > Alpha(24) && Alpha(24) > 0 && Alpha(60) == 0 &&
            !rgba.SequenceEqual(movedPixels), "soft wide glow fades inward without an inner stroke and ripple phase changes the water shape");
        var work = new Rect(0, 0, 1920, 1040);
        check(BallWindow.ConstrainPosition(new Point(1910, 1030), work, new Size(88, 88)) == new Point(1832, 952) &&
            BallWindow.ConstrainPosition(new Point(1910, 1030), work, new Size(WhalePrompt.WidthDip, 88)) == new Point(1560, 952),
            "actual whale and inline surface sizes share the same visible work-area constraint");
        var normal = BallWindow.TopDockPosition(new Rect(100, 180, 900, 700), work);
        var full = BallWindow.TopDockPosition(new Rect(0, 0, 1920, 1040), work);
        check(normal.Y + WhalePrompt.DockFoot == 180 && full.Y == 0,
            "normal top docking stays outside the title bar and fullscreen docking stays inside the work area");
        foreach (var target in new[] { new Rect(-500, -200, 900, 700), new Rect(1800, 900, 800, 700), new Rect(0, 0, 80, 60) })
            check(work.Contains(new Rect(BallWindow.TopDockPosition(target, work), new Size(WhalePrompt.WidthDip, 88))),
                "top input remains fully reachable for shifted or small targets");
        var prompt = new WhalePrompt(); var submissions = new List<string>(); var focus = 0;
        prompt.Submitted += submissions.Add; prompt.FocusRequested += () => focus++;
        var send = prompt.Send;
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
            draw.DrawRectangle(new SolidColorBrush(Color.FromRgb(230, 240, 247)), null, new Rect(0, 0, 1260, 660));
            var poses = new[] { new WhalePose(109), new WhalePose(170), WhaleArt.DropPose(0),
                WhaleArt.DropPose(.16), WhaleArt.DropPose(.4), WhaleArt.DropPose(.8) };
            var labels = new[] { "Idle (static)", "Drag / mirrored", "Drop / immediate spray", "Spray + morph (160ms)", "Wash + morph (400ms)", "Native input (800ms)" };
            for (var i = 0; i < poses.Length; i++)
            {
                var x = i % 3 * 420d + 20; var y = i / 3 * 210d + 44;
                draw.DrawText(new FormattedText(labels[i], CultureInfo.InvariantCulture, FlowDirection.LeftToRight,
                    new Typeface("Segoe UI"), 14, GuideTheme.Ink, 1), new Point(x, y - 22));
                draw.DrawRoundedRectangle(Brushes.White, new Pen(GuideTheme.Outline, 1), new Rect(x, y + 88, 380, 80), 4, 4);
                var view = new WhaleVisual(); view.SetPose(poses[i], i == 1 ? 1 : -1);
                view.Measure(new Size(WhalePrompt.WidthDip, 88)); view.Arrange(new Rect(0, 0, WhalePrompt.WidthDip, 88));
                var bitmap = new RenderTargetBitmap((int)WhalePrompt.WidthDip, 88, 96, 96, PixelFormats.Pbgra32); bitmap.Render(view);
                draw.DrawImage(bitmap, new Rect(x + 10, y, WhalePrompt.WidthDip, 88));
                if (i == 5)
                {
                    var input = new WhalePrompt(); input.SetQuestion(true);
                    input.Measure(new Size(WhalePrompt.WidthDip, 88)); input.Arrange(new Rect(0, 0, WhalePrompt.WidthDip, 88));
                    var prompt = new RenderTargetBitmap((int)WhalePrompt.WidthDip, 88, 96, 96, PixelFormats.Pbgra32); prompt.Render(input);
                    draw.DrawImage(prompt, new Rect(x + 10, y, WhalePrompt.WidthDip, 88));
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
        var result = new RenderTargetBitmap(1260, 660, 96, 96, PixelFormats.Pbgra32); result.Render(sheet);
        var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(result));
        var file = Path.GetFullPath(".artifacts/checks/whale-native-states.png"); Directory.CreateDirectory(Path.GetDirectoryName(file)!);
        using var output = File.Create(file); encoder.Save(output);
        Console.WriteLine("Offscreen whale states: " + file + "; no native window, capture or network");
        RenderFeedback();
    }

    internal static RenderTargetBitmap RenderFrame(bool control, double progress, int width = 320, int height = 180, double phase = 0)
    {
        var overlay = new OverlayWindow(control);
        try
        {
            overlay.SetWaterProgress(progress);
            var frame = (Border)overlay.Content; overlay.Content = null;
            ((GuideFrameVisual)frame.Child).Phase = phase;
            frame.Measure(new Size(width, height)); frame.Arrange(new Rect(0, 0, width, height));
            var bitmap = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32); bitmap.Render(frame);
            return bitmap;
        }
        finally { overlay.Close(); }
    }

    static void RenderFeedback()
    {
        var bitmap = FeedbackFrame(0, 1, true);
        var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap));
        var file = Path.GetFullPath(".artifacts/checks/whale-feedback-states.png");
        using (var output = File.Create(file)) encoder.Save(output);
        var frames = Path.GetFullPath(".artifacts/checks/soft-glow-frames"); Directory.CreateDirectory(frames);
        var blink = OverlayWindow.CreateBlink(); var clock = blink.CreateClock(); clock.Controller!.Begin();
        for (var i = 0; i < 120; i++)
        {
            var seconds = i / 10d;
            clock.Controller.SeekAlignedToLastTick(TimeSpan.FromSeconds(seconds), System.Windows.Media.Animation.TimeSeekOrigin.BeginTime);
            var png = new PngBitmapEncoder();
            png.Frames.Add(BitmapFrame.Create(FeedbackFrame(seconds * Math.PI / 2, blink.GetCurrentValue(1, 1, clock))));
            using var output = File.Create(Path.Combine(frames, $"{i:000}.png")); png.Save(output);
        }
        clock.Controller.Remove();
        Console.WriteLine("Offscreen vector feedback: " + file + "; 120 playback frames at10fps: " + frames);
    }

    static RenderTargetBitmap FeedbackFrame(double phase, double opacity, bool comparison = false)
    {
        var sheet = new DrawingVisual();
        using (var draw = sheet.RenderOpen())
        {
            draw.DrawRectangle(GuideTheme.Surface, null, new Rect(0, 0, 1000, 620));
            for (var i = 0; i < 4; i++)
            {
                var x = 24 + i % 2 * 490; var y = 44 + i / 2 * 286;
                var rect = new Rect(x, y, 456, 236);
                draw.DrawRectangle(i == 3 ? GuideTheme.Ink : Brushes.White, null, rect);
                draw.DrawText(new FormattedText(new[] { "Drop / wash midpoint", "Soft inward water glow", "Control / blinking on light", "Control / blinking on dark" }[i],
                    CultureInfo.InvariantCulture, FlowDirection.LeftToRight, new Typeface("Segoe UI"), 16, GuideTheme.Ink, 1), new Point(x, y - 26));
                draw.DrawText(new FormattedText("Selected window content stays readable", CultureInfo.InvariantCulture,
                    FlowDirection.LeftToRight, new Typeface("Segoe UI"), 16, i == 3 ? Brushes.White : GuideTheme.Ink, 1), new Point(x + 32, y + 65));
                draw.DrawImage(RenderFrame(false, i == 0 ? .5 : 0, 456, 236, phase), rect);
                if (i >= 2)
                {
                    var control = new Rect(x + 300, y + 142, 52, 26);
                    draw.DrawRoundedRectangle(GuideTheme.Accent, null, control, 13, 13);
                    draw.DrawEllipse(Brushes.White, null, new Point(control.Right - 13, control.Top + 13), 9, 9);
                    var expanded = OverlayWindow.ControlBounds(control, rect, rect, 1);
                    draw.PushOpacity(comparison && i == 3 ? 0 : opacity);
                    draw.DrawImage(RenderFrame(true, 0, (int)expanded.Width, (int)expanded.Height), expanded);
                    draw.Pop();
                }
            }
        }
        var bitmap = new RenderTargetBitmap(1000, 620, 96, 96, PixelFormats.Pbgra32); bitmap.Render(sheet);
        return bitmap;
    }
}
