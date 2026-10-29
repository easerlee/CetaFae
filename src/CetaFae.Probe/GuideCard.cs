using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;

namespace CetaFae.Probe;

internal enum GuideCardView { Session, Menu, Consent }

/// <summary>The same fixed-data visual is rendered offscreen and hosted in the live window.</summary>
internal sealed class GuideCard : Border
{
    readonly TextBlock _caption = new() { FontSize = 12, Foreground = GuideTheme.Muted };
    readonly TextBlock _status = new() { TextWrapping = TextWrapping.Wrap, FontSize = 14, LineHeight = 23 };
    readonly TextBlock _disclosure;
    readonly DockPanel _question = new() { Margin = new Thickness(0, 12, 0, 0) };
    readonly TextBox _answer = new() { MaxLength = 300, AcceptsReturn = false, MinWidth = 120,
        Padding = new Thickness(8), VerticalContentAlignment = VerticalAlignment.Center,
        ToolTip = "点击输入，按 Enter 回答；最多300字" };
    readonly Button _submit;
    readonly Button _menu;
    readonly Button _cloud;
    readonly Button _consent;
    readonly Button _clear;
    readonly Button _region;
    readonly Button _exit;
    readonly Button _back;
    bool _cloudEnabled;
    bool _regionAvailable;
    bool _questionAvailable;
    bool _hasSession;

    internal GuideCardView View { get; private set; } = GuideCardView.Menu;
    internal bool HasQuestion => View == GuideCardView.Session && _questionAvailable;
    internal bool HasSession => _hasSession;
    internal bool HasRegion => View == GuideCardView.Session && _regionAvailable;
    internal ScrollViewer BodyScroll { get; }
    internal WrapPanel Actions { get; } = new() { Margin = new Thickness(0, 14, 0, 0) };
    internal TextBox Answer => _answer;
    internal event Action? CloudToggled;
    internal event Action? Cancelled;
    internal event Action? RegionRequested;
    internal event Action? ExitRequested;
    internal event Action? Dismissed;
    internal event Action? AnswerFocusRequested;
    internal event Action<string>? AnswerSubmitted;

    internal GuideCard(GuideOptions options)
    {
        Background = GuideTheme.Surface;
        BorderBrush = GuideTheme.Outline;
        BorderThickness = new Thickness(1);
        CornerRadius = GuideTheme.Radius;
        Padding = GuideTheme.Padding;
        TextElement.SetForeground(this, GuideTheme.Ink);
        TextElement.SetFontFamily(this, new FontFamily("Microsoft YaHei UI"));
        _disclosure = new TextBlock { Text = GuidePanel.Disclosure(options), TextWrapping = TextWrapping.Wrap,
            FontSize = 12, LineHeight = 19, Foreground = GuideTheme.Muted };
        var layout = new DockPanel();
        var header = new StackPanel { Margin = new Thickness(0, 0, 0, 14) };
        header.Children.Add(new TextBlock { Text = "鲸汐", FontSize = 18, FontWeight = FontWeights.SemiBold,
            Foreground = GuideTheme.Accent, Margin = new Thickness(0, 0, 0, 4) });
        header.Children.Add(_caption);
        DockPanel.SetDock(header, Dock.Top);
        layout.Children.Add(header);
        _submit = Button("回答", Submit);
        DockPanel.SetDock(_submit, Dock.Right);
        _submit.Margin = new Thickness(8, 0, 0, 0);
        _question.Children.Add(_submit);
        _question.Children.Add(_answer);
        _answer.PreviewMouseLeftButtonDown += (_, _) => AnswerFocusRequested?.Invoke();
        _answer.PreviewKeyDown += (_, e) =>
        {
            if (e.Key == Key.Enter) { Submit(); e.Handled = true; }
        };
        var body = new StackPanel();
        body.Children.Add(_disclosure);
        body.Children.Add(_status);
        body.Children.Add(_question);
        BodyScroll = new ScrollViewer { Content = body, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
        _menu = Button("菜单", ShowMenu);
        _cloud = Button("查看云端授权", () => { if (_cloudEnabled) CloudToggled?.Invoke(); else ShowConsent(); });
        _consent = Button("同意并启用云端", () =>
        {
            // A hidden button or stale callback cannot bypass the visible disclosure state.
            if (View == GuideCardView.Consent && !_cloudEnabled) CloudToggled?.Invoke();
        });
        _clear = Button("清除标记", () => Cancelled?.Invoke());
        _region = Button("选择识别区域", () => { if (HasRegion) RegionRequested?.Invoke(); });
        _exit = Button("退出", () => ExitRequested?.Invoke());
        _back = Button("返回", () => { if (_hasSession) { View = GuideCardView.Session; Refresh(); } else Dismissed?.Invoke(); });
        foreach (var button in new[] { _region, _consent, _cloud, _clear, _menu, _exit, _back }) Actions.Children.Add(button);
        DockPanel.SetDock(Actions, Dock.Bottom);
        layout.Children.Add(Actions);
        layout.Children.Add(BodyScroll);
        Child = layout;
        Refresh();
    }

    static Button Button(string text, Action clicked)
    {
        var button = new Button { Content = text, Padding = new Thickness(10, 7, 10, 7), MinHeight = 32,
            Margin = new Thickness(0, 0, 8, 6), FontSize = 12 };
        button.Click += (_, _) => clicked();
        return button;
    }

    void Submit()
    {
        if (HasQuestion && !string.IsNullOrWhiteSpace(_answer.Text)) AnswerSubmitted?.Invoke(_answer.Text);
    }

    internal void SetCloud(bool enabled) { _cloudEnabled = enabled; Refresh(); }
    internal void SetStatus(string text) { _status.Text = text; _hasSession = true; View = GuideCardView.Session; Refresh(); }
    internal void ShowMenu() { View = GuideCardView.Menu; Refresh(); }
    internal void ShowConsent() { View = GuideCardView.Consent; Refresh(); }
    internal void ShowQuestion() { _questionAvailable = true; Refresh(); }
    internal void SetRegionAvailable(bool available) { _regionAvailable = available; Refresh(); }
    internal void ClearQuestion() { _answer.Clear(); _questionAvailable = false; Refresh(); }
    internal void Reset()
    {
        _answer.Clear();
        _status.Text = "";
        _hasSession = _questionAvailable = _regionAvailable = false;
        View = GuideCardView.Menu;
        Refresh();
    }
    internal void SetKeyboardAccess(bool enabled)
    {
        _answer.Focusable = enabled;
        _submit.Focusable = enabled;
        foreach (Button button in Actions.Children) button.Focusable = enabled;
    }

    void Refresh()
    {
        static void Visible(UIElement element, bool visible) => element.Visibility = visible ? Visibility.Visible : Visibility.Collapsed;
        _caption.Text = View == GuideCardView.Consent ? "启用前，请了解发送与留存范围" :
            View == GuideCardView.Menu ? (_cloudEnabled ? "云端已启用 · 由你操作目标软件" : "云端关闭 · 拖动入口即可选窗") : "当前窗口 · 解释与指路";
        Visible(_disclosure, View == GuideCardView.Consent);
        Visible(_status, View == GuideCardView.Session);
        Visible(_question, HasQuestion);
        Visible(_region, HasRegion);
        Visible(_consent, View == GuideCardView.Consent && !_cloudEnabled);
        Visible(_cloud, View != GuideCardView.Consent);
        _cloud.Content = _cloudEnabled ? "关闭云端" : "查看云端授权";
        Visible(_clear, View != GuideCardView.Consent && _hasSession);
        Visible(_menu, View == GuideCardView.Session);
        Visible(_exit, View == GuideCardView.Menu);
        Visible(_back, View != GuideCardView.Session);
    }
}
