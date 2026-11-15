using System.Buffers.Binary;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Text.Json;
using CetaFae.Plugins;

namespace CetaFae.Probe;

/// <summary>Bounded process reuse; every invocation still owns a fresh interpreter context.</summary>
internal static class PluginWorker
{
    sealed record Request(PluginProgram Program, string Operation, JsonElement Input);
    static readonly JsonSerializerOptions Json = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        UnmappedMemberHandling = System.Text.Json.Serialization.JsonUnmappedMemberHandling.Disallow, MaxDepth = 20 };
    static readonly UTF8Encoding Utf8 = new(false, true);
    internal const int MaxRequestBytes = PluginExecutor.MaxInputBytes + PluginProgram.MaxSourceBytes * 6;
    internal const int MaxReplyBytes = PluginExecutor.MaxOutputBytes + 256;
    internal const int MaxCalls = 32;
    internal static readonly TimeSpan IdleTimeout = TimeSpan.FromSeconds(15);
    static readonly SemaphoreSlim Gate = new(1, 1);
    static Worker? _idle;
    static Timer? _idleTimer;
    static long _lease;

    internal static Task<string> InvokeAsync(PluginProgram program, string operation, object input,
        CancellationToken token, Action<int>? started = null)
    {
        var queued = Stopwatch.GetTimestamp();
        return Task.Run(async () =>
        {
            await Gate.WaitAsync(token);
            try
            {
                var queueMs = Stopwatch.GetElapsedTime(queued).TotalMilliseconds;
                return await InvokeCoreAsync(program, operation, input, token, started, queueMs);
            }
            finally { Gate.Release(); }
        }, token);
    }

    static async Task<string> InvokeCoreAsync(PluginProgram program, string operation, object input,
        CancellationToken token, Action<int>? started, double queueMs)
    {
        token.ThrowIfCancellationRequested();
        var request = JsonSerializer.Serialize(new Request(program, operation, JsonSerializer.SerializeToElement(input, Json)), Json);
        if (Utf8.GetByteCount(request) > MaxRequestBytes) throw new InvalidDataException("plugin_input_size");
        var worker = _idle;
        _idle = null;
        _idleTimer?.Dispose(); _idleTimer = null; _lease++;
        if (worker is not null && (!worker.Matches(program) || !worker.Reusable))
        { await worker.DisposeAsync(); worker = null; }
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
        deadline.CancelAfter(TimeSpan.FromSeconds(4));
        var clock = Stopwatch.StartNew();
        var reused = worker is not null;
        try
        {
            deadline.Token.ThrowIfCancellationRequested();
            worker ??= Worker.Start(program);
            started?.Invoke(worker.Process.Id);
            await WriteFrameAsync(worker.Process.StandardInput.BaseStream, request, MaxRequestBytes, deadline.Token);
            var reply = await ReadFrameAsync(worker.Process.StandardOutput.BaseStream, MaxReplyBytes, deadline.Token)
                ?? throw new InvalidDataException("plugin_execution_failed");
            if (worker.Errors.IsCompleted && (await worker.Errors).Length != 0)
                throw new InvalidDataException("plugin_execution_failed");
            using var json = JsonDocument.Parse(reply, new JsonDocumentOptions { MaxDepth = 18 });
            if (json.RootElement.ValueKind != JsonValueKind.Object || json.RootElement.EnumerateObject().Count() != 2 ||
                !json.RootElement.TryGetProperty("ok", out var ok) || ok.ValueKind != JsonValueKind.True ||
                !json.RootElement.TryGetProperty("result", out var result) || result.ValueKind != JsonValueKind.Object)
                throw new InvalidDataException("plugin_execution_failed");
            var value = result.GetRawText();
            deadline.Token.ThrowIfCancellationRequested();
            ProbeLog.Log("guide_plugin_timing", ("reused", reused), ("queueMs", Math.Round(queueMs, 2)),
                ("callMs", Math.Round(clock.Elapsed.TotalMilliseconds, 2)));
            worker.Calls++;
            if (worker.Reusable)
            {
                _idle = worker;
                var lease = ++_lease;
                _idleTimer = new Timer(_ => _ = ExpireAsync(lease), null, IdleTimeout, Timeout.InfiniteTimeSpan);
                worker = null;
            }
            return value;
        }
        catch (Exception) when (token.IsCancellationRequested) { throw new OperationCanceledException(token); }
        catch (OperationCanceledException) { throw new InvalidDataException("plugin_timeout"); }
        catch (Exception ex) when (ex is InvalidDataException or IOException or JsonException or DecoderFallbackException or System.ComponentModel.Win32Exception)
        {
            if (ex is InvalidDataException data && data.Message.StartsWith("plugin_", StringComparison.Ordinal)) throw;
            throw new InvalidDataException("plugin_execution_failed");
        }
        finally { if (worker is not null) await worker.DisposeAsync(); }
    }

    static async Task ExpireAsync(long lease)
    {
        await Gate.WaitAsync();
        try { if (lease == _lease) await ClearIdleCoreAsync(); }
        finally { Gate.Release(); }
    }

    internal static async Task ClearIdleAsync()
    {
        await Gate.WaitAsync();
        try { await ClearIdleCoreAsync(); }
        finally { Gate.Release(); }
    }

    static async Task ClearIdleCoreAsync()
    {
        _idleTimer?.Dispose(); _idleTimer = null; _lease++;
        var worker = _idle; _idle = null;
        if (worker is not null) await worker.DisposeAsync();
    }

    sealed class Worker(PluginProgram program, Process process)
    {
        internal Process Process { get; } = process;
        internal Task<string> Errors { get; } = ObservationWorker.ReadBoundedAsync(process.StandardError, 4096, default);
        readonly long _created = Stopwatch.GetTimestamp();
        internal int Calls;
        internal bool Matches(PluginProgram other) => program.ApiVersion == other.ApiVersion && program.Source == other.Source;
        internal bool Reusable => Calls < MaxCalls && Stopwatch.GetElapsedTime(_created) < TimeSpan.FromSeconds(60) &&
            !Process.HasExited && !Errors.IsCompleted;

        [System.Diagnostics.CodeAnalysis.UnconditionalSuppressMessage("SingleFile", "IL3000",
            Justification = "An empty assembly location selects the self-contained apphost.")]
        internal static Worker Start(PluginProgram program)
        {
            var process = new Process { StartInfo = StartInfo(Environment.ProcessPath!, typeof(Program).Assembly.Location, session: true) };
            try
            {
                if (!process.Start()) throw new InvalidDataException("plugin_execution_failed");
                return new Worker(program, process);
            }
            catch { process.Dispose(); throw; }
        }

        internal async Task DisposeAsync()
        {
            try
            {
                try { if (!Process.HasExited) Process.Kill(); }
                catch (InvalidOperationException) { }
                catch (System.ComponentModel.Win32Exception) { }
                await Process.WaitForExitAsync();
                try { await Errors; } catch { }
            }
            finally { Process.Dispose(); }
        }
    }

    internal static async Task WriteFrameAsync(Stream stream, string text, int limit, CancellationToken token = default)
    {
        var count = Utf8.GetByteCount(text);
        if (count is < 1 || count > limit) throw new InvalidDataException("plugin_frame_size");
        var header = new byte[4];
        BinaryPrimitives.WriteInt32BigEndian(header, count);
        await stream.WriteAsync(header, token);
        await stream.WriteAsync(Utf8.GetBytes(text), token);
        await stream.FlushAsync(token);
    }

    internal static async Task<string?> ReadFrameAsync(Stream stream, int limit, CancellationToken token = default)
    {
        var header = new byte[4];
        if (await stream.ReadAsync(header.AsMemory(0, 1), token) == 0) return null;
        await stream.ReadExactlyAsync(header.AsMemory(1), token);
        var count = BinaryPrimitives.ReadInt32BigEndian(header);
        if (count is < 1 || count > limit) throw new InvalidDataException("plugin_frame_size");
        var bytes = new byte[count];
        await stream.ReadExactlyAsync(bytes, token);
        return Utf8.GetString(bytes);
    }

    internal static ProcessStartInfo StartInfo(string processPath, string assemblyPath, bool session = false)
    {
        var start = new ProcessStartInfo(assemblyPath.Length == 0 ? processPath : "dotnet")
        {
            StandardInputEncoding = new UTF8Encoding(false), StandardOutputEncoding = new UTF8Encoding(false),
            StandardErrorEncoding = new UTF8Encoding(false), UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true
        };
        start.Environment.Clear();
        foreach (var key in new[] { "SystemRoot", "WINDIR", "PATH", "TEMP", "TMP", "DOTNET_ROOT" })
            if (Environment.GetEnvironmentVariable(key) is { } value) start.Environment[key] = value;
        if (assemblyPath.Length > 0) start.ArgumentList.Add(assemblyPath);
        start.ArgumentList.Add(session ? "--plugin-session" : "--plugin-worker");
        return start;
    }

    internal static void Run(bool session = false)
    {
        using var watchdog = new Timer(_ => Environment.Exit(3), null, TimeSpan.FromSeconds(session ? 20 : 5), Timeout.InfiniteTimeSpan);
        using var output = Console.OpenStandardOutput();
        using var input = Console.OpenStandardInput();
        using var reader = new StreamReader(input, Utf8, detectEncodingFromByteOrderMarks: false);
        Console.SetOut(TextWriter.Null);
        try
        {
            using var limits = PluginProcessLimits.Attach();
            PluginProgram? bound = null;
            for (var call = 0; call < (session ? MaxCalls : 1); call++)
            {
                var text = session ? ReadFrameAsync(input, MaxRequestBytes).GetAwaiter().GetResult() :
                    ObservationWorker.ReadBoundedAsync(reader, MaxRequestBytes, default).GetAwaiter().GetResult();
                if (text is null) return;
                watchdog.Change(TimeSpan.FromSeconds(5), Timeout.InfiniteTimeSpan);
                var ok = false;
                string reply;
                try
                {
                    var request = JsonSerializer.Deserialize<Request>(text, Json) ?? throw new InvalidDataException("plugin_request");
                    if (bound is not null && (bound.ApiVersion != request.Program.ApiVersion || bound.Source != request.Program.Source))
                        throw new InvalidDataException("plugin_program_changed");
                    bound ??= request.Program;
                    var result = PluginExecutor.Invoke(request.Program, request.Operation, request.Input);
                    reply = "{\"ok\":true,\"result\":" + result + "}";
                    ok = true;
                }
                catch (Exception ex) when (ex is not OutOfMemoryException && ex is not StackOverflowException)
                { reply = "{\"ok\":false}"; }
                if (session) WriteFrameAsync(output, reply, MaxReplyBytes).GetAwaiter().GetResult();
                else { var bytes = Utf8.GetBytes(reply); output.Write(bytes); output.Flush(); }
                if (!ok) return;
                watchdog.Change(TimeSpan.FromSeconds(20), Timeout.InfiniteTimeSpan);
            }
        }
        catch (Exception ex) when (ex is not OutOfMemoryException && ex is not StackOverflowException)
        {
            // Broken or oversized frames end the session without logging untrusted bytes.
            Environment.ExitCode = 2;
        }
    }
}
