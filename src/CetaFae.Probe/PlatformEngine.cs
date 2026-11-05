using System.Diagnostics;
using System.Diagnostics.Eventing.Reader;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using Microsoft.Win32;
using CetaFae.Plugins;

namespace CetaFae.Probe;

/// <summary>Generic native mechanisms; business intent and application matching stay in feature packages.</summary>
internal static class PlatformEngine
{
    internal static readonly IReadOnlyDictionary<string, string[]> Contracts = new Dictionary<string, string[]>
    {
        ["system.info"] = [], ["storage.list"] = [],
        ["file.info"] = ["path"], ["file.list"] = ["path"], ["file.readText"] = ["path"], ["directory.scan"] = ["path"],
        ["file.write"] = ["path", "text", "expectedHash"], ["file.delete"] = ["path", "expectedHash"],
        ["file.copy"] = ["source", "destination", "sourceHash", "expectedHash"],
        ["file.move"] = ["source", "destination", "sourceHash", "expectedHash"], ["recovery.restore"] = ["recoveryId"],
        ["registry.read"] = ["key", "name"], ["registry.write"] = ["key", "name", "text", "expectedHash"],
        ["registry.delete"] = ["key", "name", "expectedHash"],
        ["process.inspect"] = ["processId"], ["process.start"] = ["executable", "arguments"], ["process.stop"] = ["processId"],
        ["service.inspect"] = ["service"], ["service.start"] = ["service"], ["service.stop"] = ["service"],
        ["events.query"] = ["log", "minutes", "limit"], ["network.resolve"] = ["host", "port"],
        ["network.connect"] = ["host", "port"], ["window.inspect"] = ["windowId"], ["window.activate"] = ["windowId"],
        ["ui.invoke"] = ["windowId", "controlId"], ["ui.setValue"] = ["windowId", "controlId", "text"],
        ["ui.point"] = ["windowId", "pointId"],
        ["ui.scroll"] = ["windowId", "controlId", "direction"], ["ui.click"] = ["windowId", "controlId"],
        ["ui.text"] = ["windowId", "text"], ["ui.key"] = ["windowId", "key"]
    };

    internal static void Validate(PlatformRequest call)
    {
        _ = PlatformRequest.Parse(JsonSerializer.Serialize(call, PlatformRequest.Json));
        if (!Contracts.TryGetValue(call.Operation, out var fields)) throw new PlatformFailure("unsupported_operation");
        var names = call.Parameters.EnumerateObject().Select(p => p.Name).ToArray();
        if (names.Length != fields.Length || fields.Except(names, StringComparer.Ordinal).Any()) throw new PlatformFailure("invalid_parameters");
    }

    internal static async Task<PlatformResult> ExecuteAsync(PlatformRequest call, PlatformScope scope, string journal)
    {
        try
        {
            Validate(call); scope.Permit(call.Operation);
            var p = call.Parameters;
            switch (call.Operation)
            {
                case "system.info":
                    return Result(call, "observed", "system_observed", new { os = Environment.OSVersion.Version.ToString(),
                        architecture = System.Runtime.InteropServices.RuntimeInformation.OSArchitecture.ToString(),
                        processors = Environment.ProcessorCount, elevated = IsElevated() });
                case "storage.list":
                    return Result(call, "observed", "storage_observed", DriveInfo.GetDrives().Where(d => d.IsReady)
                        .Take(32).Select(d => new { root = d.Name, totalBytes = d.TotalSize, availableBytes = d.AvailableFreeSpace }).ToArray());
                case "file.info": return Result(call, "observed", "file_observed", PlatformFiles.Inspect(scope.FilePath(Text(p, "path"), false)));
                case "directory.scan": return Result(call, "observed", "directory_observed", PlatformFiles.Scan(scope.FilePath(Text(p, "path"), false), scope));
                case "file.list":
                    var entries = Directory.EnumerateFileSystemEntries(scope.FilePath(Text(p, "path"), false)).Take(101).ToArray();
                    return Result(call, "observed", "directory_observed", new { truncated = entries.Length > 100,
                        entries = entries.Take(100).Select(path => new { name = Path.GetFileName(path), directory = Directory.Exists(path) }).ToArray() });
                case "file.readText":
                    var bytes = PlatformFiles.Read(scope.FilePath(Text(p, "path"), false));
                    if (bytes.Length > 24000) throw new PlatformFailure("text_limit");
                    var text = new UTF8Encoding(false, true).GetString(bytes); GuidePrivacy.EnsureTextSafe(text);
                    return Result(call, "observed", "text_observed", new { text, sha256 = PlatformScope.Hash(bytes) });
                case "file.write": case "file.copy": case "file.move": case "file.delete": return PlatformFiles.Mutate(call, scope, journal);
                case "recovery.restore": return PlatformFiles.Restore(call, scope, journal);
                case "registry.read": case "registry.write": case "registry.delete": return RegistryOperation(call, scope, journal);
                case "process.inspect": case "process.stop":
                    var pid = Number(p, "processId", 1, int.MaxValue);
                    var identity = scope.Processes.SingleOrDefault(item => item.Id == pid) ?? throw new PlatformFailure("resource_denied");
                    using (var process = scope.Process(identity))
                    {
                        if (call.Operation == "process.inspect") return Result(call, "observed", "process_observed",
                            new { processId = pid, startedAtTicks = identity.StartedAtTicks, privateBytes = process.PrivateMemorySize64 });
                        if (pid == Environment.ProcessId) throw new PlatformFailure("self_stop_denied");
                        process.Kill(entireProcessTree: false);
                        using var wait = new CancellationTokenSource(TimeSpan.FromSeconds(3));
                        await process.WaitForExitAsync(wait.Token);
                        return Result(call, "verified", "process_exited");
                    }
                case "process.start": return StartProcess(call, scope);
                case "service.inspect": case "service.start": case "service.stop": return PlatformServices.Execute(call, scope);
                case "events.query": return Events(call, scope);
                case "network.resolve": case "network.connect": return await NetworkAsync(call, scope);
                default: return PlatformDesktop.Execute(call, scope);
            }
        }
        catch (PlatformFailure ex) { return Result(call, "denied", ex.Message); }
        catch (UnauthorizedAccessException) { return Result(call, "denied", "access_denied"); }
        catch (System.ComponentModel.Win32Exception ex) when (ex.NativeErrorCode == 5) { return Result(call, "denied", "access_denied"); }
        catch (Exception ex) when (ex is not OutOfMemoryException && ex is not StackOverflowException)
        { return Result(call, IsMutation(call.Operation) ? "outcome_unknown" : "failed", "operation_failed"); }
    }

    internal static bool IsMutation(string operation) => operation is not ("system.info" or "storage.list" or "file.info" or
        "file.list" or "file.readText" or "directory.scan" or "registry.read" or "process.inspect" or "service.inspect" or "events.query" or
        "network.resolve" or "network.connect" or "window.inspect");

    internal static PlatformResult Result(PlatformRequest call, string status, string code, object? evidence = null, string? recovery = null) =>
        new(call.Id, status, code, evidence is null ? null : JsonSerializer.SerializeToElement(evidence, PlatformRequest.Json), recovery);

    internal static string Text(JsonElement p, string name, bool allowEmpty = false, int max = 1024)
    {
        if (!p.TryGetProperty(name, out var value) || value.ValueKind != JsonValueKind.String || value.GetString() is not { } text ||
            text.Length > max || !allowEmpty && string.IsNullOrWhiteSpace(text) || text.Contains('\0')) throw new PlatformFailure("invalid_parameters");
        return text;
    }
    internal static int Number(JsonElement p, string name, int min, int max) => p.TryGetProperty(name, out var value) &&
        value.TryGetInt32(out var number) && number >= min && number <= max ? number : throw new PlatformFailure("invalid_parameters");

    static PlatformResult StartProcess(PlatformRequest call, PlatformScope scope)
    {
        var path = PlatformScope.LocalPath(Text(call.Parameters, "executable"));
        PlatformScope.CheckAncestors(path);
        if (!Path.GetExtension(path).Equals(".exe", StringComparison.OrdinalIgnoreCase)) throw new PlatformFailure("resource_denied");
        var args = call.Parameters.GetProperty("arguments");
        if (args.ValueKind != JsonValueKind.Array || args.GetArrayLength() > 16) throw new PlatformFailure("invalid_parameters");
        var arguments = new List<string>();
        // A grant to start an executable is code-execution authority, including its explicit arguments. It is never supplied by a feature package.
        foreach (var item in args.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.String || item.GetString() is not { Length: <= 2048 } argument || argument.Contains('\0'))
                throw new PlatformFailure("invalid_parameters");
            arguments.Add(argument);
        }
        if (!scope.Executables.Any(item => PlatformScope.LocalPath(item.Path).Equals(path, StringComparison.OrdinalIgnoreCase) &&
            item.Arguments.SequenceEqual(arguments, StringComparer.Ordinal))) throw new PlatformFailure("resource_denied");
        var process = PlatformLaunch.Start(path, arguments);
        return Result(call, "observed", "process_started", new { processId = process.Id, startedAtTicks = process.StartedAtTicks });
    }

    static string RegistryPath(PlatformScope scope, string input)
    {
        if (!(input.StartsWith("HKCU\\", StringComparison.Ordinal) || input.StartsWith("HKLM\\", StringComparison.Ordinal)) ||
            input.Length > 512 || input.Any(char.IsControl) || input.Split('\\').Any(s => s.Length == 0 || s is "." or ".."))
            throw new PlatformFailure("invalid_registry_path");
        if (!scope.RegistryRoots.Any(root => input.Equals(root, StringComparison.OrdinalIgnoreCase) ||
            input.StartsWith(root.TrimEnd('\\') + "\\", StringComparison.OrdinalIgnoreCase))) throw new PlatformFailure("resource_denied");
        return input;
    }
    sealed record RegistryValue(int Kind, JsonElement Value);
    static string? RegistryState(string path, string name)
    {
        using var root = RegistryKey.OpenBaseKey(path.StartsWith("HKCU", StringComparison.Ordinal) ? RegistryHive.CurrentUser : RegistryHive.LocalMachine, RegistryView.Registry64);
        using var key = root.OpenSubKey(path[5..], false);
        if (key is null || !key.GetValueNames().Contains(name, StringComparer.OrdinalIgnoreCase)) return null;
        var value = key.GetValue(name, null, RegistryValueOptions.DoNotExpandEnvironmentNames)!;
        var bytes = JsonSerializer.SerializeToUtf8Bytes(value);
        if (bytes.Length > 24000) throw new PlatformFailure("value_limit");
        var state = JsonSerializer.Serialize(new RegistryValue((int)key.GetValueKind(name), JsonSerializer.SerializeToElement(value)), PlatformRequest.Json);
        GuidePrivacy.EnsureTextSafe(state); return state;
    }
    static string RegistryHash(string? value) => value is null ? "missing" : PlatformScope.HashText(value);
    static void WriteRegistry(string path, string name, string? state)
    {
        using var root = RegistryKey.OpenBaseKey(path.StartsWith("HKCU", StringComparison.Ordinal) ? RegistryHive.CurrentUser : RegistryHive.LocalMachine, RegistryView.Registry64);
        // Existing key only: rollback must not leave a newly created branch behind.
        using var key = root.OpenSubKey(path[5..], true) ?? throw new PlatformFailure("registry_key_missing");
        if (state is null) { key.DeleteValue(name, throwOnMissingValue: false); return; }
        var saved = JsonSerializer.Deserialize<RegistryValue>(state, PlatformRequest.Json) ?? throw new PlatformFailure("invalid_recovery");
        var kind = (RegistryValueKind)saved.Kind;
        object value = kind switch
        {
            RegistryValueKind.String or RegistryValueKind.ExpandString => saved.Value.GetString()!,
            RegistryValueKind.DWord => saved.Value.GetInt32(), RegistryValueKind.QWord => saved.Value.GetInt64(),
            RegistryValueKind.MultiString => saved.Value.EnumerateArray().Select(v => v.GetString()!).ToArray(),
            RegistryValueKind.Binary or RegistryValueKind.None => saved.Value.GetBytesFromBase64(),
            _ => throw new PlatformFailure("unsupported_registry_type")
        };
        key.SetValue(name, value, kind); key.Flush();
    }
    static PlatformResult RegistryOperation(PlatformRequest call, PlatformScope scope, string journal)
    {
        var path = RegistryPath(scope, Text(call.Parameters, "key"));
        var name = Text(call.Parameters, "name", true, 256);
        var before = RegistryState(path, name);
        if (call.Operation == "registry.read") return Result(call, "observed", "registry_observed", new { sha256 = RegistryHash(before),
            value = before is null ? (JsonElement?)null : JsonSerializer.Deserialize<JsonElement>(before) });
        PlatformFiles.Expect(RegistryHash(before), Text(call.Parameters, "expectedHash"));
        string? after = null;
        if (call.Operation == "registry.write")
        {
            var text = Text(call.Parameters, "text", true, 12000); GuidePrivacy.EnsureTextSafe(text);
            after = JsonSerializer.Serialize(new RegistryValue((int)RegistryValueKind.String, JsonSerializer.SerializeToElement(text)), PlatformRequest.Json);
        }
        var id = PlatformFiles.SaveRecovery(journal, new([], new(path, name, before, RegistryHash(after))));
        try
        {
            scope.Permit(call.Operation); PlatformFiles.Expect(RegistryHash(RegistryState(path, name)), RegistryHash(before));
            WriteRegistry(path, name, after); PlatformFiles.Expect(RegistryHash(RegistryState(path, name)), RegistryHash(after));
            return Result(call, "verified", "registry_verified", new { sha256 = RegistryHash(after) }, id);
        }
        catch { return Result(call, "outcome_unknown", "inspect_recovery_before_retry", recovery: id); }
    }
    internal static PlatformResult RestoreRegistry(PlatformRequest call, PlatformScope scope, PlatformFiles.RegistryState state)
    {
        var path = RegistryPath(scope, state.Key);
        PlatformFiles.Expect(RegistryHash(RegistryState(path, state.Name)), state.ExpectedAfter);
        try
        {
            WriteRegistry(path, state.Name, state.ValueJson);
            PlatformFiles.Expect(RegistryHash(RegistryState(path, state.Name)), RegistryHash(state.ValueJson));
            return Result(call, "verified", "restore_verified");
        }
        catch { return Result(call, "outcome_unknown", "restore_incomplete", recovery: Text(call.Parameters, "recoveryId")); }
    }

    static PlatformResult Events(PlatformRequest call, PlatformScope scope)
    {
        var log = Text(call.Parameters, "log", max: 256);
        if (!scope.EventLogs.Contains(log, StringComparer.Ordinal)) throw new PlatformFailure("resource_denied");
        var milliseconds = Number(call.Parameters, "minutes", 1, 1440) * 60000L;
        var limit = Number(call.Parameters, "limit", 1, 20);
        using var reader = new EventLogReader(new EventLogQuery(log, PathType.LogName,
            $"*[System[TimeCreated[timediff(@SystemTime) <= {milliseconds}]]]") { ReverseDirection = true });
        var events = new List<object>();
        for (var i = 0; i < limit; i++)
        {
            using var record = reader.ReadEvent(TimeSpan.FromSeconds(1)); if (record is null) break;
            // Raw event XML/message fields may contain unrelated user data; return diagnostic metadata only.
            events.Add(new { id = record.Id, level = record.Level, time = record.TimeCreated, provider = record.ProviderName });
        }
        return Result(call, "observed", "events_observed", events);
    }
    static async Task<PlatformResult> NetworkAsync(PlatformRequest call, PlatformScope scope)
    {
        var host = Text(call.Parameters, "host", max: 253);
        var port = Number(call.Parameters, "port", 1, 65535);
        if (Uri.CheckHostName(host) == UriHostNameType.Unknown || !scope.NetworkEndpoints.Contains(host + ":" + port, StringComparer.OrdinalIgnoreCase))
            throw new PlatformFailure("resource_denied");
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(3));
        var addresses = await Dns.GetHostAddressesAsync(host, deadline.Token);
        if (call.Operation == "network.resolve") return Result(call, "observed", "dns_observed", new { addresses = addresses.Take(16).Select(a => a.ToString()).ToArray() });
        using var client = new TcpClient(); await client.ConnectAsync(host, port, deadline.Token);
        return Result(call, "observed", "tcp_connected", new { connected = client.Connected });
    }
    internal static bool IsElevated()
    {
        using var identity = System.Security.Principal.WindowsIdentity.GetCurrent();
        return new System.Security.Principal.WindowsPrincipal(identity).IsInRole(System.Security.Principal.WindowsBuiltInRole.Administrator);
    }
}
