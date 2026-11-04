using System.IO;
using System.Text.Json;
using CetaFae.Plugins;

namespace CetaFae.Probe;

internal sealed record FeatureMessage(string Role, string Content);

/// <summary>Host trust boundary: packages propose business data, never transport or native authority.</summary>
internal static class GuideFeature
{
    internal static PluginCatalog DefaultCatalog() => PluginCatalog.Load(Path.Combine(AppContext.BaseDirectory, "plugins"));
    internal static GuidePlugin Select(PluginCatalog catalog, GuidePlugin? adapter = null) =>
        adapter?.Program is not null ? adapter : catalog.GeneralProvider is { Program: not null } provider ? provider :
        throw new InvalidDataException("plugin_program_unavailable");

    internal static Task<string> InvokeAsync(GuidePlugin plugin, string operation, object input, CancellationToken token = default) =>
        PluginWorker.InvokeAsync(plugin.Program ?? throw new InvalidDataException("plugin_program_unavailable"), operation, input, token);

    internal static async Task<List<FeatureMessage>> MessagesAsync(GuidePlugin plugin, string operation, object input, CancellationToken token)
    {
        using var document = JsonDocument.Parse(await InvokeAsync(plugin, operation, input, token));
        var root = document.RootElement;
        if (root.EnumerateObject().Count() != 1 || !root.TryGetProperty("messages", out var messages) ||
            messages.ValueKind != JsonValueKind.Array || messages.GetArrayLength() is < 2 or > 14)
            throw new InvalidDataException("plugin_messages");
        var result = new List<FeatureMessage>();
        foreach (var message in messages.EnumerateArray())
        {
            if (message.ValueKind != JsonValueKind.Object || message.EnumerateObject().Count() != 2 ||
                !message.TryGetProperty("role", out var role) || role.ValueKind != JsonValueKind.String ||
                !message.TryGetProperty("content", out var content) || content.ValueKind != JsonValueKind.String ||
                content.GetString() is not { Length: > 0 and <= 128000 } text ||
                role.GetString() is not ("system" or "user" or "assistant"))
                throw new InvalidDataException("plugin_messages");
            if ((result.Count == 0) != (role.GetString() == "system")) throw new InvalidDataException("plugin_messages");
            if ((result.Count == 0 || operation != "window") && text.Length > 24000)
                throw new InvalidDataException("plugin_messages");
            if (operation == "window" && result.Count == 1 && text.Length > 32 * 1024)
            {
                // A bounded context aggregates many individually bounded names; do not treat it as one UI field.
                using var context = JsonDocument.Parse(text, new JsonDocumentOptions { MaxDepth = 16 });
                CheckContextText(context.RootElement);
            }
            else GuidePrivacy.EnsureTextSafe(text);
            result.Add(new(role.GetString()!, text));
        }
        if (result[^1].Role != "user" || operation == "window" && result.Count != 2)
            throw new InvalidDataException("plugin_messages");
        return result;
    }

    static void CheckContextText(JsonElement value)
    {
        if (value.ValueKind == JsonValueKind.String) GuidePrivacy.EnsureTextSafe(value.GetString()!);
        else if (value.ValueKind == JsonValueKind.Object)
            foreach (var property in value.EnumerateObject())
            {
                GuidePrivacy.EnsureTextSafe(property.Name);
                CheckContextText(property.Value);
            }
        else if (value.ValueKind == JsonValueKind.Array)
            foreach (var child in value.EnumerateArray()) CheckContextText(child);
    }

    internal static GuideAnswer Present(string operation, object input, GuidePlugin? plugin = null, CancellationToken token = default)
    {
        plugin ??= Select(DefaultCatalog());
        using var document = JsonDocument.Parse(InvokeAsync(plugin, operation, input, token).GetAwaiter().GetResult());
        var root = document.RootElement;
        if (!root.EnumerateObject().Select(p => p.Name).Order().SequenceEqual(new[] { "candidateId", "explanation", "mode" }) ||
            root.GetProperty("candidateId").ValueKind != JsonValueKind.Null ||
            root.GetProperty("mode").ValueKind != JsonValueKind.String ||
            root.GetProperty("mode").GetString() is not ("ask" or "explain") ||
            root.GetProperty("explanation").ValueKind != JsonValueKind.String ||
            root.GetProperty("explanation").GetString() is not { Length: > 0 and <= 1000 } text ||
            text.Any(c => char.IsControl(c) && c is not ('\n' or '\r' or '\t')))
            throw new InvalidDataException("plugin_presentation");
        GuidePrivacy.EnsureTextSafe(text);
        return new(root.GetProperty("mode").GetString()!, text, null);
    }

    internal static string FollowupTask(object context, GuidePlugin? plugin = null)
    {
        plugin ??= Select(DefaultCatalog());
        return TextAsync(plugin, "followup", new { followup = context }, default).GetAwaiter().GetResult();
    }

    internal static async Task<string> TextAsync(GuidePlugin plugin, string operation, object input, CancellationToken token)
    {
        using var document = JsonDocument.Parse(await InvokeAsync(plugin, operation, input, token).ConfigureAwait(false));
        if (document.RootElement.EnumerateObject().Count() != 1 ||
            !document.RootElement.TryGetProperty("text", out var value) || value.ValueKind != JsonValueKind.String ||
            value.GetString() is not { Length: > 0 and <= 8000 } text)
            throw new InvalidDataException("plugin_followup");
        GuidePrivacy.EnsureTextSafe(text);
        return text;
    }
}
