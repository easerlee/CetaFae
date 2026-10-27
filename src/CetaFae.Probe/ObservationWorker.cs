using System.Diagnostics;
using System.IO;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Windows;
using CetaFae.Plugins;

[assembly: InternalsVisibleTo("CetaFae.Checks")]

namespace CetaFae.Probe;

public static class ObservationWorker
{
    const int MaxReplyCharacters = 12 * 1024 * 1024;
    // JSON escaping can expand each byte of a validated data package into six characters.
    internal const int MaxRequestCharacters = PluginCatalog.MaxFileBytes * 6 + 4096;
    static readonly SemaphoreSlim Gate = new(1, 1);
    internal static readonly JsonSerializerOptions Json = new()
    {
        MaxDepth = 16,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        Converters = { new HandleConverter(), new RectangleConverter() }
    };
    sealed record Request(long Window, bool IncludeImage, GuidePlugin Plugin);
    sealed record Reply(GuideObservation? Observation, string? Error);

    public static async Task<GuideObservation> ReadAsync(IntPtr window, bool includeImage, GuidePlugin plugin, CancellationToken token)
    {
        await Gate.WaitAsync(token);
        try
        {
            var start = new ProcessStartInfo("dotnet");
            start.Environment.Remove("CETAFAE_API_KEY");
            start.ArgumentList.Add(typeof(ObservationWorker).Assembly.Location);
            start.ArgumentList.Add("--observe-worker");
            var body = await ExchangeAsync(start, JsonSerializer.Serialize(new Request(window.ToInt64(), includeImage, plugin)), token);
            var reply = JsonSerializer.Deserialize<Reply>(body, Json) ?? throw new InvalidDataException("worker_reply");
            if (reply.Error is { } error) throw new InvalidDataException(SafeError(error));
            var observation = reply.Observation ?? throw new InvalidDataException("worker_reply");
            if (observation.Window != window || observation.Candidates is null || observation.Candidates.Count > 128 ||
                observation.PluginId != plugin.Id || observation.RawView != plugin.UsesRawView ||
                includeImage != (observation.Png is { Length: > 0 }) ||
                !GuideObservation.IsCurrent(window, observation.ProcessId, observation.Frame))
                throw new InvalidDataException("target_changed");
            ProbeLog.Log("guide_worker_complete", ("candidates", observation.Candidates.Count),
                ("imageBytes", observation.Png?.Length ?? 0));
            return observation;
        }
        finally { Gate.Release(); }
    }

    internal static async Task<string> ExchangeAsync(ProcessStartInfo start, string input, CancellationToken token,
        Action<int>? started = null)
    {
        token.ThrowIfCancellationRequested();
        start.UseShellExecute = false;
        start.CreateNoWindow = true;
        start.RedirectStandardInput = true;
        start.RedirectStandardOutput = true;
        start.RedirectStandardError = true;
        using var process = new Process { StartInfo = start };
        process.Start();
        using var io = CancellationTokenSource.CreateLinkedTokenSource(token);
        // Cancellation kills only the child created here, including a stuck native provider call.
        using var stop = io.Token.Register(() => StopOwnedProcess(process));
        async Task<string> ReadPipeAsync(TextReader reader, int limit)
        {
            try { return await ReadBoundedAsync(reader, limit, io.Token); }
            catch { io.Cancel(); throw; }
        }
        var output = ReadPipeAsync(process.StandardOutput, MaxReplyCharacters);
        var errors = ReadPipeAsync(process.StandardError, 4096);
        try
        {
            started?.Invoke(process.Id);
            await process.StandardInput.WriteLineAsync(input.AsMemory(), token);
            process.StandardInput.Close();
            await Task.WhenAll(output, errors);
            await process.WaitForExitAsync(token);
            var reply = await output;
            token.ThrowIfCancellationRequested();
            if (process.ExitCode != 0) throw new InvalidDataException("worker_failed");
            return reply;
        }
        catch (Exception) when (token.IsCancellationRequested)
        {
            throw new OperationCanceledException(token);
        }
        finally
        {
            StopOwnedProcess(process);
            // Reap before releasing the single-reader gate; abandoning an await is insufficient.
            await process.WaitForExitAsync();
            // Drain both completed/faulted readers so no detached pipe exception survives cleanup.
            try { await Task.WhenAll(output, errors); } catch { }
        }
    }

    static void StopOwnedProcess(Process process)
    {
        try { if (!process.HasExited) process.Kill(); }
        catch (InvalidOperationException) { }
        catch (System.ComponentModel.Win32Exception)
        {
            // Keep the admission gate until exit even if immediate termination is refused.
            // The worker watchdog provides a second bound; never start overlapping native reads.
            ProbeLog.Log("guide_worker_stop_refused");
        }
    }

    internal static async Task<string> ReadBoundedAsync(TextReader reader, int limit, CancellationToken token)
    {
        var text = new StringBuilder();
        var buffer = new char[4096];
        int count;
        while ((count = await reader.ReadAsync(buffer.AsMemory(), token)) != 0)
        {
            if (text.Length + count > limit) throw new InvalidDataException("worker_reply_too_large");
            text.Append(buffer, 0, count);
        }
        return text.ToString();
    }

    public static void Run()
    {
        // Also bound an orphan if the parent crashes while UIA/PrintWindow is stuck.
        using var watchdog = new Timer(_ => Environment.Exit(3), null, TimeSpan.FromSeconds(15), Timeout.InfiniteTimeSpan);
        using var output = new StreamWriter(Console.OpenStandardOutput(), new UTF8Encoding(false));
        Console.SetOut(TextWriter.Null);
        Reply reply;
        try
        {
            var input = ReadBoundedAsync(Console.In, MaxRequestCharacters, CancellationToken.None).GetAwaiter().GetResult();
            var request = JsonSerializer.Deserialize<Request>(input, Json) ?? throw new InvalidDataException("worker_request");
            if (request.Plugin is null) throw new InvalidDataException("worker_request");
            var observation = GuideObservation.Read(new IntPtr(request.Window), request.IncludeImage, request.Plugin, CancellationToken.None);
            reply = new Reply(observation, null);
        }
        catch (Exception ex) { reply = new Reply(null, ex is InvalidDataException ? SafeError(ex.Message) : "worker_failed"); }
        output.Write(JsonSerializer.Serialize(reply, Json));
    }

    static string SafeError(string error) => error is "target_not_foreground" or "target_unavailable" or
        "unsupported_target" or "elevated_target" or "target_changed" ? error : "worker_failed";

    sealed class HandleConverter : JsonConverter<IntPtr>
    {
        public override IntPtr Read(ref Utf8JsonReader reader, Type type, JsonSerializerOptions options) => new(reader.GetInt64());
        public override void Write(Utf8JsonWriter writer, IntPtr value, JsonSerializerOptions options) => writer.WriteNumberValue(value.ToInt64());
    }

    sealed class RectangleConverter : JsonConverter<Rect>
    {
        public override Rect Read(ref Utf8JsonReader reader, Type type, JsonSerializerOptions options)
        {
            var values = JsonSerializer.Deserialize<double[]>(ref reader, options);
            if (values is not { Length: 4 } || values.Any(v => !double.IsFinite(v)) || values[2] < 0 || values[3] < 0)
                throw new JsonException("worker_rectangle");
            return new Rect(values[0], values[1], values[2], values[3]);
        }
        public override void Write(Utf8JsonWriter writer, Rect value, JsonSerializerOptions options) =>
            JsonSerializer.Serialize(writer, new[] { value.X, value.Y, value.Width, value.Height }, options);
    }
}
