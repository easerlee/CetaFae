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
        var poses = Enumerable.Range(0, 21).Select(i => WhaleArt.DropPose(i * .04)).ToArray();
        check(poses.Take(20).All(p => p.Submerge && p.Alpha == 1 && p.ScaleX == 1 && p.ScaleY == 1) &&
            poses.Zip(poses.Skip(1)).All(pair => pair.First.Y <= pair.Second.Y),
            "immersion is downward occlusion at constant scale and alpha rather than shrinking or fading");
        var bodies = poses.Select(p => WhaleVisual.WaterBody(p.Progress, 360)).ToArray();
        check(bodies[0].Width >= 88 && bodies[0].Height > 0 && bodies.All(b => b.Bottom == WhalePrompt.DockFoot) &&
            bodies.Zip(bodies.Skip(1)).All(pair => pair.First.Width <= pair.Second.Width && pair.First.Top >= pair.Second.Top) &&
            bodies[^1] == new Rect(0, 26, 360, 50),
            "the water dialog appears on drop and rises outward from a fixed foot into the native shell bounds");
        check(poses.Take(12).All(p => p.Spout >= 0) && poses[^1].Spout == -1 && poses[^1].Alpha == 0 &&
            WhaleArt.DropPose(0, true) == poses[^1],
            "spray overlaps emergence and sinking while completion and reduced motion remove the whale");
        var full = WhaleChecks.RenderFrame(false, 0, 600, 400);
        var pixels = new byte[600 * 400 * 4]; full.CopyPixels(pixels, 2400, 0);
        byte Alpha(int y) => pixels[(y * 600 + 300) * 4 + 3];
        check(Alpha(0) == 255 && Alpha(14) == 255 && Alpha(20) < 255 && Alpha(20) > Alpha(50) &&
            Alpha(50) > Alpha(80) && Alpha(110) == 0 && GuideFrameVisual.WaterDepth == 96 && GuideFrameVisual.SolidDepth == 16,
            "full-size water retains sixteen solid blue DIP before its wider fade and transparent center");
    }

    internal static void Render()
    {
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
                    var host = new Grid { Width = 360, Height = 88, ClipToBounds = true };
                    FrameworkElement content;
                    if (seconds >= WhaleArt.DropDuration)
                    { var prompt = new WhalePrompt(); prompt.SetQuestion(false); content = prompt; }
                    else
                    { var whale = new WhaleVisual(); whale.SetPose(seconds < 0 ? new WhalePose(109) : WhaleArt.DropPose(seconds)); content = whale; }
                    if (pane == 1) content.RenderTransform = new TranslateTransform(0, -12);
                    host.Children.Add(content); host.Measure(new Size(360, 88)); host.Arrange(new Rect(0, 0, 360, 88)); host.UpdateLayout();
                    var bitmap = new RenderTargetBitmap(360, 88, 96, 96, PixelFormats.Pbgra32); bitmap.Render(host);
                    draw.DrawImage(bitmap, new Rect(target.Left + 75, pane == 0 ? target.Top - WhalePrompt.DockFoot : target.Top, 360, 88));
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
