using System.IO;
using System.Text.Json;

namespace CetaFae.Probe;

public static class ProbeLog
{
    static readonly object Gate = new();
    static string _path = "";

    public static string Path => _path;

    public static void Open(string directory)
    {
        Directory.CreateDirectory(directory);
        _path = System.IO.Path.Combine(directory, $"probe-{DateTime.Now:yyyyMMdd-HHmmss}.jsonl");
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
            if (_path.Length > 0) File.AppendAllText(_path, line + Environment.NewLine);
        }
        Console.WriteLine(line);
    }
}
