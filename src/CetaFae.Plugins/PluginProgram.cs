using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Jint;

namespace CetaFae.Plugins;

/// <summary>A pure ECMAScript program, compiled from TypeScript; no host object crosses this boundary.</summary>
public sealed class PluginProgram
{
    public const int MaxSourceBytes = 24 * 1024;
    public int ApiVersion { get; }
    public string Source { get; }

    [JsonConstructor]
    public PluginProgram(int apiVersion, string source)
    {
        if (apiVersion != 1 || string.IsNullOrWhiteSpace(source) || source.Contains('\0') ||
            Encoding.UTF8.GetByteCount(source) > MaxSourceBytes)
            throw new InvalidDataException("plugin_program");
        ApiVersion = apiVersion;
        Source = source;
    }
}

/// <summary>Use inside a disposable worker for external programs; this interpreter is not an OS sandbox.</summary>
public static class PluginExecutor
{
    public const int MaxInputBytes = 256 * 1024;
    public const int MaxOutputBytes = 256 * 1024;
    static readonly JsonSerializerOptions Json = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };

    public static string Invoke(PluginProgram program, string operation, object input, CancellationToken token = default)
    {
        token.ThrowIfCancellationRequested();
        var payload = JsonSerializer.Serialize(new { apiVersion = 1, operation, input }, Json);
        if (Encoding.UTF8.GetByteCount(payload) > MaxInputBytes) throw new InvalidDataException("plugin_input_size");
        try
        {
            // No AllowClr, SetValue callbacks, module loader or native object projection.
            using var engine = new Engine(options =>
            {
                options.Strict().DisableStringCompilation().MaxStatements(100_000)
                    .LimitMemory(16 * 1024 * 1024).TimeoutInterval(TimeSpan.FromSeconds(1)).CancellationToken(token);
                options.Constraints.MaxRecursionDepth = 64;
                options.Constraints.MaxArraySize = 100_000;
                options.Constraints.RegexTimeout = TimeSpan.FromMilliseconds(100);
            });
            engine.Execute(program.Source);
            var value = engine.Invoke("invoke", payload);
            if (!value.IsString()) throw new InvalidDataException("plugin_result_type");
            var result = value.AsString();
            if (Encoding.UTF8.GetByteCount(result) > MaxOutputBytes) throw new InvalidDataException("plugin_output_size");
            using var document = JsonDocument.Parse(result, new JsonDocumentOptions { MaxDepth = 16 });
            if (document.RootElement.ValueKind != JsonValueKind.Object) throw new InvalidDataException("plugin_result_shape");
            return result;
        }
        catch (Exception) when (token.IsCancellationRequested) { throw new OperationCanceledException(token); }
        catch (Exception ex) when (ex is not OutOfMemoryException && ex is not StackOverflowException)
        {
            // Script text, stack traces and user inputs never enter diagnostics.
            throw new InvalidDataException("plugin_execution_failed");
        }
    }
}
