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
    Func<bool>? _topAttached;
    Func<Rect>? _workspace;
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
    internal void BeginProcessing()
    {
        _presentationRevision++;
        _control = null; _locatedResult = _avoidanceCollapsed = false;
        _card.Reset(); Hide();
    }
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
    internal void BindEntry(Func<Rect> entry, Func<double> scale, Func<bool> hasTarget, Func<bool>? topAttached = null,
        Func<Rect>? workspace = null)
    { _entry = entry; _scale = scale; _hasTarget = hasTarget; _topAttached = topAttached; _workspace = workspace; }
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
    public void EndSession() { _presentationRevision++; _control = null; _locatedResult = _avoidanceCollapsed = false; _card.Reset(); Hide(); }

    internal static string Disclosure(GuideOptions options) =>
        "隐私说明\n\n" +
        "一、识别用途、发送范围和接收方\n" +
        $"当前接收地址：{options.BaseUrl.AbsoluteUri}\n当前模型：{options.Model}\n" +
        (options.IncludeImage
            ? "当前为图文模式。目标未明时，鲸汐可能发送所选整窗画面及可用控件文字，以解释软件和界面用途。目标明确后，优先发送可靠局部画面、范围内控件文字及用户输入的回答；没有匹配的局部规则时，可发送所选整窗画面和可用文字来回答问题。本机可识读该范围内的文字及位置，并随图片提供给模型，用于提示文字位置；这不代表按钮的点击范围。你可以主动框选整个目标窗口。已声明的局部范围无法定位时等待你框选，不改发整窗；主动选区不会自动扩大。截图仅针对所选窗口。部分截图方式会在本机内存绘制所选整窗，再裁剪出发送范围。\n"
            : "当前为纯文字模式。目标未明时发送所选窗口控件文字。目标明确后，优先发送可靠局部范围或你框选范围内的控件文字及用户输入的回答；没有匹配局部规则时，可发送所选窗口的可用控件文字，不含图片。已有局部规则无法定位时等待你框选，主动选区不自动扩大。\n") +
        "自由问答仅在你发送时，将你输入的文字和本轮最近四组问答发送给上方接收方。窗口绑定后先根据文字判断是否需要界面信息，普通问题直接回答，需要界面时按上方范围重新读取；本轮问答可含已给出的窗口说明。待命问答不读取窗口、文件或剪贴板。关闭时清除本机本轮问答，请勿输入敏感信息。\n" +
        "接收地址可能是代理，代理可能继续转发。鲸汐尚未核实代理与模型服务商的实际留存期限及处理地点。现阶段仅用于开发验证，正式发布前仍须核实。请先隐藏不愿发送的内容。鲸汐尽力检查密码字段和明显密钥，发现时停止识别。部分软件提供的检查信息有限，检查可能遗漏敏感内容。鲸汐不会读取密码框的值。\n\n" +
        "二、何时读取与本机处理\n" +
        "拖放鲸鱼、提交回答或确认选区时，鲸汐读取所选窗口。显示指路位置期间，鲸汐在本机复核窗口变化。切换窗口时，已送出的识别会继续，返回所选窗口后才显示结果；如果首次读取中途被打断，返回时继续这次识别。鲸汐不会读取你切去的窗口。连续指导最多8步，可发送原始目标、本轮问题及步骤编号，旧模型回答不随请求发送。启动本身不读取窗口。\n" +
        "为核对所选软件，鲸汐可在本机读取产品名、描述、版本、公司、离线签名和包身份。程序路径只用于本机读取。上述归属字段以及窗口标题不作为独立元数据发送给模型，但画面本身可能显示软件名或标题。若使用可选的联网核对，鲸汐会先展示产品名称查询词和公共百科接收方，再由你单独确认发送。\n\n" +
        "三、保存期限与撤回\n" +
        "客户端不保存画面、控件原文和问答。会话结束时清除显示与可复用引用。元数据日志默认保留7天，按小时清理，清理失败时可能延迟。\n" +
        "安装同意按当前 Windows 用户及已展示的配置保存在本机。你可以在“鲸汐设置 → 隐私”撤回同意，停止新采集和请求。已发送内容无法从上游撤回。撤回后，或接收地址、模型、发送范围或条款改变后，再次使用识别功能须通过安装程序确认。";
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
            var work = _workspace?.Invoke() ?? SystemParameters.WorkArea;
            var entry = _entry?.Invoke() ?? new Rect(work.Right - 96, work.Bottom - 96, 88, 88);
            var session = _card.View is GuideCardView.Session or GuideCardView.Notice;
            var compactBubble = session && _card.IsBubble;
            var topAttached = _hasTarget?.Invoke() == true && _topAttached?.Invoke() == true;
            _surface.SetBubble(compactBubble);
            var limit = FeedbackLayout.AvailableWidth(entry, work, topAttached);
            var preferredWidth = _card.View == GuideCardView.Session && _hasTarget?.Invoke() == true ? entry.Width : (double?)null;
            var size = _surface.Fit(new Size(Math.Min(work.Width, limit), work.Height), preferredWidth);
            Width = size.Width;
            MaxHeight = Math.Min(work.Height, GuideTheme.CardMaxHeight + (compactBubble ? 20 : 0));
            _surface.InvalidateMeasure();
            _surface.Measure(size);
            Rect? bubble = compactBubble && _control is { } control
                ? (topAttached ? FeedbackLayout.Reader(control, entry, work, size, true) : FeedbackLayout.Bubble(control, entry, work, size)) : null;
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
                reader = FeedbackLayout.Reader(protectedControl, entry, work, size, topAttached);
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
            var bounds = bubble ?? reader ?? FeedbackLayout.Attached(entry, work, size, topAttached);
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
