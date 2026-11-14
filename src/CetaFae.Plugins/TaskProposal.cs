using System.Text.Json;

namespace CetaFae.Plugins;

/// <summary>A feature's requested next step, with resource references resolved by the host.</summary>
public sealed record TaskProposal(string Operation, string? Text = null, string? Find = null, string? Replacement = null)
{
    public static TaskProposal Parse(JsonElement element)
    {
        var proposal = JsonSerializer.Deserialize<TaskProposal>(element.GetRawText(), PlatformRequest.Json)
            ?? throw new InvalidDataException("task_shape");
        var names = element.EnumerateObject().Select(p => p.Name).ToArray();
        if (names.Distinct().Count() != names.Length || !names.Contains("operation") ||
            proposal.Operation is not ("system.info" or "storage.list" or "directory.scan" or "file.info" or
                "file.readText" or "file.write" or "file.copy" or "file.move" or "file.delete" or "text.replace"))
            throw new InvalidDataException("task_operation");
        if (proposal.Text is { Length: > 12000 } || proposal.Text?.Contains('\0') == true ||
            proposal.Find is { Length: > 300 } || proposal.Replacement is { Length: > 300 } ||
            proposal.Operation == "file.write" && proposal.Text is null ||
            proposal.Operation == "text.replace" && (string.IsNullOrEmpty(proposal.Find) || proposal.Replacement is null) ||
            proposal.Operation != "file.write" && proposal.Text is not null ||
            proposal.Operation != "text.replace" && (proposal.Find is not null || proposal.Replacement is not null))
            throw new InvalidDataException("task_parameters");
        return proposal;
    }
}
