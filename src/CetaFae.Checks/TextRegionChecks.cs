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
        var workerRequest = new ObservationWorker.Request(42, true, package, "generated text location", new Rect(1, 1, 20, 20));
        var workerCopy = System.Text.Json.JsonSerializer.Deserialize<ObservationWorker.Request>(
            ObservationWorker.SerializeRequest(workerRequest), ObservationWorker.Json)!;
        check(workerCopy.Plugin.InterpretationPolicy?.LocateTextRegions == true && workerCopy.Selection == workerRequest.Selection,
            "the production worker request preserves the declared OCR capability and selected crop");
        var rejectedVersion = false;
        try { CetaFae.Plugins.PluginCatalog.ParsePlugin(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "plugins", package.Id, "plugin.json"))
            .Replace("\"schemaVersion\": 6", "\"schemaVersion\": 5")); }
        catch (InvalidDataException) { rejectedVersion = true; }
        check(rejectedVersion, "old plugin contracts cannot acquire new OCR authority by adding a flag");
    }

    internal static async Task RunOcrAsync(Action<bool, string> check)
    {
        Console.WriteLine($"OCR LANGUAGE: {Windows.Media.Ocr.OcrEngine.TryCreateFromUserProfileLanguages()?.RecognizerLanguage.LanguageTag ?? "unavailable"}; AVAILABLE: {string.Join(',', Windows.Media.Ocr.OcrEngine.AvailableRecognizerLanguages.Select(language => language.LanguageTag))}");
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
        var chinese = new DrawingVisual();
        const string chineseLine = "生成测试：待办事项是整理材料、核对结果。";
        using (var dc = chinese.RenderOpen())
        {
            dc.DrawRectangle(Brushes.White, null, new Rect(0, 0, 900, 100));
            dc.DrawRectangle(Brushes.LightYellow, null, new Rect(15, 15, 850, 55));
            dc.DrawText(new FormattedText(chineseLine, CultureInfo.GetCultureInfo("zh-CN"), FlowDirection.LeftToRight,
                new Typeface("Microsoft YaHei"), 24, Brushes.Black, 1), new Point(20, 20));
        }
        var chineseBitmap = new RenderTargetBitmap(900, 100, 96, 96, PixelFormats.Pbgra32);
        chineseBitmap.Render(chinese);
        var chineseEncoder = new PngBitmapEncoder(); chineseEncoder.Frames.Add(BitmapFrame.Create(chineseBitmap));
        using var chineseBytes = new MemoryStream(); chineseEncoder.Save(chineseBytes);
        var chineseCandidates = await GuideTextRegions.ReadAsync(chineseBytes.ToArray(), new Rect(0, 0, 900, 100), deadline.Token);
        Console.WriteLine($"GENERATED CHINESE OCR CANDIDATES: {chineseCandidates.Count}");
        check(chineseCandidates.Any(line => line.Name.Replace(" ", "").Contains("整理材料、核对结果") &&
                new Rect(15, 15, 850, 55).Contains(line.Bounds)),
            "actual Chinese OCR retains the generated highlighted task line with independently known bounds");
        foreach (var fontSize in new[] { 12d, 16d })
        {
            var (smallPng, width, height, ink) = SmallChineseImage(fontSize);
            var inkMargin = ink; inkMargin.Inflate(2, 2);
            var localRegion = new Rect(-700, 200, width, height);
            var local = await Task.Run(() => GuideTextRegions.Read(smallPng, localRegion, CancellationToken.None));
            var observation = new GuideObservation(new IntPtr(42), 5, new Rect(-800, 100, 1000, 400), local,
                smallPng, PluginId: "generated.plugin", Scope: new GuideScope(localRegion, null, true), ScopeIntent: "generated text location");
            var payload = GuidePayload.From(observation);
            Console.WriteLine($"GENERATED SMALL CHINESE OCR: font={fontSize}, local={local.Count}, payload={payload.Candidates.Count}, image={width}x{height}");
            check(payload.Candidates.Any(line => line.Name.Replace(" ", "").Contains("核对结果") &&
                    new Rect(0, 0, width, height).Contains(line.Bounds) &&
                    inkMargin.Contains(line.Bounds) &&
                    Rect.Inflate(line.Bounds, 2, 2).Contains(ink)) && payload.Png!.SequenceEqual(smallPng),
                $"MTA worker-shaped Chinese {fontSize}px crop retains actual text regions through outbound payload reduction");
        }
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
        var (smallPng, width, height, ink) = SmallChineseImage(16);
        var localRegion = new Rect(-700, 200, width, height);
        var local = await Task.Run(() => GuideTextRegions.Read(smallPng, localRegion, CancellationToken.None));
        var observation = new GuideObservation(new IntPtr(42), 5, new Rect(-800, 100, 1000, 400), local,
            smallPng, PluginId: "generated.plugin", Scope: new GuideScope(localRegion, null, true), ScopeIntent: "generated text location");
        var payload = GuidePayload.From(observation);
        var chinese = await model.AskAsync("标出包含‘整理材料、核对结果’的整行文字的位置，只指出文字，不执行操作。",
            payload.Candidates, payload.Png, CancellationToken.None);
        var marked = payload.Candidates.SingleOrDefault(line => line.Id == chinese.CandidateId);
        check(chinese.Mode == "point" && marked is not null && Rect.Inflate(marked.Bounds, 2, 2).Contains(ink),
            "real model selects actual small Chinese text through the production scoped-payload path");
        var missing = await model.AskAsync("标出图片中714382这段文字的位置，不执行操作。", [], png, CancellationToken.None);
        Console.WriteLine("GENERATED MISSING-LOCATION REPLY: " + missing.Explanation);
        check(missing.Mode == "explain" && missing.CandidateId is null &&
            !new[] { "候选", "OCR", "控件树", "坐标", "元数据" }.Any(word => missing.Explanation.Contains(word, StringComparison.OrdinalIgnoreCase)),
            "real model explains unavailable location in everyday language without internal candidate details");
    }

    static (byte[] Png, int Width, int Height, Rect Ink) SmallChineseImage(double fontSize)
    {
        var drawing = new DrawingVisual();
        var text = new FormattedText("生成测试：待办事项是整理材料、核对结果。", CultureInfo.GetCultureInfo("zh-CN"),
            FlowDirection.LeftToRight, new Typeface("Microsoft YaHei"), fontSize, Brushes.Black, 1);
        var width = (int)Math.Ceiling(text.Width) + 8;
        var height = (int)Math.Ceiling(text.Height) + 8;
        using (var dc = drawing.RenderOpen())
        {
            dc.DrawRectangle(Brushes.LightYellow, null, new Rect(0, 0, width, height));
            dc.DrawText(text, new Point(4, 4));
        }
        var raster = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32); raster.Render(drawing);
        var pixels = new byte[width * height * 4]; raster.CopyPixels(pixels, width * 4, 0);
        var ink = Rect.Empty;
        for (var y = 0; y < height; y++)
            for (var x = 0; x < width; x++)
            {
                var offset = (y * width + x) * 4;
                if (pixels[offset] < 160 && pixels[offset + 1] < 160 && pixels[offset + 2] < 160)
                    ink.Union(new Rect(x, y, 1, 1));
            }
        var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(raster));
        using var bytes = new MemoryStream(); encoder.Save(bytes);
        return (bytes.ToArray(), width, height, ink);
    }
}
