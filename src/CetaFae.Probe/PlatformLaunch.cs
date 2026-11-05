using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;

namespace CetaFae.Probe;

internal static class PlatformLaunch
{
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    struct Startup
    {
        public int Size;
        public IntPtr Reserved, Desktop, Title;
        public uint X, Y, Width, Height, XCount, YCount, Fill, Flags;
        public ushort Show, ReservedSize;
        public IntPtr ReservedBytes, Input, Output, Error;
    }
    [StructLayout(LayoutKind.Sequential)] struct Information { public IntPtr Process, Thread; public uint ProcessId, ThreadId; }
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    static extern bool CreateProcess(string application, StringBuilder commandLine, IntPtr processAttributes, IntPtr threadAttributes,
        bool inheritHandles, uint flags, IntPtr environment, string? directory, ref Startup startup, out Information information);
    [DllImport("kernel32.dll")] static extern bool CloseHandle(IntPtr handle);

    // Standard Windows argv quoting, without a command interpreter. Exact argv was already authorized by the host.
    internal static string Quote(string argument)
    {
        var result = new StringBuilder("\""); var slashes = 0;
        foreach (var c in argument)
        {
            if (c == '\\') { slashes++; continue; }
            result.Append('\\', c == '"' ? slashes * 2 + 1 : slashes); slashes = 0; result.Append(c);
        }
        return result.Append('\\', slashes * 2).Append('"').ToString();
    }

    internal static PlatformProcess Start(string executable, IEnumerable<string> arguments)
    {
        var command = string.Join(' ', new[] { executable }.Concat(arguments).Select(Quote));
        if (command.Length >= 32767) throw new PlatformFailure("argument_limit");
        var startup = new Startup { Size = Marshal.SizeOf<Startup>() };
        // Unlike a normal redirected .NET Process, this never inherits the worker's original pipe handles.
        if (!CreateProcess(executable, new StringBuilder(command), IntPtr.Zero, IntPtr.Zero, false, 0x08000000,
                IntPtr.Zero, null, ref startup, out var information)) throw new Win32Exception();
        try
        {
            using var process = Process.GetProcessById(checked((int)information.ProcessId));
            return new(process.Id, process.StartTime.ToUniversalTime().Ticks);
        }
        finally { CloseHandle(information.Thread); CloseHandle(information.Process); }
    }
}
