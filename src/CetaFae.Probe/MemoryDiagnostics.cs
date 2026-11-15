using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using CetaFae.Plugins;
using Microsoft.Win32.SafeHandles;

namespace CetaFae.Probe;

internal sealed record MemoryConsumer(int Id, long StartedAtTicks, string Name, long WorkingSet, long PrivateBytes, double CpuSeconds)
{
    public override string ToString() => $"{Name}    {WorkingSet / 1048576d:N0} MB";
}

internal static class MemoryDiagnostics
{
    internal static MemoryConsumer[] Snapshot()
    {
        var result = new List<MemoryConsumer>();
        foreach (var process in Process.GetProcesses())
        using (process)
        {
            try
            {
                if (process.Id is 0 or 4 || process.Id == Environment.ProcessId) continue;
                result.Add(new(process.Id, process.StartTime.ToUniversalTime().Ticks, process.ProcessName,
                    process.WorkingSet64, process.PrivateMemorySize64, process.TotalProcessorTime.TotalSeconds));
            }
            catch (Exception ex) when (ex is Win32Exception or InvalidOperationException or NotSupportedException) { }
        }
        return result.OrderByDescending(p => p.WorkingSet).Take(12).ToArray();
    }
    internal static PlatformResult Trim(PlatformRequest call, Process process, PlatformProcess identity)
    {
        if (process.Id is <= 4 || process.Id == Environment.ProcessId) throw new PlatformFailure("resource_denied");
        var beforeSystem = MemoryMonitor.Read();
        var before = process.WorkingSet64;
        // Open only the two rights documented for EmptyWorkingSet. Keep one handle across identity/effect checks.
        using var handle = OpenProcess(0x0100 | 0x1000, false, process.Id);
        if (handle.IsInvalid) throw new Win32Exception(Marshal.GetLastWin32Error());
        if (!GetProcessTimes(handle, out var creation, out _, out _, out _) ||
            DateTime.FromFileTimeUtc(creation).Ticks != identity.StartedAtTicks)
            throw new PlatformFailure("target_changed");
        if (!EmptyWorkingSet(handle)) throw new Win32Exception(Marshal.GetLastWin32Error());
        process.Refresh();
        var afterSystem = MemoryMonitor.Read();
        return PlatformEngine.Result(call, "verified", "working_set_request_completed", new {
            beforeBytes = before, afterBytes = process.WorkingSet64, privateBytes = process.PrivateMemorySize64,
            beforeAvailable = beforeSystem.Available, afterAvailable = afterSystem.Available,
            applicationImprovementVerified = false, reversible = false });
    }
    [DllImport("kernel32.dll", SetLastError = true)]
    static extern SafeProcessHandle OpenProcess(uint access, bool inherit, int processId);
    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    static extern bool GetProcessTimes(SafeProcessHandle process, out long creation, out long exit, out long kernel, out long user);
    [DllImport("psapi.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    static extern bool EmptyWorkingSet(SafeProcessHandle process);
}
