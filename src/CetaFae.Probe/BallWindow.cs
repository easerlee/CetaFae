using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Shapes;
using System.Windows.Threading;

namespace CetaFae.Probe;

public sealed class BallWindow : Window
{
    public const int BallSize = 44;
    const int HotkeyId = 0xC7A1;
    const int WmHotkey = 0x0312;

    readonly OverlayWindow _overlay = new();
    readonly Stopwatch _settle = new();
    Native.WinEventDelegate? _hookCallback;
    IntPtr _hook;
    IntPtr _hwnd;
    IntPtr _target;
    bool _dragging;
    Native.POINT _dragOriginCursor;
    double _dragOriginLeft, _dragOriginTop;
    double _scale = 1;
    int _overlayUpdates;

    public IntPtr TargetHwnd => _target;
    public double Scale => _scale;
    public int OverlayUpdates => _overlayUpdates;
    public bool OverlayVisible => _overlay.IsVisible;

    public BallWindow()
    {
        WindowStyle = WindowStyle.None;
        AllowsTransparency = true;
        Background = Brushes.Transparent;
        Topmost = true;
        ShowInTaskbar = false;
        ShowActivated = false;
        ResizeMode = ResizeMode.NoResize;
        WindowStartupLocation = WindowStartupLocation.Manual;
        Width = BallSize;
        Height = BallSize;

        var grid = new Grid();
        grid.Children.Add(new Ellipse
        {
            Fill = new SolidColorBrush(Color.FromArgb(0xD8, 0x1F, 0x6F, 0xD1)),
            Stroke = Brushes.White,
            StrokeThickness = 1.5
        });
        grid.Children.Add(new TextBlock
        {
            Text = "?",
            Foreground = Brushes.White,
            FontSize = 18,
            FontWeight = FontWeights.SemiBold,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center
        });
        Content = grid;

        SourceInitialized += OnSourceInitialized;
        MouseLeftButtonDown += OnDown;
        MouseMove += OnMove;
        MouseLeftButtonUp += OnUp;
    }

    void OnSourceInitialized(object? sender, EventArgs e)
    {
        _hwnd = new WindowInteropHelper(this).Handle;
        var ex = Native.GetWindowLong(_hwnd, Native.GWL_EXSTYLE);
        Native.SetWindowLong(_hwnd, Native.GWL_EXSTYLE, ex | Native.WS_EX_NOACTIVATE | Native.WS_EX_TOOLWINDOW);
        HwndSource.FromHwnd(_hwnd)!.AddHook(WndProc);

        _scale = Math.Max(1.0, Native.GetDpiForSystem() / 96.0);
        var work = SystemParameters.WorkArea;
        Left = work.Right - BallSize - 8;
        Top = work.Top + (work.Height - BallSize) / 2;

        var hotkey = Native.RegisterHotKey(_hwnd, HotkeyId, 0, Native.VK_ESCAPE);
        ProbeLog.Log("start",
            ("hwnd", Native.Hex(_hwnd)),
            ("dpiScale", _scale),
            ("ballDip", FrameDip()),
            ("escHotkey", hotkey),
            ("fg", Native.Hex(Native.GetForegroundWindow())));
    }

    IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == WmHotkey && wParam.ToInt32() == HotkeyId)
        {
            ProbeLog.Log("esc", ("hotkey", HotkeyId), ("fg", Native.Hex(Native.GetForegroundWindow())));
            StopTracking("esc");
            handled = true;
        }
        return IntPtr.Zero;
    }

    void OnDown(object sender, MouseButtonEventArgs e)
    {
        if (e.LeftButton != MouseButtonState.Pressed) return;
        _dragging = true;
        _dragOriginCursor = Native.Cursor();
        _dragOriginLeft = Left;
        _dragOriginTop = Top;
        _overlay.Hide();
        CaptureMouse();
        e.Handled = true;

        var fg = Native.GetForegroundWindow();
        ProbeLog.Log("drag_start",
            ("fg", Native.Hex(fg)),
            ("fgClass", Native.Class(fg)),
            ("cursor", $"{_dragOriginCursor.X},{_dragOriginCursor.Y}"),
            ("ballDip", FrameDip()));
    }

    void OnMove(object sender, MouseEventArgs e)
    {
        if (!_dragging) return;
        var p = Native.Cursor();
        Left = _dragOriginLeft + (p.X - _dragOriginCursor.X) / _scale;
        Top = _dragOriginTop + (p.Y - _dragOriginCursor.Y) / _scale;
    }

    void OnUp(object sender, MouseButtonEventArgs e)
    {
        if (!_dragging) return;
        _dragging = false;
        ReleaseMouseCapture();

        var p = Native.Cursor();
        var target = HitTestUnderCursor(p);
        ProbeLog.Log("drop",
            ("cursor", $"{p.X},{p.Y}"),
            ("hit", Native.Hex(target)),
            ("hitClass", target == IntPtr.Zero ? "" : Native.Class(target)),
            ("hitTitle", target == IntPtr.Zero ? "" : Native.Title(target)));

        if (target == IntPtr.Zero)
        {
            ProbeLog.Log("drop_miss", ("ballDip", FrameDip()));
            return;
        }

        _target = target;
        SnapToEdge(target);
        StartTracking();
    }

    IntPtr HitTestUnderCursor(Native.POINT p)
    {
        var ex = Native.GetWindowLong(_hwnd, Native.GWL_EXSTYLE);
        Native.SetWindowLong(_hwnd, Native.GWL_EXSTYLE, ex | Native.WS_EX_TRANSPARENT);
        var hit = Native.WindowFromPoint(p);
        Native.SetWindowLong(_hwnd, Native.GWL_EXSTYLE, ex);

        if (hit == IntPtr.Zero) return IntPtr.Zero;
        var root = Native.GetAncestor(hit, Native.GA_ROOT);
        if (root == IntPtr.Zero) root = hit;
        if (root == _hwnd) return IntPtr.Zero;

        var cls = Native.Class(root);
        if (cls is "Progman" or "WorkerW" or "Shell_TrayWnd") return IntPtr.Zero;
        return root;
    }

    void SnapToEdge(IntPtr target)
    {
        var r = Native.Frame(target);
        var cur = Native.Cursor();
        int px = (int)(BallSize * _scale);

        int left = cur.X - r.Left;
        int right = r.Right - cur.X;
        int top = cur.Y - r.Top;
        int bottom = r.Bottom - cur.Y;
        int nearest = Math.Min(Math.Min(left, right), Math.Min(top, bottom));

        string edge;
        int x, y;
        if (nearest == left)
        {
            edge = "left";
            x = r.Left - px;
            y = cur.Y - px / 2;
        }
        else if (nearest == right)
        {
            edge = "right";
            x = r.Right;
            y = cur.Y - px / 2;
        }
        else if (nearest == top)
        {
            edge = "top";
            x = cur.X - px / 2;
            y = r.Top - px;
        }
        else
        {
            edge = "bottom";
            x = cur.X - px / 2;
            y = r.Bottom;
        }

        Left = x / _scale;
        Top = y / _scale;

        ProbeLog.Log("snap",
            ("edge", edge),
            ("target", Native.Hex(target)),
            ("targetRectPx", RectPx(r)),
            ("ballDip", FrameDip()),
            ("ballPx", BallRectPxString()));
    }

    void StartTracking()
    {
        UpdateOverlay(initial: true);
        _overlay.Show();
        _overlayUpdates = 0;
        _settle.Restart();

        _hookCallback ??= OnWinEvent;
        _hook = Native.SetWinEventHook(
            Native.EVENT_OBJECT_LOCATIONCHANGE, Native.EVENT_OBJECT_LOCATIONCHANGE,
            IntPtr.Zero, _hookCallback, 0, 0,
            Native.WINEVENT_OUTOFCONTEXT | Native.WINEVENT_SKIPOWNPROCESS);

        ProbeLog.Log("track_start",
            ("target", Native.Hex(_target)),
            ("targetClass", Native.Class(_target)),
            ("hookOk", _hook != IntPtr.Zero),
            ("fg", Native.Hex(Native.GetForegroundWindow())));
    }

    void OnWinEvent(IntPtr hook, uint evt, IntPtr hwnd, int idObject, int idChild, uint thread, uint time)
    {
        if (hwnd != _target || idObject != Native.OBJID_WINDOW) return;
        long sinceEvent = _settle.ElapsedMilliseconds;
        Dispatcher.BeginInvoke(new Action(() => UpdateOverlay(sinceEvent)), DispatcherPriority.Background);
    }

    void UpdateOverlay(bool initial = false) => UpdateOverlay(-1);

    void UpdateOverlay(long sinceEventMs)
    {
        if (_target == IntPtr.Zero) return;
        if (!Native.IsWindow(_target))
        {
            StopTracking("target_gone");
            return;
        }

        var r = Native.Frame(_target);
        _overlay.Left = r.Left / _scale;
        _overlay.Top = r.Top / _scale;
        _overlay.Width = Math.Max(1, (r.Right - r.Left) / _scale);
        _overlay.Height = Math.Max(1, (r.Bottom - r.Top) / _scale);
        _overlayUpdates++;

        ProbeLog.Log("overlay",
            ("target", Native.Hex(_target)),
            ("targetRectPx", RectPx(r)),
            ("overlayDip", FrameDipOf(_overlay)),
            ("overlayPx", OverlayRectPxString()),
            ("sinceEventMs", sinceEventMs),
            ("n", _overlayUpdates));
    }

    public void StopTracking(string reason)
    {
        var had = _target;
        if (_hook != IntPtr.Zero)
        {
            Native.UnhookWinEvent(_hook);
            _hook = IntPtr.Zero;
        }
        _target = IntPtr.Zero;
        _overlay.Hide();

        ProbeLog.Log("cleanup",
            ("reason", reason),
            ("target", Native.Hex(had)),
            ("overlayVisible", _overlay.IsVisible),
            ("overlayUpdates", _overlayUpdates),
            ("fg", Native.Hex(Native.GetForegroundWindow())));
    }

    public void Cleanup()
    {
        if (_target != IntPtr.Zero) StopTracking("exit");
        Native.UnregisterHotKey(_hwnd, HotkeyId);
        ProbeLog.Log("exit", ("fg", Native.Hex(Native.GetForegroundWindow())), ("overlayVisible", _overlay.IsVisible));
    }

    public (int L, int T, int W, int H) BallRectPx() => (
        (int)Math.Round(Left * _scale),
        (int)Math.Round(Top * _scale),
        (int)Math.Round(BallSize * _scale),
        (int)Math.Round(BallSize * _scale));

    string BallRectPxString()
    {
        var (l, t, w, h) = BallRectPx();
        return $"{l},{t},{w},{h}";
    }

    string OverlayRectPxString()
    {
        return $"{Math.Round(_overlay.Left * _scale)},{Math.Round(_overlay.Top * _scale)},{Math.Round(_overlay.Width * _scale)},{Math.Round(_overlay.Height * _scale)}";
    }

    string FrameDip() => $"{Left:F0},{Top:F0},{Width:F0},{Height:F0}";

    static string FrameDipOf(Window w) => $"{w.Left:F0},{w.Top:F0},{w.Width:F0},{w.Height:F0}";

    static string RectPx(Native.RECT r) => $"{r.Left},{r.Top},{r.Right - r.Left},{r.Bottom - r.Top}";
}
