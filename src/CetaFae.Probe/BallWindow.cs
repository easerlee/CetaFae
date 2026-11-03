using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using Microsoft.Win32.SafeHandles;

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
    SafeProcessHandle? _targetOwner;
    readonly GuideBinding _binding = new();
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
    readonly DispatcherTimer _presence = new(DispatcherPriority.Background) { Interval = TimeSpan.FromMilliseconds(500) };
    bool _fullscreenHidden;
    readonly Stopwatch _motion = new();
    readonly Stopwatch _idle = Stopwatch.StartNew();
    double _idleDelay = Random.Shared.Next(20, 41);
    enum Motion { Idle, Swim, Jump, Drag, Dock, Scan, Return }
    Motion _state;
    Point _returnFrom;
    int _facing = -1;
    double _lastPointerX;
    bool _inline;
    bool _question;
    bool _processing;
    internal bool Processing => _processing;
    internal bool Scanning => _state == Motion.Scan;
    internal bool IdleSwimming => _state == Motion.Swim;
    internal WhalePose CurrentPose => _whale.Pose;
    internal bool TopAttached => _whale.TopAttached;
    bool ReducedMotion => !SystemParameters.ClientAreaAnimation;

    public double Scale => _scale;
    internal Rect Workspace
    {
        get
        {
            var frame = Native.Frame(HasTarget ? _target : _hwnd);
            return GuideCoordinates.WorkspaceAt(new Point((frame.Left + (double)frame.Right) / 2,
                (frame.Top + (double)frame.Bottom) / 2), _scale);
        }
    }
    internal bool HasTarget => _target != IntPtr.Zero;
    internal IntPtr BoundTarget => _target;
    internal bool BindingSuspended => _binding.Suspended;
    internal bool BindingConfirmed => _binding.Confirmed;
    internal event Action? BindingSuspendedChanged;
    internal event Action? BindingResumed;
    internal event Action? GuidanceCleared;
    internal event Action? FullscreenHidden;
    public IntPtr Handle => _hwnd;
    public Rect InteractionBounds => new(Left, Top, Width, Height);
    internal Rect FeedbackBounds => _target != IntPtr.Zero && Width > WhaleVisual.CanvasSize
        ? new Rect(Left, Top + WhalePrompt.BodyTop(Height, TopAttached), Width, WhalePrompt.BodyHeight)
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
        var work = GuideCoordinates.WorkspaceAt(new Point((position.X + Width / 2) * _scale,
            (position.Y + Height / 2) * _scale), _scale);
        var visible = ConstrainPosition(position, work, new Size(Width, Height));
        Left = visible.X; Top = visible.Y;
    }

    public static Point TopDockPosition(Rect target, Rect work, double width = WhalePrompt.WidthDip,
        double height = WhaleVisual.CanvasSize)
    {
        var centerX = Math.Clamp(target.Left + target.Width / 2,
            work.Left + width / 2, Math.Max(work.Left + width / 2, work.Right - width / 2));
        var top = target.Top - (WhalePrompt.BodyTop(height, false) + WhalePrompt.BodyHeight);
        if (top < work.Top) top = target.Top;
        return new Point(centerX - width / 2, Math.Clamp(top, work.Top, Math.Max(work.Top, work.Bottom - height)));
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
        _presence.Tick += (_, _) => { UpdateBoundPresence(); UpdateFullscreenPresence(); UpdateIdleSwim(); };
        Loaded += (_, _) => _presence.Start();
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
        Closed += (_, _) => { _presence.Stop(); _animation.Stop(); _targetOwner?.Dispose(); _targetOwner = null; _whale.Release(); _wake.Close(); _overlay.Close(); };
        PreviewKeyDown += (_, e) =>
        {
            if (e.Key == Key.Escape) { if (HasTarget) GuidanceCleared?.Invoke(); e.Handled = true; }
        };
    }

    internal static bool ShouldHideForFullscreen(bool fullscreen, bool hasTarget, bool pressed) =>
        fullscreen && !hasTarget && !pressed;

    internal static bool CanIdleSwim(bool visible, bool atHome, bool busy, bool reducedMotion) =>
        visible && atHome && !busy && !reducedMotion;

    void UpdateIdleSwim()
    {
        var home = HomePosition(Workspace);
        var eligible = CanIdleSwim(IsVisible && !_fullscreenHidden,
            Math.Abs(Left - home.X) < 2 && Math.Abs(Top - home.Y) < 2,
            _pressed || HasTarget || _inline || _state is not (Motion.Idle or Motion.Swim), ReducedMotion);
        if (!eligible)
        {
            if (_state == Motion.Swim) StopAnimation();
            _idle.Restart(); return;
        }
        if (_state == Motion.Idle && _idle.Elapsed.TotalSeconds >= _idleDelay) BeginIdleSwim();
    }

    internal void BeginIdleSwim()
    {
        if (_state != Motion.Idle || _pressed || HasTarget || _inline || _fullscreenHidden || ReducedMotion) return;
        Animate(Motion.Swim);
    }

    void UpdateFullscreenPresence()
    {
        var hide = ShouldHideForFullscreen(!_pressed && !HasTarget && Native.ForegroundFullscreen(_hwnd), HasTarget, _pressed);
        if (hide == _fullscreenHidden) return;
        _fullscreenHidden = hide;
        if (hide)
        {
            if (_state == Motion.Return) PlaceBall(HomePosition(Workspace));
            StopAnimation();
            FullscreenHidden?.Invoke();
            Hide();
        }
        else Show();
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
        if (IsLoaded)
        {
            if (HasTarget) { UpdateOverlay(-1); TargetMoved?.Invoke(); }
            else PlaceBall(HomePosition(Workspace));
        }
    }

    static IntPtr PreventMouseActivation(IntPtr hwnd, int message, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (message == 0x0021) { handled = true; return new IntPtr(3); }
        return IntPtr.Zero;
    }

    void Animate(Motion state)
    {
        _state = state; _motion.Restart();
        _animation.Interval = TimeSpan.FromMilliseconds(state == Motion.Scan ? 66 : 40);
        if (!ReducedMotion) _animation.Start();
        AdvanceMotion();
    }

    void AdvanceMotion() => AdvanceMotion(_motion.Elapsed.TotalSeconds);

    internal void AdvanceMotion(double seconds)
    {
        if (_state == Motion.Swim)
        {
            if (ReducedMotion || seconds >= WhaleArt.IdleSwimDuration) StopAnimation();
            else _whale.SetPose(WhaleArt.IdlePose(seconds), _facing);
        }
        else if (_state == Motion.Drag)
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
        else if (_state == Motion.Scan)
        {
            if (!_processing || ReducedMotion) StopAnimation();
            else _overlay.SetWaterProgress(WindowWash.ScanProgress(seconds), _hwnd, scanning: true, upward: WindowWash.ScanUpward(seconds));
        }
        else if (_state == Motion.Return)
        {
            var work = Workspace;
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
        _idle.Restart(); _idleDelay = Random.Shared.Next(20, 41);
        _wake.Reset();
        _whale.SetPose(new WhalePose(109), _facing);
        _overlay.SetWaterProgress(0);
    }

    public void BeginDock()
    {
        if (_target == IntPtr.Zero || _state == Motion.Dock || _inline) return;
        SnapToEdge(_target);
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
            if (_processing && !ReducedMotion) Animate(Motion.Scan);
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
    internal void SetProcessing(bool processing)
    {
        _processing = processing;
        if (!processing) { if (_state == Motion.Scan) StopAnimation(); return; }
        ClearQuestion();
        if (_inline && _state != Motion.Dock && !ReducedMotion) Animate(Motion.Scan);
    }
    public void SetResult(bool canContinue = false)
    {
        SetProcessing(false);
        _question = canContinue;
        if (_inline) { _prompt.SetQuestion(canContinue); _prompt.SetResult(); }
    }
    internal void ResumeProcessing()
    {
        FinishMorph(complete: true);
        SetProcessing(true);
    }

    internal bool IsBoundForeground(IntPtr window, uint processId) =>
        window != IntPtr.Zero && _target == window && processId != 0 &&
        Native.GetForegroundWindow() == window && Native.IsWindowVisible(window) &&
        !Native.IsIconic(window) && Native.ProcessId(window) == processId;

    internal void SetFailure()
    {
        SetProcessing(false);
        BeginDock();
        FinishMorph(complete: true);
        _overlay.HideAndReset();
        _prompt.SetResult("可拖动继续求助");
    }

    internal void ResetArt()
    {
        _processing = false; ClearQuestion(); StopAnimation();
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
        if (_state is Motion.Return or Motion.Swim) StopAnimation();
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
        _binding.Begin(target, Native.ProcessId(target));
        try
        {
            _targetOwner = Native.PinProcess(_binding.ProcessId);
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or System.ComponentModel.Win32Exception)
        { _targetOwner?.Dispose(); _targetOwner = null; }
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
        var work = Workspace;
        Width = WhalePrompt.DockWidth((r.Right - r.Left) / _scale, work.Width);
        // Windows can keep the previous pixel height after a DPI change. The morph uses the full canvas.
        Height = WhaleVisual.CanvasSize;
        PlaceBall(TopDockPosition(new Rect(r.Left / _scale, r.Top / _scale,
            (r.Right - r.Left) / _scale, (r.Bottom - r.Top) / _scale), work, Width, Height));
        SetDockLayout(Top >= r.Top / _scale);

        ProbeLog.Log("snap",
            ("edge", "top"),
            ("target", Native.Hex(target)),
            ("targetRectPx", RectPx(r)),
            ("ballDip", FrameDip()),
            ("ballPx", BallRectPxString()));
    }

    void StartTracking()
    {
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
        if (evt == 3)
        {
            var generation = _binding.Generation;
            Dispatcher.BeginInvoke(new Action(() =>
            {
                if (_binding.Generation == generation) UpdateBoundPresence();
            }));
            return;
        }
        if (evt == 0x8001 && hwnd == _target && idObject == Native.OBJID_WINDOW)
        {
            var selected = _target;
            var generation = _binding.Generation;
            Dispatcher.BeginInvoke(new Action(() =>
            {
                if (_target == selected && _binding.Generation == generation) StopTracking("target_gone");
            }));
            return;
        }
        if (evt is >= 0x8002 and <= 0x8017 && evt != 0x8005 &&
            (hwnd == _target || Native.GetAncestor(hwnd, Native.GA_ROOT) == _target))
        {
            var selected = _target;
            ProbeLog.Log("target_content_event", ("event", evt), ("object", idObject), ("child", idChild),
                ("eventProcess", Native.ProcessId(hwnd)), ("targetProcess", Native.ProcessId(selected)));
            var generation = _binding.Generation;
            Dispatcher.BeginInvoke(new Action(() =>
            {
                if (_target != selected || _binding.Generation != generation) return;
                UpdateBoundPresence();
                if (_target == selected && !_binding.Suspended) TargetMoved?.Invoke();
            }));
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
        UpdateBoundPresence();
        if (_target == IntPtr.Zero || _binding.Suspended) return;

        var r = Native.Frame(_target);
        _overlay.Place(new Rect(r.Left / _scale, r.Top / _scale,
            Math.Max(1, (r.Right - r.Left) / _scale), Math.Max(1, (r.Bottom - r.Top) / _scale)));
        if (_inline || _state == Motion.Dock)
            SnapToEdge(_target);
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
        _targetOwner?.Dispose(); _targetOwner = null;
        _binding.Clear();
        if (had != IntPtr.Zero && !_fullscreenHidden && !IsVisible && IsLoaded) Show();
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

    internal void ConfirmBinding()
    {
        _binding.Confirm();
        UpdateOverlay(-1);
        if (HasTarget && !_binding.Suspended && !_overlay.IsVisible) _overlay.Show();
    }

    internal void ReadyBoundInput()
    {
        if (!HasTarget || _binding.Suspended) return;
        SetFailure();
        UpdateOverlay(-1);
        if (!HasTarget || _binding.Suspended) return;
        _overlay.Show();
        _overlay.SetWaterProgress(0);
        ShowQuestion();
    }

    internal void UpdateBoundPresence()
    {
        if (!HasTarget) return;
        var foreground = Native.GetForegroundWindow();
        var change = _binding.Update(TargetOwnerAlive(),
            Native.IsWindowVisible(_target) && !Native.IsIconic(_target),
            foreground == _target || (AllowsForeground?.Invoke(foreground) ?? false));
        if (change == BindingChange.Ended) { StopTracking("target_gone"); return; }
        if (change == BindingChange.Suspended)
        {
            SetProcessing(false); StopAnimation();
            _overlay.HideAndReset();
            BindingSuspendedChanged?.Invoke();
            Hide();
            ProbeLog.Log("binding_suspended");
        }
        else if (change == BindingChange.Resumed)
        {
            UpdateOverlay(-1);
            Show();
            BindingResumed?.Invoke();
            ProbeLog.Log("binding_resumed");
        }
    }

    bool TargetOwnerAlive()
    {
        try { return _targetOwner is not null && Native.ProcessAlive(_targetOwner) && Native.IsWindow(_target) &&
            Native.ProcessId(_target) == _binding.ProcessId; }
        catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception)
        { return false; }
    }

    internal void ReturnHome()
    {
        _returnFrom = new Point(Left, Top);
        Animate(Motion.Return);
    }

    public void Cleanup()
    {
        _presence.Stop();
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
