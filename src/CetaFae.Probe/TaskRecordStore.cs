using System.IO;
using System.Security.Cryptography;
using System.Text.Json;
using CetaFae.Plugins;

namespace CetaFae.Probe;

internal sealed record TaskReceipt(string Id, DateTimeOffset At, string Operation, string[] Resources,
    string Fingerprint, string State, string? RecoveryId = null);

/// <summary>Write-ahead evidence only. Reading this archive never restores a grant or dispatches work.</summary>
internal sealed class TaskRecordStore(string root)
{
    internal string Root { get; } = PlatformScope.LocalPath(root);
    internal string Recovery => Path.Combine(Root, "recovery");
    const int MaxBytes = 8 * 1024 * 1024;

    FileStream Lock()
    {
        PlatformScope.CheckAncestors(Root);
        Directory.CreateDirectory(Root);
        var path = Path.Combine(Root, "records.lock"); PlatformScope.CheckAncestors(path);
        return new FileStream(path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
    }

    internal TaskReceipt[] Read()
    {
        using var lease = Lock();
        var records = ReadLocked();
        return records.Select(record =>
        {
            if (record.State is "pending" or "outcome_unknown" && record.RecoveryId is null &&
                record.Operation.StartsWith("file.", StringComparison.Ordinal) && File.Exists(Path.Combine(Recovery, record.Id + ".dat")))
            {
                var recovery = PlatformFiles.ReadRecovery(Recovery, record.Id);
                if (recovery.Registry is not null || !recovery.Files.All(f => record.Resources.Contains(f.Path, StringComparer.OrdinalIgnoreCase)))
                    throw new InvalidDataException("task_recovery_identity");
                record = record with { State = "outcome_unknown", RecoveryId = record.Id }; Write(record);
            }
            return record;
        }).ToArray();
    }

    TaskReceipt[] ReadLocked()
    {
        var files = Directory.EnumerateFiles(Root, "*.task").Take(129).ToArray();
        if (files.Length > 128 || files.Sum(f => new FileInfo(f).Length) > MaxBytes)
            throw new InvalidDataException("task_storage_limit");
        return files.Select(file =>
        {
            PlatformScope.CheckAncestors(file);
            if (new FileInfo(file).Length > 64 * 1024) throw new InvalidDataException("task_record_size");
            var plain = ProtectedData.Unprotect(File.ReadAllBytes(file), null, DataProtectionScope.CurrentUser);
            try
            {
                var record = JsonSerializer.Deserialize<TaskReceipt>(plain, PlatformRequest.Json) ?? throw new InvalidDataException("task_record");
                if (record.Id != Path.GetFileNameWithoutExtension(file) || !Guid.TryParseExact(record.Id, "N", out _) ||
                    record.Resources is null || record.Resources.Length > 4 || record.Resources.Any(string.IsNullOrWhiteSpace) ||
                    record.Fingerprint is not { Length: 64 } ||
                    record.State is not ("pending" or "verified" or "denied" or "failed" or "outcome_unknown" or "restored"))
                    throw new InvalidDataException("task_record");
                return record;
            }
            finally { CryptographicOperations.ZeroMemory(plain); }
        }).ToArray();
    }

    void Write(TaskReceipt record)
    {
        var plain = JsonSerializer.SerializeToUtf8Bytes(record, PlatformRequest.Json);
        byte[] bytes;
        try { bytes = ProtectedData.Protect(plain, null, DataProtectionScope.CurrentUser); }
        finally { CryptographicOperations.ZeroMemory(plain); }
        var file = Path.Combine(Root, record.Id + ".task");
        if (bytes.Length > 64 * 1024 || Directory.EnumerateFiles(Root, "*.task")
            .Where(path => !path.Equals(file, StringComparison.OrdinalIgnoreCase)).Sum(path => new FileInfo(path).Length) + bytes.Length > MaxBytes)
            throw new InvalidDataException("task_storage_limit");
        PlatformFiles.WriteAtomic(file, bytes);
    }

    internal TaskReceipt Begin(PlatformRequest call, string[] resources, string? resolving = null)
    {
        using var lease = Lock();
        if (!Guid.TryParseExact(call.Id, "N", out _)) throw new InvalidDataException("task_identity");
        var records = ReadLocked();
        foreach (var old in records.Where(r => r.RecoveryId is null && r.State is "verified" or "denied" or "failed" or "restored" &&
                     r.At < DateTimeOffset.UtcNow.AddDays(-30)))
            File.Delete(Path.Combine(Root, old.Id + ".task"));
        records = ReadLocked();
        if (records.Any(r => r.Id == call.Id)) throw new InvalidDataException("task_replay");
        if (records.Length >= 128) throw new InvalidDataException("task_storage_limit");
        if (resolving is not null && (call.Operation != "recovery.restore" ||
            !records.Any(r => r.Id == resolving && r.RecoveryId == call.Parameters.GetProperty("recoveryId").GetString())))
            throw new InvalidDataException("task_recovery_identity");
        if (records.Any(r => r.Id != resolving && r.State is "pending" or "outcome_unknown" && r.Resources.Intersect(resources, StringComparer.OrdinalIgnoreCase).Any()))
            throw new InvalidDataException("task_needs_inspection");
        var record = new TaskReceipt(call.Id, DateTimeOffset.UtcNow, call.Operation, resources,
            PlatformScope.HashText(JsonSerializer.Serialize(call, PlatformRequest.Json)), "pending",
            call.Operation == "recovery.restore" ? call.Parameters.GetProperty("recoveryId").GetString() : null);
        Write(record); return record;
    }

    internal void Finish(TaskReceipt receipt, PlatformResult result)
    {
        using var lease = Lock();
        var current = ReadLocked().Single(r => r.Id == receipt.Id);
        if (current.Fingerprint != receipt.Fingerprint || current.State != "pending" || result.Id != receipt.Id)
            throw new InvalidDataException("task_record_changed");
        Write(receipt with { State = result.Status is "verified" or "denied" or "failed" ? result.Status : "outcome_unknown",
            RecoveryId = result.RecoveryId ?? (result.Status == "outcome_unknown" ? receipt.RecoveryId : null) });
    }

    internal void Restored(TaskReceipt receipt)
    {
        using var lease = Lock();
        var current = ReadLocked().Single(r => r.Id == receipt.Id);
        Write(current with { State = "restored", RecoveryId = null });
    }

    internal void Reconciled(TaskReceipt receipt, string state)
    {
        using var lease = Lock();
        var current = ReadLocked().Single(r => r.Id == receipt.Id);
        if (current.State is not ("pending" or "outcome_unknown") || state is not ("verified" or "denied"))
            throw new InvalidDataException("task_record_changed");
        Write(current with { State = state, RecoveryId = state == "verified" && current.Operation != "recovery.restore" ? receipt.RecoveryId : null });
    }
}
