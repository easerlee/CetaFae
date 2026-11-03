using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using CetaFae.Probe;

namespace CetaFae.Checks;

static class TextRegionChecks
{
    internal static void Run(Action<bool, string> check)
    {
        var region = new Rect(-1200, 300, 800, 400);
        GuideTextRegions.Line[] lines = [new("Generated target", new Rect(20, 40, 180, 30))];
        var candidate = GuideTextRegions.Candidates(lines, region, 400, 200).Single();
        check(candidate.Bounds == new Rect(-1160, 380, 360, 60) && candidate.ControlType == "Visual.Text" &&
            candidate.RuntimeId.SequenceEqual(new[] { -1, 1 }), "OCR text evidence maps cropped pixels to physical target coordinates once");
        var rotated = GuideTextRegions.ImageBounds(new Rect(40, 20, 20, 10), 100, 100, 90);
        check(Math.Abs(rotated.X - 70) < .001 && Math.Abs(rotated.Y - 40) < .001 &&
            Math.Abs(rotated.Width - 10) < .001 && Math.Abs(rotated.Height - 20) < .001,
            "OCR deskewed bounds rotate clockwise around image center before physical projection");
        check(GuideTextRegions.ImageBounds(new Rect(1, 1, 10, 10), 100, 100, double.NaN).IsEmpty,
            "invalid OCR angle cannot produce coordinates");
        var control = candidate with { Id = 7, Name = "Close", ControlType = "ControlType.Button", RuntimeId = [7] };
        var merged = GuideTextRegions.Merge([control], [candidate]);
        check(merged.Count == 2 && merged[1].Id == 8 && merged[1].RuntimeId.SequenceEqual(new[] { -1, 8 }),
            "title-bar controls do not disable independent text-region evidence or collide with its IDs");
        check(GuideTextRegions.Merge([control with { Name = candidate.Name }], [candidate]).Count == 1,
            "an overlapping control with the same name takes precedence over an OCR hint");
        var observed = new GuideObservation(new IntPtr(10), 20, region, [candidate], null);
        var changed = false;
        try { observed.ValidateContext([candidate with { Name = "Changed text" }], candidate.Id); }
        catch (InvalidDataException) { changed = true; }
        check(changed, "changed OCR text invalidates the selected region before presentation");
        changed = false;
        try { observed.ValidateContext([candidate with { Bounds = new Rect(-1100, 380, 360, 60) }], candidate.Id); }
        catch (InvalidDataException) { changed = true; }
        check(changed, "moved OCR text invalidates an otherwise matching candidate ID");
        check(GuideTextRegions.Candidates([.. lines, lines[0] with { Bounds = new Rect(20, 90, 180, 30) }], region, 400, 200).Count == 0,
            "repeated visible text supplies no ambiguous region candidate");
        check(GuideTextRegions.Candidates([lines[0] with { Bounds = new Rect(390, 10, 30, 20) }], region, 400, 200).Count == 0,
            "text evidence outside the actual image cannot become a marker");
        check(GuideTextRegions.Candidates([new("", Rect.Empty)], region, 400, 200).Count == 0,
            "missing text and missing geometry remain explanation-only");
        var rejected = false;
        try { GuideTextRegions.Candidates([new("api_key=sk-abcdefghijklmnopqrstuvwxyz1234567890", new Rect(1, 1, 300, 20))], region, 400, 200); }
        catch (InvalidDataException) { rejected = true; }
        check(rejected, "positive OCR credential evidence stops the request rather than merely hiding its candidate");
        var answer = GuideAnswer.Parse("""{"mode":"point","explanation":"文字在这里。","candidateId":1}""", [candidate]);
        check(answer.CandidateId == 1, "text-region result uses the same bounded candidate-ID contract as controls");
        var package = CetaFae.Plugins.PluginCatalog.Load(Path.Combine(AppContext.BaseDirectory, "plugins")).GeneralProvider!;
        check(package.SchemaVersion == 6 && package.InterpretationPolicy?.LocateTextRegions == true,
            "only a declared version-six general capability enables OCR text regions");
        var rejectedVersion = false;
        try { CetaFae.Plugins.PluginCatalog.ParsePlugin(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "plugins", package.Id, "plugin.json"))
            .Replace("\"schemaVersion\": 6", "\"schemaVersion\": 5")); }
        catch (InvalidDataException) { rejectedVersion = true; }
        check(rejectedVersion, "old plugin contracts cannot acquire new OCR authority by adding a flag");
    }

    internal static async Task RunOcrAsync(Action<bool, string> check)
    {
        var drawing = new DrawingVisual();
        var expected = new Rect(40, 40, 600, 75);
        using (var dc = drawing.RenderOpen())
        {
            dc.DrawRectangle(Brushes.White, null, new Rect(0, 0, 760, 220));
            var text = new FormattedText("GENERATED TARGET", CultureInfo.InvariantCulture, FlowDirection.LeftToRight,
                new Typeface("Arial"), 40, Brushes.Black, 1);
            dc.DrawText(text, new Point(expected.X, expected.Y));
        }
        var bitmap = new RenderTargetBitmap(760, 220, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(drawing);
        var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var bytes = new MemoryStream(); encoder.Save(bytes);
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(8));
        var candidates = await GuideTextRegions.ReadAsync(bytes.ToArray(), new Rect(0, 0, 760, 220), deadline.Token);
        check(candidates.Count == 1 && candidates[0].Name == "GENERATED TARGET" && expected.Contains(candidates[0].Bounds),
            "actual Windows OCR matches independently rendered generated text and its known rectangle without any UIA control");
        var digits = await GuideTextRegions.ReadAsync(GuideConnection.CreateImage("714382"), new Rect(0, 0, 360, 100), deadline.Token);
        // Deskew interpolation may expand the raster edge by less than one physical pixel.
        check(digits.Count == 1 && digits[0].Name == "714382" && new Rect(29, 19, 242, 67).Contains(digits[0].Bounds),
            "actual OCR restores its nonzero deskew estimate into the known unrotated digit rectangle");
    }

    internal static async Task RunLiveAsync(Action<bool, string> check)
    {
        var options = GuidePreferences.Default.Options;
        var status = await GuideConnection.TestImageAsync(options, CancellationToken.None);
        Console.WriteLine($"GENERATED IMAGE CAPABILITY: {status}");
        check(status == ConnectionStatus.ImageVerified, "selected real service reads the generated challenge, independently of its model list");
        var png = GuideConnection.CreateImage("714382");
        var candidates = await GuideTextRegions.ReadAsync(png, new Rect(0, 0, 360, 100), CancellationToken.None);
        check(candidates.Count == 1 && candidates[0].Name == "714382", "real-model region input comes from actual local OCR, not invented test candidate coordinates");
        using var model = new GuideModel(options);
        var answer = await model.AskAsync("请指出图片中714382这段文字的位置，只标出文字，不要描述为按钮，不执行操作。", candidates, png, CancellationToken.None);
        check(answer.Mode == "point" && answer.CandidateId == candidates[0].Id &&
            new Rect(29, 19, 242, 67).Contains(candidates[0].Bounds),
            "real model selects the independently rendered text region using only a supplied ID");
        var absent = await model.AskAsync("请指出图片中的齿轮按钮。只有数字文字不能作为齿轮按钮的位置；没有这个按钮就说明找不到，不执行操作。",
            candidates, png, CancellationToken.None);
        check(absent.Mode == "explain" && absent.CandidateId is null,
            "real model does not turn a text region into an absent icon button");
    }
}
