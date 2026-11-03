using System.Windows;
using CetaFae.Probe;

namespace CetaFae.Checks;

static class CoordinateChecks
{
    internal static void Run(Action<bool, string> check)
    {
        foreach (var scale in new[] { 1d, 1.25, 1.5, 2 })
        foreach (var work in new[] { new Rect(0, 0, 1920, 1040), new Rect(-2560, -240, 2560, 1400), new Rect(1920, 100, 3840, 2120) })
        {
            var logical = GuideCoordinates.ToDips(work, scale);
            check(Math.Abs(logical.X * scale - work.X) < .001 && Math.Abs(logical.Y * scale - work.Y) < .001 &&
                logical.Size == new Size(work.Width / scale, work.Height / scale),
                "each monitor keeps its physical origin and current scale without primary-display translation");
            var target = new Rect(logical.Left + 40, logical.Top + 40, Math.Min(800, logical.Width - 80), 400);
            var entryPoint = BallWindow.TopDockPosition(target, logical, 280);
            var entry = new Rect(entryPoint, new Size(280, 36));
            var reader = FeedbackLayout.Attached(entry, logical, new Size(280, 200), true);
            check(logical.Contains(reader) && logical.Contains(entry),
                "docking and feedback remain inside the selected monitor at supported scales and negative origins");
            var physicalControl = new Rect(work.Left + 60, work.Top + 80, 120, 28);
            var marker = OverlayWindow.ControlBounds(physicalControl, work, work, scale);
            check(work.Contains(marker) && marker.Contains(physicalControl),
                "marker padding uses the selected scale without clipping against the primary monitor");
        }
        var rejected = false;
        try { GuideCoordinates.ToDips(new Rect(0, 0, 100, 100), double.NaN); }
        catch (System.IO.InvalidDataException) { rejected = true; }
        check(rejected, "unknown display scale cannot produce a guessed coordinate mapping");
    }
}
