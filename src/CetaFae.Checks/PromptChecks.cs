using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Automation;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using CetaFae.Probe;

namespace CetaFae.Checks;

internal static class PromptChecks
{
    internal static void Run(Action<bool, string> check)
    {
        foreach (var width in new[] { 320d, 360d, 420d })
        foreach (var state in new[] { "waiting", "question", "answer", "result" })
        {
            var prompt = new WhalePrompt(); prompt.SetQuestion(state is "question" or "answer");
            if (state == "answer") prompt.Answer.Text = "请指出位置，不修改。";
            if (state == "result") prompt.SetResult();
            prompt.Measure(new Size(width, 88)); prompt.Arrange(new Rect(0, 0, width, 88)); prompt.UpdateLayout();
            new RenderTargetBitmap((int)width, 88, 96, 96, PixelFormats.Pbgra32).Render(prompt);
            Rect Bounds(FrameworkElement element) => element.TransformToAncestor(prompt).TransformBounds(new Rect(element.RenderSize));
            var area = new Rect(prompt.RenderSize);
            check(area.Contains(Bounds(prompt.Dismiss)) && area.Contains(Bounds(prompt.Answer)) && area.Contains(Bounds(prompt.DragSurface)) &&
                (!prompt.CanAnswer || area.Contains(Bounds(prompt.Send))) && prompt.Answer.ActualWidth >= 120,
                $"attached prompt {state}/{width}: input, blue drag surface and actions fit without clipping");
            if (prompt.CanAnswer)
                check(Bounds(prompt.DragSurface).Contains(Bounds(prompt.Dismiss)) &&
                    Bounds(prompt.Dismiss).Left > Bounds(prompt.Send).Right && prompt.Dismiss.Width < prompt.Send.Width &&
                    prompt.Dismiss.Background == Brushes.Transparent && prompt.Dismiss.Opacity < 1,
                    $"attached prompt {state}/{width}: close stays inside the blue shell, separate and visually secondary to send");
            check(ReferenceEquals(VisualTreeHelper.HitTest(prompt, new Point(width / 2, prompt.Height - 2))?.VisualHit, prompt.DragSurface) &&
                    !ReferenceEquals(VisualTreeHelper.HitTest(prompt, Bounds(prompt.Answer).TopLeft + new Vector(12, 12))?.VisualHit, prompt.DragSurface),
                $"attached prompt {state}/{width}: blue background accepts dragging while input remains a separate hit target");
            check(prompt.Send.Visibility == (prompt.CanAnswer ? Visibility.Visible : Visibility.Collapsed) &&
                prompt.Answer.IsReadOnly == !prompt.CanAnswer && AutomationProperties.GetName(prompt.Dismiss).Contains("本次指路"),
                $"attached prompt {state}/{width}: state and accessible close meaning remain explicit");
        }
        var ball = new BallWindow { Width = WhalePrompt.WidthDip }; string? reason = null; var submissions = 0;
        ball.TrackingStopped += value => reason = value; ball.AnswerSubmitted += _ => submissions++;
        try
        {
            ball.ShowQuestion(); ball.Prompt.Answer.Text = "不会发送的生成回答";
            ball.Prompt.Dismiss.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            ball.Prompt.Send.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            check(reason == "inline_closed" && !ball.HasInlineInput && !ball.Prompt.CanAnswer && ball.Prompt.Answer.Text == "" &&
                ball.Width == 88 && submissions == 0 && new WindowInteropHelper(ball).Handle == IntPtr.Zero,
                "inline close uses existing cancellation, restores whale and prevents stale submission without launching a window");
        }
        finally { ball.Close(); }
        var shell = PromptShell.Shape(WhalePrompt.WidthDip, 50);
        check(shell.IsFrozen && shell.Bounds == new Rect(0, 0, 360, 50), "native prompt and morph share one bounded shoulder/foot geometry");
    }

    internal static void Render()
    {
        var sheet = new DrawingVisual();
        using (var draw = sheet.RenderOpen())
        {
            draw.DrawRectangle(GuideTheme.Surface, null, new Rect(0, 0, 1100, 820));
            var labels = new[] { "Question / exterior dock", "Chinese answer / exterior dock", "Waiting / no send action",
                "Result / close ends the session", "Narrow320DIP / complete controls", "Maximized / prompt stays inside" };
            for (var i = 0; i < labels.Length; i++)
            {
                var x = 20 + i % 2 * 550; var y = 34 + i / 2 * 268;
                draw.DrawText(Text(labels[i], 15, GuideTheme.Ink), new Point(x, y - 22));
                var target = new Rect(x, y + 94, 510, 140);
                draw.DrawRectangle(Brushes.White, new Pen(GuideTheme.Muted, .5), target);
                draw.DrawRectangle(new SolidColorBrush(Color.FromRgb(231, 235, 239)), null,
                    new Rect(target.X, target.Y, target.Width, 32));
                draw.DrawText(Text("Generated application     +                                      —   □   ×", 12, GuideTheme.Muted),
                    new Point(x + 12, target.Y + 7));
                draw.DrawText(Text("This is a fixed-data layout preview.", 14, GuideTheme.Ink), new Point(x + 22, target.Y + (i == 5 ? 110 : 65)));
                draw.DrawImage(WhaleChecks.RenderFrame(false, 0, 510, 140, .5), target);
                var width = i == 4 ? 320 : WhalePrompt.WidthDip;
                var prompt = new WhalePrompt(); prompt.SetQuestion(i is 0 or 1 or 4 or 5);
                if (i == 1) prompt.Answer.Text = "请指出下载开关的位置，不修改。";
                if (i == 3) prompt.SetResult();
                prompt.Measure(new Size(width, 88)); prompt.Arrange(new Rect(0, 0, width, 88)); prompt.UpdateLayout();
                var bitmap = new RenderTargetBitmap((int)width, 88, 96, 96, PixelFormats.Pbgra32); bitmap.Render(prompt);
                draw.DrawImage(bitmap, new Rect(x + 70, i == 5 ? target.Top - 12 : target.Top - WhalePrompt.DockFoot, width, 88));
            }
        }
        var bitmapSheet = new RenderTargetBitmap(1100, 820, 96, 96, PixelFormats.Pbgra32); bitmapSheet.Render(sheet);
        var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmapSheet));
        var file = Path.GetFullPath(".artifacts/checks/prompt-attached-states.png"); Directory.CreateDirectory(Path.GetDirectoryName(file)!);
        using var output = File.Create(file); encoder.Save(output);
        Console.WriteLine("Offscreen attached prompt: " + file + "; no native window, capture or network");
    }

    static FormattedText Text(string text, double size, Brush ink) => new(text, CultureInfo.InvariantCulture,
        FlowDirection.LeftToRight, new Typeface("Segoe UI"), size, ink, 1);
}
