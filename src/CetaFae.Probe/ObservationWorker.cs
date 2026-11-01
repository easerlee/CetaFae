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
    internal const int MaxRequestCharacters = PluginCatalog.MaxFileBytes * 12 + 4096;
    static readonly SemaphoreSlim Gate = new(1, 1);
    internal static readonly JsonSerializerOptions Json = new()
    {
        MaxDepth = 16,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        Converters = { new HandleConverter(), new RectangleConverter() }
    };
    internal sealed record Request(long Window, bool IncludeImage, GuidePlugin Plugin, string? Intent = null, Rect? Selection = null,
        GuidePlugin? IdentityProvider = null);
    sealed record Reply(GuideObservation? Observation, string? Error);

    internal static string SerializeRequest(Request request) => JsonSerializer.Serialize(request, Json);

    [System.Diagnostics.CodeAnalysis.UnconditionalSuppressMessage("SingleFile", "IL3000",
        Justification = "An empty assembly location intentionally selects the bundled apphost instead of dotnet.")]
    public static async Task<GuideObservation> ReadAsync(IntPtr window, bool includeImage, GuidePlugin plugin, CancellationToken token,
        string? intent = null, Rect? selection = null, GuidePlugin? identityProvider = null)
    {
        await Gate.WaitAsync(token);
        try
        {
            var start = CreateStartInfo(Environment.ProcessPath!, typeof(ObservationWorker).Assembly.Location);
            var body = await ExchangeAsync(start, SerializeRequest(new Request(window.ToInt64(), includeImage, plugin, intent, selection, identityProvider)), token);
            var reply = JsonSerializer.Deserialize<Reply>(body, Json) ?? throw new InvalidDataException("worker_reply");
            if (reply.Error is { } error) throw new InvalidDataException(SafeError(error));
            var observation = reply.Observation ?? throw new InvalidDataException("worker_reply");
            if (observation.Window != window || observation.Candidates is null || observation.Candidates.Count > 128 ||
                observation.PluginId != plugin.Id || observation.RawView != plugin.UsesRawView ||
                observation.ScopeIntent != intent || (intent is not null || selection is not null) != (observation.Scope is not null) ||
                includeImage != (observation.Png is { Length: > 0 }) ||
                !GuideObservation.IsCurrent(window, observation.ProcessId, observation.Frame))
                throw new InvalidDataException("target_changed");
            ValidateScopeReply(observation, plugin, intent, selection, includeImage);
            if (observation.Identity is { } identity && (identityProvider?.Kind != "identity" || intent is not null || selection is not null ||
                !includeImage || !identity.IsValid(observation.ProcessId))) throw new InvalidDataException("worker_reply");
            ProbeLog.Log("guide_worker_complete", ("candidates", observation.Candidates.Count),
                ("imageBytes", observation.Png?.Length ?? 0));
            return observation;
        }
        finally { Gate.Release(); }
    }

    internal static ProcessStartInfo CreateStartInfo(string processPath, string assemblyPath)
    {
        // Single-file deployments have no assembly path and must not require an installed dotnet host.
        var start = new ProcessStartInfo(assemblyPath.Length == 0 ? processPath : "dotnet");
        start.Environment.Remove("CETAFAE_API_KEY");
        if (assemblyPath.Length > 0) start.ArgumentList.Add(assemblyPath);
        start.ArgumentList.Add("--observe-worker");
        return start;
    }

    internal static void ValidateScopeReply(GuideObservation observation, GuidePlugin plugin, string? intent,
        Rect? selection, bool includeImage)
    {
        if (selection is { } region)
        {
            if (observation.Scope?.HasSameAnchor(GuideScope.FromUserSelection(observation.Frame, region)) != true)
                throw new InvalidDataException("scope_unavailable");
        }
        else if (observation.Scope is { } scope && (scope.UserSelected || scope.Anchor is null || intent is null ||
                 !scope.HasSameAnchor(GuideScope.Resolve(observation.Frame, plugin.ScopeFor(intent), [scope.Anchor]))))
            throw new InvalidDataException("scope_unavailable");
        var expected = includeImage && (plugin.Kind == "general" || selection is not null)
            ? GuideObservation.Fingerprint(observation.Png ?? throw new InvalidDataException("worker_reply")) : null;
        if (observation.VisualHash != expected) throw new InvalidDataException("target_changed");
        var visual = expected is not null ? GuideVisual.Read(observation.Png!,
            observation.Scope?.Region ?? observation.Frame, observation.Visual?.CaretBounds) : null;
        if (observation.Visual != visual) throw new InvalidDataException("target_changed");
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
            var request = JsonSerializer.Deserialize<Request>(input, Json) ?? throw new InvalidDataException("worker_request_invalid");
            if (request.Plugin is null) throw new InvalidDataException("worker_request_invalid");
            if (request.IdentityProvider is not null && request.IdentityProvider.Kind != "identity")
                throw new InvalidDataException("worker_request_invalid");
            if (request.Intent is { } intent)
            {
                if (string.IsNullOrWhiteSpace(intent) || intent.Length > 300 || intent.Any(char.IsControl))
                    throw new InvalidDataException("scope_unavailable");
                GuidePrivacy.EnsureTextSafe(intent);
            }
            var observation = GuideObservation.Read(new IntPtr(request.Window), request.IncludeImage, request.Plugin, CancellationToken.None,
                request.Intent, request.Selection, request.IdentityProvider);
            reply = new Reply(observation, null);
        }
        catch (Exception ex) { reply = new Reply(null, ex is InvalidDataException ? SafeError(ex.Message) :
            ex is JsonException ? "worker_request_invalid" : "worker_failed"); }
        output.Write(JsonSerializer.Serialize(reply, Json));
    }

    internal static string SafeError(string error) => error is "target_not_foreground" or "target_unavailable" or
        "unsupported_target" or "elevated_target" or "target_changed" or "sensitive_content" or
        "privacy_unavailable" or "scope_unavailable" or "capture_target_unavailable" or "capture_dimensions" or
        "capture_unusable" or "worker_request_invalid" ? error : "worker_failed";

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
