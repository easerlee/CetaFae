using System.IO;
using System.Text.Json;
using CetaFae.Plugins;

namespace CetaFae.Probe;

internal sealed partial class TaskCoordinator
{
    async Task<TaskStepResult> CompressFileAsync(TaskProposal proposal, CancellationToken token)
    {
        var source = await Resource("conversion-file", token);
        var format = Path.GetExtension(source).TrimStart('.').ToLowerInvariant();
        var maximum = proposal.MaxBytes ?? 1048576;
        var matches = 0;
        foreach (var plugin in plugins?.Plugins.Where(p => p.Kind == "conversion" && p.SchemaVersion == 10) ?? [])
        {
            using var doc = JsonDocument.Parse(await GuideFeature.InvokeAsync(plugin, "compression-plan", new {
                compression = new { sourceFormat = format, maxBytes = maximum } }, token));
            if (doc.RootElement.EnumerateObject().Count() != 1 || !doc.RootElement.TryGetProperty("action", out var action))
                throw new InvalidDataException("compression_plan");
            if (action.ValueKind == JsonValueKind.Null) continue;
            if (action.GetString() != "image.jpeg.bounded") throw new InvalidDataException("compression_plan");
            matches++;
        }
        if (matches != 1 || format is not ("jpg" or "jpeg" or "png" or "bmp"))
            return new("denied", "这版支持压缩 JPG、PNG 和 BMP 单张图片。其他格式暂不处理。", null);
        string hash;
        using (var input = TaskConversion.OpenSource(source, format))
        {
            if (input.Length <= maximum) return new("observed", "原图已经小于这个大小，不用压缩。", null);
            hash = TaskConversion.Hash(input);
        }
        var destination = TaskConversion.Destination(source, "jpg");
        if (!await interaction.Decide($"尝试压缩到 {maximum / 1024d:N0} KB 以内，另存为 JPG，保留原文件。图片可能变模糊，尺寸最多缩至一半；透明区域变为白色。无法在这些限制内达到目标就停止。", "生成压缩副本", token))
            return new("denied", "已取消，原图没有改变。", null);
        var scratch = Path.Combine(records.Root, "compression-" + Guid.NewGuid().ToString("N"));
        try
        {
            using var input = TaskConversion.OpenSource(source, format);
            if (TaskConversion.Hash(input) != hash) return new("denied", "原图已被改动，请重新选择。", null);
            interaction.Progress("正在本机压缩并检查副本…");
            var output = await Task.Run(() => ImageCompression.Encode(input, maximum, token), token);
            PlatformScope.CheckAncestors(scratch); Directory.CreateDirectory(scratch);
            var staged = Path.Combine(scratch, "compressed.jpg"); await File.WriteAllBytesAsync(staged, output.Bytes, token);
            var verified = TaskConversion.VerifyOutput(staged, "jpg");
            var call = Call("file.copy", new { source = staged, destination, sourceHash = verified, expectedHash = "missing" });
            var receipt = records.Begin(call, [destination]); LatestReceipt = receipt;
            var result = await Session(["file.copy"], [staged], [destination]).ExecuteAsync(call, token);
            records.Finish(receipt, result); LatestReceipt = records.Read().Single(r => r.Id == receipt.Id);
            return new(result.Status, result.Status == "verified" ?
                $"已生成约 {output.Bytes.Length / 1024d:N0} KB 的 JPG 副本，尺寸 {output.Width}×{output.Height}，原图保留。请打开检查文字和细节是否够清楚。" :
                "无法确认压缩副本是否保存，请先查看原文件夹和操作记录。原图保留。", null, LatestReceipt);
        }
        finally
        {
            try { PlatformScope.CheckAncestors(scratch); if (Directory.Exists(scratch)) Directory.Delete(scratch, true); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or PlatformFailure) { ProbeLog.Log("compression_cleanup_failed"); }
        }
    }
}
