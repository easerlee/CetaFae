using System.IO;
using System.Text.Json;
using CetaFae.Plugins;

namespace CetaFae.Probe;

/// <summary>Non-secret user choices; this record can never grant collection permission.</summary>
internal sealed record GuidePreferences(string BaseUrl, string Model, bool IncludeImage, IReadOnlyList<string> DisabledPlugins)
{
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
        return this with { BaseUrl = options.BaseUrl.AbsoluteUri, Model = options.Model.Trim(),
            DisabledPlugins = Array.AsReadOnly(DisabledPlugins.Order(StringComparer.Ordinal).ToArray()) };
    }

    internal PluginCatalog Filter(PluginCatalog shipped) => new(shipped.Plugins.Where(p =>
        !DisabledPlugins.Contains(p.Id, StringComparer.OrdinalIgnoreCase)));

    internal static GuidePreferences Parse(byte[] bytes)
    {
        if (bytes.Length > 16 * 1024) throw new InvalidDataException("preferences_size");
        using var json = JsonDocument.Parse(bytes, new JsonDocumentOptions { MaxDepth = 4 });
        var root = json.RootElement;
        var expected = new[] { "version", "baseUrl", "model", "includeImage", "disabledPlugins" };
        if (root.ValueKind != JsonValueKind.Object || root.EnumerateObject().Count() != expected.Length ||
            !root.EnumerateObject().Select(p => p.Name).Order().SequenceEqual(expected.Order()) ||
            root.GetProperty("version").GetInt32() != 1)
            throw new InvalidDataException("preferences_shape");
        return new GuidePreferences(root.GetProperty("baseUrl").GetString() ?? "",
            root.GetProperty("model").GetString() ?? "", root.GetProperty("includeImage").GetBoolean(),
            root.GetProperty("disabledPlugins").EnumerateArray().Select(e => e.GetString() ?? "").ToArray()).Validate();
    }

    internal byte[] Serialize()
    {
        var valid = Validate();
        return JsonSerializer.SerializeToUtf8Bytes(new { version = 1, baseUrl = valid.BaseUrl,
            model = valid.Model, includeImage = valid.IncludeImage, disabledPlugins = valid.DisabledPlugins });
    }

    internal bool SameAs(GuidePreferences other) => BaseUrl == other.BaseUrl && Model == other.Model &&
        IncludeImage == other.IncludeImage && DisabledPlugins.SequenceEqual(other.DisabledPlugins, StringComparer.Ordinal);
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
            if (stream.Length is <= 0 or > 16 * 1024) return (GuidePreferences.Default, PreferencesStatus.Invalid);
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
