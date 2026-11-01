using System.IO;
using System.Text.Json;
using System.Text.RegularExpressions;
using CetaFae.Plugins;

namespace CetaFae.Probe;

/// <summary>Preserves the recognized source and rejects altered literal facts; does not certify OCR.</summary>
internal static class GuideTranslation
{
    static readonly Regex Literals = new(
        @"0[xX][0-9a-fA-F]+|[A-Z]{1,16}[_-][A-Z0-9_-]{1,48}|(?<![\p{L}\d])[+-]?\d+(?:[./:,\-]\d+)*(?:\s?(?:GHz|MHz|KB|MB|GB|TB|ms|px|秒|分钟|毫秒|字节|°C|℃|%))?",
        RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(100));

    internal static GuideAnswer Parse(JsonElement root, InterpretationPolicy? policy)
    {
        var fields = root.EnumerateObject().Select(p => p.Name).Order().ToArray();
        if (policy?.TranslateSelectedText != true || !fields.SequenceEqual(new[]
            { "candidateId", "language", "mode", "sourceText", "translation" }) ||
            root.GetProperty("candidateId").ValueKind != JsonValueKind.Null)
            throw new InvalidDataException("translation_shape");
        var source = Text(root.GetProperty("sourceText"), 280);
        var translation = Text(root.GetProperty("translation"), 600);
        var language = Text(root.GetProperty("language"), 8);
        if (!policy.Languages.Contains(language, StringComparer.Ordinal))
            throw new InvalidDataException("translation_language");
        var preserved = Tokens(translation);
        if (!Tokens(source).SetEquals(preserved))
            throw new InvalidDataException("translation_literals");
        return new GuideAnswer("explain", "原文（画面识读）：" + source + "\n" +
            (language == "en" ? "英文翻译：" : "中文翻译：") + translation, null);
    }

    static HashSet<string> Tokens(string text) => Literals.Matches(text).Select(m => m.Value)
        .ToHashSet(StringComparer.Ordinal);

    static string Text(JsonElement value, int limit)
    {
        if (value.ValueKind != JsonValueKind.String || string.IsNullOrWhiteSpace(value.GetString()))
            throw new InvalidDataException("translation_value");
        var text = value.GetString()!;
        if (text.Length > limit || text.Any(char.IsControl)) throw new InvalidDataException("translation_value");
        GuidePrivacy.EnsureTextSafe(text);
        return text.Trim();
    }
}
