using System.Diagnostics;
using System.IO;
using System.Text;
using System.Text.Json;
using CetaFae.Plugins;

namespace CetaFae.Probe;

/// <summary>One JSON-only invocation per owned child; cancellation never leaves a script running.</summary>
internal static class PluginWorker
{
    sealed record Request(PluginProgram Program, string Operation, JsonElement Input);
    static readonly JsonSerializerOptions Json = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        UnmappedMemberHandling = System.Text.Json.Serialization.JsonUnmappedMemberHandling.Disallow, MaxDepth = 20 };
    static readonly SemaphoreSlim Gate = new(1, 1);

    internal static Task<string> InvokeAsync(PluginProgram program, string operation, object input, CancellationToken token) =>
        Task.Run(async () =>
        {
            await Gate.WaitAsync(token);
            try { return await InvokeCoreAsync(program, operation, input, token); }
            finally { Gate.Release(); }
        }, token);

    [System.Diagnostics.CodeAnalysis.UnconditionalSuppressMessage("SingleFile", "IL3000",
        Justification = "An empty assembly location selects the self-contained apphost.")]
    static async Task<string> InvokeCoreAsync(PluginProgram program, string operation, object input, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        var request = JsonSerializer.Serialize(new Request(program, operation, JsonSerializer.SerializeToElement(input, Json)), Json);
        if (Encoding.UTF8.GetByteCount(request) > PluginExecutor.MaxInputBytes + PluginProgram.MaxSourceBytes * 6)
            throw new InvalidDataException("plugin_input_size");
        var start = StartInfo(Environment.ProcessPath!, typeof(Program).Assembly.Location);
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
        deadline.CancelAfter(TimeSpan.FromSeconds(4));
        try
        {
            var reply = await ObservationWorker.ExchangeAsync(start, request, deadline.Token);
            if (Encoding.UTF8.GetByteCount(reply) > PluginExecutor.MaxOutputBytes + 256)
                throw new InvalidDataException("plugin_output_size");
            using var json = JsonDocument.Parse(reply, new JsonDocumentOptions { MaxDepth = 18 });
            if (json.RootElement.ValueKind != JsonValueKind.Object || json.RootElement.EnumerateObject().Count() != 2 ||
                !json.RootElement.TryGetProperty("ok", out var ok) || ok.ValueKind != JsonValueKind.True ||
                !json.RootElement.TryGetProperty("result", out var result) || result.ValueKind != JsonValueKind.Object)
                throw new InvalidDataException("plugin_execution_failed");
            return result.GetRawText();
        }
        catch (Exception) when (token.IsCancellationRequested) { throw new OperationCanceledException(token); }
        catch (OperationCanceledException) { throw new InvalidDataException("plugin_timeout"); }
        catch (Exception ex) when (ex is InvalidDataException or IOException or JsonException or System.ComponentModel.Win32Exception)
        {
            if (ex is InvalidDataException data && data.Message.StartsWith("plugin_", StringComparison.Ordinal)) throw;
            throw new InvalidDataException("plugin_execution_failed");
        }
    }

    [System.Diagnostics.CodeAnalysis.UnconditionalSuppressMessage("SingleFile", "IL3000",
        Justification = "An empty assembly location selects the self-contained apphost.")]
    internal static ProcessStartInfo StartInfo(string processPath, string assemblyPath)
    {
        var start = new ProcessStartInfo(assemblyPath.Length == 0 ? processPath : "dotnet")
        {
            StandardInputEncoding = new UTF8Encoding(false), StandardOutputEncoding = new UTF8Encoding(false),
            StandardErrorEncoding = new UTF8Encoding(false)
        };
        start.Environment.Clear();
        foreach (var key in new[] { "SystemRoot", "WINDIR", "PATH", "TEMP", "TMP", "DOTNET_ROOT" })
            if (Environment.GetEnvironmentVariable(key) is { } value) start.Environment[key] = value;
        if (assemblyPath.Length > 0) start.ArgumentList.Add(assemblyPath);
        start.ArgumentList.Add("--plugin-worker");
        return start;
    }

    internal static void Run()
    {
        using var watchdog = new Timer(_ => Environment.Exit(3), null, TimeSpan.FromSeconds(5), Timeout.InfiniteTimeSpan);
        using var output = new StreamWriter(Console.OpenStandardOutput(), new UTF8Encoding(false));
        using var inputStream = new StreamReader(Console.OpenStandardInput(), new UTF8Encoding(false, true), detectEncodingFromByteOrderMarks: false);
        Console.SetOut(TextWriter.Null);
        try
        {
            using var limits = PluginProcessLimits.Attach();
            var input = ObservationWorker.ReadBoundedAsync(inputStream,
                PluginExecutor.MaxInputBytes + PluginProgram.MaxSourceBytes * 6, default).GetAwaiter().GetResult();
            var request = JsonSerializer.Deserialize<Request>(input, Json) ?? throw new InvalidDataException("plugin_request");
            var result = PluginExecutor.Invoke(request.Program, request.Operation, request.Input);
            output.Write("{\"ok\":true,\"result\":" + result + "}");
        }
        catch (Exception ex) when (ex is not OutOfMemoryException && ex is not StackOverflowException)
        { output.Write("{\"ok\":false}"); }
    }
}
