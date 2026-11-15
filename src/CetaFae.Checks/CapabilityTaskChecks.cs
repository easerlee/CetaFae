using System.IO;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using CetaFae.Plugins;
using CetaFae.Probe;

namespace CetaFae.Checks;

internal static class CapabilityTaskChecks
{
    internal static async Task RunAsync(Action<bool, string> check)
    {
        var root = Path.GetFullPath(Path.Combine(".artifacts", "checks", "task-" + Guid.NewGuid().ToString("N")));
        Directory.CreateDirectory(root);
        var plugin = GuideFeature.DefaultCatalog().ChatProvider!;
        var source = Path.Combine(root, "generated.txt");
        File.WriteAllText(source, "generated original", new UTF8Encoding(false));
        var store = new TaskRecordStore(Path.Combine(root, "records"));
        TaskCoordinator Create(Func<string, string?, CancellationToken, Task<bool>>? decide = null, string? destination = null) => new(store,
            new(decide ?? ((_, _, _) => Task.FromResult(true)), (kind, _) => Task.FromResult<string?>(kind == "folder" ? root : kind == "destination" ? destination : source), _ => { }));
        async Task Reject(Func<Task> action, string name)
        {
            var denied = false;
            try { await action(); }
            catch (Exception ex) when (ex is InvalidDataException or JsonException or OperationCanceledException or System.Security.Cryptography.CryptographicException or IOException)
            { denied = true; }
            check(denied, name);
        }
        Task Parse(string json) { _ = TaskProposal.Parse(JsonSerializer.Deserialize<JsonElement>(json)); return Task.CompletedTask; }
        foreach (var json in new[] { "null", "[]", "{}", "{\"operation\":\"shell.run\"}",
            "{\"operation\":\"file.write\"}", "{\"operation\":\"file.delete\",\"path\":\"ignored\"}",
            "{\"operation\":\"file.delete\",\"grant\":true}", "{\"operation\":\"file.delete\",\"operation\":\"file.info\"}",
            "{\"operation\":\"text.replace\",\"find\":\"\",\"replacement\":\"b\"}" })
            await Reject(() => Parse(json), "task rejects malformed, duplicate or authority-bearing proposal " + json);

        await Parse("{\"operation\":\"file.convert\",\"purpose\":\"compatible\"}");
        check(true, "conversion admits a purpose without a supplied path or command");

        var inspected = await Create().StepAsync(new("file.info"), plugin, default);
        var inspectedJson = JsonSerializer.Serialize(inspected.Evidence);
        check(inspected.Status == "observed" && !inspectedJson.Contains("sha256") && !inspectedJson.Contains(root), "file facts sent to model exclude authority hashes and paths");
        check(store.Read().Length == 0, "read-only inspection does not create action authority records");
        var scan = await Create().StepAsync(new("directory.scan"), plugin, default);
        check(scan.Status == "observed" && !JsonSerializer.Serialize(scan.Evidence).Contains("generated.txt"), "folder inspection returns bounded totals without names or content");
        var prompts = 0;
        var reuse = Create((_, _, _) => { prompts++; return Task.FromResult(true); });
        await reuse.StepAsync(new("file.info"), plugin, default); await reuse.StepAsync(new("file.info"), plugin, default);
        check(prompts == 1, "same inspection scope is disclosed once within a task");
        await Reject(() => Create((_, _, _) => Task.FromResult(false)).StepAsync(new("file.write", "refused"), plugin, default), "refusing preview dispatches no mutation");
        check(File.ReadAllText(source) == "generated original" && store.Read().Length == 0, "refused file preview leaves file and journal unchanged");
        using (var cancelled = new CancellationTokenSource())
        {
            await Reject(() => Create((_, _, _) => { cancelled.Cancel(); return Task.FromResult(true); })
                .StepAsync(new("file.write", "cancelled"), plugin, cancelled.Token), "cancellation after preview is checked before write-ahead admission");
            check(store.Read().Length == 0 && File.ReadAllText(source) == "generated original", "cancelled preview creates no pending effect");
        }
        var changed = await Create().StepAsync(new("file.write", "generated changed"), plugin, default);
        check(changed.Status == "verified" && File.ReadAllText(source) == "generated changed", "conversation file write executes real worker and verifies actual bytes");
        var persisted = new TaskRecordStore(store.Root).Read().Single();
        check(persisted.State == "verified" && persisted.RecoveryId is not null && persisted.Id == changed.Receipt!.Id, "new store instance recovers durable verified receipt without replay");
        var encrypted = File.ReadAllBytes(Path.Combine(store.Root, persisted.Id + ".task"));
        check(!Encoding.UTF8.GetString(encrypted).Contains(root) && !Encoding.UTF8.GetString(encrypted).Contains("generated changed"), "durable receipt does not expose plaintext path or file text");
        check((await Create().RestoreAsync(persisted, default)).Contains("完成") && File.ReadAllText(source) == "generated original", "user-selected recovery restores exact previous content");
        check(store.Read().Single(r => r.Id == persisted.Id).State == "restored", "completed recovery retires original recovery action");

        var stale = await Create((_, _, _) => { File.WriteAllText(source, "newer external edit"); return Task.FromResult(true); })
            .StepAsync(new("file.write", "must not replace"), plugin, default);
        check(stale.Status == "denied" && File.ReadAllText(source) == "newer external edit", "edit between preview and dispatch is refused without overwrite");
        var destination = Path.Combine(root, "copy.txt");
        var copyConflict = await Create((_, _, _) => { File.WriteAllText(source, "changed source again"); return Task.FromResult(true); }, destination)
            .StepAsync(new("file.copy"), plugin, default);
        check(copyConflict.Status == "denied" && !File.Exists(destination), "copy pins source evidence before preview and rejects intervening edits");
        var copied = await Create(destination: destination).StepAsync(new("file.copy"), plugin, default);
        check(copied.Status == "verified" && File.ReadAllText(destination) == "changed source again", "selected copy verifies target content without deleting source");
        await Create().RestoreAsync(copied.Receipt!, default);
        check(!File.Exists(destination) && File.Exists(source), "copy recovery removes only created destination");
        var transformed = await Create().StepAsync(new("text.replace", Find: "changed", Replacement: "replaced"), plugin, default);
        check(transformed.Status == "verified" && File.ReadAllText(source) == "replaced source again", "JSON-only TS transform commits previewed result through same native verifier");
        File.WriteAllText(source, "subsequent user edit");
        var conflict = await Create().RestoreAsync(transformed.Receipt!, default);
        check(!conflict.Contains("完成") && File.ReadAllText(source) == "subsequent user edit", "recovery refuses to overwrite newer user work");
        check((await Create().StepAsync(new("text.replace", Find: "absent", Replacement: "value"), plugin, default)).Status == "denied", "missing text target makes no speculative edit");

        var beforeWorkflow = store.Read().Select(r => r.Id).ToHashSet();
        var continuation = 0;
        var workflow = await TaskWorkflow.RunAsync(Create(destination: destination), plugin,
            new(null, new("file.write", "workflow result")), (facts, _) =>
            {
                check(facts.Count == ++continuation && !JsonSerializer.Serialize(facts).Contains("sha256"), "workflow passes only bounded observed evidence to next conversation step");
                return Task.FromResult(continuation == 1 ? new ConversationReply(null, new("file.copy")) : new("生成任务已完成。", null));
            }, default);
        check(workflow.StartsWith("已核验2项") && File.ReadAllText(source) == "workflow result" && File.ReadAllText(destination) == "workflow result",
            "live bounded workflow combines real write and copy with independent postconditions");
        foreach (var receipt in store.Read().Where(r => !beforeWorkflow.Contains(r.Id)).OrderByDescending(r => r.At))
            await Create().RestoreAsync(receipt, default);
        check(File.ReadAllText(source) == "subsequent user edit" && !File.Exists(destination), "combined workflow can be restored in reverse order without touching earlier state");
        var repeated = 0;
        await TaskWorkflow.RunAsync(Create(), plugin, new(null, new("file.info")), (_, _) =>
        { repeated++; return Task.FromResult(new ConversationReply(null, new("file.info"))); }, default);
        check(repeated == 1, "repeated identical task proposal stops before a second dispatch");

        // Simulate a crash after the effect but before receipt finalization, using only generated files.
        var interrupted = Path.Combine(root, "interrupted.txt"); File.WriteAllText(interrupted, "before crash");
        var call = TaskCoordinator.Call("file.write", new { path = interrupted, text = "after crash", expectedHash = PlatformFiles.State(interrupted) });
        var pending = store.Begin(call, [interrupted]);
        var scope = new PlatformScope { Operations = ["file.write"], ReadRoots = [interrupted], WriteRoots = [interrupted], ExpiresAt = DateTimeOffset.UtcNow.AddMinutes(2) };
        var dispatched = await new PlatformSession(scope, store.Recovery).ExecuteAsync(call);
        check(dispatched.Status == "verified", "generated interruption fixture commits before losing receipt");
        var unknown = new TaskRecordStore(store.Root).Read().Single(r => r.Id == pending.Id);
        check(unknown.State == "outcome_unknown" && unknown.RecoveryId == pending.Id, "restart links encrypted recovery to incomplete write-ahead record");
        await Reject(() => Task.FromResult(store.Begin(TaskCoordinator.Call("file.delete", new { path = interrupted, expectedHash = PlatformFiles.State(interrupted) }), [interrupted])), "unknown effect blocks new overlapping modification across instances");
        check((await Create().ReconcileAsync(unknown, default)).Contains("完成后") && store.Read().Single(r => r.Id == pending.Id).State == "verified", "explicit reconciliation verifies poststate without replay");
        var restoreCall = TaskCoordinator.Call("recovery.restore", new { recoveryId = pending.Id });
        var pendingRestore = store.Begin(restoreCall, [interrupted], pending.Id);
        check((await new PlatformSession(scope with { Operations = ["recovery.restore"] }, store.Recovery).ExecuteAsync(restoreCall)).Status == "verified",
            "recovery interruption fixture commits restoration before receipt finalization");
        var unknownRestore = new TaskRecordStore(store.Root).Read().Single(r => r.Id == pendingRestore.Id);
        check(unknownRestore.RecoveryId == pending.Id, "pending recovery retains its identity across restart");
        await Create().ReconcileAsync(unknownRestore, default);
        check(File.ReadAllText(interrupted) == "before crash" && store.Read().Single(r => r.Id == pending.Id).State == "restored" &&
            store.Read().Single(r => r.Id == pendingRestore.Id).State == "verified", "reconciliation recognizes restored bytes in correct direction and retires original effect");
        await Reject(() => Task.FromResult(store.Begin(call, [interrupted])), "same completed task identity cannot be replayed after restart");
        var bounded = Create();
        for (var i = 0; i < TaskCoordinator.MaxSteps; i++) await bounded.StepAsync(new("file.info"), plugin, default);
        await Reject(() => bounded.StepAsync(new("file.info"), plugin, default), "task step limit stops unbounded tool loops");
        var corrupt = new TaskRecordStore(Path.Combine(root, "corrupt")); Directory.CreateDirectory(corrupt.Root);
        File.WriteAllBytes(Path.Combine(corrupt.Root, Guid.NewGuid().ToString("N") + ".task"), [1, 2, 3]);
        await Reject(() => Task.FromResult(corrupt.Begin(TaskCoordinator.Call("file.delete", new { path = source, expectedHash = PlatformFiles.State(source) }), [source])), "damaged journal prevents dispatch rather than overwriting history");
        using (var lease = new FileStream(Path.Combine(store.Root, "records.lock"), FileMode.Open, FileAccess.ReadWrite, FileShare.None))
            await Reject(() => Task.FromResult(store.Read()), "another task owner cannot read through an exclusive journal lease");
        var fullRecovery = Path.Combine(root, "full-recovery"); Directory.CreateDirectory(fullRecovery);
        using (var occupied = File.Create(Path.Combine(fullRecovery, Guid.NewGuid().ToString("N") + ".dat"))) occupied.SetLength(64L * 1024 * 1024 - 1);
        var quotaCall = TaskCoordinator.Call("file.write", new { path = source, text = "must not fit", expectedHash = PlatformFiles.State(source) });
        var quotaResult = await new PlatformSession(scope with { ReadRoots = [source], WriteRoots = [source] }, fullRecovery).ExecuteAsync(quotaCall);
        check(quotaResult.Code == "recovery_storage_limit" && File.ReadAllText(source) == "subsequent user edit", "recovery quota accounts for incoming snapshot before any file effect");

        var options = GuideOptions.Create("http://127.0.0.1:8787/v1", "generated-task");
        using var model = new GuideModel(options, new Handler(), plugins: GuideFeature.DefaultCatalog());
        var proposal = await model.ConverseAsync([], "检查磁盘剩余空间", false, default, taskAvailable: true);
        check(proposal.Task?.Operation == "storage.list" && proposal.Reply is null, "enabled conversation parses one task proposal through real plugin messages and stub transport");
        await Reject(() => model.ConverseAsync([], "检查磁盘剩余空间", false, default), "legacy text-only conversation does not accept executable proposals");
        Render(check);
    }

    sealed class Handler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token) => Task.FromResult(
            new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(JsonSerializer.Serialize(new
            { choices = new[] { new { finish_reason = "stop", message = new { content = "{\"task\":{\"operation\":\"storage.list\"}}" } } } })) });
    }

    static void Render(Action<bool, string> check)
    {
        var folder = Path.GetFullPath(Path.Combine(".artifacts", "checks", "task-ui")); Directory.CreateDirectory(folder);
        foreach (var scale in new[] { 1d, 1.25, 1.5, 2 })
        foreach (var width in new[] { 300d, 420d })
        {
            var surface = new TaskActionSurface();
            surface.Present("保存修改\n生成文件：sample.txt\n目标文件的原内容将被替换。会先保留恢复材料，文件后来变化时不会覆盖新内容。\n将保存的完整文字：\n" + string.Concat(Enumerable.Repeat("这是一段生成的文件预览。", 70)), "保存修改", () => { }, () => { });
            var visual = new Border { Width = width, Padding = new Thickness(16), Background = Brushes.White, Child = surface };
            visual.Measure(new Size(width, 500)); visual.Arrange(new Rect(new Size(width, visual.DesiredSize.Height))); visual.UpdateLayout();
            check(visual.ActualHeight < 370 && surface.ActualWidth > 200, $"inline task preview and actions fit bounded layout at {width} DIP and {scale * 100}% raster scale");
            var actions = (WrapPanel)((StackPanel)surface.Child).Children[1];
            check(actions.Children.OfType<Button>().Count() == 2 && actions.Children.OfType<Button>().All(b =>
                b.ActualHeight >= 30 && b.ActualWidth >= 56 && b.TransformToAncestor(visual).Transform(new Point(0, b.ActualHeight)).Y <= visual.ActualHeight),
                $"proceed and stop remain visible below long preview at {width} DIP and {scale * 100}% raster scale");
            var bitmap = new RenderTargetBitmap((int)Math.Ceiling(width * scale), (int)Math.Ceiling(visual.ActualHeight * scale), 96 * scale, 96 * scale, PixelFormats.Pbgra32);
            bitmap.Render(visual); var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap));
            using var output = File.Create(Path.Combine(folder, $"preview-{width}-{scale * 100}.png")); encoder.Save(output);
        }
    }
}
