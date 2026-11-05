using System.Diagnostics;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace CetaFae.Probe;

internal sealed record PlatformProcess(int Id, long StartedAtTicks);
internal sealed record PlatformExecutable(string Path, string[] Arguments);
internal sealed record PlatformControl(string Id, int[] RuntimeId);
internal sealed record PlatformPoint(string Id, double X, double Y, double Left, double Top, double Width, double Height);
internal sealed record PlatformWindow(string Id, long Handle, PlatformProcess Process, PlatformControl[] Controls, PlatformPoint[]? Points = null);

/// <summary>Created only by trusted host code after resource selection; never deserialized from a plugin proposal.</summary>
internal sealed record PlatformScope
{
    public string[] Operations { get; init; } = [];
    public string[] ReadRoots { get; init; } = [];
    public string[] WriteRoots { get; init; } = [];
    public PlatformExecutable[] Executables { get; init; } = [];
    public PlatformProcess[] Processes { get; init; } = [];
    public PlatformWindow[] Windows { get; init; } = [];
    public string[] RegistryRoots { get; init; } = [];
    public string[] Services { get; init; } = [];
    public string[] EventLogs { get; init; } = [];
    public string[] NetworkEndpoints { get; init; } = [];
    public DateTimeOffset ExpiresAt { get; init; }
    public bool AllowElevation { get; init; }

    internal void Permit(string operation)
    {
        if (DateTimeOffset.UtcNow >= ExpiresAt) throw new PlatformFailure("scope_expired");
        if (!Operations.Contains(operation, StringComparer.Ordinal)) throw new PlatformFailure("operation_denied");
    }

    internal string FilePath(string input, bool write)
    {
        var path = LocalPath(input);
        var roots = write ? WriteRoots : ReadRoots.Concat(WriteRoots);
        if (!roots.Any(root => Within(path, LocalPath(root)))) throw new PlatformFailure("resource_denied");
        CheckAncestors(path);
        var parts = path.Split(Path.DirectorySeparatorChar, StringSplitOptions.RemoveEmptyEntries);
        if (parts.Any(part => part.EndsWith('.') || part.EndsWith(' ') ||
            new[] { "CON", "PRN", "AUX", "NUL", "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9",
                "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9" }
                .Contains(part.Split('.')[0], StringComparer.OrdinalIgnoreCase))) throw new PlatformFailure("invalid_path");
        if (parts.Any(part => part.Equals(".env", StringComparison.OrdinalIgnoreCase) || part.StartsWith(".env.", StringComparison.OrdinalIgnoreCase) ||
            new[] { ".ssh", ".aws", ".azure", ".git-credentials", "credentials", "credentials.json" }.Contains(part, StringComparer.OrdinalIgnoreCase)) ||
            new[] { ".pem", ".key", ".pfx", ".p12" }.Contains(Path.GetExtension(path), StringComparer.OrdinalIgnoreCase))
            throw new PlatformFailure("sensitive_resource");
        return path;
    }

    internal static string LocalPath(string value)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Length > 1024 || value.Any(char.IsControl) ||
            !Path.IsPathFullyQualified(value) || value.StartsWith(@"\", StringComparison.Ordinal) ||
            value.AsSpan(2).Contains(':')) throw new PlatformFailure("invalid_path");
        return Path.TrimEndingDirectorySeparator(Path.GetFullPath(value));
    }

    internal static bool Within(string path, string root) => path.Equals(root, StringComparison.OrdinalIgnoreCase) ||
        path.StartsWith(Path.EndsInDirectorySeparator(root) ? root : root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);

    internal static void CheckAncestors(string path)
    {
        for (var current = path; current is not null; current = Path.GetDirectoryName(current))
        {
            try
            {
                if ((File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0) throw new PlatformFailure("linked_resource");
            }
            catch (FileNotFoundException) { }
            catch (DirectoryNotFoundException) { }
        }
    }

    internal Process Process(PlatformProcess target)
    {
        var process = System.Diagnostics.Process.GetProcessById(target.Id);
        try
        {
            if (process.HasExited || process.StartTime.ToUniversalTime().Ticks != target.StartedAtTicks)
                throw new PlatformFailure("target_changed");
            return process;
        }
        catch { process.Dispose(); throw; }
    }

    internal static string Hash(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes));
    internal static string HashText(string text) => Hash(Encoding.UTF8.GetBytes(text));
}

internal sealed class PlatformFailure(string code) : Exception(code);
