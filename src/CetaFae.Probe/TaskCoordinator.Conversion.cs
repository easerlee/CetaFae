using System.IO;
using CetaFae.Plugins;

namespace CetaFae.Probe;

internal sealed partial class TaskCoordinator
{
    async Task<TaskStepResult> ConvertFileAsync(TaskProposal proposal, CancellationToken token)
    {
        if (plugins is null || !plugins.Plugins.Any(p => p.Kind == "conversion"))
            return new("denied", "当前没有启用文件转换插件。", null);
        var source = await Resource("conversion-file", token);
        if (new DriveInfo(Path.GetPathRoot(source)!).DriveType is not (DriveType.Fixed or DriveType.Removable))
            return new("denied", "请选择保存在本机的图片。", null);
        var sourceFormat = Path.GetExtension(source).TrimStart('.').ToLowerInvariant();
        var plan = await TaskConversion.PlanAsync(plugins, proposal, sourceFormat, token);
        if (plan is null) return new("denied", "这版暂不支持所选文件和目标格式的组合。可以换一张常见格式的图片，或告诉我用途。", null);
        if (plan.Tool is { Installed: false })
            return new("denied", "已为你选好 " + plan.Format.ToUpperInvariant() + "。这台电脑还缺少转换工具，请在“设置 → 功能 → 导入插件文件”安装 Filee 转换插件。", null);

        string sourceHash;
        using (var selected = TaskConversion.OpenSource(source, sourceFormat))
        {
            if (plan.Tool is null) return new("observed", $"这张图已经是 {plan.Format.ToUpperInvariant()} 格式，无需转换。", null);
            sourceHash = TaskConversion.Hash(selected);
        }
        var format = plan.Format.ToUpperInvariant();
        var destination = TaskConversion.Destination(source, plan.Format);
        var preview = $"建议转为 {format}。{plan.Reason}\n" +
            $"在原文件夹生成「{Path.GetFileName(destination)}」，保留原文件。转换在这台电脑上完成。";
        if (!await interaction.Decide(preview, "转换为 " + format, token)) return new("denied", "已取消，原文件保持不变。", null);
        token.ThrowIfCancellationRequested();
        using var sourceLease = TaskConversion.OpenSource(source, sourceFormat);
        if (TaskConversion.Hash(sourceLease) != sourceHash)
            return new("denied", "这张图刚被改动了。请重新选择要转换的文件。", null);
        if (File.Exists(destination) || Directory.Exists(destination))
            return new("denied", "原文件夹里出现了同名文件，原文件保持不变。请重新发起转换。", null);

        var scratch = Path.Combine(records.Root, "conversion-" + Guid.NewGuid().ToString("N"));
        try
        {
            interaction.Progress("正在本机转换并检查新文件…");
            await TaskConversion.StageAsync(plan.Tool, scratch, sourceFormat, plan.Format, sourceLease, token);
            var staged = TaskConversion.StagedOutput(scratch, plan.Format);
            var stagedHash = TaskConversion.VerifyOutput(staged, plan.Format);
            token.ThrowIfCancellationRequested();

            var call = Call("file.copy", new { source = staged, destination, sourceHash = stagedHash, expectedHash = "missing" });
            var receipt = records.Begin(call, [destination]);
            LatestReceipt = receipt;
            var session = Session(["file.copy"], [staged], [destination]);
            var copied = await session.ExecuteAsync(call, token);
            records.Finish(receipt, copied);
            LatestReceipt = records.Read().Single(r => r.Id == receipt.Id);
            return copied.Status switch
            {
                "verified" => new("verified", $"已转换为 {format}，新文件在原文件夹，原文件仍保留。", null, LatestReceipt),
                "denied" => new("denied", "新文件无法保存到原文件夹。原文件仍保留。", null, LatestReceipt),
                _ => new(copied.Status, "暂时无法确认副本是否已保存。请先查看原文件夹或鲸汐的操作记录。", null, LatestReceipt)
            };
        }
        finally
        {
            try
            {
                PlatformScope.CheckAncestors(scratch);
                if (Directory.Exists(scratch)) Directory.Delete(scratch, recursive: true);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or PlatformFailure)
            { ProbeLog.Log("conversion_cleanup_failed"); }
        }
    }
}
