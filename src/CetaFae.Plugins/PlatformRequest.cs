using System.Text;
using System.Text.Json;

namespace CetaFae.Plugins;

/// <summary>A proposed operation, never a permission grant. Native authority stays in the host.</summary>
public sealed record PlatformRequest(int ApiVersion, string Id, string Operation, JsonElement Parameters)
{
    public const int MaxBytes = 48 * 1024;
    public static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        UnmappedMemberHandling = System.Text.Json.Serialization.JsonUnmappedMemberHandling.Disallow,
        MaxDepth = 16
    };

    public static PlatformRequest Parse(string json)
    {
        if (Encoding.UTF8.GetByteCount(json) > MaxBytes) throw new InvalidDataException("request_size");
        using var document = JsonDocument.Parse(json, new JsonDocumentOptions { MaxDepth = 16 });
        CheckKeys(document.RootElement);
        var request = JsonSerializer.Deserialize<PlatformRequest>(json, Json) ?? throw new InvalidDataException("request_shape");
        if (request.ApiVersion != 1 || !Identifier(request.Id, 64) || !Identifier(request.Operation, 64) ||
            request.Parameters.ValueKind != JsonValueKind.Object)
            throw new InvalidDataException("request_shape");
        return request;
    }

    public static IReadOnlyList<PlatformRequest> ParseProposal(string json)
    {
        if (Encoding.UTF8.GetByteCount(json) > MaxBytes) throw new InvalidDataException("request_size");
        using var document = JsonDocument.Parse(json, new JsonDocumentOptions { MaxDepth = 16 });
        CheckKeys(document.RootElement);
        if (Encoding.UTF8.GetByteCount(json) > MaxBytes || document.RootElement.ValueKind != JsonValueKind.Object ||
            document.RootElement.EnumerateObject().Count() != 1 || !document.RootElement.TryGetProperty("requests", out var requests) ||
            requests.ValueKind != JsonValueKind.Array || requests.GetArrayLength() is < 1 or > 8)
            throw new InvalidDataException("proposal_shape");
        var parsed = requests.EnumerateArray().Select(item => Parse(item.GetRawText())).ToArray();
        if (parsed.Select(item => item.Id).Distinct(StringComparer.Ordinal).Count() != parsed.Length)
            throw new InvalidDataException("duplicate_request");
        return parsed;
    }

    static bool Identifier(string? value, int max) => value is { Length: > 0 } && value.Length <= max &&
        value.All(c => char.IsAsciiLetterOrDigit(c) || c is '.' or '-' or '_');

    static void CheckKeys(JsonElement element)
    {
        if (element.ValueKind == JsonValueKind.Object)
        {
            var keys = new HashSet<string>(StringComparer.Ordinal);
            foreach (var property in element.EnumerateObject())
            {
                if (!keys.Add(property.Name)) throw new InvalidDataException("duplicate_field");
                CheckKeys(property.Value);
            }
        }
        else if (element.ValueKind == JsonValueKind.Array)
            foreach (var item in element.EnumerateArray()) CheckKeys(item);
    }
}

/// <summary>Verified means the declared postcondition, not that the user's overall problem is solved.</summary>
public sealed record PlatformResult(string Id, string Status, string Code, JsonElement? Evidence = null,
    string? RecoveryId = null, bool Replayed = false);
