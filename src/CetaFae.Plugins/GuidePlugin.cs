using System.Text.Json.Serialization;

namespace CetaFae.Plugins;

public sealed class PluginTarget
{
    public IReadOnlyList<string> ProcessNames { get; }
    public IReadOnlyList<string> WindowTitles { get; }

    [JsonConstructor]
    public PluginTarget(IReadOnlyList<string> processNames, IReadOnlyList<string>? windowTitles = null)
    {
        ProcessNames = PluginData.Texts(processNames, 16, 128);
        if (ProcessNames.Any(name => !PluginData.IsToken(name, 128)))
            throw new InvalidDataException("plugin_target");
        WindowTitles = windowTitles is null ? Array.Empty<string>() : PluginData.Texts(windowTitles, 16, 200, true);
    }

    public bool Matches(string processName, string title) =>
        ProcessNames.Contains(processName, StringComparer.OrdinalIgnoreCase) &&
        (WindowTitles.Count == 0 || WindowTitles.Contains(title, StringComparer.Ordinal));
}

public sealed class PointBlockRule
{
    public IReadOnlyList<string> IntentContainsAny { get; }
    public IReadOnlyList<string> CandidateNameContainsAny { get; }

    [JsonConstructor]
    public PointBlockRule(IReadOnlyList<string> intentContainsAny, IReadOnlyList<string> candidateNameContainsAny)
    {
        IntentContainsAny = PluginData.Texts(intentContainsAny, 16, 200);
        CandidateNameContainsAny = PluginData.Texts(candidateNameContainsAny, 16, 200);
    }

    public bool Blocks(string intent, string candidateName) =>
        IntentContainsAny.Any(text => intent.Contains(text, StringComparison.OrdinalIgnoreCase)) &&
        CandidateNameContainsAny.Any(text => candidateName.Contains(text, StringComparison.OrdinalIgnoreCase));
}

/// <summary>Data-only application rules; no callback can obtain native, UI, file or model authority.</summary>
public sealed class ObservationScopeRule
{
    public IReadOnlyList<string> IntentContainsAny { get; }
    public IReadOnlyList<string> AnchorNameContainsAny { get; }
    public IReadOnlyList<string> AnchorControlTypes { get; }
    public int Padding { get; }

    [JsonConstructor]
    public ObservationScopeRule(IReadOnlyList<string> intentContainsAny, IReadOnlyList<string> anchorNameContainsAny,
        IReadOnlyList<string> anchorControlTypes, int padding)
    {
        IntentContainsAny = PluginData.Texts(intentContainsAny, 16, 200);
        AnchorNameContainsAny = PluginData.Texts(anchorNameContainsAny, 16, 200);
        AnchorControlTypes = PluginData.Texts(anchorControlTypes, 8, 64);
        if (padding is < 0 or > 64 || AnchorControlTypes.Any(t => t is not
            ("ControlType.Text" or "ControlType.Button" or "ControlType.CheckBox" or "ControlType.ComboBox" or
             "ControlType.RadioButton" or "ControlType.Hyperlink" or "ControlType.Edit")))
            throw new InvalidDataException("plugin_scope");
        Padding = padding;
    }

    public bool MatchesIntent(string intent) => IntentContainsAny.Any(s => intent.Contains(s, StringComparison.OrdinalIgnoreCase));
    public bool MatchesAnchor(string name, string type) => AnchorControlTypes.Contains(type, StringComparer.Ordinal) &&
        AnchorNameContainsAny.Any(s => name.Contains(s, StringComparison.OrdinalIgnoreCase));
}

public sealed class InterpretationPolicy
{
    public IReadOnlyList<string> Languages { get; }
    public bool TranslateSelectedText { get; }

    [JsonConstructor]
    public InterpretationPolicy(IReadOnlyList<string> languages, bool translateSelectedText)
    {
        Languages = PluginData.Texts(languages, 2, 8);
        if (Languages.Any(language => language is not ("zh-CN" or "en")))
            throw new InvalidDataException("plugin_interpretation");
        TranslateSelectedText = translateSelectedText;
    }
}

public sealed class GuidePlugin
{
    public int SchemaVersion { get; }
    public string Kind { get; }
    public string Id { get; }
    public string Version { get; }
    public string DisplayName { get; }
    public IReadOnlyList<PluginTarget> Targets { get; }
    public string UiaView { get; }
    public IReadOnlyList<string> TaskIds { get; }
    public IReadOnlyList<PointBlockRule> BlockedPoints { get; }
    public IReadOnlyList<ObservationScopeRule> ObservationScopes { get; }
    public string? IdentityPolicy { get; }
    public InterpretationPolicy? InterpretationPolicy { get; }

    [JsonIgnore]
    public bool UsesRawView => UiaView == "raw";

    [JsonConstructor]
    public GuidePlugin(int schemaVersion, string id, string version, string displayName,
        IReadOnlyList<PluginTarget> targets, string uiaView, IReadOnlyList<string> taskIds,
        IReadOnlyList<PointBlockRule> blockedPoints, IReadOnlyList<ObservationScopeRule>? observationScopes = null,
        string kind = "application", string? identityPolicy = null, InterpretationPolicy? interpretationPolicy = null)
    {
        if (schemaVersion is not (1 or 2 or 3 or 4 or 5) || kind is not ("application" or "general" or "identity") ||
            kind == "general" && schemaVersion is not (3 or 5) ||
            (schemaVersion == 5 ? kind != "general" || interpretationPolicy is null : interpretationPolicy is not null) ||
            (kind == "identity" ? schemaVersion != 4 || identityPolicy != "local-metadata-public-reference" : identityPolicy is not null || schemaVersion == 4) ||
            !PluginData.IsToken(id, 64) ||
            !System.Version.TryParse(version, out _) || !PluginData.IsText(displayName, 200) ||
            targets is null || targets.Count > 16 || targets.Any(t => t is null) ||
            (kind != "application" ? targets.Count != 0 : targets.Count == 0) ||
            uiaView is not ("control" or "raw") ||
            blockedPoints is null || blockedPoints.Count > 32 || blockedPoints.Any(r => r is null))
            throw new InvalidDataException("plugin_definition");
        SchemaVersion = schemaVersion;
        Kind = kind;
        IdentityPolicy = identityPolicy;
        InterpretationPolicy = interpretationPolicy;
        Id = id;
        Version = version;
        DisplayName = displayName;
        Targets = Array.AsReadOnly(targets.ToArray());
        UiaView = uiaView;
        TaskIds = PluginData.Texts(taskIds, 32, 64);
        if (TaskIds.Any(task => !PluginData.IsToken(task, 64))) throw new InvalidDataException("plugin_task");
        BlockedPoints = Array.AsReadOnly(blockedPoints.ToArray());
        if (schemaVersion >= 2 && kind == "application" && observationScopes is not { Count: > 0 and <= 16 } ||
            kind != "application" && (blockedPoints.Count != 0 || observationScopes is { Count: > 0 }) ||
            kind == "identity" && uiaView != "control" ||
            schemaVersion == 1 && observationScopes is { Count: > 0 } ||
            observationScopes?.Any(s => s is null) == true)
            throw new InvalidDataException("plugin_scope");
        ObservationScopes = Array.AsReadOnly(observationScopes?.ToArray() ?? []);
    }

    public bool Matches(string processName, string title) => Kind == "general" || Targets.Any(t => t.Matches(processName, title));

    public bool AllowsPoint(string? intent, string candidateName) =>
        !string.IsNullOrWhiteSpace(intent) && !BlockedPoints.Any(rule => rule.Blocks(intent, candidateName));

    public ObservationScopeRule ScopeFor(string intent)
    {
        var matches = ObservationScopes.Where(s => s.MatchesIntent(intent)).Take(2).ToArray();
        return matches.Length == 1 ? matches[0] : throw new InvalidDataException("scope_unavailable");
    }
}

static class PluginData
{
    internal static bool IsText(string? value, int limit) => !string.IsNullOrWhiteSpace(value) &&
        value.Length <= limit && !value.Any(char.IsControl);

    internal static bool IsToken(string? value, int limit) => IsText(value, limit) &&
        value is not ("." or "..") && value!.All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_' or '.');

    internal static IReadOnlyList<string> Texts(IReadOnlyList<string>? source, int count, int length, bool allowEmpty = false)
    {
        if (source is null || source.Count > count || (!allowEmpty && source.Count == 0) ||
            source.Any(s => !IsText(s, length)) || source.Distinct(StringComparer.Ordinal).Count() != source.Count)
            throw new InvalidDataException("plugin_values");
        return Array.AsReadOnly(source.ToArray());
    }
}
