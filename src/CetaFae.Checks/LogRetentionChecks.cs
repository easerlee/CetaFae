using System.IO;
using System.Diagnostics;
using CetaFae.Probe;

namespace CetaFae.Checks;

static class LogRetentionChecks
{
    public static void Run(Action<bool, string> check)
    {
        var parent = Path.GetFullPath(Path.Combine(".artifacts", "checks"));
        var root = Path.Combine(parent, "retention-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var expired = Path.Combine(root, "probe-20200101-000000.jsonl");
            File.WriteAllText(expired, "generated metadata");
            File.SetLastWriteTimeUtc(expired, DateTime.UtcNow.AddDays(-8));
            ProbeLog.Open(root);
            check(!File.Exists(expired), "startup removes owned diagnostic logs older than seven days");
            var now = new DateTimeOffset(2026, 10, 27, 9, 0, 0, TimeSpan.FromHours(8));
            string Fixture(string name, DateTimeOffset written)
            {
                var path = Path.Combine(root, name);
                File.WriteAllText(path, "generated metadata");
                File.SetLastWriteTimeUtc(path, written.UtcDateTime);
                return path;
            }
            var atLimit = Fixture("probe-20261020-090000.jsonl", now.AddDays(-7));
            var withinLimit = Fixture("probe-20261020-090001.jsonl", now.AddDays(-7).AddSeconds(1));
            var recentLegacy = Fixture("probe-20200102-000000.jsonl", now.AddHours(-1));
            var currentName = Fixture("probe-20200101-000000-" + new string('a', 32) + ".jsonl", now.AddDays(-8));
            var unrelated = Fixture("user-notes.jsonl", now.AddDays(-8));
            var invalidName = Fixture("probe-20269999-000000.jsonl", now.AddDays(-8));
            var nested = Path.Combine(root, "nested");
            Directory.CreateDirectory(nested);
            var nestedLog = Path.Combine(nested, "probe-20200101-000000.jsonl");
            File.WriteAllText(nestedLog, "generated metadata");
            File.SetLastWriteTimeUtc(nestedLog, now.AddDays(-8).UtcDateTime);
            check(ProbeLog.PurgeExpired(root, now.UtcDateTime) && !File.Exists(atLimit) && !File.Exists(currentName),
                "legacy and current log names expire at the seven-day UTC boundary");
            check(File.Exists(withinLimit) && File.Exists(recentLegacy), "retention uses last-write time and preserves recent metadata");
            check(File.Exists(unrelated) && File.Exists(invalidName) && File.Exists(nestedLog),
                "retention leaves unrelated files, invalid names and nested directories untouched");
            var locked = Fixture("probe-20200103-000000.jsonl", now.AddDays(-8));
            using (var handle = new FileStream(locked, FileMode.Open, FileAccess.Read, FileShare.Read))
                check(!ProbeLog.PurgeExpired(root, now.UtcDateTime) && File.Exists(locked),
                    "a locked expired log is reported as incomplete retention without a crash");
            check(ProbeLog.PurgeExpired(root, now.UtcDateTime) && !File.Exists(locked),
                "a later sweep recovers after the expired log is unlocked");
            ProbeLog.Log("generated_retention_check");
            var segments = Directory.GetFiles(root, "probe-*.jsonl").ToHashSet(StringComparer.OrdinalIgnoreCase);
            ProbeLog.Maintain(DateTimeOffset.Now.AddDays(1));
            ProbeLog.Log("generated_retention_check");
            check(Directory.GetFiles(root, "probe-*.jsonl").Except(segments).Any(),
                "maintenance and subsequent writes rotate the active diagnostic segment by date");

            var redirected = Path.Combine(root, "redirected");
            try { Directory.CreateSymbolicLink(redirected, nested); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // A generated NTFS directory junction exercises the same refusal without elevation.
                var start = new ProcessStartInfo("cmd") { UseShellExecute = false, CreateNoWindow = true,
                    RedirectStandardOutput = true, RedirectStandardError = true };
                foreach (var argument in new[] { "/d", "/c", "mklink", "/J", redirected, nested }) start.ArgumentList.Add(argument);
                using var process = Process.Start(start)!;
                _ = process.StandardOutput.ReadToEnd();
                _ = process.StandardError.ReadToEnd();
                process.WaitForExit();
                if (process.ExitCode != 0) throw new InvalidOperationException("reparse_fixture_unavailable");
            }
            if (Directory.Exists(redirected))
            {
                var refused = false;
                try { ProbeLog.Open(Path.Combine(redirected, "new-child")); }
                catch (IOException) { refused = true; }
                check(refused && !Directory.Exists(Path.Combine(nested, "new-child")),
                    "a reparse ancestor is refused before creating or deleting through it");
                check(!ProbeLog.PurgeExpired(redirected, now.UtcDateTime) && File.Exists(nestedLog),
                    "a redirected log directory cannot delete the target directory's logs");
                Directory.Delete(redirected);
            }
        }
        finally
        {
            ProbeLog.Close();
            if (!Path.GetFullPath(root).StartsWith(parent + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("fixture_directory_boundary");
            Directory.Delete(root, true);
        }
    }
}
