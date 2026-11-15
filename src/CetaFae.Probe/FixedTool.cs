using System.Diagnostics;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using CetaFae.Plugins;

namespace CetaFae.Probe;

/// <summary>Native dependencies are reviewed by the host, independently of feature packages.</summary>
internal sealed record FixedTool(string Id, string DirectoryName, string Executable, string TreeHash,
    string[] Sources, string[] Formats, string[] ArgumentTemplate, string DataVariable)
{
    // Closing this job deliberately terminates every member, including this worker.
    static Microsoft.Win32.SafeHandles.SafeFileHandle? _workerJob;
    internal static FixedTool ImageConverter { get; } = new(
        "image.filee.win.x64.2.4.0", "filee-2.4.0-win-x64", "filee-cli.exe",
        "221E621D0ED0D3BD8E8F6BFD56F29A02A2DED97EC527C68AF76D1A9C7A8078ED",
        ["jpg", "jpeg", "png", "bmp", "webp", "heic", "heif"], ["jpg", "png", "webp", "pdf"],
        ["convert", "{source}", "--to", "{format}", "-o", "{output}", "--json"], "FILEE_DATA_DIR");

    internal static FixedTool? Resolve(string? id) => id == ImageConverter.Id ? ImageConverter : null;

    internal static string ToolsRoot => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "CetaFae", "tools");

    internal string InstallDirectory => Path.Combine(ToolsRoot, DirectoryName);
    internal bool Installed => File.Exists(Path.Combine(InstallDirectory, Executable));

    internal static string DirectoryHash(string directory, CancellationToken token)
    {
        PlatformScope.CheckAncestors(directory);
        var entries = new List<string>();
        long total = 0;
        var pending = new Queue<string>();
        pending.Enqueue(directory);
        while (pending.TryDequeue(out var folder))
        {
            foreach (var path in Directory.EnumerateFileSystemEntries(folder))
            {
                token.ThrowIfCancellationRequested();
                var attributes = File.GetAttributes(path);
                if ((attributes & FileAttributes.ReparsePoint) != 0 || entries.Count + pending.Count >= 512)
                    throw new InvalidDataException("tool_identity");
                if ((attributes & FileAttributes.Directory) != 0) { pending.Enqueue(path); continue; }
                using var file = File.OpenRead(path);
                total = checked(total + file.Length);
                if (total > 512L * 1024 * 1024) throw new InvalidDataException("tool_identity");
                entries.Add(Path.GetRelativePath(directory, path).Replace('\\', '/') + " " + Convert.ToHexString(SHA256.HashData(file)));
            }
        }
        entries.Sort(StringComparer.Ordinal);
        return PlatformScope.HashText(string.Join('\n', entries));
    }

    [System.Diagnostics.CodeAnalysis.UnconditionalSuppressMessage("SingleFile", "IL3000",
        Justification = "An empty assembly location selects the bundled apphost.")]
    internal async Task ConvertAsync(string scratch, string sourceFormat, string format, CancellationToken token)
    {
        var start = PluginWorker.StartInfo(Environment.ProcessPath!, typeof(Program).Assembly.Location);
        start.ArgumentList[^1] = "--fixed-tool-worker";
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
        deadline.CancelAfter(TimeSpan.FromSeconds(35));
        try
        {
            var request = JsonSerializer.Serialize(new Request(Id, scratch, sourceFormat, format), PlatformRequest.Json);
            await ObservationWorker.ExchangeAsync(start, request, deadline.Token);
        }
        catch (OperationCanceledException) when (!token.IsCancellationRequested) { throw new InvalidDataException("conversion_timeout"); }
        catch (Exception ex) when (ex is IOException or System.ComponentModel.Win32Exception or InvalidDataException)
        { throw new InvalidDataException("conversion_failed"); }
    }

    sealed record Request(string Id, string Scratch, string SourceFormat, string Format);

    internal static void Run()
    {
        using var watchdog = new Timer(_ => Environment.Exit(3), null, TimeSpan.FromSeconds(38), Timeout.InfiniteTimeSpan);
        try
        {
            _workerJob = PluginProcessLimits.Attach(activeProcesses: 2, maxProcessMemory: 768L * 1024 * 1024, killOnClose: true);
            using var pipe = new StreamReader(Console.OpenStandardInput(), new UTF8Encoding(false, true));
            var request = JsonSerializer.Deserialize<Request>(ObservationWorker.ReadBoundedAsync(pipe, 4096, default)
                .GetAwaiter().GetResult(), PlatformRequest.Json) ?? throw new InvalidDataException("tool_request");
            (Resolve(request.Id) ?? throw new InvalidDataException("tool_recipe")).RunAsync(request).GetAwaiter().GetResult();
        }
        catch { Environment.ExitCode = 2; }
    }

    async Task RunAsync(Request request)
    {
        if (!Sources.Contains(request.SourceFormat) || !Formats.Contains(request.Format)) throw new InvalidDataException("tool_format");
        var scratch = PlatformScope.LocalPath(request.Scratch);
        PlatformScope.CheckAncestors(scratch);
        var source = Path.Combine(scratch, "input." + request.SourceFormat);
        var output = Path.Combine(scratch, "output");
        var expected = Path.Combine(output, "input." + request.Format);
        if (!File.Exists(source) || !Directory.Exists(output) || File.Exists(expected)) throw new InvalidDataException("tool_request");
        PlatformScope.CheckAncestors(source);
        PlatformScope.CheckAncestors(output);
        if (DirectoryHash(InstallDirectory, default) != TreeHash) throw new InvalidDataException("tool_identity");

        var start = new ProcessStartInfo(Path.Combine(InstallDirectory, Executable))
        {
            WorkingDirectory = scratch, StandardInputEncoding = Encoding.UTF8,
            StandardOutputEncoding = Encoding.UTF8, StandardErrorEncoding = Encoding.UTF8
        };
        start.Environment.Clear();
        foreach (var key in new[] { "SystemRoot", "WINDIR" })
            if (Environment.GetEnvironmentVariable(key) is { } value) start.Environment[key] = value;
        start.Environment["PATH"] = Environment.SystemDirectory;
        start.Environment["TEMP"] = start.Environment["TMP"] = scratch;
        start.Environment[DataVariable] = Path.Combine(scratch, "data");
        foreach (var argument in ArgumentTemplate)
            start.ArgumentList.Add(argument switch { "{source}" => source, "{format}" => request.Format, "{output}" => output, _ => argument });

        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(28));
        var reply = await ObservationWorker.ExchangeAsync(start, "", deadline.Token);
        using var json = JsonDocument.Parse(reply, new JsonDocumentOptions { MaxDepth = 6 });
        var result = json.RootElement;
        if (result.GetProperty("target").GetString() != request.Format || result.GetProperty("converted").GetInt32() != 1 ||
            result.GetProperty("skipped").GetInt32() != 0 || result.GetProperty("failed").GetInt32() != 0 ||
            result.GetProperty("files").GetArrayLength() != 1)
            throw new InvalidDataException("tool_result");
        var entry = result.GetProperty("files")[0];
        var outputs = entry.GetProperty("outputs");
        if (entry.GetProperty("state").GetString() != "done" ||
            !string.Equals(entry.GetProperty("source").GetString(), source, StringComparison.OrdinalIgnoreCase) ||
            outputs.GetArrayLength() != 1 || !string.Equals(outputs[0].GetString(), expected, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("tool_result");
    }
}
