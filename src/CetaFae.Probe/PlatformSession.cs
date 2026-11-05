using System.Diagnostics;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using CetaFae.Plugins;

namespace CetaFae.Probe;

/// <summary>Host-owned resource lease, bounded workers and at-most-once admission within this session.</summary>
internal sealed class PlatformSession
{
    readonly PlatformScope _scope;
    readonly string _journal;
    readonly SemaphoreSlim _gate = new(1, 1);
    // ponytail: receipts are session-local; durable replay protection belongs with a future product execution journal.
    readonly Dictionary<string, (string Fingerprint, PlatformResult Result)> _receipts = new(StringComparer.Ordinal);
    readonly Func<PlatformRequest, CancellationToken, Task<PlatformResult>>? _exchange;

    internal PlatformSession(PlatformScope scope, string journal,
        Func<PlatformRequest, CancellationToken, Task<PlatformResult>>? exchange = null)
    {
        // Deep snapshot: later mutation of a UI selection cannot silently enlarge an in-flight grant.
        _scope = JsonSerializer.Deserialize<PlatformScope>(JsonSerializer.Serialize(scope, PlatformRequest.Json), PlatformRequest.Json)!;
        _journal = PlatformScope.LocalPath(journal); PlatformScope.CheckAncestors(_journal);
        _exchange = exchange;
    }

    internal async Task<PlatformResult> ExecuteAsync(PlatformRequest call, CancellationToken token = default, bool elevated = false)
    {
        try { PlatformEngine.Validate(call); _scope.Permit(call.Operation); }
        catch (PlatformFailure ex) { return PlatformEngine.Result(call, "denied", ex.Message); }
        catch (Exception ex) when (ex is JsonException or InvalidDataException or ArgumentException)
        { return PlatformEngine.Result(call, "denied", "invalid_request"); }
        var fingerprint = PlatformScope.HashText(JsonSerializer.Serialize(call, PlatformRequest.Json) + (elevated ? "elevated" : "standard"));
        await _gate.WaitAsync(token);
        try
        {
            if (_receipts.TryGetValue(call.Id, out var old)) return old.Fingerprint == fingerprint ? old.Result with { Replayed = true } :
                PlatformEngine.Result(call, "denied", "request_id_conflict");
            if (_receipts.Count >= 128) return PlatformEngine.Result(call, "denied", "session_limit");
            token.ThrowIfCancellationRequested();
            _scope.Permit(call.Operation);
            if (elevated && (!_scope.AllowElevation || !PlatformWorker.CanElevate(call.Operation)))
                return PlatformEngine.Result(call, "denied", "elevation_denied");
            PlatformResult result;
            try
            {
                result = _exchange is not null ? await _exchange(call, token) :
                    await PlatformWorker.InvokeAsync(new(call, _scope, _journal), elevated, token);
                if (result.Id != call.Id || result.Status is not ("observed" or "verified" or "dispatched" or "denied" or "failed" or "outcome_unknown") ||
                    JsonSerializer.SerializeToUtf8Bytes(result, PlatformRequest.Json).Length > 96 * 1024)
                    throw new InvalidDataException("platform_reply");
            }
            catch (Exception ex) when (ex is not OutOfMemoryException && ex is not StackOverflowException)
            {
                // A killed worker may have committed an effect. Never silently retry this ID, even after cancellation.
                result = PlatformEngine.Result(call, PlatformEngine.IsMutation(call.Operation) ? "outcome_unknown" : "failed",
                    token.IsCancellationRequested ? "cancelled_inspect_before_retry" : "worker_unavailable");
            }
            _receipts.Add(call.Id, (fingerprint, result)); return result;
        }
        catch (PlatformFailure ex) { return PlatformEngine.Result(call, "denied", ex.Message); }
        finally { _gate.Release(); }
    }
}

internal static class PlatformWorker
{
    internal sealed record Envelope(PlatformRequest Request, PlatformScope Scope, string Journal);
    const int MaxEnvelope = 160 * 1024;
    internal static bool CanElevate(string operation) => operation.StartsWith("file.", StringComparison.Ordinal) ||
        operation.StartsWith("registry.", StringComparison.Ordinal) || operation.StartsWith("service.", StringComparison.Ordinal) ||
        operation is "recovery.restore" or "system.info";

    [System.Diagnostics.CodeAnalysis.UnconditionalSuppressMessage("SingleFile", "IL3000", Justification = "Empty location selects the bundled apphost.")]
    internal static ProcessStartInfo StartInfo()
    {
        var start = PluginWorker.StartInfo(Environment.ProcessPath!, typeof(Program).Assembly.Location);
        start.ArgumentList.RemoveAt(start.ArgumentList.Count - 1); start.ArgumentList.Add("--platform-worker"); return start;
    }
    internal static async Task<PlatformResult> InvokeAsync(Envelope envelope, bool elevated, CancellationToken token)
    {
        var input = JsonSerializer.Serialize(envelope, PlatformRequest.Json);
        if (Encoding.UTF8.GetByteCount(input) > MaxEnvelope) throw new InvalidDataException("platform_request_limit");
        if (elevated) return await ElevatedAsync(envelope, input, token);
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
        deadline.CancelAfter(TimeSpan.FromSeconds(12));
        var reply = await ObservationWorker.ExchangeAsync(StartInfo(), input, deadline.Token);
        if (Encoding.UTF8.GetByteCount(reply) > 96 * 1024) throw new InvalidDataException("platform_reply_limit");
        return JsonSerializer.Deserialize<PlatformResult>(reply, PlatformRequest.Json) ?? throw new InvalidDataException("platform_reply");
    }
    internal static void Run()
    {
        using var watchdog = new Timer(_ => Environment.Exit(3), null, TimeSpan.FromSeconds(15), Timeout.InfiniteTimeSpan);
        using var output = new StreamWriter(Console.OpenStandardOutput(), new UTF8Encoding(false));
        using var input = new StreamReader(Console.OpenStandardInput(), new UTF8Encoding(false, true));
        try
        {
            var body = ObservationWorker.ReadBoundedAsync(input, MaxEnvelope, default).GetAwaiter().GetResult();
            var envelope = JsonSerializer.Deserialize<Envelope>(body, PlatformRequest.Json) ?? throw new InvalidDataException();
            var result = PlatformEngine.ExecuteAsync(envelope.Request, envelope.Scope, PlatformScope.LocalPath(envelope.Journal)).GetAwaiter().GetResult();
            output.Write(JsonSerializer.Serialize(result, PlatformRequest.Json));
        }
        catch { Environment.ExitCode = 2; }
    }

    [System.Diagnostics.CodeAnalysis.UnconditionalSuppressMessage("SingleFile", "IL3000", Justification = "Empty location selects the bundled apphost.")]
    static async Task<PlatformResult> ElevatedAsync(Envelope envelope, string input, CancellationToken token)
    {
        if (!envelope.Scope.AllowElevation || !CanElevate(envelope.Request.Operation)) throw new PlatformFailure("elevation_denied");
        // runas is requested only explicitly; access_denied never automatically retries with more authority.
        var directory = Path.Combine(envelope.Journal, "elevation-" + Guid.NewGuid().ToString("N"));
        PlatformScope.CheckAncestors(directory); Directory.CreateDirectory(directory);
        var requestPath = Path.Combine(directory, "request.dat"); var replyPath = Path.Combine(directory, "reply.dat");
        var bytes = ProtectedData.Protect(Encoding.UTF8.GetBytes(input), null, DataProtectionScope.CurrentUser);
        using (var file = new FileStream(requestPath, FileMode.CreateNew, FileAccess.Write, FileShare.None))
        { file.Write(bytes); file.Flush(true); }
        var assembly = typeof(Program).Assembly.Location;
        var start = new ProcessStartInfo(assembly.Length == 0 ? Environment.ProcessPath! : Path.Combine(System.Runtime.InteropServices.RuntimeEnvironment.GetRuntimeDirectory(), "..", "..", "..", "dotnet.exe"))
            { UseShellExecute = true, Verb = "runas", WindowStyle = ProcessWindowStyle.Hidden };
        if (assembly.Length > 0) start.ArgumentList.Add(assembly);
        start.ArgumentList.Add("--platform-elevated-worker"); start.ArgumentList.Add(requestPath); start.ArgumentList.Add(PlatformScope.Hash(bytes));
        try
        {
            using var process = Process.Start(start) ?? throw new InvalidDataException("elevation_start");
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token); deadline.CancelAfter(TimeSpan.FromSeconds(30));
            // Parent cancellation cannot reliably terminate a higher-integrity child. Its own watchdog and scope expiry bound it.
            await process.WaitForExitAsync(deadline.Token);
            var reply = PlatformFiles.Read(replyPath);
            if (reply.Length > 128 * 1024) throw new InvalidDataException("platform_reply_limit");
            return JsonSerializer.Deserialize<PlatformResult>(ProtectedData.Unprotect(reply, null, DataProtectionScope.CurrentUser), PlatformRequest.Json)
                ?? throw new InvalidDataException("platform_reply");
        }
        catch (System.ComponentModel.Win32Exception ex) when (ex.NativeErrorCode == 1223)
        { return PlatformEngine.Result(envelope.Request, "denied", "elevation_declined"); }
        finally
        {
            // Keep an unknown helper's request until its watchdog has ended; do not race-delete an active elevated operation.
            if (File.Exists(replyPath)) { File.Delete(replyPath); File.Delete(requestPath); Directory.Delete(directory); }
        }
    }
    internal static void RunElevated(string path, string expectedHash)
    {
        using var watchdog = new Timer(_ => Environment.Exit(3), null, TimeSpan.FromSeconds(15), Timeout.InfiniteTimeSpan);
        try
        {
            if (!PlatformEngine.IsElevated()) throw new PlatformFailure("elevation_required");
            path = PlatformScope.LocalPath(path); PlatformScope.CheckAncestors(path);
            var encrypted = PlatformFiles.Read(path); PlatformFiles.Expect(PlatformScope.Hash(encrypted), expectedHash);
            var body = ProtectedData.Unprotect(encrypted, null, DataProtectionScope.CurrentUser);
            if (body.Length > MaxEnvelope) throw new InvalidDataException();
            var envelope = JsonSerializer.Deserialize<Envelope>(body, PlatformRequest.Json) ?? throw new InvalidDataException();
            if (!envelope.Scope.AllowElevation || !CanElevate(envelope.Request.Operation)) throw new PlatformFailure("elevation_denied");
            var result = PlatformEngine.ExecuteAsync(envelope.Request, envelope.Scope, envelope.Journal).GetAwaiter().GetResult();
            var reply = ProtectedData.Protect(JsonSerializer.SerializeToUtf8Bytes(result, PlatformRequest.Json), null, DataProtectionScope.CurrentUser);
            using var file = new FileStream(Path.Combine(Path.GetDirectoryName(path)!, "reply.dat"), FileMode.CreateNew, FileAccess.Write, FileShare.None);
            file.Write(reply); file.Flush(true);
        }
        catch { Environment.ExitCode = 2; }
    }
}
