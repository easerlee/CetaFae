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

    readonly OverlayWindow _overlay = new();
    readonly Stopwatch _settle = new();
    Native.WinEventDelegate? _hookCallback;
    IntPtr _hook;
    IntPtr _foregroundHook;
    IntPtr _destroyHook;
    IntPtr _contentHook;
    IntPtr _hwnd;
    IntPtr _target;
    bool _dragging;
    bool _redrawPending;
    long _pendingTicks;
    Point _grabPoint;
    double _scale = 1;
    int _overlayUpdates;

    public double Scale => _scale;
    public event Action<IntPtr>? TargetSelected;
    public event Action<string>? TrackingStopped;
    public event Action? TargetMoved;
    public Func<IntPtr, bool>? AllowsForeground { get; set; }

    public static Point DragPosition(Point pointer, Point grab, double scale) =>
        new(pointer.X / scale - grab.X, pointer.Y / scale - grab.Y);

    public static Point ConstrainPosition(Point position, Rect work) => new(
        Math.Clamp(position.X, work.Left, Math.Max(work.Left, work.Right - BallSize)),
        Math.Clamp(position.Y, work.Top, Math.Max(work.Top, work.Bottom - BallSize)));

    void PlaceBall(Point position)
    {
        var visible = ConstrainPosition(position, SystemParameters.WorkArea);
        Left = visible.X;
        Top = visible.Y;
    }

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
        Loaded += (_, _) => ProbeLog.Log("ball_loaded", ("ballDip", FrameDip()),
            ("nativePx", RectPx(Native.Frame(_hwnd))));
        MouseLeftButtonDown += OnDown;
        MouseMove += OnMove;
        MouseLeftButtonUp += OnUp;
        PreviewKeyDown += (_, e) =>
        {
            if (e.Key == Key.Escape) { StopTracking("esc"); e.Handled = true; }
        };
    }

    void OnSourceInitialized(object? sender, EventArgs e)
    {
        _hwnd = new WindowInteropHelper(this).Handle;
        var ex = Native.GetWindowLong(_hwnd, Native.GWL_EXSTYLE);
        Native.SetWindowLong(_hwnd, Native.GWL_EXSTYLE, ex | Native.WS_EX_NOACTIVATE | Native.WS_EX_TOOLWINDOW);

        _scale = Math.Max(1.0, Native.GetDpiForSystem() / 96.0);
        var work = SystemParameters.WorkArea;
        Left = work.Right - BallSize - 8;
        Top = work.Top + (work.Height - BallSize) / 2;

        ProbeLog.Log("start",
            ("hwnd", Native.Hex(_hwnd)),
            ("dpiScale", _scale),
            ("ballDip", FrameDip()),
            ("fg", Native.Hex(Native.GetForegroundWindow())));
    }

    void OnDown(object sender, MouseButtonEventArgs e)
    {
        if (e.LeftButton != MouseButtonState.Pressed) return;
        _grabPoint = e.GetPosition(this);
        StopTracking("drag_start");
        _dragging = true;
        if (_overlay.IsVisible) _overlay.Hide();
        CaptureMouse();
        e.Handled = true;

        var fg = Native.GetForegroundWindow();
        ProbeLog.Log("drag_start",
            ("fg", Native.Hex(fg)),
            ("fgClass", Native.Class(fg)),
            ("cursor", $"{Native.Cursor().X},{Native.Cursor().Y}"),
            ("ballDip", FrameDip()), ("nativePx", RectPx(Native.Frame(_hwnd))),
            ("pointerDip", _grabPoint.ToString()));
    }

    void OnMove(object sender, MouseEventArgs e)
    {
        if (!_dragging) return;
        var p = Native.Cursor();
        PlaceBall(DragPosition(new Point(p.X, p.Y), _grabPoint, _scale));
    }

    void OnUp(object sender, MouseButtonEventArgs e)
    {
        if (!_dragging) return;
        _dragging = false;
        ReleaseMouseCapture();

        var p = Native.Cursor();
        var target = HitTestUnderCursor(p);
        ProbeLog.Log("guide_drop", ("found", target != IntPtr.Zero));

        if (target == IntPtr.Zero)
        {
            ProbeLog.Log("drop_miss", ("ballDip", FrameDip()));
            return;
        }

        _target = target;
        SnapToEdge(target);
        StartTracking();
        TargetSelected?.Invoke(target);
    }

    IntPtr HitTestUnderCursor(Native.POINT p)
    {
        // WS_EX_TRANSPARENT does not make WindowFromPoint reliably skip this window.
        // Hiding only the ball gives a deterministic underlying hit without activating it.
        Hide();
        try
        {
            var hit = Native.WindowFromPoint(p);
            if (hit == IntPtr.Zero) return IntPtr.Zero;
            var root = Native.GetAncestor(hit, Native.GA_ROOT);
            if (root == IntPtr.Zero) root = hit;
            if (Native.ProcessId(root) == Environment.ProcessId) return IntPtr.Zero;
            var cls = Native.Class(root);
            return cls is "Progman" or "WorkerW" or "Shell_TrayWnd" ? IntPtr.Zero : root;
        }
        finally { Show(); }
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

        PlaceBall(new Point(x / _scale, y / _scale));

        ProbeLog.Log("snap",
            ("edge", edge),
            ("target", Native.Hex(target)),
            ("targetRectPx", RectPx(r)),
            ("ballDip", FrameDip()),
            ("ballPx", BallRectPxString()));
    }

    void StartTracking()
    {
        UpdateOverlay(-1);
        _overlay.Show();
        _overlayUpdates = 0;
        _settle.Restart();

        _hookCallback ??= OnWinEvent;
        _hook = Native.SetWinEventHook(
            Native.EVENT_OBJECT_LOCATIONCHANGE, Native.EVENT_OBJECT_LOCATIONCHANGE,
            IntPtr.Zero, _hookCallback, 0, 0,
            Native.WINEVENT_OUTOFCONTEXT | Native.WINEVENT_SKIPOWNPROCESS);
        _foregroundHook = Native.SetWinEventHook(3, 3, IntPtr.Zero, _hookCallback, 0, 0,
            Native.WINEVENT_OUTOFCONTEXT);
        _destroyHook = Native.SetWinEventHook(0x8001, 0x8001, IntPtr.Zero, _hookCallback, 0, 0,
            Native.WINEVENT_OUTOFCONTEXT | Native.WINEVENT_SKIPOWNPROCESS);
        // Hosted content can belong to another process; filter by target HWND ancestry in the callback.
        _contentHook = Native.SetWinEventHook(0x8002, 0x8017, IntPtr.Zero, _hookCallback, 0, 0,
            Native.WINEVENT_OUTOFCONTEXT | Native.WINEVENT_SKIPOWNPROCESS);

        ProbeLog.Log("track_start",
            ("target", Native.Hex(_target)),
            ("targetClass", Native.Class(_target)),
            ("hookOk", _hook != IntPtr.Zero),
            ("fg", Native.Hex(Native.GetForegroundWindow())));
    }

    void OnWinEvent(IntPtr hook, uint evt, IntPtr hwnd, int idObject, int idChild, uint thread, uint time)
    {
        if (evt == 3 && hwnd != _target)
        {
            var selected = _target;
            Dispatcher.BeginInvoke(new Action(() =>
            {
                var foreground = Native.GetForegroundWindow();
                if (_target == selected && foreground != selected && !(AllowsForeground?.Invoke(foreground) ?? false))
                    StopTracking("foreground_changed");
            }));
            return;
        }
        if (evt == 0x8001 && hwnd == _target && idObject == Native.OBJID_WINDOW)
        {
            var selected = _target;
            Dispatcher.BeginInvoke(new Action(() => { if (_target == selected) StopTracking("target_gone"); }));
            return;
        }
        if (evt is >= 0x8002 and <= 0x8017 && evt != 0x8005 &&
            (hwnd == _target || Native.GetAncestor(hwnd, Native.GA_ROOT) == _target))
        {
            var selected = _target;
            ProbeLog.Log("target_content_event", ("event", evt), ("object", idObject), ("child", idChild),
                ("eventProcess", Native.ProcessId(hwnd)), ("targetProcess", Native.ProcessId(selected)));
            Dispatcher.BeginInvoke(new Action(() => { if (_target == selected) TargetMoved?.Invoke(); }));
        }
        if (evt != Native.EVENT_OBJECT_LOCATIONCHANGE) return;
        if (hwnd != _target || idObject != Native.OBJID_WINDOW) return;

        // Coalesce event bursts so redraws do not accumulate behind a moving window.
        _pendingTicks = _settle.ElapsedTicks;
        if (_redrawPending)
        {
            return;
        }
        _redrawPending = true;
        Dispatcher.BeginInvoke(new Action(DrainRedraw), DispatcherPriority.Render);
    }

    void DrainRedraw()
    {
        _redrawPending = false;
        UpdateOverlay(_pendingTicks);
    }

    void UpdateOverlay(long eventTicks)
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

        long sinceEventMs = -1;
        if (eventTicks >= 0)
        {
            sinceEventMs = (long)((_settle.ElapsedTicks - eventTicks) * 1000.0 / Stopwatch.Frequency);
        }

        ProbeLog.Log("overlay",
            ("target", Native.Hex(_target)),
            ("targetRectPx", RectPx(r)),
            ("overlayDip", FrameDipOf(_overlay)),
            ("overlayPx", OverlayPx()),
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
        if (_foregroundHook != IntPtr.Zero) { Native.UnhookWinEvent(_foregroundHook); _foregroundHook = IntPtr.Zero; }
        if (_destroyHook != IntPtr.Zero) { Native.UnhookWinEvent(_destroyHook); _destroyHook = IntPtr.Zero; }
        if (_contentHook != IntPtr.Zero) { Native.UnhookWinEvent(_contentHook); _contentHook = IntPtr.Zero; }
        _target = IntPtr.Zero;
        if (_overlay.IsVisible) _overlay.Hide();
        TrackingStopped?.Invoke(reason);

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
        ProbeLog.Log("exit", ("fg", Native.Hex(Native.GetForegroundWindow())), ("overlayVisible", _overlay.IsVisible));
    }

    (int L, int T, int W, int H) BallRectPx() => (
        (int)Math.Round(Left * _scale),
        (int)Math.Round(Top * _scale),
        (int)Math.Round(BallSize * _scale),
        (int)Math.Round(BallSize * _scale));

    string BallRectPxString()
    {
        var (l, t, w, h) = BallRectPx();
        return $"{l},{t},{w},{h}";
    }

    string OverlayPx()
    {
        return $"{Math.Round(_overlay.Left * _scale)},{Math.Round(_overlay.Top * _scale)},{Math.Round(_overlay.Width * _scale)},{Math.Round(_overlay.Height * _scale)}";
    }

    string FrameDip() => $"{Left:F0},{Top:F0},{Width:F0},{Height:F0}";

    static string FrameDipOf(Window w) => $"{w.Left:F0},{w.Top:F0},{w.Width:F0},{w.Height:F0}";

    static string RectPx(Native.RECT r) => $"{r.Left},{r.Top},{r.Right - r.Left},{r.Bottom - r.Top}";
}
