using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;

namespace CetaFae.Probe;

public sealed class GuidePanel : Window
{
    readonly GuideCard _card;
    readonly FeedbackSurface _surface;
    Func<Rect>? _entry;
    Func<double>? _scale;
    Func<bool>? _hasTarget;
    readonly DispatcherTimer _noticePointer = new(DispatcherPriority.Background) { Interval = TimeSpan.FromMilliseconds(50) };
    bool _pointerDown;
    Rect? _control;
    bool _placing;
    bool _locatedResult;
    bool _avoidanceCollapsed;
    bool _repositionQueued;
    bool _closed;
    int _presentationRevision;
    public event Action? MenuRequested;
    public event Action? Cancelled;
    public event Action<string>? AnswerSubmitted;
    public event Action? RegionRequested;
    public event Action<string>? ReferenceRequested;
    public event Action? FollowupRequested;
    public bool IsAnswering => _card.HasQuestion && IsActive;
    public bool IsSessionInteraction => _card.HasSession && IsActive;
    public IntPtr Handle => new WindowInteropHelper(this).Handle;
    internal GuideCard Card => _card;

    public GuidePanel(GuideOptions options)
    {
        Title = "鲸汐";
        WindowStyle = WindowStyle.None;
        ResizeMode = ResizeMode.NoResize;
        ShowInTaskbar = false;
        ShowActivated = false;
        Topmost = true;
        AllowsTransparency = true;
        Background = Brushes.Transparent;
        Width = Math.Min(GuideTheme.CardWidth, SystemParameters.WorkArea.Width - 24);
        MaxHeight = Math.Min(GuideTheme.CardMaxHeight, SystemParameters.WorkArea.Height - 24);
        SizeToContent = SizeToContent.Manual;
        _card = new GuideCard(options);
        Content = _surface = new FeedbackSurface(_card);
        _card.PresentationChanged += () => { Reposition(); QueueReposition(); UpdateNoticeTracking(); };
        IsVisibleChanged += (_, _) => UpdateNoticeTracking();
        Closed += (_, _) => { _closed = true; _noticePointer.Stop(); };
        _noticePointer.Tick += (_, _) =>
        {
            var down = Native.MouseButtonDown();
            if (_card.View == GuideCardView.Notice)
            {
                var pointer = Native.Cursor();
                var frame = Native.Frame(Handle);
                if (ShouldDismissNotice(_pointerDown, down, new Point(pointer.X, pointer.Y),
                    new Rect(frame.Left, frame.Top, frame.Right - frame.Left, frame.Bottom - frame.Top)))
                    _card.DismissNotice();
            }
            _pointerDown = down;
        };
        _card.MenuRequested += () => MenuRequested?.Invoke();
        _card.Cancelled += () => Cancelled?.Invoke();
        _card.AnswerSubmitted += text => AnswerSubmitted?.Invoke(text);
        _card.RegionRequested += () => RegionRequested?.Invoke();
        _card.ReferenceRequested += query => ReferenceRequested?.Invoke(query);
        _card.FollowupRequested += () => FollowupRequested?.Invoke();
        _card.ExitRequested += () => Application.Current.Shutdown();
        _card.Dismissed += Hide;
        _card.AnswerFocusRequested += () =>
        {
            _card.SetKeyboardAccess(true);
            Activate();
            _card.Answer.Focus();
        };
        // Ordinary card buttons must not steal focus from the selected target.
        _card.SetKeyboardAccess(false);
        Activated += (_, _) => SetKeyboardAccess(true);
        Deactivated += (_, _) => SetKeyboardAccess(false);
        SourceInitialized += (_, _) =>
        {
            var hwnd = Handle;
            Native.SetWindowLong(hwnd, Native.GWL_EXSTYLE, Native.GetWindowLong(hwnd, Native.GWL_EXSTYLE) |
                Native.WS_EX_NOACTIVATE | Native.WS_EX_TOOLWINDOW);
            HwndSource.FromHwnd(hwnd)!.AddHook(PreventMouseActivation);
        };
        PreviewKeyDown += (_, e) =>
        {
            if (e.Key == Key.Escape) { Cancelled?.Invoke(); e.Handled = true; }
        };
        Reposition();
    }

    internal static bool ShouldDismissNotice(bool wasDown, bool down, Point pointer, Rect bounds) =>
        down && !wasDown && !bounds.IsEmpty && !bounds.Contains(pointer);

    void UpdateNoticeTracking()
    {
        if (!IsVisible || _card.View != GuideCardView.Notice) { _noticePointer.Stop(); return; }
        if (_noticePointer.IsEnabled) return;
        // Ignore the press that produced this notice; only a subsequent outside press dismisses it.
        _pointerDown = Native.MouseButtonDown();
        _noticePointer.Start();
    }

    void SetKeyboardAccess(bool active)
    {
        Focusable = active;
        _card.SetKeyboardAccess(active);
        ProbeLog.Log("guide_panel_activation", ("active", active), ("fg", Native.Hex(Native.GetForegroundWindow())));
    }

    public void SetCloud(bool enabled) => _card.SetCloud(enabled);
    public void SetStatus(string message)
    {
        if (_hasTarget?.Invoke() == false) _card.SetNotice(message); else _card.SetStatus(message);
        if (!IsVisible) Show();
        var revision = ++_presentationRevision;
        // Observe the actual post-layout state without retaining text or forcing a repaint that masks a failure.
        Dispatcher.BeginInvoke(DispatcherPriority.ContextIdle, new Action(() =>
        {
            if (revision != _presentationRevision || !IsVisible) return;
            var state = _card.FeedbackState();
            ProbeLog.Log("guide_feedback_layout", ("view", _card.View.ToString()),
                ("textPresent", state.TextPresent), ("textVisible", state.TextVisible),
                ("textInViewport", state.TextInViewport), ("viewportReady", state.ViewportReady));
        }));
    }
    internal void BindEntry(Func<Rect> entry, Func<double> scale, Func<bool> hasTarget)
    { _entry = entry; _scale = scale; _hasTarget = hasTarget; }
    internal void DismissNotice() => _card.DismissNotice();
    internal void SetPoint(bool visible)
    {
        _locatedResult = visible && _control is not null;
        _card.SetPoint(_locatedResult);
    }
    public void ShowMenu() { _card.ShowMenu(); if (!IsVisible) Show(); }
    public void ShowConsent() { _card.ShowConsent(); if (!IsVisible) Show(); }
    public void ShowQuestion() => _card.ShowQuestion();
    public void SetRegionAvailable(bool available) => _card.SetRegionAvailable(available);
    public void SetFollowupAvailable(bool available) => _card.SetFollowupAvailable(available);
    public void ClearQuestion() => _card.ClearQuestion();
    public void SetReferenceQuery(string? query) => _card.SetReferenceQuery(query);
    public void SetReferenceResult(string result) => _card.SetReferenceResult(result);
    public void EndSession() { _presentationRevision++; _control = null; _locatedResult = _avoidanceCollapsed = false; _card.Reset(); Hide(); }

    internal static string Disclosure(GuideOptions options) =>
        "安装时点击“同意协议并安装”，在本机按当前Windows用户记住同意；安装后直接拖动小鲸鱼求助，不设日常云端开关或首次启动确认。启动本身不读取窗口。接收方、模型、范围或条款改变后，须重新运行安装程序确认；可在鲸汐设置的隐私页撤回同意。\n" +
        "拖放及提交回答会读取所选窗口；展示期间按窗口变化在本机复核控件。\n" +
        "点击“继续提问”后可先手动操作；等待输入期间暂停内容读取，提交时重新检查同一目标。连续指导发送原始用户目标、本轮问题及步骤编号，不发送旧模型回答；最多8步，切窗或结束即清除。\n" +
        "初次拖入先识别软件和界面用途，不要求已有操作目标；本机归属信息仅用于核对，不发送进程名或窗口标题元数据，主反馈不展示这些技术信息。画面本身可能包含软件名或标题。\n" +
        "身份插件可在本机读取所选程序的产品名、描述、版本、公司、离线签名及包身份；路径仅用于读取，不记录或外发。这些字段不发给模型。可选联网核对会另行展示产品名查询词和公共百科接收方，须再次亲自确认；不默认联网。\n" +
        (options.IncludeImage ? "通用视觉插件可尝试理解未适配的软件，不代表所有软件已正式支持。目标未明时可能发送所选整窗画面；明确目标后发送可靠适配范围的局部画面，或由你框选的区域（可主动选整个目标窗）、范围内控件文字及用户输入的回答。范围不明时先等待选区，不自动改发整窗。PrintWindow先在本机内存绘制所选整窗，再裁剪外发。\n" :
            "目标未明时发送所选窗口控件文字；明确目标后仅发送适配或你框选范围内文字及用户输入的回答，不含图片；范围不明先等待选区。\n") +
        $"接收地址：{options.BaseUrl.AbsoluteUri}\n模型：{options.Model}。\n" +
        "客户端不保存画面、控件原文和问答；会话结束清除显示与可复用引用。元数据日志保留7天，按小时清理；清理失败可能延迟。\n" +
        "代理可能转发；代理与模型服务商留存均未核实，不具备正式发布条件。\n" +
        "未同意或撤回后不采集识别内容、不发新请求。再次使用须重新运行安装程序确认；已发送内容无法从上游撤回。";
    static IntPtr PreventMouseActivation(IntPtr hwnd, int message, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (message == 0x0021) // WM_MOUSEACTIVATE
        {
            ProbeLog.Log("guide_panel_mouse_activate", ("fg", Native.Hex(Native.GetForegroundWindow())));
            handled = true;
            return new IntPtr(3); // MA_NOACTIVATE: deliver the click without activating the window.
        }
        return IntPtr.Zero;
    }

    internal void SetControl(Rect? bounds)
    {
        _locatedResult = _avoidanceCollapsed = false;
        var scale = _scale?.Invoke() ?? 1;
        _control = bounds is { } control ? new Rect(control.X / scale, control.Y / scale, control.Width / scale, control.Height / scale) : null;
        Reposition();
    }

    internal void Reposition()
    {
        if (_placing) return;
        _placing = true;
        try
        {
            var work = SystemParameters.WorkArea;
            var entry = _entry?.Invoke() ?? new Rect(work.Right - 96, work.Bottom - 96, 88, 88);
            var session = _card.View is GuideCardView.Session or GuideCardView.Notice;
            var compactBubble = session && _card.IsBubble;
            Width = Math.Min(work.Width, session ? (compactBubble ? 258 : WhalePrompt.WidthDip) : GuideTheme.CardWidth);
            MaxHeight = Math.Min(work.Height, session ? (compactBubble ? 260 : 320) : GuideTheme.CardMaxHeight);
            _surface.SetBubble(compactBubble);
            // Child visibility invalidation otherwise waits for the dispatcher, leaving a stale HWND height.
            _card.InvalidateMeasure();
            _surface.InvalidateMeasure();
            _surface.Measure(new Size(Width, MaxHeight));
            var size = new Size(Width, Math.Min(MaxHeight, Math.Max(30, _surface.DesiredSize.Height)));
            Rect? bubble = compactBubble && _control is { } control
                ? FeedbackLayout.Bubble(control, entry, work, size) : null;
            if (compactBubble && bubble is null)
            {
                // A cramped target gets the full attached reader, never an unrelated floating bubble.
                _card.SetPoint(false);
                _placing = false;
                Reposition();
                return;
            }
            Rect? reader = null;
            if (bubble is null && session && _locatedResult && _control is { } protectedControl)
            {
                reader = FeedbackLayout.Reader(protectedControl, entry, work, size);
                if (reader is null && !_card.IsCollapsed && !_avoidanceCollapsed)
                {
                    // Collapse once; an explicit reopen must not fall into an automatic-collapse loop.
                    _avoidanceCollapsed = true;
                    _card.Collapse();
                    _placing = false;
                    Reposition();
                    return;
                }
            }
            var bounds = bubble ?? reader ?? FeedbackLayout.Attached(entry, work, size);
            if (bubble is not null && _control is { } aim)
                _surface.SetBubble(true, new Point(aim.X + aim.Width / 2 - bounds.X, aim.Y + aim.Height / 2 - bounds.Y));
            Left = bounds.Left; Top = bounds.Top; Height = bounds.Height;
        }
        finally { _placing = false; }
    }

    void QueueReposition()
    {
        if (_closed || _repositionQueued) return;
        _repositionQueued = true;
        // ScrollViewer publishes its new extent during layout; an earlier measure can still return the old height.
        Dispatcher.BeginInvoke(DispatcherPriority.Loaded, new Action(() =>
        {
            _repositionQueued = false;
            if (!_closed) Reposition();
        }));
    }
}
