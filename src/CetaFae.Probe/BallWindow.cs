using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;

namespace CetaFae.Probe;

public sealed class BallWindow : Window
{
    public const int BallSize = 44;

    readonly TargetOverlay _overlay = new();
    readonly Stopwatch _settle = new();
    Native.WinEventDelegate? _hookCallback;
    IntPtr _hook;
    IntPtr _foregroundHook;
    IntPtr _destroyHook;
    IntPtr _contentHook;
    IntPtr _hwnd;
    IntPtr _target;
    bool _dragging;
    bool _pressed;
    Point _pressPoint;
    bool _redrawPending;
    long _pendingTicks;
    Point _grabPoint;
    double _scale = 1;
    int _overlayUpdates;
    readonly WhaleVisual _whale = new();
    readonly WhalePrompt _prompt = new() { Visibility = Visibility.Collapsed };
    readonly WhaleWake _wake = new();
    readonly Border _handle = new() { Width = BallSize, Height = BallSize, Background = Brushes.Transparent };
    readonly DispatcherTimer _animation = new(DispatcherPriority.Render) { Interval = TimeSpan.FromMilliseconds(40) };
    readonly Stopwatch _motion = new();
    enum Motion { Idle, Jump, Drag, Dock, Return }
    Motion _state;
    Point _returnFrom;
    int _facing = -1;
    double _lastPointerX;
    bool _inline;
    bool _question;
    bool ReducedMotion => !SystemParameters.ClientAreaAnimation;

    public double Scale => _scale;
    public IntPtr Handle => _hwnd;
    public Rect InteractionBounds => new(Left, Top, Width, Height);
    internal Rect FeedbackBounds => _target != IntPtr.Zero && Width > WhaleVisual.CanvasSize
        ? new Rect(Left, Top + (_whale.TopAttached ? 0 : (Height - WhalePrompt.HeightDip) / 2 + WhalePrompt.HeightDip - WhalePrompt.BodyHeight), Width, WhalePrompt.BodyHeight)
        : InteractionBounds;
    public bool IsAnswering => _inline && _prompt.CanAnswer && IsActive;
    public bool IsSessionInteraction => _target != IntPtr.Zero && IsActive;
    internal bool HasInlineInput => _inline;
    internal bool ArtAnimating => _animation.IsEnabled;
    internal WhalePrompt Prompt => _prompt;
    public event Action<string>? AnswerSubmitted;
    public event Action<IntPtr>? TargetSelected;
    public event Action<string>? TrackingStopped;
    public event Action? TargetMoved;
    public event Action? MenuRequested;
    public Func<IntPtr, bool>? AllowsForeground { get; set; }

    public static Point DragPosition(Point pointer, Point grab, double scale) =>
        new(pointer.X / scale - grab.X, pointer.Y / scale - grab.Y);

    public static bool IsDrag(Point start, Point current, double scale) =>
        Math.Abs(current.X - start.X) / scale >= SystemParameters.MinimumHorizontalDragDistance ||
        Math.Abs(current.Y - start.Y) / scale >= SystemParameters.MinimumVerticalDragDistance;

    public static Point ConstrainPosition(Point position, Rect work, Size? surface = null) => new(
        Math.Clamp(position.X, work.Left, Math.Max(work.Left, work.Right - (surface?.Width ?? BallSize))),
        Math.Clamp(position.Y, work.Top, Math.Max(work.Top, work.Bottom - (surface?.Height ?? BallSize))));

    internal static Point HomePosition(Rect work) => ConstrainPosition(
        new Point(work.Right - WhaleVisual.CanvasSize - 8, work.Bottom - WhaleVisual.CanvasSize - 8),
        work, new Size(WhaleVisual.CanvasSize, WhaleVisual.CanvasSize));
    internal static Point ReturnPosition(Point from, Rect work, double seconds)
    {
        var t = Math.Clamp(seconds / .6, 0, 1); var eased = t * t * (3 - 2 * t);
        return from + (HomePosition(work) - from) * eased;
    }

    void PlaceBall(Point position)
    {
        var visible = ConstrainPosition(position, SystemParameters.WorkArea, new Size(Width, Height));
        Left = visible.X; Top = visible.Y;
    }

    public static Point TopDockPosition(Rect target, Rect work, double width = WhalePrompt.WidthDip)
    {
        var maximized = target.Top <= work.Top + 1 && target.Left <= work.Left + 1 &&
            target.Right >= work.Right - 1 && target.Bottom >= work.Bottom - 1;
        var centerX = Math.Clamp(maximized ? work.Left + work.Width / 2 : target.Left + target.Width * .4,
            work.Left + width / 2, Math.Max(work.Left + width / 2, work.Right - width / 2));
        var top = target.Top - WhalePrompt.DockFoot;
        if (top < work.Top) top = target.Top;
        return new Point(centerX - width / 2, Math.Clamp(top, work.Top, Math.Max(work.Top, work.Bottom - WhaleVisual.CanvasSize)));
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
        Width = WhaleVisual.CanvasSize;
        Height = WhaleVisual.CanvasSize;

        var grid = new Grid();
        grid.Children.Add(_whale);
        grid.Children.Add(_handle);
        grid.Children.Add(_prompt);
        Content = grid;
        _handle.MouseEnter += (_, _) => { if (!_pressed && _target == IntPtr.Zero && _state == Motion.Idle) Animate(Motion.Jump); };
        _animation.Tick += (_, _) => AdvanceMotion();
        _prompt.FocusRequested += () => { Activate(); _prompt.Answer.Focus(); };
        _prompt.Submitted += text => AnswerSubmitted?.Invoke(text);
        _prompt.DismissRequested += () => StopTracking("inline_closed");

        SourceInitialized += OnSourceInitialized;
        Loaded += (_, _) => ProbeLog.Log("ball_loaded", ("ballDip", FrameDip()),
            ("nativePx", RectPx(Native.Frame(_hwnd))));
        _handle.MouseLeftButtonDown += OnDown;
        _whale.MouseLeftButtonDown += OnDown;
        _prompt.DragSurface.MouseLeftButtonDown += OnDown;
        MouseMove += OnMove;
        MouseLeftButtonUp += OnUp;
        MouseRightButtonUp += (_, e) => { if (!_pressed) { MenuRequested?.Invoke(); e.Handled = true; } };
        LostMouseCapture += (_, _) =>
        {
            if (!_pressed) return;
            _pressed = false; _dragging = false;
            StopAnimation();
        };
        Closed += (_, _) => { _animation.Stop(); _whale.Release(); _wake.Close(); _overlay.Close(); };
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
        HwndSource.FromHwnd(_hwnd)!.AddHook(PreventMouseActivation);

        _scale = VisualTreeHelper.GetDpi(this).DpiScaleX;
        var work = SystemParameters.WorkArea;
        var home = HomePosition(work); Left = home.X; Top = home.Y;

        ProbeLog.Log("start",
            ("hwnd", Native.Hex(_hwnd)),
            ("dpiScale", _scale),
            ("ballDip", FrameDip()),
            ("fg", Native.Hex(Native.GetForegroundWindow())));
    }

    protected override void OnDpiChanged(DpiScale oldDpi, DpiScale newDpi)
    {
        base.OnDpiChanged(oldDpi, newDpi);
        _scale = newDpi.DpiScaleX;
        // Coordinates from a previous scale cannot remain valid in a target-bound session.
        if (IsLoaded) { StopTracking("dpi_changed"); PlaceBall(HomePosition(SystemParameters.WorkArea)); }
    }

    static IntPtr PreventMouseActivation(IntPtr hwnd, int message, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (message == 0x0021) { handled = true; return new IntPtr(3); }
        return IntPtr.Zero;
    }

    void Animate(Motion state)
    {
        _state = state; _motion.Restart();
        if (!ReducedMotion) _animation.Start();
        AdvanceMotion();
    }

    void AdvanceMotion() => AdvanceMotion(_motion.Elapsed.TotalSeconds);

    internal void AdvanceMotion(double seconds)
    {
        if (_state == Motion.Drag)
        {
            _whale.SetPose(new WhalePose(ReducedMotion ? 109 : 140 + (int)(seconds * 25) % 100), _facing);
            if (!ReducedMotion) _wake.Advance(seconds);
            else { _animation.Stop(); _wake.Reset(); }
        }
        else if (_state == Motion.Jump)
        {
            if (ReducedMotion || seconds >= 4.4) StopAnimation();
            else _whale.SetPose(new WhalePose(Math.Min(109, (int)(seconds * 25))), _facing);
        }
        else if (_state == Motion.Dock)
        {
            var pose = WhaleArt.DropPose(seconds, ReducedMotion);
            if (!_inline) _whale.SetPose(pose);
            _overlay.SetWaterProgress(ReducedMotion ? 0 : WindowWash.Progress(seconds), _hwnd);
            if (ReducedMotion || seconds >= WhaleArt.DropDuration)
                FinishMorph(ReducedMotion || seconds >= WindowWash.TotalDuration);
        }
        else if (_state == Motion.Return)
        {
            var work = SystemParameters.WorkArea;
            PlaceBall(ReturnPosition(_returnFrom, work, ReducedMotion ? .6 : seconds));
            if (ReducedMotion || seconds >= .6)
            {
                _facing = -1;
                if (ReducedMotion) StopAnimation(); else Animate(Motion.Jump);
            }
            else
            {
                _facing = HomePosition(work).X >= Left ? 1 : -1;
                _whale.SetPose(new WhalePose(140 + (int)(seconds * 25)), _facing);
            }
        }
    }

    void StopAnimation()
    {
        _animation.Stop(); _state = Motion.Idle;
        _wake.Reset();
        _whale.SetPose(new WhalePose(109), _facing);
        _overlay.SetWaterProgress(0);
    }

    public void BeginDock()
    {
        if (_target == IntPtr.Zero || _state == Motion.Dock || _inline) return;
        Width = Math.Min(WhalePrompt.WidthDip, SystemParameters.WorkArea.Width);
        var frame = Native.Frame(_target);
        var position = TopDockPosition(new Rect(frame.Left / _scale, frame.Top / _scale,
            (frame.Right - frame.Left) / _scale, (frame.Bottom - frame.Top) / _scale), SystemParameters.WorkArea, Width);
        PlaceBall(position);
        SetDockLayout(position.Y >= frame.Top / _scale);
        BeginDockMotion();
    }

    internal void BeginDockMotion() => Animate(Motion.Dock);

    void SetDockLayout(bool topAttached)
    {
        _whale.TopAttached = topAttached; _prompt.SetTopAttached(topAttached);
        _whale.RenderTransform = _prompt.RenderTransform = Transform.Identity;
    }

    void FinishMorph(bool complete = false)
    {
        if (complete || _state != Motion.Dock || ReducedMotion)
        {
            _animation.Stop(); _state = Motion.Idle;
            _overlay.SetWaterProgress(0);
        }
        if (_inline) return;
        _inline = true; _whale.Visibility = _handle.Visibility = Visibility.Collapsed;
        _prompt.Visibility = Visibility.Visible;
        _prompt.SetQuestion(_question);
    }

    public void ShowQuestion()
    {
        _question = true;
        // A fast model result must not wait for the decorative transition.
        if (_inline) _prompt.SetQuestion(true); else FinishMorph();
    }

    public void ClearQuestion() { _question = false; _prompt.SetQuestion(false); }
    public void SetResult() { if (_inline) _prompt.SetResult(); }

    internal void ResetArt()
    {
        ClearQuestion(); StopAnimation();
        var center = Left + Width / 2;
        _inline = false; _prompt.Visibility = Visibility.Collapsed;
        _whale.Visibility = _handle.Visibility = Visibility.Visible;
        Width = WhaleVisual.CanvasSize;
        _whale.RenderTransform = _prompt.RenderTransform = Transform.Identity;
        SetDockLayout(false);
        PlaceBall(new Point(center - Width / 2, Top));
        _whale.Release();
    }

    void OnDown(object sender, MouseButtonEventArgs e)
    {
        if (e.LeftButton != MouseButtonState.Pressed) return;
        _grabPoint = e.GetPosition(this);
        if (_inline) _grabPoint = new Point(WhaleVisual.CanvasSize / 2, WhaleVisual.CanvasSize / 2);
        var pointer = Native.Cursor();
        _pressPoint = new Point(pointer.X, pointer.Y);
        _pressed = true;
        if (_state == Motion.Return) StopAnimation();
        CaptureMouse();
        e.Handled = true;
    }

    void BeginDrag()
    {
        StopTracking("drag_start");
        _dragging = true;
        _lastPointerX = Native.Cursor().X;
        Animate(Motion.Drag);
        if (_overlay.IsVisible) _overlay.HideAndReset();

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
        if (!_pressed) return;
        var p = Native.Cursor();
        if (!_dragging && IsDrag(_pressPoint, new Point(p.X, p.Y), _scale)) BeginDrag();
        if (!_dragging) return;
        if (Math.Abs(p.X - _lastPointerX) >= 3 * _scale)
        { _facing = p.X > _lastPointerX ? 1 : -1; _lastPointerX = p.X; }
        PlaceBall(DragPosition(new Point(p.X, p.Y), _grabPoint, _scale));
        if (!ReducedMotion && WhaleArt.IsImmersed(_whale.Pose.Frame))
            _wake.Deposit(new Point(Left + Width / 2, Top + Height / 2 + 24), _motion.Elapsed.TotalSeconds);
    }

    void OnUp(object sender, MouseButtonEventArgs e)
    {
        if (!_pressed) return;
        var p = Native.Cursor();
        if (!_dragging && IsDrag(_pressPoint, new Point(p.X, p.Y), _scale)) BeginDrag();
        var wasDragging = _dragging;
        _pressed = false;
        _dragging = false;
        ReleaseMouseCapture();
        if (!wasDragging) return;
        StopAnimation();
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
        PlaceBall(TopDockPosition(new Rect(r.Left / _scale, r.Top / _scale,
            (r.Right - r.Left) / _scale, (r.Bottom - r.Top) / _scale), SystemParameters.WorkArea, Width));

        ProbeLog.Log("snap",
            ("edge", "top"),
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
        _overlay.Place(new Rect(r.Left / _scale, r.Top / _scale,
            Math.Max(1, (r.Right - r.Left) / _scale), Math.Max(1, (r.Bottom - r.Top) / _scale)));
        if (_inline || _state == Motion.Dock)
        {
            SnapToEdge(_target);
            SetDockLayout(Top >= r.Top / _scale);
        }
        _overlayUpdates++;

        long sinceEventMs = -1;
        if (eventTicks >= 0)
        {
            sinceEventMs = (long)((_settle.ElapsedTicks - eventTicks) * 1000.0 / Stopwatch.Frequency);
        }

        ProbeLog.Log("overlay",
            ("target", Native.Hex(_target)),
            ("targetRectPx", RectPx(r)),
            ("overlayDip", $"{_overlay.Bounds.Left:F0},{_overlay.Bounds.Top:F0},{_overlay.Bounds.Width:F0},{_overlay.Bounds.Height:F0}"),
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
        if (had != IntPtr.Zero || _state != Motion.Return || reason is "drag_start" or "exit") ResetArt();
        if (_overlay.IsVisible) _overlay.HideAndReset();
        TrackingStopped?.Invoke(reason);
        if (ShouldReturn(had, reason))
            ReturnHome();

        ProbeLog.Log("cleanup",
            ("reason", reason),
            ("target", Native.Hex(had)),
            ("overlayVisible", _overlay.IsVisible),
            ("overlayUpdates", _overlayUpdates),
            ("fg", Native.Hex(Native.GetForegroundWindow())));
    }

    internal static bool ShouldReturn(IntPtr target, string reason) =>
        target != IntPtr.Zero && reason is not ("drag_start" or "exit");

    internal void ReturnHome()
    {
        _returnFrom = new Point(Left, Top);
        Animate(Motion.Return);
    }

    public void Cleanup()
    {
        ResetArt();
        if (_target != IntPtr.Zero) StopTracking("exit");
        ProbeLog.Log("exit", ("fg", Native.Hex(Native.GetForegroundWindow())), ("overlayVisible", _overlay.IsVisible));
    }

    (int L, int T, int W, int H) BallRectPx() => (
        (int)Math.Round(Left * _scale),
        (int)Math.Round(Top * _scale),
        (int)Math.Round(Width * _scale),
        (int)Math.Round(Height * _scale));

    string BallRectPxString()
    {
        var (l, t, w, h) = BallRectPx();
        return $"{l},{t},{w},{h}";
    }

    string OverlayPx()
    {
        var bounds = _overlay.Bounds;
        return $"{Math.Round(bounds.Left * _scale)},{Math.Round(bounds.Top * _scale)},{Math.Round(bounds.Width * _scale)},{Math.Round(bounds.Height * _scale)}";
    }

    string FrameDip() => $"{Left:F0},{Top:F0},{Width:F0},{Height:F0}";

    static string RectPx(Native.RECT r) => $"{r.Left},{r.Top},{r.Right - r.Left},{r.Bottom - r.Top}";
}
