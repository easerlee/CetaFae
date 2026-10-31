using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using CetaFae.Probe;

namespace CetaFae.Checks;

internal static class DropChecks
{
    internal static void Run(Action<bool, string> check)
    {
        const int width = (int)WhalePrompt.WidthDip;
        var poses = Enumerable.Range(0, 21).Select(i => WhaleArt.DropPose(i * .04)).ToArray();
        check(poses.Take(20).All(p => p.Submerge && p.Alpha == 1 && p.ScaleX == 1 && p.ScaleY == 1) &&
            poses.Zip(poses.Skip(1)).All(pair => pair.First.Y <= pair.Second.Y),
            "immersion is downward occlusion at constant scale and alpha rather than shrinking or fading");
        var bodies = poses.Select(p => WhaleVisual.WaterBody(p.Progress, width)).ToArray();
        check(bodies[0].Width is > 35 and < 46 && bodies[0].Top is > 64 and < 68 &&
            bodies.All(b => b.Bottom <= WhalePrompt.DockFoot) &&
            bodies.Zip(bodies.Skip(1)).All(pair => pair.First.Width <= pair.Second.Width && pair.First.Top >= pair.Second.Top) &&
            bodies[^1] == new Rect(0, 40, width, 36),
            "the original small underbelly water expands continuously into the native shell bounds without a second puddle");
        check(poses.Take(12).All(p => p.Spout >= 0) && poses[^1].Spout == -1 && poses[^1].Alpha == 0 &&
            WhaleArt.DropPose(0, true) == poses[^1],
            "spray overlaps emergence and sinking while completion and reduced motion remove the whale");
        var full = WhaleChecks.RenderFrame(false, 0, 600, 400);
        var pixels = new byte[600 * 400 * 4]; full.CopyPixels(pixels, 2400, 0);
        byte Alpha(int y) => pixels[(y * 600 + 300) * 4 + 3];
        check(Alpha(0) == 255 && Alpha(14) == 255 && Alpha(20) < 255 && Alpha(20) > Alpha(50) &&
            Alpha(50) > Alpha(80) && Alpha(110) == 0 && GuideFrameVisual.WaterDepth == 96 && GuideFrameVisual.SolidDepth == 16,
            "full-size water retains sixteen solid blue DIP before its wider fade and transparent center");
        var original = new DrawingVisual();
        using (var draw = original.RenderOpen())
        {
            draw.PushTransform(new TranslateTransform(width / 2d, 44)); draw.PushTransform(new ScaleTransform(-1, 1));
            draw.DrawImage(new WhaleArt().Frame("water-rear", 319), new Rect(-44, -44, 88, 88));
        }
        byte[] Raster(Visual visual)
        {
            var image = new RenderTargetBitmap(width, 88, 96, 96, PixelFormats.Pbgra32); image.Render(visual);
            var result = new byte[width * 88 * 4]; image.CopyPixels(result, width * 4, 0); return result;
        }
        var firstWater = new DrawingVisual();
        using (var draw = firstWater.RenderOpen()) draw.DrawGeometry(GuideTheme.WhaleBlue, null, WhaleWater.Shape(0, width));
        var sourcePixels = Raster(original); var firstPixels = Raster(firstWater);
        var union = 0; var intersection = 0;
        for (var i = 3; i < sourcePixels.Length; i += 4)
        {
            var source = sourcePixels[i] >= 128; var first = firstPixels[i] >= 128;
            if (source || first) union++; if (source && first) intersection++;
        }
        check(intersection > union * .8, "initial water contour overlaps the embedded resting-water silhouette rather than a new rounded rectangle");
        var finalWater = new DrawingVisual(); var nativeWater = new DrawingVisual();
        using (var draw = finalWater.RenderOpen()) draw.DrawGeometry(GuideTheme.WhaleBlue, null, WhaleWater.Shape(1, width));
        using (var draw = nativeWater.RenderOpen())
        { draw.PushTransform(new TranslateTransform(0, 40)); draw.DrawGeometry(GuideTheme.WhaleBlue, null, PromptShell.Shape(width, 36)); }
        var finalPixels = Raster(finalWater); var nativePixels = Raster(nativeWater);
        check(Enumerable.Range(0, width * 88).Count(i => Math.Abs(finalPixels[i * 4 + 3] - nativePixels[i * 4 + 3]) > 32) < 60,
            "expanded source water resolves to the actual native shell with only subpixel flattening differences");
        var topWater = new DrawingVisual(); var topNative = new DrawingVisual();
        using (var draw = topWater.RenderOpen()) draw.DrawGeometry(GuideTheme.WhaleBlue, null, WhaleWater.Shape(1, width, topAttached: true));
        using (var draw = topNative.RenderOpen())
        { draw.PushTransform(new MatrixTransform(1, 0, 0, -1, 0, 36)); draw.DrawGeometry(GuideTheme.WhaleBlue, null, PromptShell.Shape(width, 36)); }
        var topPixels = Raster(topWater); var topNativePixels = Raster(topNative);
        check(WhaleWater.Shape(1, width, topAttached: true).Bounds == new Rect(0, 0, width, 36) &&
            Enumerable.Range(0, width * 88).Count(i => Math.Abs(topPixels[i * 4 + 3] - topNativePixels[i * 4 + 3]) > 32) < 60,
            "top attachment flips only the water shell into a compact36DIP hanging tab with no top gap");
        var art = new WhaleArt();
        check(Enumerable.Range(0, 17).All(i =>
        {
            var bytes = new byte[96 * 96 * 4];
            new FormatConvertedBitmap(art.Frame("spout", WhaleArt.DropPose(i * .04).Spout), PixelFormats.Bgra32, null, 0).CopyPixels(bytes, 384, 0);
            return Enumerable.Range(0, 96 * 96).Any(p => bytes[p * 4 + 3] > 0);
        }), "spray uses nonempty embedded frames throughout the first640ms instead of advancing into empty cells");
        foreach (var attached in new[] { false, true })
        {
            var hiddenWhale = new DrawingVisual(); var pose = WhaleArt.DropPose(.72);
            using (var draw = hiddenWhale.RenderOpen())
            {
                draw.PushClip(WhaleWater.Shape(pose.Progress, width, above: true, topAttached: attached));
                draw.PushTransform(new TranslateTransform(width / 2d, 44 + pose.Y * 88));
                draw.PushTransform(new ScaleTransform(-1, 1));
                draw.DrawImage(art.Frame("whale-visible", 319), new Rect(-44, -44, 88, 88));
            }
            var remaining = Raster(hiddenWhale);
            check(pose.Alpha == 1 && Enumerable.Range(0, width * 88).All(i => remaining[i * 4 + 3] == 0),
                "rising water fully occludes the still-opaque whale before native input replaces it in either layout");
        }
        check(WindowWash.Progress(0) == -1 && WindowWash.Progress(.8) == -1 &&
            WindowWash.Progress(1.2) is > .49 and < .51 && WindowWash.Progress(1.6) == 1,
            "downward coverage starts after the water-dialog rise and ends on a finite separate clock");
        foreach (var size in new[] { new Size(3440, 1392), new Size(3840, 2160), new Size(8, 8) })
        {
            var strips = Enumerable.Range(1, 199).Select(i => WindowWash.Region(size, i / 200d)).Where(r => !r.IsEmpty).ToArray();
            check(strips.All(r => r.Height <= WindowWash.Band + 10 && new Rect(size).Contains(r)) &&
                Enumerable.Range(0, (int)size.Height).All(y => strips.Any(r => r.Top <= y && r.Bottom > y)) &&
                WindowWash.Region(size, 0).IsEmpty && WindowWash.Region(size, 1).IsEmpty,
                "finite moving strip visits every window row without full-target allocation and leaves no final wash");
        }
        foreach (var progress in new[] { .12, .5, .88 })
        {
            var size = new Size(600, 320); var region = WindowWash.Region(size, progress);
            var fullWash = new DrawingVisual();
            using (var draw = fullWash.RenderOpen()) WindowWash.Draw(draw, size, progress);
            var fullImage = new RenderTargetBitmap(600, 320, 96, 96, PixelFormats.Pbgra32); fullImage.Render(fullWash);
            var all = new byte[600 * 320 * 4]; fullImage.CopyPixels(all, 2400, 0);
            var strip = new GuideFrameVisual(wash: true); strip.SetViewport(size, region); strip.SetProgress(progress);
            strip.Measure(region.Size); strip.Arrange(new Rect(region.Size)); strip.UpdateLayout();
            var image = new RenderTargetBitmap(600, (int)region.Height, 96, 96, PixelFormats.Pbgra32); image.Render(strip);
            var cropped = new byte[600 * (int)region.Height * 4]; image.CopyPixels(cropped, 2400, 0);
            check(cropped.SequenceEqual(all.Skip((int)region.Top * 2400).Take(cropped.Length)) &&
                Enumerable.Range(0, 320).Where(y => y < region.Top || y >= region.Bottom)
                    .All(y => Enumerable.Range(0, 600).All(x => all[(y * 600 + x) * 4 + 3] == 0)),
                "bounded wash surface matches full vector composition exactly and allocates no invisible rows");
        }
        var work = new Rect(-1920, -100, 1920, 1040); var start = new Point(-1700, 70);
        var travel = Enumerable.Range(0, 16).Select(i => BallWindow.ReturnPosition(start, work, i * .04)).ToArray();
        check(travel[0] == start && travel[^1] == new Point(-96, 844) &&
            travel.Zip(travel.Skip(1)).All(p => (BallWindow.HomePosition(work) - p.Second).Length <=
                (BallWindow.HomePosition(work) - p.First).Length) &&
            BallWindow.HomePosition(new Rect(10, 20, 40, 40)) == new Point(10, 20),
            "home travel eases to the work-area bottom-right including negative-origin and tiny monitors");
        check(new[] { "foreground_changed", "inline_closed", "target_gone", "cloud_changed", "cancelled" }
                .All(reason => BallWindow.ShouldReturn(new IntPtr(1), reason)) &&
            !BallWindow.ShouldReturn(new IntPtr(1), "drag_start") && !BallWindow.ShouldReturn(new IntPtr(1), "exit") &&
            !BallWindow.ShouldReturn(IntPtr.Zero, "cloud_changed"),
            "ending a bound session returns home but reselecting exiting and idle consent changes do not");
        var ball = new BallWindow();
        try
        {
            ball.BeginDockMotion(); ball.ShowQuestion(); ball.Prompt.Answer.Text = "生成回答";
            ball.AdvanceMotion(1); ball.AdvanceMotion(WindowWash.TotalDuration);
            check(ball.HasInlineInput && ball.Prompt.CanAnswer && ball.Prompt.Answer.Text == "生成回答" && !ball.ArtAnimating &&
                new System.Windows.Interop.WindowInteropHelper(ball).Handle == IntPtr.Zero,
                "a fast question accepts text during the finite wash and subsequent animation ticks never clear that text");
            ball.ResetArt(); ball.Left = 100; ball.Top = 100; ball.ReturnHome(); ball.AdvanceMotion(.6);
            var whale = (WhaleVisual)((Grid)ball.Content).Children[0];
            check(new Point(ball.Left, ball.Top) == BallWindow.HomePosition(SystemParameters.WorkArea) && whale.Facing == -1,
                "return completion places the real product surface at home and faces left without creating a native handle");
            ball.AdvanceMotion(4.4);
            check(!ball.ArtAnimating && whale.Facing == -1 && whale.Pose.Frame == 109,
                "arrival plays the existing click sequence once then stops on left-facing idle");
            ball.ReturnHome(); ball.StopTracking("drag_start");
            check(!ball.ArtAnimating && new System.Windows.Interop.WindowInteropHelper(ball).Handle == IntPtr.Zero,
                "a new drag interrupts home travel without opening or activating a desktop surface");
        }
        finally { ball.Close(); }
    }

    internal static void Render()
    {
        const int width = (int)WhalePrompt.WidthDip;
        var directory = Path.GetFullPath(".artifacts/checks/rising-water-frames"); Directory.CreateDirectory(directory);
        for (var frame = 0; frame < 210; frame++)
        {
            var seconds = (frame - 10) / 25d;
            var sheet = new DrawingVisual();
            using (var draw = sheet.RenderOpen())
            {
                draw.DrawRectangle(GuideTheme.Surface, null, new Rect(0, 0, 1100, 440));
                for (var pane = 0; pane < 2; pane++)
                {
                    var target = new Rect(20 + pane * 550, pane == 0 ? 145 : 40, 510, pane == 0 ? 250 : 355);
                    draw.DrawText(Text(pane == 0 ? "Normal / exterior water dialog" : "Maximized / bounded interior dialog"),
                        new Point(target.Left, 10));
                    draw.DrawRectangle(Brushes.White, null, target);
                    draw.DrawRectangle(new SolidColorBrush(Color.FromRgb(231, 235, 239)), null, new Rect(target.Left, target.Top, target.Width, 30));
                    draw.DrawText(Text("Generated target content remains readable."), new Point(target.Left + 32, target.Bottom - 80));
                    if (seconds >= 0 && seconds < 2.4)
                    {
                        draw.PushClip(new RectangleGeometry(target));
                        draw.PushTransform(new TranslateTransform(target.X, target.Y));
                        var progress = WindowWash.Progress(seconds);
                        GuideFrameVisual.DrawWater(draw, target.Size, seconds * Math.PI / 2, progress >= 1 ? 0 : progress);
                        WindowWash.Draw(draw, target.Size, progress);
                        draw.Pop(); draw.Pop();
                    }
                    var returning = seconds >= 2.4;
                    var surfaceWidth = returning ? 88 : width;
                    var host = new Grid { Width = surfaceWidth, Height = 88, ClipToBounds = true };
                    FrameworkElement content;
                    if (returning)
                    {
                        var age = seconds - 2.4;
                        var whale = new WhaleVisual();
                        whale.SetPose(age < .6 ? new WhalePose(140 + (int)(age * 25)) :
                            new WhalePose(Math.Min(109, (int)((age - .6) * 25))), age < .6 ? 1 : -1);
                        content = whale;
                    }
                    else if (seconds >= WhaleArt.DropDuration)
                    { var prompt = new WhalePrompt(); prompt.SetTopAttached(pane == 1); prompt.SetQuestion(false); content = prompt; }
                    else
                    { var whale = new WhaleVisual { TopAttached = pane == 1 }; whale.SetPose(seconds < 0 ? new WhalePose(319) : WhaleArt.DropPose(seconds)); content = whale; }
                    host.Children.Add(content); host.Measure(new Size(surfaceWidth, 88)); host.Arrange(new Rect(0, 0, surfaceWidth, 88)); host.UpdateLayout();
                    var bitmap = new RenderTargetBitmap(surfaceWidth, 88, 96, 96, PixelFormats.Pbgra32); bitmap.Render(host);
                    var dock = new Point(target.Left + (target.Width - width) / 2, pane == 0 ? target.Top - WhalePrompt.DockFoot : target.Top);
                    var point = returning ? BallWindow.ReturnPosition(new Point(dock.X + (width - 88) / 2d, dock.Y),
                        new Rect(target.Left, 40, target.Width, 355), seconds - 2.4) : dock;
                    draw.DrawImage(bitmap, new Rect(point, new Size(surfaceWidth, 88)));
                }
            }
            var image = new RenderTargetBitmap(1100, 440, 96, 96, PixelFormats.Pbgra32); image.Render(sheet);
            var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(image));
            using var file = File.Create(Path.Combine(directory, $"{frame:000}.png")); encoder.Save(file);
        }
        Console.WriteLine("Offscreen rising-water / sweep / return frames: " + directory + ";210 frames at25fps; no desktop or network");
    }
    static FormattedText Text(string text) => new(text, CultureInfo.InvariantCulture, FlowDirection.LeftToRight,
        new Typeface("Segoe UI"), 13, GuideTheme.Ink, 1);
}
