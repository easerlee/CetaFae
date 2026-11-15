using System.IO;
using System.Security.Cryptography;
using System.Text.Json;
using CetaFae.Plugins;

namespace CetaFae.Probe;

internal sealed record ConversionPlan(string Format, string Reason, FixedTool? Tool);

internal static class TaskConversion
{
    const int MaxSourceBytes = 32 * 1024 * 1024;

    internal static async Task<ConversionPlan?> PlanAsync(PluginCatalog catalog, TaskProposal proposal, string sourceFormat,
        CancellationToken token)
    {
        if (sourceFormat.Length is < 1 or > 12 || sourceFormat.Any(c => !char.IsAsciiLetterOrDigit(c))) return null;
        ConversionPlan? selected = null;
        var input = new { conversion = new { sourceFormat, purpose = proposal.Purpose, format = proposal.Format } };
        foreach (var plugin in catalog.Plugins.Where(p => p.Kind == "conversion"))
        {
            using var result = JsonDocument.Parse(await GuideFeature.InvokeAsync(plugin, "conversion-plan", input, token));
            var root = result.RootElement;
            if (root.EnumerateObject().Count() != 1 || !root.TryGetProperty("conversion", out var answer))
                throw new InvalidDataException("conversion_plan");
            if (answer.ValueKind == JsonValueKind.Null) continue;
            if (answer.ValueKind != JsonValueKind.Object ||
                !answer.EnumerateObject().Select(p => p.Name).Order(StringComparer.Ordinal).SequenceEqual(new[] { "format", "reason", "recipe" }) ||
                answer.GetProperty("format").ValueKind != JsonValueKind.String ||
                answer.GetProperty("reason").ValueKind != JsonValueKind.String ||
                answer.GetProperty("recipe").ValueKind is not (JsonValueKind.String or JsonValueKind.Null))
                throw new InvalidDataException("conversion_plan");
            var format = answer.GetProperty("format").GetString()!;
            var reason = answer.GetProperty("reason").GetString()!;
            var recipe = answer.GetProperty("recipe").GetString();
            var tool = FixedTool.Resolve(recipe);
            if (reason.Length is < 1 or > 220 || reason.Any(char.IsControl) ||
                (proposal.Format is not null && Normalize(proposal.Format) != format) ||
                (recipe is null ? format != Normalize(sourceFormat) :
                    tool is null || !tool.Sources.Contains(sourceFormat) || !tool.Formats.Contains(format) || format == Normalize(sourceFormat)))
                throw new InvalidDataException("conversion_plan");
            GuidePrivacy.EnsureTextSafe(reason);
            if (selected is not null) throw new InvalidDataException("conversion_conflict");
            selected = new(format, reason, tool);
        }
        return selected;
    }

    static string Normalize(string format) => format == "jpeg" ? "jpg" : format;

    internal static FileStream OpenSource(string path, string format)
    {
        PlatformScope.CheckAncestors(path);
        var file = File.OpenRead(path);
        try
        {
            if (file.Length > MaxSourceBytes) throw new InvalidDataException("conversion_file_limit");
            if (!HasFormat(file, Normalize(format))) throw new InvalidDataException("conversion_invalid_source");
            file.Position = 0;
            return file;
        }
        catch { file.Dispose(); throw; }
    }

    internal static string Hash(Stream stream)
    {
        var hash = Convert.ToHexString(SHA256.HashData(stream));
        stream.Position = 0;
        return hash;
    }

    internal static string Destination(string source, string format)
    {
        var directory = Path.GetDirectoryName(source)!;
        var stem = Path.GetFileNameWithoutExtension(source);
        for (var i = 1; i <= 100; i++)
        {
            var name = stem + (i == 1 ? "" : $" ({i})") + "." + format;
            var path = Path.Combine(directory, name);
            if (File.Exists(path) || Directory.Exists(path)) continue;
            return new PlatformScope { WriteRoots = [path] }.FilePath(path, write: true);
        }
        throw new InvalidDataException("conversion_destination");
    }

    internal static async Task StageAsync(FixedTool tool, string scratch, string sourceFormat, string format, Stream source,
        CancellationToken token)
    {
        PlatformScope.CheckAncestors(scratch);
        Directory.CreateDirectory(Path.Combine(scratch, "output"));
        await using (var staged = new FileStream(Path.Combine(scratch, "input." + sourceFormat), FileMode.CreateNew, FileAccess.Write, FileShare.None))
            await source.CopyToAsync(staged, token);
        await tool.ConvertAsync(scratch, sourceFormat, format, token);
    }

    internal static string StagedOutput(string scratch, string format) => Path.Combine(scratch, "output", "input." + format);

    internal static string VerifyOutput(string output, string format)
    {
        PlatformScope.CheckAncestors(output);
        using var stream = File.OpenRead(output);
        if (stream.Length > PlatformFiles.MaxFileBytes) throw new InvalidDataException("conversion_output_limit");
        if (!HasFormat(stream, format)) throw new InvalidDataException("conversion_invalid_output");
        if (format is "jpg" or "png")
        {
            // The decoder reads the result, not the converter's status message.
            stream.Position = 0;
            try
            {
                var bitmap = System.Windows.Media.Imaging.BitmapDecoder.Create(stream,
                    System.Windows.Media.Imaging.BitmapCreateOptions.DelayCreation, System.Windows.Media.Imaging.BitmapCacheOption.None);
                if (bitmap.Frames.Count != 1 || bitmap.Frames[0].PixelWidth <= 0 || bitmap.Frames[0].PixelHeight <= 0)
                    throw new InvalidDataException("conversion_invalid_output");
            }
            catch (Exception ex) when (ex is FileFormatException or NotSupportedException or System.Runtime.InteropServices.COMException)
            { throw new InvalidDataException("conversion_invalid_output"); }
        }
        stream.Position = 0;
        return Hash(stream);
    }

    static bool HasFormat(Stream stream, string format)
    {
        Span<byte> header = stackalloc byte[16];
        if (stream.Length < header.Length) return false;
        stream.ReadExactly(header);
        return format switch
        {
            "jpg" => header[0] == 0xff && header[1] == 0xd8 && header[2] == 0xff,
            "png" => header[0] == 0x89 && header[1..8].SequenceEqual("PNG\r\n\u001a\n"u8),
            "bmp" => header[..2].SequenceEqual("BM"u8),
            "webp" => header[..4].SequenceEqual("RIFF"u8) && header[8..12].SequenceEqual("WEBP"u8) &&
                System.Buffers.Binary.BinaryPrimitives.ReadUInt32LittleEndian(header[4..8]) + 8L == stream.Length &&
                (header[12..].SequenceEqual("VP8 "u8) || header[12..].SequenceEqual("VP8L"u8) || header[12..].SequenceEqual("VP8X"u8)),
            "heic" or "heif" => header[4..8].SequenceEqual("ftyp"u8) &&
                (header[8..12].SequenceEqual("heic"u8) || header[8..12].SequenceEqual("heix"u8) ||
                 header[8..12].SequenceEqual("mif1"u8) || header[8..12].SequenceEqual("msf1"u8)),
            "pdf" => header[..5].SequenceEqual("%PDF-"u8) && HasPdfTrailer(stream),
            _ => false
        };
    }

    static bool HasPdfTrailer(Stream stream)
    {
        Span<byte> end = stackalloc byte[(int)Math.Min(stream.Length, 128)];
        stream.Seek(-end.Length, SeekOrigin.End);
        stream.ReadExactly(end);
        return end.IndexOf("%%EOF"u8) >= 0;
    }
}
