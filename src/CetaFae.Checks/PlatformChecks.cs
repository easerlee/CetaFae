using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using Microsoft.Win32;
using CetaFae.Plugins;
using CetaFae.Probe;

namespace CetaFae.Checks;

internal static class PlatformChecks
{
    static PlatformRequest Call(string operation, object parameters, string? id = null) => PlatformRequest.Parse(
        JsonSerializer.Serialize(new { apiVersion = 1, id = id ?? Guid.NewGuid().ToString("N"), operation, parameters }, PlatformRequest.Json));

    internal static async Task RunAsync(Action<bool, string> check)
    {
        var root = Path.GetFullPath(Path.Combine(".artifacts", "checks", "platform-" + Guid.NewGuid().ToString("N")));
        Directory.CreateDirectory(root);
        var data = Path.Combine(root, "data"); Directory.CreateDirectory(data);
        var journal = Path.Combine(root, "recovery");
        var registryKey = @"Software\CetaFae\GeneratedChecks\" + Guid.NewGuid().ToString("N");
        using var generatedKey = Registry.CurrentUser.CreateSubKey(registryKey);
        var scope = new PlatformScope { Operations = PlatformEngine.Contracts.Keys.ToArray(), ReadRoots = [data], WriteRoots = [data],
            RegistryRoots = ["HKCU\\" + registryKey], ExpiresAt = DateTimeOffset.UtcNow.AddMinutes(5) };
        var session = new PlatformSession(scope, journal);
        async Task<PlatformResult> Run(string op, object p) => await session.ExecuteAsync(Call(op, p));
        void Reject(Action action, string name)
        {
            var rejected = false;
            try { action(); } catch (Exception ex) when (ex is PlatformFailure or InvalidDataException or JsonException or ArgumentException) { rejected = true; }
            check(rejected, name);
        }
        try
        {
            check(PlatformScope.Within(Path.Combine(Path.GetPathRoot(data)!, "generated"), Path.GetPathRoot(data)!) &&
                !PlatformScope.Within(data + "-sibling", data), "drive-root scope includes descendants without accepting same-prefix siblings");
            var visual = new PlatformPoint("generated", -20, 40, -100, 20, 200, 100);
            check(PlatformDesktop.Point(visual, new System.Windows.Rect(-100, 20, 200, 100)) == (-20, 40), "host-selected visual point supports negative monitor coordinates without UIA");
            Reject(() => PlatformDesktop.Point(visual, new System.Windows.Rect(-90, 20, 200, 100)), "moved window invalidates a visual point lease");
            Reject(() => PlatformDesktop.Point(visual with { X = 500 }, new System.Windows.Rect(-100, 20, 200, 100)), "visual point must stay inside selected window");
            Reject(() => PlatformRequest.Parse("{\"apiVersion\":1,\"id\":\"a\",\"id\":\"b\",\"operation\":\"file.info\",\"parameters\":{}}"), "capability duplicate envelope fields rejected");
            Reject(() => PlatformRequest.Parse("{\"apiVersion\":1,\"id\":\"a\",\"operation\":\"file.info\",\"parameters\":{},\"scope\":{}}"), "a proposal cannot carry a permission grant");
            Reject(() => PlatformRequest.Parse(new string('x', PlatformRequest.MaxBytes + 1)), "oversized capability request rejected before parsing");
            Reject(() => PlatformRequest.ParseProposal("{\"requests\":[]}"), "empty capability proposal rejected");
            Reject(() => PlatformEngine.Validate(Call("file.info", new { path = data, command = "ignored" })), "unknown operation parameters rejected");
            Reject(() => PlatformEngine.Validate(Call("shell.run", new { command = "ignored" })), "no generic shell escape operation");
            foreach (var path in new[] { @"relative.txt", @"\\server\share\file.txt", data + ":stream", Path.Combine(data, "..", "outside.txt"),
                Path.Combine(data, ".env"), Path.Combine(data, "key.pem"), Path.Combine(data, "key.pem."), Path.Combine(data, "NUL.txt") })
                Reject(() => scope.FilePath(path, false), "resource scope rejects unsupported or sensitive path " + Path.GetFileName(path));
            check((await new PlatformSession(new PlatformScope { ExpiresAt = DateTimeOffset.UtcNow.AddMinutes(1) }, journal)
                .ExecuteAsync(Call("system.info", new { }))).Code == "operation_denied", "empty grants provide no authority");
            check((await new PlatformSession(scope with { ExpiresAt = DateTimeOffset.UtcNow.AddSeconds(-1) }, journal)
                .ExecuteAsync(Call("system.info", new { }))).Code == "scope_expired", "expired scope prevents dispatch");
            var info = await Run("system.info", new { });
            check(info.Status == "observed" && info.Evidence!.Value.TryGetProperty("elevated", out _), "real hidden worker returns system facts without model or desktop");
            check((await Run("storage.list", new { })).Status == "observed", "volume metadata available through generic worker");

            var pathA = Path.Combine(data, "sample.txt"); var pathB = Path.Combine(data, "copy.txt"); var pathC = Path.Combine(data, "moved.txt");
            var write = Call("file.write", new { path = pathA, text = "generated test", expectedHash = "missing" }, "write-once");
            var written = await session.ExecuteAsync(write);
            check(written.Status == "verified" && File.ReadAllText(pathA) == "generated test" && written.RecoveryId is not null,
                "file creation has durable recovery material and verified postcondition");
            check((await session.ExecuteAsync(write)).Replayed && File.ReadAllText(pathA) == "generated test", "repeated mutation ID reuses receipt without writing");
            check((await session.ExecuteAsync(Call("file.write", new { path = pathA, text = "changed", expectedHash = "missing" }, "write-once"))).Code == "request_id_conflict",
                "same request ID cannot be reused for a different operation");
            check((await Run("file.write", new { path = pathA, text = "changed", expectedHash = "missing" })).Code == "state_conflict", "stale file precondition refuses overwrite");
            var read = await Run("file.readText", new { path = pathA });
            check(read.Evidence!.Value.GetProperty("text").GetString() == "generated test", "scoped UTF8 content and hash returned");
            var hash = read.Evidence.Value.GetProperty("sha256").GetString()!;
            check((await Run("file.info", new { path = pathA })).Status == "observed", "scoped file metadata is inspectable");
            check((await Run("file.list", new { path = data })).Evidence!.Value.GetProperty("entries").GetArrayLength() == 1, "directory enumeration is bounded and scoped");
            check((await Run("file.copy", new { source = pathA, destination = pathB, sourceHash = hash, expectedHash = "missing" })).Status == "verified" && File.Exists(pathA), "copy verifies source and destination states");
            var moved = await Run("file.move", new { source = pathB, destination = pathC, sourceHash = hash, expectedHash = "missing" });
            check(moved.Status == "verified" && !File.Exists(pathB) && File.Exists(pathC), "move verifies both resource states");
            check((await Run("recovery.restore", new { recoveryId = moved.RecoveryId })).Status == "verified" && File.Exists(pathB) && !File.Exists(pathC), "move recovery restores both original states");
            var deleted = await Run("file.delete", new { path = pathB, expectedHash = hash });
            check(deleted.Status == "verified" && !File.Exists(pathB), "file deletion requires matching observed hash");
            File.WriteAllText(pathB, "new user content");
            check((await Run("recovery.restore", new { recoveryId = deleted.RecoveryId })).Code == "state_conflict" && File.ReadAllText(pathB) == "new user content", "restore cannot overwrite newer content");
            File.Delete(pathB);
            check((await Run("recovery.restore", new { recoveryId = deleted.RecoveryId })).Status == "verified" && File.ReadAllText(pathB) == "generated test", "encrypted delete recovery restores generated content");
            var encrypted = File.ReadAllBytes(Path.Combine(journal, deleted.RecoveryId + ".dat"));
            check(!Encoding.UTF8.GetString(encrypted).Contains("generated test", StringComparison.Ordinal), "recovery storage contains no plaintext generated document");
            var noFiles = new PlatformSession(scope with { ReadRoots = [], WriteRoots = [] }, journal);
            check((await noFiles.ExecuteAsync(Call("recovery.restore", new { recoveryId = deleted.RecoveryId }))).Code == "resource_denied",
                "recovery material cannot enlarge the current resource grant");
            var damaged = Path.Combine(journal, written.RecoveryId + ".dat"); File.WriteAllBytes(damaged, [1, 2, 3]);
            check((await Run("recovery.restore", new { recoveryId = written.RecoveryId })).Status != "verified" && File.ReadAllText(pathA) == "generated test",
                "damaged encrypted recovery is refused without touching generated file");
            check((await Run("recovery.restore", new { recoveryId = "../escape" })).Code == "invalid_recovery", "recovery token cannot select an arbitrary file");
            var locked = Path.Combine(data, "locked.txt"); File.WriteAllText(locked, "locked");
            using (var handle = new FileStream(locked, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
                check((await Run("file.readText", new { path = locked })).Status == "failed", "locked file returns bounded failure without content");
            var large = Path.Combine(data, "large.txt"); File.WriteAllBytes(large, new byte[PlatformFiles.MaxFileBytes + 1]);
            check((await Run("file.info", new { path = large })).Evidence!.Value.GetProperty("length").GetInt64() == PlatformFiles.MaxFileBytes + 1,
                "large file metadata remains available without loading its content");
            check((await Run("file.readText", new { path = large })).Code == "file_limit", "file content memory limit is enforced before allocation");
            var scan = await Run("directory.scan", new { path = data });
            check(scan.Status == "observed" && scan.Evidence!.Value.GetProperty("files").GetInt32() == 4 &&
                scan.Evidence.Value.GetProperty("complete").GetBoolean(), "generated directory size scan uses bounded metadata without reading file contents");
            check(PlatformLaunch.Quote("a b") == "\"a b\"" && PlatformLaunch.Quote("a\"b") == "\"a\\\"b\"" &&
                PlatformLaunch.Quote("tail\\") == "\"tail\\\\\"", "native argv quoting preserves spaces quotes and trailing slashes without a shell");
            var link = Path.Combine(data, "linked");
            try { Directory.CreateSymbolicLink(link, root); }
            catch (IOException)
            {
                var start = new ProcessStartInfo("cmd.exe") { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
                foreach (var argument in new[] { "/d", "/c", "mklink", "/J", link, root }) start.ArgumentList.Add(argument);
                using var junction = Process.Start(start)!; await junction.WaitForExitAsync();
                if (junction.ExitCode != 0) throw new InvalidOperationException("generated_junction_failed");
            }
            try { check((await Run("file.info", new { path = Path.Combine(link, "generated.txt") })).Code == "linked_resource", "generated junction cannot redirect scoped file access"); }
            finally { Directory.Delete(link); }

            var key = "HKCU\\" + registryKey;
            generatedKey.SetValue("sample", "before", RegistryValueKind.String);
            var original = await Run("registry.read", new { key, name = "sample" });
            var updated = await Run("registry.write", new { key, name = "sample", text = "after", expectedHash = original.Evidence!.Value.GetProperty("sha256").GetString() });
            check(updated.Status == "verified" && generatedKey.GetValue("sample")?.ToString() == "after", "generated HKCU value has verified write and recovery");
            check((await Run("recovery.restore", new { recoveryId = updated.RecoveryId })).Status == "verified" && generatedKey.GetValue("sample")?.ToString() == "before", "registry recovery preserves original type and value");
            check((await Run("registry.read", new { key = @"HKCU\Software\Unselected", name = "sample" })).Code == "resource_denied", "unselected registry branch denied");
            check((await Run("registry.write", new { key, name = "sample", text = "changed", expectedHash = "missing" })).Code == "state_conflict", "stale registry precondition denied");
            foreach (var (kind, value) in new (RegistryValueKind, object)[] { (RegistryValueKind.DWord, 42), (RegistryValueKind.QWord, 12345678901L),
                (RegistryValueKind.Binary, new byte[] { 4, 2 }), (RegistryValueKind.MultiString, new[] { "generated", "test" }),
                (RegistryValueKind.ExpandString, "%CETAFAE_GENERATED%") })
            {
                generatedKey.SetValue("typed", value, kind);
                var before = await Run("registry.read", new { key, name = "typed" });
                var removed = await Run("registry.delete", new { key, name = "typed", expectedHash = before.Evidence!.Value.GetProperty("sha256").GetString() });
                check(removed.Status == "verified" && (await Run("recovery.restore", new { recoveryId = removed.RecoveryId })).Status == "verified" &&
                    generatedKey.GetValueKind("typed") == kind, "generated registry recovery preserves " + kind);
            }

            using var own = Process.GetCurrentProcess();
            var ownIdentity = new PlatformProcess(own.Id, own.StartTime.ToUniversalTime().Ticks);
            var processSession = new PlatformSession(scope with { Processes = [ownIdentity] }, journal);
            check((await processSession.ExecuteAsync(Call("process.inspect", new { processId = own.Id }))).Status == "observed", "explicit process identity can be inspected");
            check((await new PlatformSession(scope with { Processes = [ownIdentity with { StartedAtTicks = 1 }] }, journal)
                .ExecuteAsync(Call("process.inspect", new { processId = own.Id }))).Code == "target_changed", "PID reuse/stale process identity rejected");
            check((await Run("process.inspect", new { processId = own.Id })).Code == "resource_denied", "process access is not implied by an operation ID");
            var dotnet = Path.GetFullPath(Path.Combine(System.Runtime.InteropServices.RuntimeEnvironment.GetRuntimeDirectory(), "..", "..", "..", "dotnet.exe"));
            string[] arguments = [typeof(PlatformChecks).Assembly.Location, "--worker-fixture", "blocked"];
            var launchScope = scope with { Executables = [new(dotnet, arguments)] };
            var launchSession = new PlatformSession(launchScope, journal);
            check((await launchSession.ExecuteAsync(Call("process.start", new { executable = dotnet, arguments = new[] { "--info" } }))).Code == "resource_denied", "executable grant pins exact argument list");
            var started = await launchSession.ExecuteAsync(Call("process.start", new { executable = dotnet, arguments }));
            check(started.Status == "observed" && started.Code == "process_started", "owned generated hidden process starts without inheriting worker IPC");
            var childId = started.Evidence!.Value.GetProperty("processId").GetInt32();
            using (var child = Process.GetProcessById(childId))
            {
                try
                {
                    var childScope = scope with { Processes = [new(childId, started.Evidence.Value.GetProperty("startedAtTicks").GetInt64())] };
                    var stoppedChild = await new PlatformSession(childScope, journal).ExecuteAsync(Call("process.stop", new { processId = childId }));
                    check(stoppedChild.Status == "verified" && child.HasExited, "scoped generated process stop independently verifies exit");
                }
                finally { if (!child.HasExited) { child.Kill(); await child.WaitForExitAsync(); } }
            }
            check((await Run("service.stop", new { service = "unselected" })).Code == "resource_denied", "service mutation rejected before opening unselected service");
            check((await Run("events.query", new { log = "unselected", minutes = 1, limit = 1 })).Code == "resource_denied", "event logs need explicit channel scope");
            check((await Run("network.connect", new { host = "127.0.0.1", port = 1 })).Code == "resource_denied", "network requests require endpoint scope");
            check((await Run("window.activate", new { windowId = "unselected" })).Code == "resource_denied", "unselected windows cannot be activated");
            check((await Run("ui.text", new { windowId = "unselected", text = "test" })).Code == "resource_denied", "raw input cannot target an unselected window");
            check((await session.ExecuteAsync(Call("system.info", new { }), elevated: true)).Code == "elevation_denied", "normal grant never silently elevates or opens UAC");
            check(!PlatformWorker.CanElevate("process.start") && !PlatformWorker.CanElevate("ui.text"), "elevated helper excludes arbitrary executable and desktop input bridges");

            using var listener = new TcpListener(IPAddress.Loopback, 0); listener.Start();
            var port = ((IPEndPoint)listener.LocalEndpoint).Port;
            var network = new PlatformSession(scope with { NetworkEndpoints = ["127.0.0.1:" + port] }, journal);
            check((await network.ExecuteAsync(Call("network.resolve", new { host = "127.0.0.1", port }))).Status == "observed", "scoped generated loopback address resolves");
            check((await network.ExecuteAsync(Call("network.connect", new { host = "127.0.0.1", port }))).Status == "observed", "generated loopback TCP endpoint is reachable");
            listener.Stop();

            var exchanges = 0;
            var once = new PlatformSession(scope, journal, async (call, token) => { exchanges++; await Task.Delay(20, token); return PlatformEngine.Result(call, "verified", "generated"); });
            var concurrentCall = Call("file.write", new { path = pathA, text = "test", expectedHash = hash });
            var concurrent = await Task.WhenAll(once.ExecuteAsync(concurrentCall), once.ExecuteAsync(concurrentCall));
            check(exchanges == 1 && concurrent.Count(r => r.Replayed) == 1, "concurrent repeated requests dispatch once within a session");
            var unknown = new PlatformSession(scope, journal, (_, _) => throw new IOException("generated failure"));
            var uncertain = await unknown.ExecuteAsync(concurrentCall);
            check(uncertain.Status == "outcome_unknown" && (await unknown.ExecuteAsync(concurrentCall)).Replayed, "lost mutation receipt is unknown and cannot auto-retry");
            using var cancel = new CancellationTokenSource(); cancel.Cancel();
            var stopped = false; try { await once.ExecuteAsync(concurrentCall with { Id = "cancelled" }, cancel.Token); } catch (OperationCanceledException) { stopped = true; }
            check(stopped && exchanges == 1, "pre-dispatch cancellation starts no worker");
            var forged = new PlatformSession(scope, journal, (call, _) => Task.FromResult(new PlatformResult("other", "verified", "forged")));
            check((await forged.ExecuteAsync(concurrentCall)).Status == "outcome_unknown", "mismatched worker receipt cannot report success");

            var plugin = PluginCatalog.ParsePlugin(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "examples", "example.capabilities.json")));
            var proposals = await GuideFeature.CapabilitiesAsync(plugin, new { capabilityIntent = "inspect-file", selectedFile = pathA });
            check(proposals.Count == 1 && proposals[0].Operation == "file.info", "independent TS plugin proposes typed native request through existing worker");
            check((await session.ExecuteAsync(proposals[0])).Status == "observed", "host grants execute independent plugin proposal with verified scope");
            var denied = await new PlatformSession(scope with { ReadRoots = [], WriteRoots = [] }, journal).ExecuteAsync(proposals[0]);
            check(denied.Code == "resource_denied", "same TS proposal has no authority without host resource selection");
            var messageRejected = false;
            try { await GuideFeature.MessagesAsync(plugin, "capability-plan", new { capabilityIntent = "inspect-file", selectedFile = pathA }, default); }
            catch (InvalidDataException) { messageRejected = true; }
            check(messageRejected, "ordinary chat validator refuses capability proposals rather than executing them");
        }
        finally
        {
            Registry.CurrentUser.DeleteSubKeyTree(registryKey, throwOnMissingSubKey: false);
            if (!PlatformScope.Within(root, Path.GetFullPath(".artifacts/checks"))) throw new InvalidOperationException("cleanup_scope");
            Directory.Delete(root, recursive: true);
        }
    }
}
