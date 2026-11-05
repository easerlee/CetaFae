using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using CetaFae.Plugins;

namespace CetaFae.Probe;

/// <summary>Bounded local transactions. The encrypted journal survives a worker exit; it is not a system snapshot.</summary>
internal static class PlatformFiles
{
    internal const int MaxFileBytes = 4 * 1024 * 1024;
    internal sealed record FileState(string Path, byte[]? Bytes, string ExpectedAfter);
    internal sealed record RegistryState(string Key, string Name, string? ValueJson, string ExpectedAfter);
    internal sealed record Recovery(FileState[] Files, RegistryState? Registry = null);

    internal static byte[] Read(string path)
    {
        using var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        if (file.Length > MaxFileBytes) throw new PlatformFailure("file_limit");
        var bytes = new byte[checked((int)file.Length)];
        file.ReadExactly(bytes);
        return bytes;
    }

    internal static string State(string path) => File.Exists(path) ? PlatformScope.Hash(Read(path)) : "missing";
    internal static void Expect(string actual, string expected)
    {
        if (!actual.Equals(expected, StringComparison.Ordinal)) throw new PlatformFailure("state_conflict");
    }

    internal static object Inspect(string path)
    {
        if (Directory.Exists(path)) return new { exists = true, kind = "directory" };
        if (!File.Exists(path)) return new { exists = false, sha256 = "missing" };
        var metadata = new FileInfo(path);
        if (metadata.Length > MaxFileBytes) return new { exists = true, kind = "file", length = metadata.Length,
            sha256 = (string?)null, hashUnavailable = "snapshot_limit" };
        var bytes = Read(path);
        return new { exists = true, kind = "file", length = bytes.Length, sha256 = PlatformScope.Hash(bytes) };
    }

    internal static object Scan(string root, PlatformScope scope)
    {
        var pending = new Queue<(string Path, int Depth)>(); pending.Enqueue((root, 0));
        var files = 0; var directories = 0; var skipped = 0; var visited = 0; long bytes = 0; var truncated = false;
        while (pending.TryDequeue(out var folder))
        {
            try
            {
                scope.Permit("directory.scan"); scope.FilePath(folder.Path, false);
                foreach (var entry in Directory.EnumerateFileSystemEntries(folder.Path))
                {
                    if (++visited > 2000) { truncated = true; pending.Clear(); break; }
                    try
                    {
                        scope.FilePath(entry, false);
                        if (Directory.Exists(entry))
                        {
                            directories++;
                            if (folder.Depth < 6) pending.Enqueue((entry, folder.Depth + 1)); else truncated = true;
                        }
                        else { files++; bytes = checked(bytes + new FileInfo(entry).Length); }
                    }
                    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or PlatformFailure) { skipped++; }
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { skipped++; }
        }
        return new { files, directories, logicalBytes = bytes, skipped, truncated, complete = !truncated && skipped == 0 };
    }

    internal static string SaveRecovery(string directory, Recovery recovery)
    {
        PlatformScope.CheckAncestors(directory);
        Directory.CreateDirectory(directory);
        var existing = Directory.EnumerateFiles(directory, "*.dat").Take(257).Select(path => new FileInfo(path).Length).ToArray();
        if (existing.Length > 256 || existing.Sum() > 64L * 1024 * 1024) throw new PlatformFailure("recovery_storage_limit");
        var id = Guid.NewGuid().ToString("N");
        var bytes = JsonSerializer.SerializeToUtf8Bytes(recovery, PlatformRequest.Json);
        var protectedBytes = ProtectedData.Protect(bytes, null, DataProtectionScope.CurrentUser);
        using var file = new FileStream(Path.Combine(directory, id + ".dat"), FileMode.CreateNew, FileAccess.Write, FileShare.None);
        file.Write(protectedBytes); file.Flush(flushToDisk: true);
        return id;
    }

    internal static Recovery ReadRecovery(string directory, string id)
    {
        if (!Guid.TryParseExact(id, "N", out _)) throw new PlatformFailure("invalid_recovery");
        var path = Path.Combine(directory, id + ".dat");
        PlatformScope.CheckAncestors(path);
        using var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        if (file.Length > 3L * MaxFileBytes) throw new PlatformFailure("recovery_limit");
        var bytes = new byte[checked((int)file.Length)]; file.ReadExactly(bytes);
        return JsonSerializer.Deserialize<Recovery>(ProtectedData.Unprotect(bytes, null, DataProtectionScope.CurrentUser), PlatformRequest.Json)
            ?? throw new PlatformFailure("invalid_recovery");
    }

    internal static void WriteAtomic(string path, byte[]? bytes)
    {
        PlatformScope.CheckAncestors(path);
        if (bytes is null) { File.Delete(path); return; }
        var temporary = Path.Combine(Path.GetDirectoryName(path)!, ".cetafae-" + Guid.NewGuid().ToString("N") + ".tmp");
        try
        {
            using (var file = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            { file.Write(bytes); file.Flush(flushToDisk: true); }
            PlatformScope.CheckAncestors(path);
            File.Move(temporary, path, overwrite: true);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }

    internal static PlatformResult Mutate(PlatformRequest call, PlatformScope scope, string journal)
    {
        var p = call.Parameters;
        var copy = call.Operation is "file.copy" or "file.move";
        var destination = scope.FilePath(PlatformEngine.Text(p, copy ? "destination" : "path"), write: true);
        if (Directory.Exists(destination)) throw new PlatformFailure("directory_mutation_unsupported");
        var before = File.Exists(destination) ? Read(destination) : null;
        Expect(before is null ? "missing" : PlatformScope.Hash(before), PlatformEngine.Text(p, "expectedHash"));
        byte[]? content = null;
        string? source = null;
        if (copy)
        {
            source = scope.FilePath(PlatformEngine.Text(p, "source"), write: call.Operation == "file.move");
            if (source.Equals(destination, StringComparison.OrdinalIgnoreCase)) throw new PlatformFailure("same_resource");
            content = Read(source);
            Expect(PlatformScope.Hash(content), PlatformEngine.Text(p, "sourceHash"));
        }
        else if (call.Operation == "file.write")
        {
            var text = PlatformEngine.Text(p, "text", allowEmpty: true, max: 24000);
            GuidePrivacy.EnsureTextSafe(text);
            content = new UTF8Encoding(false, true).GetBytes(text);
        }
        else if (before is null) throw new PlatformFailure("resource_missing");
        var afterHash = content is null ? "missing" : PlatformScope.Hash(content);
        var states = new List<FileState> { new(destination, before, afterHash) };
        if (call.Operation == "file.move") states.Add(new(source!, content, "missing"));
        var recovery = SaveRecovery(journal, new(states.ToArray()));
        try
        {
            scope.Permit(call.Operation);
            Expect(State(destination), before is null ? "missing" : PlatformScope.Hash(before));
            if (source is not null) Expect(State(source), PlatformScope.Hash(content!));
            WriteAtomic(destination, content);
            if (call.Operation == "file.move")
            {
                scope.FilePath(source!, write: true);
                Expect(State(source!), PlatformScope.Hash(content!));
                File.Delete(source!);
            }
            foreach (var state in states) Expect(State(state.Path), state.ExpectedAfter);
            return PlatformEngine.Result(call, "verified", "postcondition_verified", new { sha256 = afterHash }, recovery);
        }
        catch { return PlatformEngine.Result(call, "outcome_unknown", "inspect_recovery_before_retry", recovery: recovery); }
    }

    internal static PlatformResult Restore(PlatformRequest call, PlatformScope scope, string journal)
    {
        var id = PlatformEngine.Text(call.Parameters, "recoveryId");
        var recovery = ReadRecovery(journal, id);
        if (recovery.Registry is { } registry) return PlatformEngine.RestoreRegistry(call, scope, registry);
        foreach (var state in recovery.Files)
        {
            scope.FilePath(state.Path, write: true);
            Expect(State(state.Path), state.ExpectedAfter);
        }
        try
        {
            foreach (var state in recovery.Files)
            {
                scope.Permit(call.Operation);
                scope.FilePath(state.Path, write: true);
                Expect(State(state.Path), state.ExpectedAfter);
                WriteAtomic(state.Path, state.Bytes);
            }
            foreach (var state in recovery.Files)
                Expect(State(state.Path), state.Bytes is null ? "missing" : PlatformScope.Hash(state.Bytes));
            return PlatformEngine.Result(call, "verified", "restore_verified");
        }
        catch { return PlatformEngine.Result(call, "outcome_unknown", "restore_incomplete", recovery: id); }
    }
}
