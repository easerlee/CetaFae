using System.Runtime.InteropServices;
using System.Text;

namespace CetaFae.Probe;

internal static class Native
{
    public const int GWL_EXSTYLE = -20;
    public const int WS_EX_TRANSPARENT = 0x00000020;
    public const int WS_EX_TOOLWINDOW = 0x00000080;
    public const int WS_EX_NOACTIVATE = 0x08000000;

    public const uint GA_ROOT = 2;
    public const int DWMWA_EXTENDED_FRAME_BOUNDS = 9;

    public const uint EVENT_OBJECT_LOCATIONCHANGE = 0x800B;
    public const int OBJID_WINDOW = 0;
    public const uint WINEVENT_OUTOFCONTEXT = 0x0000;
    public const uint WINEVENT_SKIPOWNPROCESS = 0x0002;

    public delegate void WinEventDelegate(IntPtr hook, uint evt, IntPtr hwnd, int idObject, int idChild, uint thread, uint time);

    [StructLayout(LayoutKind.Sequential)]
    public struct POINT { public int X; public int Y; }

    [StructLayout(LayoutKind.Sequential)]
    public struct RECT { public int Left; public int Top; public int Right; public int Bottom; }

    [StructLayout(LayoutKind.Sequential)]
    struct GUITHREADINFO
    {
        public uint Size, Flags;
        public IntPtr Active, Focus, Capture, MenuOwner, MoveSize, Caret;
        public RECT CaretRect;
    }

    [DllImport("user32.dll")] static extern bool GetGUIThreadInfo(uint thread, ref GUITHREADINFO info);
    [DllImport("user32.dll")] static extern bool ClientToScreen(IntPtr window, ref POINT point);

    internal static System.Windows.Rect? CaretRect(IntPtr window)
    {
        if (GetForegroundWindow() != window) return null;
        var thread = GetWindowThreadProcessId(window, out var pid);
        var info = new GUITHREADINFO { Size = (uint)Marshal.SizeOf<GUITHREADINFO>() };
        if (thread == 0 || !GetGUIThreadInfo(thread, ref info) || info.Caret == IntPtr.Zero ||
            GetAncestor(info.Caret, GA_ROOT) != window || ProcessId(info.Caret) != pid) return null;
        var top = new POINT { X = info.CaretRect.Left, Y = info.CaretRect.Top };
        var bottom = new POINT { X = info.CaretRect.Right, Y = info.CaretRect.Bottom };
        if (!ClientToScreen(info.Caret, ref top) || !ClientToScreen(info.Caret, ref bottom) ||
            bottom.X <= top.X || bottom.Y <= top.Y || GetForegroundWindow() != window) return null;
        return new System.Windows.Rect(top.X, top.Y, bottom.X - top.X, bottom.Y - top.Y);
    }

    [DllImport("user32.dll")] public static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] public static extern IntPtr WindowFromPoint(POINT p);
    [DllImport("user32.dll")] public static extern IntPtr GetAncestor(IntPtr hwnd, uint flags);
    [DllImport("user32.dll")] public static extern int GetWindowLong(IntPtr hwnd, int index);
    [DllImport("user32.dll")] public static extern int SetWindowLong(IntPtr hwnd, int index, int value);
    [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr hwnd, out RECT rect);
    [DllImport("user32.dll")] public static extern bool IsWindow(IntPtr hwnd);
    [DllImport("user32.dll")] public static extern bool IsWindowVisible(IntPtr hwnd);
    [DllImport("user32.dll")] public static extern bool IsIconic(IntPtr hwnd);
    [DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr hwnd);
    [DllImport("user32.dll")] static extern bool SetWindowPos(IntPtr hwnd, IntPtr after, int x, int y, int width, int height, uint flags);
    // Reorder only our existing prompt; never steal foreground while a new wash surface is shown.
    internal static void RaiseWithoutActivation(IntPtr hwnd)
    { if (hwnd != IntPtr.Zero) SetWindowPos(hwnd, new IntPtr(-1), 0, 0, 0, 0, 0x0013); }

    [StructLayout(LayoutKind.Sequential)]
    public struct BITMAPINFOHEADER
    {
        public int biSize;
        public int biWidth;
        public int biHeight;
        public short biPlanes;
        public short biBitCount;
        public int biCompression;
        public int biSizeImage;
        public int biXPelsPerMeter;
        public int biYPelsPerMeter;
        public int biClrUsed;
        public int biClrImportant;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct BITMAPINFO
    {
        public BITMAPINFOHEADER bmiHeader;
        public int bmiColors;
    }

    [DllImport("user32.dll")] public static extern IntPtr GetDC(IntPtr hwnd);
    [DllImport("user32.dll")] public static extern int ReleaseDC(IntPtr hwnd, IntPtr dc);
    [DllImport("gdi32.dll")] public static extern IntPtr CreateCompatibleDC(IntPtr dc);
    [DllImport("gdi32.dll")] public static extern IntPtr CreateCompatibleBitmap(IntPtr dc, int w, int h);
    [DllImport("gdi32.dll")] public static extern IntPtr SelectObject(IntPtr dc, IntPtr obj);
    [DllImport("gdi32.dll")] public static extern bool DeleteObject(IntPtr obj);
    [DllImport("gdi32.dll")] public static extern bool DeleteDC(IntPtr dc);
    [DllImport("gdi32.dll")] public static extern bool PatBlt(IntPtr dc, int x, int y, int w, int h, uint rop);
    [DllImport("gdi32.dll")] public static extern int GetDIBits(IntPtr dc, IntPtr bmp, uint start, uint lines, byte[] bits, ref BITMAPINFO info, uint usage);
    [DllImport("user32.dll")] public static extern bool PrintWindow(IntPtr hwnd, IntPtr dc, uint flags);

    [DllImport("user32.dll")] public static extern uint GetDpiForSystem();
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] public static extern int GetClassName(IntPtr hwnd, StringBuilder buffer, int max);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] public static extern int GetWindowText(IntPtr hwnd, StringBuilder buffer, int max);

    [DllImport("dwmapi.dll")]
    public static extern int DwmGetWindowAttribute(IntPtr hwnd, int attribute, out RECT rect, int size);

    [DllImport("user32.dll")]
    public static extern IntPtr SetWinEventHook(uint min, uint max, IntPtr module, WinEventDelegate callback, uint pid, uint tid, uint flags);

    [DllImport("user32.dll")] public static extern bool UnhookWinEvent(IntPtr hook);

    [DllImport("user32.dll")] static extern uint GetWindowThreadProcessId(IntPtr hwnd, out uint processId);
    [DllImport("kernel32.dll")] static extern IntPtr OpenProcess(uint access, bool inherit, uint processId);
    [DllImport("kernel32.dll")] static extern bool CloseHandle(IntPtr handle);
    [DllImport("advapi32.dll")] static extern bool OpenProcessToken(IntPtr process, uint access, out IntPtr token);
    [DllImport("advapi32.dll")] static extern bool GetTokenInformation(IntPtr token, int infoClass,
        out int information, int length, out int returnLength);

    public static uint ProcessId(IntPtr hwnd)
    {
        GetWindowThreadProcessId(hwnd, out var pid);
        return pid;
    }

    public static bool IsNonElevated(IntPtr hwnd)
    {
        var process = OpenProcess(0x1000, false, ProcessId(hwnd));
        if (process == IntPtr.Zero) return false;
        try
        {
            if (!OpenProcessToken(process, 0x0008, out var token)) return false;
            try { return GetTokenInformation(token, 20, out var elevated, sizeof(int), out _) && elevated == 0; }
            finally { CloseHandle(token); }
        }
        finally { CloseHandle(process); }
    }

    public static string Hex(IntPtr hwnd) => "0x" + hwnd.ToInt64().ToString("X");

    public static POINT Cursor()
    {
        var p = new POINT();
        GetCursorPos(out p);
        return p;
    }

    [DllImport("user32.dll")] static extern bool GetCursorPos(out POINT p);
    [DllImport("user32.dll")] static extern short GetAsyncKeyState(int key);
    internal static bool MouseButtonDown() =>
        (GetAsyncKeyState(0x01) & 0x8000) != 0 || (GetAsyncKeyState(0x02) & 0x8000) != 0 ||
        (GetAsyncKeyState(0x04) & 0x8000) != 0;

    [StructLayout(LayoutKind.Sequential)]
    struct MONITORINFO { public int Size; public RECT Monitor, Work; public uint Flags; }
    [DllImport("user32.dll")] static extern IntPtr MonitorFromWindow(IntPtr window, uint flags);
    [DllImport("user32.dll")] static extern bool GetMonitorInfo(IntPtr monitor, ref MONITORINFO info);
    [DllImport("user32.dll")] static extern bool GetClientRect(IntPtr window, out RECT rect);
    [DllImport("user32.dll")] static extern IntPtr GetShellWindow();
    [DllImport("user32.dll")] static extern IntPtr GetDesktopWindow();

    internal static bool IsFullscreenGeometry(System.Windows.Rect frame, System.Windows.Rect client, System.Windows.Rect monitor)
    {
        static bool Covers(System.Windows.Rect rectangle, System.Windows.Rect area) => !rectangle.IsEmpty &&
            Math.Abs(rectangle.Left - area.Left) <= 1 && Math.Abs(rectangle.Top - area.Top) <= 1 &&
            Math.Abs(rectangle.Right - area.Right) <= 1 && Math.Abs(rectangle.Bottom - area.Bottom) <= 1;
        // A fullscreen client can retain native frame borders outside its monitor.
        return !monitor.IsEmpty && monitor.Width > 0 && monitor.Height > 0 && !frame.IsEmpty &&
            frame.Left <= monitor.Left + 1 && frame.Top <= monitor.Top + 1 &&
            frame.Right >= monitor.Right - 1 && frame.Bottom >= monitor.Bottom - 1 && Covers(client, monitor);
    }

    internal static bool ForegroundFullscreen(IntPtr entry)
    {
        var window = GetForegroundWindow();
        if (window == IntPtr.Zero || window == GetShellWindow() || window == GetDesktopWindow() ||
            !IsWindowVisible(window) || IsIconic(window) || ProcessId(window) == Environment.ProcessId) return false;
        var monitor = MonitorFromWindow(window, 0);
        if (monitor == IntPtr.Zero || monitor != MonitorFromWindow(entry, 0)) return false;
        var info = new MONITORINFO { Size = Marshal.SizeOf<MONITORINFO>() };
        if (!GetMonitorInfo(monitor, ref info) || info.Monitor.Right <= info.Monitor.Left ||
            info.Monitor.Bottom <= info.Monitor.Top || !GetClientRect(window, out var client)) return false;
        var start = new POINT { X = client.Left, Y = client.Top };
        var end = new POINT { X = client.Right, Y = client.Bottom };
        if (!ClientToScreen(window, ref start) || !ClientToScreen(window, ref end) || end.X <= start.X || end.Y <= start.Y) return false;
        var frame = Frame(window);
        if (frame.Right <= frame.Left || frame.Bottom <= frame.Top || GetForegroundWindow() != window) return false;
        static System.Windows.Rect Rect(RECT value) => new(value.Left, value.Top, value.Right - value.Left, value.Bottom - value.Top);
        return IsFullscreenGeometry(Rect(frame), new System.Windows.Rect(start.X, start.Y, end.X - start.X, end.Y - start.Y), Rect(info.Monitor));
    }

    public static string Title(IntPtr hwnd)
    {
        var sb = new StringBuilder(512);
        GetWindowText(hwnd, sb, sb.Capacity);
        return sb.ToString();
    }

    public static string Class(IntPtr hwnd)
    {
        var sb = new StringBuilder(256);
        GetClassName(hwnd, sb, sb.Capacity);
        return sb.ToString();
    }

    /// <summary>Use DWM visible frame bounds, falling back to the native window rectangle.</summary>
    public static RECT Frame(IntPtr hwnd)
    {
        if (DwmGetWindowAttribute(hwnd, DWMWA_EXTENDED_FRAME_BOUNDS, out var r, Marshal.SizeOf<RECT>()) == 0
            && r.Right > r.Left && r.Bottom > r.Top)
            return r;
        GetWindowRect(hwnd, out r);
        return r;
    }
}
