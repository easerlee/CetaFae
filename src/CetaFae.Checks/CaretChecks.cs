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

        var colored = Image(true, stroke: 91);
        var preceding = Image(true, stroke: 91, column: 24);
        check(before.Matches(GuideVisual.Read(preceding, region, caret)) &&
              GuideVisual.Read(preceding, region, caret).Matches(before),
            "a full-height solid caret at the preceding native column may blink on an exact flat background");
        check(before.Matches(GuideVisual.Read(colored, region, caret)),
            "a full-height colored native caret may blink without requiring RGB inversion");
        check(!noCaret.Matches(GuideVisual.Read(preceding, region, caret)),
            "solid-line compatibility requires native caret evidence on both observations");
        check(!before.Matches(GuideVisual.Read(Image(true, stroke: 91, column: 23), region, caret)),
            "a line two pixels from the native caret cannot grant compatibility");
        check(!before.Matches(GuideVisual.Read(Image(true, stroke: 91, rows: 20), region, caret)),
            "a partial-height colored line is a real visual change");
        check(!before.Matches(GuideVisual.Read(Image(true, stroke: 91, columns: 2), region, caret)),
            "multi-column colored changes are not treated as a one-pixel caret");
        check(!before.Matches(GuideVisual.Read(colored, new Rect(-100, 30, 160, 100), caret)),
            "scaled pixels cannot grant colored-caret reconstruction");
        check(GuideVisual.Read(Image(alpha: 0), region, caret).Matches(
                GuideVisual.Read(Image(true, stroke: 91, column: 24, alpha: 0), region, caret)),
            "unchanged zero capture alpha is preserved rather than assumed opaque");
        check(!before.Matches(GuideVisual.Read(Image(true, changedByte: 0, stroke: 91, column: 24), region, caret)),
            "an outside change cannot hide behind a supported colored caret");
        check(!before.Matches(GuideVisual.Read(Image(true, changedByte: (10 * 80 + 24) * 4, stroke: 91, column: 24), region, caret)),
            "a nonuniform pixel inside the colored line is not ignored");
        check(!before.Matches(GuideVisual.Read(Image(true, changedByte: (10 * 80 + 24) * 4 + 3, stroke: 91, column: 24), region, caret)),
            "solid-line reconstruction never tolerates changed alpha");
        check(!GuideVisual.Read(colored, region, caret).Matches(GuideVisual.Read(Image(true, stroke: 92), region, caret)),
            "two different visible line colors are not treated as a blink");
        check(!before.Matches(GuideVisual.Read(preceding, region, new Rect(-74, 40, 1, 21))),
            "solid-line reconstruction cannot bypass moved native geometry");
        var textured = Image(changedByte: (12 * 80 + 23) * 4);
        check(!GuideVisual.Read(textured, region, caret).Matches(GuideVisual.Read(
                Image(true, changedByte: (12 * 80 + 23) * 4, stroke: 91, column: 24), region, caret)),
            "unchanged textured neighboring pixels do not authorize background reconstruction");

        var plugin = PluginCatalog.ParsePlugin(GeneralGuideChecks.Package);
        GuideObservation Observation(byte[] png, GuideVisual visual) =>
            new(new IntPtr(1), 1, region, [], png, PluginId: plugin.Id,
                VisualHash: GuideObservation.Fingerprint(png), Visual: visual);
        var original = Observation(plain, before);
        var current = Observation(blinking, after);
        var coloredCurrent = Observation(preceding, GuideVisual.Read(preceding, region, caret));
        var coloredDecoded = JsonSerializer.Deserialize<GuideObservation>(
            JsonSerializer.Serialize(coloredCurrent, ObservationWorker.Json), ObservationWorker.Json)!;
        ObservationWorker.ValidateScopeReply(coloredDecoded, plugin, null, null, true);
        check(coloredDecoded.Visual == coloredCurrent.Visual && original.HasSameVisual(coloredDecoded),
            "colored-caret evidence round-trips and is recomputed across the real IPC boundary");
        foreach (var forged in new[] { coloredCurrent.Visual! with { LeadingCaretHash = "forged" },
                     coloredCurrent.Visual! with { SolidCaretHash = "forged" } })
        {
            try { ObservationWorker.ValidateScopeReply(coloredCurrent with { Visual = forged }, plugin, null, null, true); }
            catch (InvalidDataException) { check(true, "IPC rejects forged reconstructed full-image caret evidence"); continue; }
            throw new InvalidOperationException("forged colored caret evidence accepted");
        }
        check(GuidePayload.From(coloredCurrent).Png!.SequenceEqual(preceding) &&
              !JsonSerializer.Serialize(GuidePayload.From(coloredCurrent)).Contains("CaretHash", StringComparison.Ordinal),
            "reconstruction never modifies the model image or sends local caret evidence");
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

        static byte[] Image(bool blink = false, int? changedByte = null, byte stroke = 0, int column = 25, int rows = 21,
            int columns = 1, byte alpha = 255)
        {
            var pixels = Enumerable.Repeat((byte)255, 80 * 50 * 4).ToArray();
            for (var alphaByte = 3; alphaByte < pixels.Length; alphaByte += 4) pixels[alphaByte] = alpha;
            if (blink)
                for (var y = 10; y < 10 + rows; y++)
                    for (var x = column; x < column + columns; x++)
                        for (var c = 0; c < 3; c++) pixels[(y * 80 + x) * 4 + c] = stroke;
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
