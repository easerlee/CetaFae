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
public sealed class GuidePlugin
{
    public int SchemaVersion { get; }
    public string Id { get; }
    public string Version { get; }
    public string DisplayName { get; }
    public IReadOnlyList<PluginTarget> Targets { get; }
    public string UiaView { get; }
    public IReadOnlyList<string> TaskIds { get; }
    public IReadOnlyList<PointBlockRule> BlockedPoints { get; }

    [JsonIgnore]
    public bool UsesRawView => UiaView == "raw";

    [JsonConstructor]
    public GuidePlugin(int schemaVersion, string id, string version, string displayName,
        IReadOnlyList<PluginTarget> targets, string uiaView, IReadOnlyList<string> taskIds,
        IReadOnlyList<PointBlockRule> blockedPoints)
    {
        if (schemaVersion != 1 || !PluginData.IsToken(id, 64) ||
            !System.Version.TryParse(version, out _) || !PluginData.IsText(displayName, 200) ||
            targets is not { Count: > 0 and <= 16 } || targets.Any(t => t is null) ||
            uiaView is not ("control" or "raw") ||
            blockedPoints is null || blockedPoints.Count > 32 || blockedPoints.Any(r => r is null))
            throw new InvalidDataException("plugin_definition");
        SchemaVersion = schemaVersion;
        Id = id;
        Version = version;
        DisplayName = displayName;
        Targets = Array.AsReadOnly(targets.ToArray());
        UiaView = uiaView;
        TaskIds = PluginData.Texts(taskIds, 32, 64);
        if (TaskIds.Any(task => !PluginData.IsToken(task, 64))) throw new InvalidDataException("plugin_task");
        BlockedPoints = Array.AsReadOnly(blockedPoints.ToArray());
    }

    public bool Matches(string processName, string title) => Targets.Any(t => t.Matches(processName, title));

    public bool AllowsPoint(string? intent, string candidateName) =>
        !string.IsNullOrWhiteSpace(intent) && !BlockedPoints.Any(rule => rule.Blocks(intent, candidateName));
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
