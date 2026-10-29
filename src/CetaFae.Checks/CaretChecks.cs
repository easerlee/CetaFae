using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Text.Json;
using CetaFae.Plugins;
using CetaFae.Probe;

namespace CetaFae.Checks;

static class CaretChecks
{
    internal static void Run(Action<bool, string> check)
    {
        var region = new Rect(-100, 30, 80, 50);
        var caret = new Rect(-75, 40, 1, 21);
        var plain = Image();
        var blinking = Image(true);
        var before = GuideVisual.Read(plain, region, caret);
        var after = GuideVisual.Read(blinking, region, caret);
        check(before.Matches(after) && after.Matches(before),
            "an exact native solid-caret inversion preserves full-image visual context");
        var noCaret = GuideVisual.Read(plain, region, null);
        check(after.Matches(noCaret) && noCaret.Matches(after),
            "a known caret may blink out or disappear without discarding unchanged content");
        check(!noCaret.Matches(GuideVisual.Read(blinking, region, null)),
            "unknown or custom caret pixels retain exact comparison");
        check(!before.Matches(GuideVisual.Read(Image(true, 0), region, caret)),
            "a changed pixel outside the caret still invalidates an answer");
        check(!before.Matches(GuideVisual.Read(Image(true, (10 * 80 + 25) * 4), region, caret)),
            "an arbitrary change inside the caret is not masked or tolerated");
        check(!before.Matches(GuideVisual.Read(Image(true, 3), region, caret)),
            "alpha changes are not treated as caret RGB inversion");
        check(!before.Matches(GuideVisual.Read(blinking, region, new Rect(-74, 40, 1, 21))),
            "different known caret geometry cannot explain changed pixels");
        foreach (var invalid in new[] { new Rect(-101, 40, 1, 21), new Rect(-75, 40, 3, 21),
                     new Rect(-75, 40, 1, 65), new Rect(-75.5, 40, 1, 21), Rect.Empty })
            check(GuideVisual.Read(plain, region, invalid).CaretBounds is null,
                "outside, oversized and noninteger caret geometry cannot grant an exception");
        check(GuideVisual.Read(plain, new Rect(-100, 30, 160, 100), caret).CaretBounds is null,
            "resized image geometry retains strict comparison");

        var plugin = PluginCatalog.ParsePlugin(GeneralGuideChecks.Package);
        GuideObservation Observation(byte[] png, GuideVisual visual) =>
            new(new IntPtr(1), 1, region, [], png, PluginId: plugin.Id,
                VisualHash: GuideObservation.Fingerprint(png), Visual: visual);
        var original = Observation(plain, before);
        var current = Observation(blinking, after);
        original.ValidateContext(current, null);
        check(true, "zero-actionable-control context accepts only the verified caret inversion");
        var decoded = JsonSerializer.Deserialize<GuideObservation>(
            JsonSerializer.Serialize(current, ObservationWorker.Json), ObservationWorker.Json)!;
        ObservationWorker.ValidateScopeReply(decoded, plugin, null, null, true);
        check(decoded.Visual == current.Visual, "local caret evidence round-trips through the production IPC converters");
        foreach (var forged in new GuideVisual?[] { null, after with { PixelHash = "forged" },
                     after with { BlinkHash = "forged" }, after with { CaretBounds = new Rect(-75, 40, 3, 21) } })
        {
            try { ObservationWorker.ValidateScopeReply(current with { Visual = forged }, plugin, null, null, true); }
            catch (InvalidDataException) { check(true, "IPC recomputes and rejects missing or forged caret evidence"); continue; }
            throw new InvalidOperationException("forged caret evidence accepted");
        }
        check(GuidePayload.From(current).Png!.SequenceEqual(blinking) &&
              !JsonSerializer.Serialize(GuidePayload.From(current)).Contains("BlinkHash", StringComparison.Ordinal),
            "model image bytes are unchanged and local caret evidence is not an outbound field");
        try { original.ValidateContext(current with { Frame = new Rect(0, 0, 80, 50) }, null); }
        catch (InvalidDataException) { check(true, "caret handling cannot bypass target-frame binding"); return; }
        throw new InvalidOperationException("changed target frame accepted");

        static byte[] Image(bool blink = false, int? changedByte = null)
        {
            var pixels = Enumerable.Repeat((byte)255, 80 * 50 * 4).ToArray();
            if (blink)
                for (var y = 10; y < 31; y++)
                    for (var c = 0; c < 3; c++) pixels[(y * 80 + 25) * 4 + c] = 0;
            if (changedByte is { } offset) pixels[offset] ^= 127;
            var bitmap = BitmapSource.Create(80, 50, 96, 96, PixelFormats.Bgra32, null, pixels, 80 * 4);
            var encoder = new PngBitmapEncoder();
            encoder.Frames.Add(BitmapFrame.Create(bitmap));
            using var stream = new MemoryStream();
            encoder.Save(stream);
            return stream.ToArray();
        }
    }
}
