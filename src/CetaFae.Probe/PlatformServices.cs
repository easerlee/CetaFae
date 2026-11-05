using System.ComponentModel;
using System.Runtime.InteropServices;
using CetaFae.Plugins;

namespace CetaFae.Probe;

internal static class PlatformServices
{
    [StructLayout(LayoutKind.Sequential)]
    struct Status { public uint Type, State, Accepted, Error, SpecificError, Checkpoint, WaitHint, ProcessId, Flags; }
    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)] static extern IntPtr OpenSCManager(string? machine, string? database, uint access);
    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)] static extern IntPtr OpenService(IntPtr manager, string name, uint access);
    [DllImport("advapi32.dll", SetLastError = true)] static extern bool QueryServiceStatusEx(IntPtr service, int level, out Status status, int size, out int needed);
    [DllImport("advapi32.dll", SetLastError = true)] static extern bool StartService(IntPtr service, int count, IntPtr arguments);
    [DllImport("advapi32.dll", SetLastError = true)] static extern bool ControlService(IntPtr service, uint control, out Status status);
    [DllImport("advapi32.dll")] static extern bool CloseServiceHandle(IntPtr handle);

    internal static PlatformResult Execute(PlatformRequest call, PlatformScope scope)
    {
        var name = PlatformEngine.Text(call.Parameters, "service", max: 256);
        if (!scope.Services.Contains(name, StringComparer.OrdinalIgnoreCase)) throw new PlatformFailure("resource_denied");
        var manager = OpenSCManager(null, null, 1); if (manager == IntPtr.Zero) throw new Win32Exception();
        try
        {
            var access = 4u | (call.Operation == "service.start" ? 16u : call.Operation == "service.stop" ? 32u : 0);
            var service = OpenService(manager, name, access); if (service == IntPtr.Zero) throw new Win32Exception();
            try
            {
                Status Read() => QueryServiceStatusEx(service, 0, out var status, Marshal.SizeOf<Status>(), out _) ? status : throw new Win32Exception();
                var before = Read();
                if (call.Operation == "service.inspect") return PlatformEngine.Result(call, "observed", "service_observed", new { state = before.State, processId = before.ProcessId });
                scope.Permit(call.Operation);
                var expected = call.Operation == "service.start" ? 4u : 1u;
                if (before.State != expected && !(call.Operation == "service.start" ? StartService(service, 0, IntPtr.Zero) : ControlService(service, 1, out _)))
                    throw new Win32Exception();
                var until = DateTime.UtcNow.AddSeconds(3);
                do
                {
                    var after = Read();
                    if (after.State == expected) return PlatformEngine.Result(call, "verified", "service_state_verified", new { state = after.State });
                    Thread.Sleep(100);
                } while (DateTime.UtcNow < until);
                return PlatformEngine.Result(call, "outcome_unknown", "service_transition_pending");
            }
            finally { CloseServiceHandle(service); }
        }
        finally { CloseServiceHandle(manager); }
    }
}
