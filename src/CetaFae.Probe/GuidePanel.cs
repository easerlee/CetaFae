using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;

namespace CetaFae.Probe;

public sealed class GuidePanel : Window
{
    readonly TextBlock _status = new() { TextWrapping = TextWrapping.Wrap, FontSize = 14, LineHeight = 22 };
    readonly Button _cloud = new() { Content = "同意并启用云端", Padding = new Thickness(12, 6, 12, 6) };
    readonly TextBox _answer = new() { MaxLength = 300, AcceptsReturn = false, MinWidth = 160,
        VerticalContentAlignment = VerticalAlignment.Center, Padding = new Thickness(6),
        ToolTip = "点击后输入你想处理的事，按 Enter 回答" };
    readonly DockPanel _question = new() { Visibility = Visibility.Collapsed, Margin = new Thickness(0, 0, 0, 12) };
    public event Action? CloudToggled;
    public event Action? Cancelled;
    public event Action<string>? AnswerSubmitted;
    public bool IsAnswering => _question.Visibility == Visibility.Visible && IsActive;
    public IntPtr Handle => new WindowInteropHelper(this).Handle;

    public GuidePanel(GuideOptions options)
    {
        Title = "鲸汐";
        WindowStyle = WindowStyle.None;
        ResizeMode = ResizeMode.NoResize;
        ShowInTaskbar = true;
        ShowActivated = false;
        Topmost = true;
        Width = Math.Min(360, SystemParameters.WorkArea.Width - 24);
        MaxHeight = Math.Min(460, SystemParameters.WorkArea.Height - 24);
        SizeToContent = SizeToContent.Height;
        var body = new StackPanel();
        body.Children.Add(new TextBlock { Text = "鲸汐", FontWeight = FontWeights.SemiBold,
            FontSize = 16, Margin = new Thickness(0, 0, 0, 10) });
        body.Children.Add(new TextBlock
        {
            Text = $"启用后，仅在拖放时读取目标窗口，将{(options.IncludeImage ? "窗口画面及控件文字" : "控件文字（不含图片）")}发送至\n" +
                $"{options.BaseUrl.GetLeftPart(UriPartial.Authority)}\n模型：{options.Model}。代理可能转发至上游服务，留存策略尚未核实。拒绝则不读取、不发送；随时可关闭。",
            TextWrapping = TextWrapping.Wrap, FontSize = 12, Foreground = Brushes.DimGray,
            Margin = new Thickness(0, 0, 0, 12)
        });
        body.Children.Add(new ScrollViewer { Content = _status, MaxHeight = 170,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Margin = new Thickness(0, 0, 0, 12) });
        var submit = new Button { Content = "回答", Padding = new Thickness(10, 6, 10, 6),
            Margin = new Thickness(8, 0, 0, 0) };
        var actions = new StackPanel { Orientation = Orientation.Horizontal };
        void Submit()
        {
            if (!string.IsNullOrWhiteSpace(_answer.Text)) AnswerSubmitted?.Invoke(_answer.Text);
        }
        submit.Click += (_, _) => Submit();
        _answer.PreviewMouseLeftButtonDown += (_, _) =>
        {
            // Activation is permitted only by the user's deliberate click in this transient field.
            SetKeyboardAccess(true);
            Activate();
            _answer.Focus();
        };
        _answer.PreviewKeyDown += (_, e) =>
        {
            if (e.Key == Key.Enter) { Submit(); e.Handled = true; }
        };
        DockPanel.SetDock(submit, Dock.Right);
        _question.Children.Add(submit);
        _question.Children.Add(_answer);
        body.Children.Add(_question);
        _cloud.Click += async (_, _) =>
        {
            var elapsed = Stopwatch.StartNew();
            CloudToggled?.Invoke();
            // Sample the foreground without activating either window or reading its contents.
            foreach (var delay in new[] { 0, 200, 800 })
            {
                if (delay > 0) await Task.Delay(delay);
                ProbeLog.Log("guide_consent_focus", ("elapsedMs", elapsed.ElapsedMilliseconds),
                    ("fg", Native.Hex(Native.GetForegroundWindow())), ("panelActive", IsActive));
            }
        };
        actions.Children.Add(_cloud);
        var stop = new Button { Content = "清除标记", Padding = new Thickness(10, 6, 10, 6), Margin = new Thickness(8, 0, 0, 0) };
        stop.Click += (_, _) => Cancelled?.Invoke();
        actions.Children.Add(stop);
        var close = new Button { Content = "退出", Padding = new Thickness(10, 6, 10, 6), Margin = new Thickness(8, 0, 0, 0) };
        close.Click += (_, _) => Application.Current.Shutdown();
        actions.Children.Add(close);
        body.Children.Add(actions);
        // WPF buttons may request keyboard focus even on a WS_EX_NOACTIVATE window.
        // Enable keyboard navigation only after an explicit activation (for example, from the taskbar).
        void SetKeyboardAccess(bool active)
        {
            Focusable = active;
            _answer.Focusable = active;
            submit.Focusable = active;
            foreach (Button button in actions.Children) button.Focusable = active;
        }
        SetKeyboardAccess(false);
        Activated += (_, _) =>
        {
            SetKeyboardAccess(true);
            ProbeLog.Log("guide_panel_activation", ("active", true), ("fg", Native.Hex(Native.GetForegroundWindow())));
        };
        Deactivated += (_, _) =>
        {
            SetKeyboardAccess(false);
            ProbeLog.Log("guide_panel_activation", ("active", false), ("fg", Native.Hex(Native.GetForegroundWindow())));
        };
        Content = new Border { Background = Brushes.White, BorderBrush = Brushes.SlateGray,
            BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(8), Padding = new Thickness(16), Child = body };
        SourceInitialized += (_, _) =>
        {
            var hwnd = new WindowInteropHelper(this).Handle;
            Native.SetWindowLong(hwnd, Native.GWL_EXSTYLE, Native.GetWindowLong(hwnd, Native.GWL_EXSTYLE) |
                Native.WS_EX_NOACTIVATE);
            HwndSource.FromHwnd(hwnd)!.AddHook(PreventMouseActivation);
        };
        PreviewKeyDown += (_, e) => { if (e.Key == Key.Escape) { Cancelled?.Invoke(); e.Handled = true; } };
        SetStatus("尚未启用。此验证版仅解释和指路，不会替你点击或修改设置。");
        Place(null);
    }

    public void SetCloud(bool enabled) => _cloud.Content = enabled ? "关闭云端" : "同意并启用云端";
    public void SetStatus(string message) => _status.Text = message;
    public void ShowQuestion() => _question.Visibility = Visibility.Visible;
    public void ClearQuestion()
    {
        _answer.Clear();
        _question.Visibility = Visibility.Collapsed;
    }

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

    public void Place(Rect? target, Rect? avoid = null)
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
            if (!work.Contains(rect) || avoid is { } marker && rect.IntersectsWith(marker)) continue;
            Left = point.X;
            Top = point.Y;
            return;
        }
        Left = work.Left + 8;
        Top = work.Top + 8;
    }
}
