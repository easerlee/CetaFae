using System.IO;
using System.Text.Json;
using CetaFae.Plugins;

namespace CetaFae.Probe;

/// <summary>Visual interpretation is separate from locally observed process ownership.</summary>
public sealed record GuideIdentification(string? Application, string? Page, string? Question, string Kind = "uncertain", string? State = null,
    string? NextCheck = null)
{
    internal const string Processing = "正在看这个窗口，请稍等…";
    public static GuideIdentification Parse(string json, InterpretationPolicy? policy = null)
    {
        using var document = JsonDocument.Parse(json, new JsonDocumentOptions { MaxDepth = 4 });
        var root = document.RootElement;
        if (root.ValueKind != JsonValueKind.Object) throw new InvalidDataException("identification_shape");
        var names = root.EnumerateObject().Select(p => p.Name).Order().ToArray();
        var includesCheck = policy?.ExplainVisibleErrors == true &&
            names.SequenceEqual(new[] { "application", "kind", "nextCheck", "page", "question", "state" });
        if (!includesCheck && !names.SequenceEqual(new[] { "application", "kind", "page", "question", "state" }))
            throw new InvalidDataException("identification_fields");
        var kind = Read(root.GetProperty("kind"), 20, false)!;
        var page = Read(root.GetProperty("page"), 300, true);
        var state = Read(root.GetProperty("state"), 300, true);
        if (kind is not ("regular" or "attention" or "uncertain") || page is null && kind != "uncertain" ||
            kind == "attention" && state is null)
            throw new InvalidDataException("identification_kind");
        var question = Read(root.GetProperty("question"), 120, true);
        var nextCheck = includesCheck ? Read(root.GetProperty("nextCheck"), 120, true) : null;
        if (nextCheck is not null && (kind != "attention" || question is not null))
            throw new InvalidDataException("identification_next_check");
        return new GuideIdentification(Read(root.GetProperty("application"), 100, true), page, question, kind, state, nextCheck);
    }

    static string? Read(JsonElement value, int maximum, bool nullable)
    {
        if (nullable && value.ValueKind == JsonValueKind.Null) return null;
        if (value.ValueKind != JsonValueKind.String) throw new InvalidDataException("identification_value");
        var text = value.GetString()!;
        if (string.IsNullOrWhiteSpace(text) || text.Length > maximum || text.Any(char.IsControl))
            throw new InvalidDataException("identification_value");
        GuidePrivacy.EnsureTextSafe(text);
        return text.Trim();
    }

    static void ValidateProcessName(string processName)
    {
        if (string.IsNullOrWhiteSpace(processName) || processName.Length > 128 || processName.Any(char.IsControl))
            throw new InvalidDataException("target_unavailable");
        GuidePrivacy.EnsureTextSafe(processName);
    }

    internal static string BindProcess(int selectedProcessId, uint observedProcessId, string processName)
    {
        if (selectedProcessId <= 0 || observedProcessId != (uint)selectedProcessId)
            throw new InvalidDataException("target_changed");
        ValidateProcessName(processName);
        return processName;
    }

    public GuideAnswer ToAnswer(string processName, SoftwareIdentity? identity = null, GuidePlugin? feature = null, CancellationToken token = default)
    {
        ValidateProcessName(processName);
        return GuideFeature.Present("identify-result", new { identification = new
        { application = Application, page = Page, question = Question, kind = Kind, state = State, nextCheck = NextCheck },
            product = SoftwareIdentity.Clean(identity?.Product) }, feature, token);
    }

    internal bool CanStartErrorFollowup(InterpretationPolicy? policy) =>
        policy is { ExplainVisibleErrors: true, UserLedFollowup: true } &&
        Kind == "attention" && NextCheck is not null && Question is null;


    static string LocalName(SoftwareIdentity? identity) => SoftwareIdentity.Clean(identity?.Product) is { } name
        ? $"这个软件显示的名称是“{name}”。\n" : "";

    internal static string Unavailable(string processName, string reason, SoftwareIdentity? identity = null)
    {
        ValidateProcessName(processName);
        return LocalName(identity) + reason;
    }
}
