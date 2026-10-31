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
    }

    internal static void Render()
    {
        const int width = (int)WhalePrompt.WidthDip;
        var directory = Path.GetFullPath(".artifacts/checks/sinking-frames"); Directory.CreateDirectory(directory);
        for (var frame = 0; frame < 72; frame++)
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
                    if (seconds >= 0) draw.DrawImage(WhaleChecks.RenderFrame(false,
                        WhaleArt.DropPose(seconds).Progress, 510, (int)target.Height, seconds * Math.PI / 2), target);
                    var host = new Grid { Width = width, Height = 88, ClipToBounds = true };
                    FrameworkElement content;
                    if (seconds >= WhaleArt.DropDuration)
                    { var prompt = new WhalePrompt(); prompt.SetTopAttached(pane == 1); prompt.SetQuestion(false); content = prompt; }
                    else
                    { var whale = new WhaleVisual { TopAttached = pane == 1 }; whale.SetPose(seconds < 0 ? new WhalePose(319) : WhaleArt.DropPose(seconds)); content = whale; }
                    host.Children.Add(content); host.Measure(new Size(width, 88)); host.Arrange(new Rect(0, 0, width, 88)); host.UpdateLayout();
                    var bitmap = new RenderTargetBitmap(width, 88, 96, 96, PixelFormats.Pbgra32); bitmap.Render(host);
                    draw.DrawImage(bitmap, new Rect(target.Left + (target.Width - width) / 2, pane == 0 ? target.Top - WhalePrompt.DockFoot : target.Top, width, 88));
                    draw.DrawText(Text("Generated target content remains readable."), new Point(target.Left + 32, target.Bottom - 80));
                }
            }
            var image = new RenderTargetBitmap(1100, 440, 96, 96, PixelFormats.Pbgra32); image.Render(sheet);
            var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(image));
            using var file = File.Create(Path.Combine(directory, $"{frame:000}.png")); encoder.Save(file);
        }
        Console.WriteLine("Offscreen sinking frames: " + directory + ";72 frames at25fps; no desktop or network");
    }
    static FormattedText Text(string text) => new(text, CultureInfo.InvariantCulture, FlowDirection.LeftToRight,
        new Typeface("Segoe UI"), 13, GuideTheme.Ink, 1);
}
