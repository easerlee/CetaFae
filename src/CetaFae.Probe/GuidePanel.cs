using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;

namespace CetaFae.Probe;

public sealed class GuidePanel : Window
{
    readonly GuideCard _card;
    public event Action? CloudToggled;
    public event Action? Cancelled;
    public event Action<string>? AnswerSubmitted;
    public event Action? RegionRequested;
    public event Action<string>? ReferenceRequested;
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
        Width = Math.Min(GuideTheme.CardWidth, SystemParameters.WorkArea.Width - 24);
        MaxHeight = Math.Min(GuideTheme.CardMaxHeight, SystemParameters.WorkArea.Height - 24);
        SizeToContent = SizeToContent.Height;
        _card = new GuideCard(options);
        Content = _card;
        _card.CloudToggled += () => CloudToggled?.Invoke();
        _card.Cancelled += () => Cancelled?.Invoke();
        _card.AnswerSubmitted += text => AnswerSubmitted?.Invoke(text);
        _card.RegionRequested += () => RegionRequested?.Invoke();
        _card.ReferenceRequested += query => ReferenceRequested?.Invoke(query);
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
        Place(null);
    }

    void SetKeyboardAccess(bool active)
    {
        Focusable = active;
        _card.SetKeyboardAccess(active);
        ProbeLog.Log("guide_panel_activation", ("active", active), ("fg", Native.Hex(Native.GetForegroundWindow())));
    }

    public void SetCloud(bool enabled) => _card.SetCloud(enabled);
    public void SetStatus(string message) { _card.SetStatus(message); if (!IsVisible) Show(); }
    public void ShowMenu() { _card.ShowMenu(); if (!IsVisible) Show(); }
    public void ShowQuestion() => _card.ShowQuestion();
    public void SetRegionAvailable(bool available) => _card.SetRegionAvailable(available);
    public void ClearQuestion() => _card.ClearQuestion();
    public void SetReferenceQuery(string? query) => _card.SetReferenceQuery(query);
    public void SetReferenceResult(string result) => _card.SetReferenceResult(result);
    public void EndSession() { _card.Reset(); Hide(); }

    internal static string Disclosure(GuideOptions options) =>
        "开发验证版。启用后，拖放及提交回答会读取所选窗口；展示期间按窗口变化在本机复核控件。\n" +
        "初次拖入先识别软件和界面用途，不要求已有操作目标；所属进程仅在本机显示，不发送进程名或窗口标题元数据。画面本身可能包含软件名或标题。\n" +
        "身份插件可在本机读取所选程序的产品名、描述、版本、公司、离线签名及包身份；路径仅用于读取，不记录或外发。这些字段不发给模型。可选联网核对会另行展示产品名查询词和公共百科接收方，须再次亲自确认；不默认联网。\n" +
        (options.IncludeImage ? "通用视觉插件可尝试理解未适配的软件，不代表所有软件已正式支持。目标未明时可能发送所选整窗画面；明确目标后发送可靠适配范围的局部画面，或由你框选的区域（可主动选整个目标窗）、范围内控件文字及用户输入的回答。范围不明时先等待选区，不自动改发整窗。PrintWindow先在本机内存绘制所选整窗，再裁剪外发。\n" :
            "目标未明时发送所选窗口控件文字；明确目标后仅发送适配或你框选范围内文字及用户输入的回答，不含图片；范围不明先等待选区。\n") +
        $"接收地址：{options.BaseUrl.AbsoluteUri}\n模型：{options.Model}。\n" +
        "客户端不保存画面、控件原文和问答；会话结束清除显示与可复用引用。元数据日志保留7天，按小时清理；清理失败可能延迟。\n" +
        "代理可能转发；代理与模型服务商留存均未核实，不具备正式发布条件。\n" +
        "拒绝或关闭后不采集识别内容、不发新请求。可随时关闭；已发送内容无法从上游撤回。";
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

    public void Place(Rect? target, Rect? avoid = null, Rect? entry = null)
    {
        var work = SystemParameters.WorkArea;
        var frame = target ?? work;
        var positions = new[]
        {
            new Point(frame.Right + 8, frame.Top), new Point(frame.Left - Width - 8, frame.Top),
            new Point(work.Right - Width - 8, work.Top + 8), new Point(work.Left + 8, work.Top + 8),
            new Point(work.Right - Width - 8, work.Bottom - MaxHeight - 8),
            new Point(work.Left + 8, work.Bottom - MaxHeight - 8)
        };
        foreach (var point in positions)
        {
            var rect = new Rect(point, new Size(Width, MaxHeight));
            if (!work.Contains(rect) || avoid is { } marker && rect.IntersectsWith(marker) ||
                entry is { } input && rect.IntersectsWith(input)) continue;
            Left = point.X;
            Top = point.Y;
            return;
        }
        Left = work.Left + 8;
        Top = work.Top + 8;
    }
}
