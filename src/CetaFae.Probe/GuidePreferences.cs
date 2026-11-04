using System.IO;
using System.Text.Json;
using CetaFae.Plugins;

namespace CetaFae.Probe;

/// <summary>Non-secret user choices; this record can never grant collection permission.</summary>
internal sealed record GuidePreferences(string BaseUrl, string Model, bool IncludeImage, IReadOnlyList<string> DisabledPlugins,
    IReadOnlyList<GuidePlugin>? InstalledPlugins = null)
{
    internal const int MaxBytes = 1024 * 1024;
    internal IReadOnlyList<GuidePlugin> CustomPlugins => InstalledPlugins ?? [];
    internal static GuidePreferences Default => new("http://127.0.0.1:8787/v1", "deepseek-v4.1-flash", true, []);
    internal GuideOptions Options => GuideOptions.Create(BaseUrl, Model, IncludeImage);

    internal GuidePreferences Validate()
    {
        if (BaseUrl.Length > 2048 || BaseUrl.Any(char.IsControl) || DisabledPlugins.Count > 32 ||
            DisabledPlugins.Any(id => string.IsNullOrWhiteSpace(id) || id.Length > 64 ||
                id.Any(c => !char.IsAsciiLetterOrDigit(c) && c is not ('.' or '-' or '_'))) ||
            DisabledPlugins.Distinct(StringComparer.OrdinalIgnoreCase).Count() != DisabledPlugins.Count)
            throw new ArgumentException("invalid_preferences");
        var options = Options;
        GuidePrivacy.EnsureTextSafe(options.Model);
        if (CustomPlugins.Count > 32 || CustomPlugins.Any(p => p is null) ||
            CustomPlugins.Select(p => p.Id).Distinct(StringComparer.OrdinalIgnoreCase).Count() != CustomPlugins.Count)
            throw new InvalidDataException("plugin_catalog");
        var manifests = CustomPlugins.Select(p => PluginCatalog.ParsePlugin(PluginCatalog.SerializePlugin(p))).ToArray();
        return this with { BaseUrl = options.BaseUrl.AbsoluteUri, Model = options.Model.Trim(),
            DisabledPlugins = Array.AsReadOnly(DisabledPlugins.Order(StringComparer.Ordinal).ToArray()),
            InstalledPlugins = Array.AsReadOnly(manifests) };
    }

    internal IReadOnlyList<GuidePlugin> AvailablePlugins(PluginCatalog shipped)
    {
        var all = shipped.Plugins.Concat(CustomPlugins).ToArray();
        if (all.Length > 32 || all.Select(p => p.Id).Distinct(StringComparer.OrdinalIgnoreCase).Count() != all.Length)
            throw new InvalidDataException("plugin_catalog");
        return Array.AsReadOnly(all);
    }

    internal PluginCatalog Filter(PluginCatalog shipped)
    {
        var catalog = new PluginCatalog(AvailablePlugins(shipped).Where(p =>
            !DisabledPlugins.Contains(p.Id, StringComparer.OrdinalIgnoreCase)));
        catalog.ValidateActivation();
        return catalog;
    }

    internal GuidePreferences ImportPlugin(GuidePlugin plugin, PluginCatalog shipped)
    {
        if (shipped.Plugins.Any(p => string.Equals(p.Id, plugin.Id, StringComparison.OrdinalIgnoreCase)))
            throw new InvalidDataException("plugin_shipped_identity");
        var previous = CustomPlugins.SingleOrDefault(p => string.Equals(p.Id, plugin.Id, StringComparison.OrdinalIgnoreCase));
        if (previous is not null && Version.Parse(plugin.Version) <= Version.Parse(previous.Version))
            throw new InvalidDataException("plugin_update_version");
        var next = (this with
        {
            InstalledPlugins = CustomPlugins.Where(p => p != previous).Append(plugin).ToArray(),
            DisabledPlugins = DisabledPlugins.Where(id => !string.Equals(id, plugin.Id, StringComparison.OrdinalIgnoreCase))
                .Append(plugin.Id).ToArray()
        }).Validate();
        _ = next.AvailablePlugins(shipped);
        return next;
    }

    internal GuidePreferences RemovePlugin(string id) => this with
    {
        InstalledPlugins = CustomPlugins.Where(p => p.Id != id).ToArray(),
        DisabledPlugins = DisabledPlugins.Where(disabled => !string.Equals(disabled, id, StringComparison.OrdinalIgnoreCase)).ToArray()
    };

    internal static GuidePreferences Parse(byte[] bytes)
    {
        if (bytes.Length > MaxBytes) throw new InvalidDataException("preferences_size");
        using var json = JsonDocument.Parse(bytes, new JsonDocumentOptions { MaxDepth = 14 });
        var root = json.RootElement;
        if (root.ValueKind != JsonValueKind.Object) throw new InvalidDataException("preferences_shape");
        if (!root.TryGetProperty("version", out var versionField) || versionField.ValueKind != JsonValueKind.Number ||
            !versionField.TryGetInt32(out var version)) throw new InvalidDataException("preferences_shape");
        var expected = version == 1 ? new[] { "version", "baseUrl", "model", "includeImage", "disabledPlugins" } :
            new[] { "version", "baseUrl", "model", "includeImage", "disabledPlugins", "installedPlugins" };
        if (root.ValueKind != JsonValueKind.Object || root.EnumerateObject().Count() != expected.Length ||
            !root.EnumerateObject().Select(p => p.Name).Order().SequenceEqual(expected.Order()) ||
            version is not (1 or 2))
            throw new InvalidDataException("preferences_shape");
        return new GuidePreferences(root.GetProperty("baseUrl").GetString() ?? "",
            root.GetProperty("model").GetString() ?? "", root.GetProperty("includeImage").GetBoolean(),
            root.GetProperty("disabledPlugins").EnumerateArray().Select(e => e.GetString() ?? "").ToArray(),
            version == 1 ? [] : root.GetProperty("installedPlugins").EnumerateArray().Select(e =>
                PluginCatalog.ParsePlugin(e.GetRawText())).ToArray()).Validate();
    }

    internal byte[] Serialize()
    {
        var valid = Validate();
        var bytes = JsonSerializer.SerializeToUtf8Bytes(new { version = 2, baseUrl = valid.BaseUrl,
            model = valid.Model, includeImage = valid.IncludeImage, disabledPlugins = valid.DisabledPlugins,
            installedPlugins = valid.CustomPlugins.Select(p => JsonSerializer.Deserialize<JsonElement>(PluginCatalog.SerializePlugin(p))) });
        if (bytes.Length > MaxBytes) throw new InvalidDataException("preferences_size");
        return bytes;
    }

    internal bool SameAs(GuidePreferences other) => BaseUrl == other.BaseUrl && Model == other.Model &&
        IncludeImage == other.IncludeImage && DisabledPlugins.SequenceEqual(other.DisabledPlugins, StringComparer.Ordinal) &&
        CustomPlugins.Select(PluginCatalog.SerializePlugin).SequenceEqual(other.CustomPlugins.Select(PluginCatalog.SerializePlugin), StringComparer.Ordinal);
    internal bool RequiresNewConsent(GuidePreferences previous) =>
        GuidePanel.Disclosure(Options) != GuidePanel.Disclosure(previous.Options);
}

internal enum PreferencesStatus { Missing, Loaded, Invalid }

internal sealed class GuidePreferencesStore(string? file = null)
{
    readonly string _file = file ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "CetaFae", "settings.json");
    static bool Failure(Exception ex) => ex is IOException or UnauthorizedAccessException or JsonException or
        ArgumentException or InvalidOperationException or FormatException or InvalidDataException;

    void CheckPath()
    {
        for (var path = Path.GetFullPath(_file); path is not null; path = Path.GetDirectoryName(path))
            if ((File.Exists(path) || Directory.Exists(path)) && (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
                throw new IOException("preferences_path_not_regular");
    }

    internal (GuidePreferences Value, PreferencesStatus Status) Read()
    {
        try
        {
            CheckPath();
            using var stream = new FileStream(_file, FileMode.Open, FileAccess.Read, FileShare.Read);
            if (stream.Length is <= 0 or > GuidePreferences.MaxBytes) return (GuidePreferences.Default, PreferencesStatus.Invalid);
            var bytes = new byte[(int)stream.Length]; stream.ReadExactly(bytes);
            return (GuidePreferences.Parse(bytes), PreferencesStatus.Loaded);
        }
        catch (FileNotFoundException) { return (GuidePreferences.Default, PreferencesStatus.Missing); }
        catch (DirectoryNotFoundException) { return (GuidePreferences.Default, PreferencesStatus.Missing); }
        catch (Exception ex) when (Failure(ex)) { return (GuidePreferences.Default, PreferencesStatus.Invalid); }
    }

    internal bool Save(GuidePreferences preferences)
    {
        var temporary = _file + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            var bytes = preferences.Serialize();
            CheckPath();
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(_file))!);
            using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            { stream.Write(bytes); stream.Flush(flushToDisk: true); }
            CheckPath();
            File.Move(temporary, _file, overwrite: true);
            return true;
        }
        catch (Exception ex) when (Failure(ex)) { return false; }
        finally { try { File.Delete(temporary); } catch (Exception ex) when (Failure(ex)) { } }
    }
}
