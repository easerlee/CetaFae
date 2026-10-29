using System.IO;
using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace CetaFae.Probe;

public static class ProbeLog
{
    static readonly object Gate = new();
    static readonly Regex OwnedName = new(@"^probe-\d{8}-\d{6}(?:-[0-9a-f]{32})?\.jsonl$",
        RegexOptions.CultureInvariant | RegexOptions.NonBacktracking);
    static string _directory = "";
    static string _path = "";
    static DateOnly _date;
    static Timer? _maintenance;

    public static void Open(string directory)
    {
        var fullPath = Path.GetFullPath(directory);
        ValidateDirectory(fullPath);
        Directory.CreateDirectory(fullPath);
        ValidateDirectory(fullPath);
        lock (Gate)
        {
            _maintenance?.Dispose();
            _directory = fullPath;
            _path = "";
            Maintain(DateTimeOffset.Now);
            _maintenance = new Timer(_ => Maintain(DateTimeOffset.Now), null, TimeSpan.FromHours(1), TimeSpan.FromHours(1));
        }
    }

    public static void Close()
    {
        lock (Gate)
        {
            _maintenance?.Dispose();
            _maintenance = null;
            _directory = _path = "";
        }
    }

    internal static void Maintain(DateTimeOffset now)
    {
        lock (Gate)
        {
            if (_directory.Length == 0) return;
            Rotate(now);
            if (!PurgeExpired(_directory, now.UtcDateTime)) Log("diagnostic_retention_incomplete");
        }
    }

    static void Rotate(DateTimeOffset now)
    {
        var date = DateOnly.FromDateTime(now.DateTime);
        if (_path.Length > 0 && date == _date) return;
        _date = date;
        _path = Path.Combine(_directory, $"probe-{now.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture)}-{Guid.NewGuid():N}.jsonl");
    }

    internal static bool PurgeExpired(string directory, DateTime utcNow)
    {
        var complete = true;
        try
        {
            ValidateDirectory(directory);
            foreach (var path in Directory.EnumerateFiles(directory, "probe-*.jsonl", SearchOption.TopDirectoryOnly))
            {
                var name = Path.GetFileName(path);
                if (!OwnedName.IsMatch(name) || !DateTime.TryParseExact(name.AsSpan(6, 15), "yyyyMMdd-HHmmss",
                        CultureInfo.InvariantCulture, DateTimeStyles.None, out _)) continue;
                try
                {
                    if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0) continue;
                    if (File.GetLastWriteTimeUtc(path) <= utcNow.AddDays(-7)) File.Delete(path);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { complete = false; }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { complete = false; }
        return complete;
    }

    static void ValidateDirectory(string directory)
    {
        // Inspect existing ancestors before creating or deleting anything beneath a redirected directory.
        for (var current = new DirectoryInfo(Path.GetFullPath(directory)); current is not null; current = current.Parent)
            if (current.Exists && (current.Attributes & FileAttributes.ReparsePoint) != 0)
                throw new IOException("log_directory_reparse");
    }

    public static void Log(string evt, params (string Key, object? Value)[] fields)
    {
        var row = new Dictionary<string, object?>
        {
            ["ts"] = DateTime.Now.ToString("HH:mm:ss.fff"),
            ["evt"] = evt
        };
        foreach (var (key, value) in fields) row[key] = value;

        var line = JsonSerializer.Serialize(row);
        lock (Gate)
        {
            if (_path.Length > 0)
            {
                Rotate(DateTimeOffset.Now);
                try
                {
                    ValidateDirectory(_directory);
                    if (File.Exists(_path) && (File.GetAttributes(_path) & FileAttributes.ReparsePoint) != 0)
                        throw new IOException("log_file_reparse");
                    File.AppendAllText(_path, line + Environment.NewLine);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    Console.WriteLine("{\"evt\":\"diagnostic_write_failed\"}");
                }
            }
        }
        Console.WriteLine(line);
    }
}
