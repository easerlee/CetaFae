using System.IO;
using System.Text.Json;
using CetaFae.Plugins;

namespace CetaFae.Probe;

/// <summary>Labels uncertainty in visible-error assistance without granting diagnostic or repair authority.</summary>
internal static class GuideError
{
    internal const string ContextQuestion = "出现这个提示前，你刚才在做什么？";
    internal static GuideAnswer Parse(JsonElement root, InterpretationPolicy? policy, GuidePlugin? feature = null, CancellationToken token = default)
    {
        if (policy?.ExplainVisibleErrors != true || !root.EnumerateObject().Select(p => p.Name).Order().SequenceEqual(new[]
            { "candidateId", "hypotheses", "missingEvidence", "mode", "nextCheck", "observedText" }) ||
            root.GetProperty("candidateId").ValueKind != JsonValueKind.Null)
            throw new InvalidDataException("error_shape");
        var observed = Text(root.GetProperty("observedText"), 180);
        var next = Text(root.GetProperty("nextCheck"), 120);
        _ = Text(root.GetProperty("missingEvidence"), 120);
        var values = root.GetProperty("hypotheses");
        if (values.ValueKind != JsonValueKind.Array || values.GetArrayLength() > 2)
            throw new InvalidDataException("error_hypotheses");
        foreach (var value in values.EnumerateArray()) _ = Text(value, 80);
        return GuideFeature.Present("error-result", new { problem = observed, next }, feature, token);
    }

    static string Text(JsonElement value, int limit)
    {
        if (value.ValueKind != JsonValueKind.String || string.IsNullOrWhiteSpace(value.GetString()))
            throw new InvalidDataException("error_value");
        var text = value.GetString()!;
        if (text.Length > limit || text.Any(char.IsControl)) throw new InvalidDataException("error_value");
        GuidePrivacy.EnsureTextSafe(text);
        return text.Trim();
    }
}
