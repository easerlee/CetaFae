using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace CetaFae.Plugins;

/// <summary>A startup snapshot of explicitly enabled, validated data packages.</summary>
public sealed class PluginCatalog
{
    public const int MaxFileBytes = 32 * 1024;
    static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        MaxDepth = 12
    };
    sealed record InstalledCatalog(int SchemaVersion, IReadOnlyList<string> Enabled);

    public IReadOnlyList<GuidePlugin> Plugins { get; }

    public PluginCatalog(IEnumerable<GuidePlugin> plugins)
    {
        var definitions = plugins.Take(33).ToArray();
        if (definitions.Length > 32 || definitions.Any(p => p is null) ||
            definitions.Select(p => p.Id).Distinct(StringComparer.OrdinalIgnoreCase).Count() != definitions.Length ||
            definitions.Count(p => p.Kind == "general") > 1 || definitions.Count(p => p.Kind == "identity") > 1)
            throw new InvalidDataException("plugin_catalog");
        Plugins = Array.AsReadOnly(definitions);
    }

    public GuidePlugin Get(string id) => Plugins.SingleOrDefault(p => p.Id == id) ??
        throw new InvalidDataException("unsupported_target");

    public GuidePlugin? IdentityProvider => Plugins.SingleOrDefault(p => p.Kind == "identity");

    public GuidePlugin Resolve(string processName, string title)
    {
        var matches = Plugins.Where(p => p.Kind == "application" && p.Matches(processName, title)).Take(2).ToArray();
        return matches.Length switch
        {
            1 => matches[0],
            0 => Plugins.SingleOrDefault(p => p.Kind == "general") ?? throw new InvalidDataException("unsupported_target"),
            _ => throw new InvalidDataException("plugin_conflict")
        };
    }

    public static GuidePlugin ParsePlugin(string json)
    {
        if (Encoding.UTF8.GetByteCount(json) > MaxFileBytes) throw new InvalidDataException("plugin_size");
        // Duplicate keys must not silently replace reviewed identity or task rules.
        using var document = JsonDocument.Parse(json, new JsonDocumentOptions { MaxDepth = 12 });
        RejectDuplicates(document.RootElement);
        return JsonSerializer.Deserialize<GuidePlugin>(json, Json) ?? throw new InvalidDataException("plugin_definition");
    }

    public static PluginCatalog Load(string directory)
    {
        CheckRegularPath(directory);
        var json = ReadFile(Path.Combine(directory, "catalog.json"));
        using var document = JsonDocument.Parse(json, new JsonDocumentOptions { MaxDepth = 12 });
        RejectDuplicates(document.RootElement);
        var catalog = JsonSerializer.Deserialize<InstalledCatalog>(json, Json) ?? throw new InvalidDataException("plugin_catalog");
        if (catalog.SchemaVersion != 1 || catalog.Enabled is null || catalog.Enabled.Count > 32 ||
            catalog.Enabled.Any(id => !PluginData.IsToken(id, 64)) ||
            catalog.Enabled.Distinct(StringComparer.OrdinalIgnoreCase).Count() != catalog.Enabled.Count)
            throw new InvalidDataException("plugin_catalog");
        return new PluginCatalog(catalog.Enabled.Select(id =>
        {
            var packageDirectory = Path.Combine(directory, id);
            CheckRegularPath(packageDirectory);
            var plugin = ParsePlugin(ReadFile(Path.Combine(packageDirectory, "plugin.json")));
            if (plugin.Id != id) throw new InvalidDataException("plugin_identity");
            return plugin;
        }));
    }

    static string ReadFile(string path)
    {
        CheckRegularPath(path);
        using var stream = File.OpenRead(path);
        var bytes = new byte[MaxFileBytes + 1];
        var used = 0;
        int count;
        while (used < bytes.Length && (count = stream.Read(bytes, used, bytes.Length - used)) != 0) used += count;
        if (used > MaxFileBytes) throw new InvalidDataException("plugin_size");
        return new UTF8Encoding(false, true).GetString(bytes, 0, used).TrimStart('\uFEFF');
    }

    static void CheckRegularPath(string path)
    {
        if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
            throw new InvalidDataException("plugin_path");
    }

    static void RejectDuplicates(JsonElement element)
    {
        if (element.ValueKind == JsonValueKind.Object)
        {
            var names = new HashSet<string>(StringComparer.Ordinal);
            foreach (var field in element.EnumerateObject())
            {
                if (!names.Add(field.Name)) throw new InvalidDataException("plugin_duplicate_field");
                RejectDuplicates(field.Value);
            }
        }
        else if (element.ValueKind == JsonValueKind.Array)
            foreach (var item in element.EnumerateArray()) RejectDuplicates(item);
    }
}
