using System.Text.Json;
using CetaFae.Plugins;

namespace CetaFae.Probe;

/// <summary>Bounded proposal/evidence loop shared by the live conversation and generated checks.</summary>
internal static class TaskWorkflow
{
    internal static async Task<string> RunAsync(TaskCoordinator task, GuidePlugin plugin, ConversationReply response,
        Func<IReadOnlyList<object>, CancellationToken, Task<ConversationReply>> next, CancellationToken token)
    {
        var facts = new List<object>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var applied = 0;
        string? outcome = null;
        string Finish(string? answer)
        {
            var prefix = applied == 0 ? "" : "已核验" + applied + "项文件改动，可从“操作记录”查看和恢复。\n";
            var body = answer ?? outcome ?? "本次检查结束。";
            return prefix.Length + body.Length <= 1200 ? prefix + body : prefix + (outcome ?? "本次检查结束。请继续询问具体结果。");
        }
        for (var step = 0; step < TaskCoordinator.MaxSteps; step++)
        {
            token.ThrowIfCancellationRequested();
            if (response.Task is not { } proposal) return Finish(response.Reply);
            if (!seen.Add(JsonSerializer.Serialize(proposal, PlatformRequest.Json)))
                return Finish(outcome ?? "这一步刚刚已检查过，暂不重复。请说明还缺什么结果。");
            var result = await task.StepAsync(proposal, plugin, token);
            outcome = result.Message;
            // The local result may identify a user file; do not give it to the model or restart the workflow.
            if (proposal.Operation == "file.convert") return Finish(result.Message);
            if (result.Status == "verified") applied++;
            else if (result.Status != "observed") return Finish(result.Message);
            GuidePrivacy.EnsureTextSafe(JsonSerializer.Serialize(result.Evidence));
            facts.Add(new { operation = proposal.Operation, result.Status, result.Evidence });
            response = await next(facts, token);
            if (response.Task is null) return Finish(response.Reply);
        }
        return Finish("本次已完成允许的处理步数。" + outcome + "请根据这些结果继续提问。");
    }
}
