using System.IO;
using System.Text.Json;
using CetaFae.Plugins;

namespace CetaFae.Probe;

internal sealed record TaskInteraction(
    Func<string, string?, CancellationToken, Task<bool>> Decide,
    Func<string, CancellationToken, Task<string?>> Select,
    Action<string> Progress);
internal sealed record TaskStepResult(string Status, string Message, object? Evidence, TaskReceipt? Receipt = null);

/// <summary>Host-owned object resolution and evidence. Plugins select methods, never permissions.</summary>
internal sealed partial class TaskCoordinator(TaskRecordStore records, TaskInteraction interaction, PluginCatalog? plugins = null,
    Action<string>? utility = null)
{
    readonly DateTimeOffset _expires = DateTimeOffset.UtcNow.AddMinutes(10);
    readonly HashSet<string> _disclosed = new(StringComparer.Ordinal);
    string? _file;
    string? _folder;
    int _steps;
    internal const int MaxSteps = 8;
    internal TaskReceipt? LatestReceipt { get; private set; }

    internal static PlatformRequest Call(string operation, object parameters) => PlatformRequest.Parse(
        JsonSerializer.Serialize(new { apiVersion = 1, id = Guid.NewGuid().ToString("N"), operation, parameters }, PlatformRequest.Json));

    async Task<string> Resource(string kind, CancellationToken token)
    {
        var existing = kind == "folder" ? _folder : _file;
        if (existing is not null) return existing;
        var selected = await interaction.Select(kind, token) ?? throw new OperationCanceledException(token);
        var path = PlatformScope.LocalPath(selected);
        new PlatformScope { ReadRoots = [path] }.FilePath(path, false);
        if (kind == "folder" ? !Directory.Exists(path) : !File.Exists(path)) throw new InvalidDataException("task_object_missing");
        if (kind == "folder") _folder = path; else _file = path;
        return path;
    }

    async Task Disclose(string key, string description, CancellationToken token)
    {
        if (_disclosed.Contains(key)) return;
        if (!await interaction.Decide(description + "\n只处理本次明确选择的对象；检查结果会发送给当前设置的识别服务，用来继续处理这个问题。", "开始检查", token))
            throw new OperationCanceledException(token);
        _disclosed.Add(key);
    }

    PlatformSession Session(string[] operations, string[] roots, string[]? writes = null) => new(new PlatformScope
    { Operations = operations, ReadRoots = roots, WriteRoots = writes ?? [], ExpiresAt = _expires }, records.Recovery);

    static void Observed(PlatformResult result)
    {
        if (result.Status != "observed" || result.Evidence is null) throw new InvalidDataException(result.Code);
    }

    static string ObservedHash(PlatformResult result) => result.Evidence!.Value.TryGetProperty("sha256", out var hash) &&
        hash.ValueKind == JsonValueKind.String ? hash.GetString()! : throw new InvalidDataException("task_file_too_large");

    internal async Task<TaskStepResult> StepAsync(TaskProposal proposal, GuidePlugin plugin, CancellationToken token)
    {
        _ = TaskProposal.Parse(JsonSerializer.SerializeToElement(proposal, PlatformRequest.Json));
        if (++_steps > MaxSteps || DateTimeOffset.UtcNow >= _expires) throw new InvalidDataException("task_limit");
        GuidePrivacy.EnsureTextSafe(JsonSerializer.Serialize(proposal, PlatformRequest.Json));
        var op = proposal.Operation;
        if (op == "file.convert") return await ConvertFileAsync(proposal, token);
        if (op == "file.compress") return await CompressFileAsync(proposal, token);
        if (op == "utility.run")
        {
            if (plugins?.UtilityProvider is null || utility is null) return new("denied", "当前没有启用这项工具。", null);
            utility(proposal.Action!);
            return new("observed", "工具已打开，请在工具中继续。", null);
        }
        if (op is "system.info" or "storage.list")
        {
            await Disclose(op, op == "system.info" ? "查看这台电脑的系统版本和基本情况，不读取文档。" :
                "查看各个磁盘的总容量和剩余空间，不读取文件内容。", token);
            interaction.Progress("正在检查…");
            var result = await Session([op], []).ExecuteAsync(Call(op, new { }), token); Observed(result);
            return new("observed", "已取得本次检查结果。", result.Evidence);
        }
        var resource = await Resource(op == "directory.scan" ? "folder" : "file", token);
        if (op is "directory.scan" or "file.info" or "file.readText")
        {
            var content = op == "file.readText";
            await Disclose(op + resource, (content ? "读取并发送这个文本文件的内容（最多约两万字节）：" :
                op == "directory.scan" ? "统计这个文件夹内文件的大小和数量；数量较多时可能只统计一部分，不发送文件内容：" :
                "查看这个文件的大小，不发送文件内容：") + "\n" + resource, token);
            interaction.Progress("正在检查所选对象…");
            var result = await Session([op], [resource]).ExecuteAsync(Call(op, new { path = resource }), token); Observed(result);
            object? evidence = result.Evidence;
            if (op is "file.info" or "file.readText")
            {
                var facts = result.Evidence!.Value;
                evidence = op == "file.readText" ? new { text = facts.GetProperty("text").GetString() } :
                    new { exists = facts.GetProperty("exists").GetBoolean(), length = facts.TryGetProperty("length", out var length) ? length.GetInt64() : (long?)null };
            }
            return new("observed", "已取得所选对象的检查结果。", evidence);
        }

        // Hashes are resolved from trusted observations, never accepted from the feature/model.
        var destination = op is "file.copy" or "file.move" ?
            PlatformScope.LocalPath(await interaction.Select("destination", token) ?? throw new OperationCanceledException(token)) : resource;
        var roots = new[] { resource, destination }.Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        var nativeOperation = op == "text.replace" ? "file.write" : op;
        var session = Session(["file.info", "file.readText", nativeOperation], roots, roots);
        var before = await session.ExecuteAsync(Call("file.info", new { path = destination }), token); Observed(before);
        var expected = ObservedHash(before);
        string? sourceHash = null;
        if (op is "file.copy" or "file.move")
        {
            if (resource.Equals(destination, StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("task_same_file");
            var source = await session.ExecuteAsync(Call("file.info", new { path = resource }), token); Observed(source);
            sourceHash = ObservedHash(source);
        }
        string? text = proposal.Text;
        if (op == "text.replace")
        {
            var source = await session.ExecuteAsync(Call("file.readText", new { path = resource }), token); Observed(source);
            if (!source.Evidence!.Value.GetProperty("text").GetString()!.Contains(proposal.Find!, StringComparison.Ordinal))
                return new("denied", "所选文件里没有找到要替换的文字，没有改动文件。", null);
            using var transformed = JsonDocument.Parse(await GuideFeature.InvokeAsync(plugin, "task-transform", new
                { source = source.Evidence!.Value.GetProperty("text").GetString(), find = proposal.Find, replacement = proposal.Replacement }, token));
            if (transformed.RootElement.EnumerateObject().Count() != 1 ||
                !transformed.RootElement.TryGetProperty("text", out var output) || output.ValueKind != JsonValueKind.String ||
                output.GetString() is not { Length: <= 12000 } value) throw new InvalidDataException("task_transform");
            text = value;
        }
        var action = op switch { "file.copy" => "复制文件", "file.move" => "移动文件", "file.delete" => "删除文件", _ => "保存修改" };
        var preview = action + "\n" + resource + (destination != resource ? "\n到：" + destination : "") + "\n" +
            (op == "file.delete" ? "文件会从原位置移除。" : op == "file.move" ? "原位置将不再保留这个文件。" :
                expected == "missing" ? "创建目标文件。" : "目标文件的原内容将被替换。") +
            "本次会先保留恢复材料；如果文件后来被修改，恢复会停下，避免覆盖新内容。" +
            (text is not null ? "\n将保存的完整文字：\n" + text : "");
        if (!await interaction.Decide(preview, action, token)) throw new OperationCanceledException(token);
        token.ThrowIfCancellationRequested();
        PlatformRequest request;
        if (op is "file.copy" or "file.move")
        {
            request = Call(op, new { source = resource, destination, sourceHash, expectedHash = expected });
        }
        else request = nativeOperation == "file.write" ? Call(nativeOperation, new { path = resource, text, expectedHash = expected }) :
            Call(nativeOperation, new { path = resource, expectedHash = expected });
        interaction.Progress("正在处理并核对结果…");
        var receipt = records.Begin(request, roots);
        LatestReceipt = receipt;
        var changed = await session.ExecuteAsync(request, token);
        records.Finish(receipt, changed);
        LatestReceipt = records.Read().Single(r => r.Id == receipt.Id);
        return new(changed.Status, changed.Status == "verified" ? "已核验本次文件操作；这不等于其他问题也已解决。" :
            changed.Status == "denied" ? "对象或状态已变化，这一步没有执行。" : "这一步的结果还不能确认，请先查看操作记录，不要重复执行。",
            new { operation = op, status = changed.Status, canRecover = changed.RecoveryId is not null }, LatestReceipt);
    }

    internal async Task<string> RestoreAsync(TaskReceipt receipt, CancellationToken token)
    {
        if (receipt.RecoveryId is null) return "这项操作没有可用的恢复材料。";
        if (!await interaction.Decide("恢复这次文件改动：\n" + string.Join("\n", receipt.Resources) +
            "\n若文件已被再次修改，会停止恢复，不覆盖新内容。", "恢复这次改动", token)) return "未执行恢复。";
        var request = Call("recovery.restore", new { recoveryId = receipt.RecoveryId });
        var pending = records.Begin(request, receipt.Resources, receipt.Id);
        var result = await Session(["recovery.restore"], receipt.Resources, receipt.Resources).ExecuteAsync(request, token);
        records.Finish(pending, result);
        if (result.Status == "verified") { records.Restored(receipt); return "已核验恢复完成。"; }
        return "暂时无法恢复。可能文件已变化；原文件和恢复材料均保留，请先查看，不要重复覆盖。";
    }

    internal async Task<string> ReconcileAsync(TaskReceipt receipt, CancellationToken token)
    {
        if (receipt.RecoveryId is null) return "缺少可比较的恢复材料，暂不能确认结果；没有再次执行原操作。";
        if (!await interaction.Decide("核对这些文件的当前状态，不修改文件：\n" + string.Join("\n", receipt.Resources), "核对结果", token))
            return "未检查文件，原记录保留。";
        var recovery = PlatformFiles.ReadRecovery(records.Recovery, receipt.RecoveryId);
        if (recovery.Registry is not null || !recovery.Files.All(f => receipt.Resources.Contains(f.Path, StringComparer.OrdinalIgnoreCase)))
            throw new InvalidDataException("task_recovery_identity");
        var session = Session(["file.info"], receipt.Resources);
        var before = true; var after = true;
        foreach (var file in recovery.Files)
        {
            var result = await session.ExecuteAsync(Call("file.info", new { path = file.Path }), token); Observed(result);
            var current = result.Evidence!.Value.TryGetProperty("sha256", out var hash) ? hash.GetString() : null;
            before &= current == (file.Bytes is null ? "missing" : PlatformScope.Hash(file.Bytes));
            after &= current == file.ExpectedAfter;
        }
        var restoring = receipt.Operation == "recovery.restore";
        if (restoring ? before : after)
        {
            records.Reconciled(receipt, "verified");
            if (restoring && records.Read().SingleOrDefault(r => r.Id == receipt.RecoveryId) is { } original) records.Restored(original);
            return "已核对，文件符合这次操作完成后的状态；没有重做操作。";
        }
        if (restoring ? after : before) { records.Reconciled(receipt, "denied"); return "文件仍是操作前的状态，没有需要恢复的改动；没有重做操作。"; }
        return "文件与操作前、完成后的状态均不一致，可能有其他改动。仍保留为待核对，没有覆盖或重复执行。";
    }
}
