using System.Text.Json;
using System.Text.Json.Serialization;

namespace CetaFae.Plugins;

/// <summary>A feature's requested next step, with resource references resolved by the host.</summary>
public sealed record TaskProposal(string Operation, string? Text = null, string? Find = null, string? Replacement = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? Purpose = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? Format = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] int? MaxBytes = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? Action = null)
{
    public static TaskProposal Parse(JsonElement element)
    {
        var proposal = JsonSerializer.Deserialize<TaskProposal>(element.GetRawText(), PlatformRequest.Json)
            ?? throw new InvalidDataException("task_shape");
        var names = element.EnumerateObject().Select(p => p.Name).ToArray();
        if (names.Distinct().Count() != names.Length || !names.Contains("operation") ||
            proposal.Operation is not ("system.info" or "storage.list" or "directory.scan" or "file.info" or
                "file.readText" or "file.write" or "file.copy" or "file.move" or "file.delete" or "text.replace" or "file.convert" or "file.compress" or "utility.run"))
            throw new InvalidDataException("task_operation");
        if (proposal.Text is { Length: > 12000 } || proposal.Text?.Contains('\0') == true ||
            proposal.Find is { Length: > 300 } || proposal.Replacement is { Length: > 300 } ||
            proposal.Operation == "file.write" && proposal.Text is null ||
            proposal.Operation == "text.replace" && (string.IsNullOrEmpty(proposal.Find) || proposal.Replacement is null) ||
            proposal.Operation != "file.write" && proposal.Text is not null ||
            proposal.Operation != "text.replace" && (proposal.Find is not null || proposal.Replacement is not null) ||
            (proposal.Operation == "file.convert" ? !ValidConversion(proposal) :
                names.Contains("purpose") || names.Contains("format")) ||
            (proposal.Operation == "file.compress" ? proposal.MaxBytes is < 65536 or > 4194304 : names.Contains("maxBytes")) ||
            (proposal.Operation == "utility.run" ? proposal.Action is not ("image.capture" or "text.extract" or "text.translate" or "text.paste" or "memory.inspect") : names.Contains("action")))
            throw new InvalidDataException("task_parameters");
        return proposal;
    }

    static bool ValidConversion(TaskProposal proposal) =>
        (proposal.Purpose is "compatible" or "transparent" or "web" or "print" || proposal.Purpose is null) &&
        (proposal.Format is null || proposal.Format is { Length: > 0 and <= 12 } format &&
            format.All(c => c is >= 'a' and <= 'z' or >= '0' and <= '9')) &&
        (proposal.Purpose is not null || proposal.Format is not null);
}
