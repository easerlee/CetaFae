using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Data;

namespace CetaFae.Probe;

internal enum GuideCardView { Session, Menu, Consent, ReferenceConsent, Notice }

/// <summary>The same fixed-data visual is rendered offscreen and hosted in the live window.</summary>
internal sealed class GuideCard : Border
{
    readonly TextBlock _caption = new() { FontSize = 12, Foreground = GuideTheme.Muted };
    readonly TextBlock _status = new() { TextWrapping = TextWrapping.Wrap, FontSize = 14, LineHeight = 23 };
    readonly TextBlock _disclosure;
    readonly TextBlock _referenceDisclosure = new() { TextWrapping = TextWrapping.Wrap, FontSize = 13, LineHeight = 21 };
    readonly TextBlock _referenceResult = new() { TextWrapping = TextWrapping.Wrap, FontSize = 13, LineHeight = 21, Margin = new Thickness(0, 10, 0, 0) };
    readonly DockPanel _question = new() { Margin = new Thickness(0, 12, 0, 0) };
    readonly TextBox _answer = new() { MaxLength = 300, AcceptsReturn = false, MinWidth = 120,
        Padding = new Thickness(8), VerticalContentAlignment = VerticalAlignment.Center,
        ToolTip = "点击输入，按 Enter 回答；最多300字" };
    readonly Button _submit;
    readonly Button _menu;
    readonly Button _cloud;
    readonly Button _clear;
    readonly Button _region;
    readonly Button _exit;
    readonly Button _back;
    readonly Button _reference;
    readonly Button _confirmReference;
    readonly Button _details;
    readonly Button _collapse;
    readonly Button _followup;
    readonly StackPanel _header;
    string? _referenceQuery;
    bool _cloudEnabled;
    bool _regionAvailable;
    bool _questionAvailable;
    bool _hasSession;
    bool _followupAvailable;

    internal GuideCardView View { get; private set; } = GuideCardView.Menu;
    internal bool HasQuestion => View == GuideCardView.Session && !IsCollapsed && !IsBubble && _questionAvailable;
    internal bool HasSession => _hasSession;
    internal bool HasRegion => View == GuideCardView.Session && !IsCollapsed && !IsBubble && _regionAvailable;
    internal bool IsBubble { get; private set; }
    internal bool IsCollapsed { get; private set; }
    internal bool HasFollowup => _followupAvailable && _cloudEnabled && _hasSession && View == GuideCardView.Session && !_questionAvailable;
    internal string FullText => _status.Text;
    internal (bool TextPresent, bool TextVisible, bool TextInViewport, bool ViewportReady) FeedbackState()
    {
        var ready = BodyScroll.ViewportWidth > 0 && BodyScroll.ViewportHeight > 0;
        var visible = _status.Visibility == Visibility.Visible && _status.ActualWidth > 0 && _status.ActualHeight > 0;
        var bounds = _status.TransformToAncestor(BodyScroll).TransformBounds(new Rect(_status.RenderSize));
        return (!string.IsNullOrWhiteSpace(_status.Text), visible,
            ready && visible && bounds.IntersectsWith(new Rect(0, 0, BodyScroll.ViewportWidth, BodyScroll.ViewportHeight)), ready);
    }
    internal event Action? PresentationChanged;
    internal ScrollViewer BodyScroll { get; }
    internal WrapPanel Actions { get; } = new() { Margin = new Thickness(0, 14, 0, 0) };
    internal Button DismissButton { get; }
    internal TextBox Answer => _answer;
    internal event Action? MenuRequested;
    internal event Action? Cancelled;
    internal event Action? RegionRequested;
    internal event Action? ExitRequested;
    internal event Action? Dismissed;
    internal event Action? AnswerFocusRequested;
    internal event Action<string>? AnswerSubmitted;
    internal event Action<string>? ReferenceRequested;
    internal event Action? FollowupRequested;

    internal GuideCard(GuideOptions options)
    {
        Background = GuideTheme.Surface;
        BorderBrush = GuideTheme.WhaleBlue;
        BorderThickness = new Thickness(2);
        CornerRadius = new CornerRadius(0, 0, 19, 19);
        Padding = GuideTheme.Padding;
        TextElement.SetForeground(this, GuideTheme.Ink);
        TextElement.SetFontFamily(this, new FontFamily("Microsoft YaHei UI"));
        _disclosure = new TextBlock { Text = InstallationAgreement.Text(options), TextWrapping = TextWrapping.Wrap,
            FontSize = 12, LineHeight = 19, Foreground = GuideTheme.Muted };
        var layout = new DockPanel();
        var header = _header = new StackPanel { Margin = new Thickness(0, 0, 0, 10) };
        header.Children.Add(new TextBlock { Text = "鲸汐说明", FontSize = 14, FontWeight = FontWeights.SemiBold,
            Foreground = GuideTheme.WhaleBlue, Margin = new Thickness(0, 0, 0, 4) });
        header.Children.Add(_caption);
        var heading = new DockPanel();
        DismissButton = Button("×", DismissNotice);
        DismissButton.FontSize = 22;
        DismissButton.Padding = new Thickness(5, 0, 5, 0);
        DismissButton.Margin = new Thickness(8, 0, 0, 0);
        DismissButton.VerticalAlignment = VerticalAlignment.Top;
        DismissButton.ToolTip = "关闭提示";
        System.Windows.Automation.AutomationProperties.SetName(DismissButton, "关闭提示");
        DockPanel.SetDock(DismissButton, Dock.Right);
        heading.Children.Add(DismissButton);
        heading.Children.Add(header);
        DockPanel.SetDock(heading, Dock.Top);
        layout.Children.Add(heading);
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
        body.Children.Add(_referenceDisclosure);
        body.Children.Add(_referenceResult);
        body.Children.Add(_question);
        BodyScroll = new ScrollViewer { Content = body, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
        _menu = Button("菜单", () => MenuRequested?.Invoke());
        _cloud = Button("使用与隐私说明", () =>
        {
            if (View != GuideCardView.Menu) return;
            ShowConsent();
        });
        _clear = Button("清除标记", () => { if (View == GuideCardView.Session) Cancelled?.Invoke(); });
        _region = Button("选择识别区域", () => { if (HasRegion) RegionRequested?.Invoke(); });
        _exit = Button("退出", () => ExitRequested?.Invoke());
        _back = Button("返回", () =>
        {
            if (_hasSession) { View = GuideCardView.Session; Refresh(); }
            else { Reset(); Dismissed?.Invoke(); }
        });
        _reference = Button("联网核对软件", () =>
        {
            if (View != GuideCardView.Session || IsCollapsed || IsBubble || !_cloudEnabled || _referenceQuery is null) return;
            _referenceDisclosure.Text = SoftwareReference.Disclosure(_referenceQuery);
            View = GuideCardView.ReferenceConsent; Refresh();
        });
        _confirmReference = Button("确认本次联网", () =>
        {
            if (View != GuideCardView.ReferenceConsent || !_cloudEnabled || _referenceQuery is null) return;
            var query = _referenceQuery;
            _referenceQuery = null; _referenceDisclosure.Text = "";
            View = GuideCardView.Session; Refresh(); ReferenceRequested?.Invoke(query);
        });
        _details = Button("展开详情", () => { IsBubble = IsCollapsed = false; Refresh(); });
        _collapse = Button("收起", Collapse);
        _followup = Button("继续提问", () => { if (HasFollowup) FollowupRequested?.Invoke(); });
        foreach (var button in new[] { _details, _followup, _region, _reference, _confirmReference, _cloud, _clear, _collapse, _menu, _exit, _back }) Actions.Children.Add(button);
        DockPanel.SetDock(Actions, Dock.Bottom);
        layout.Children.Add(Actions);
        layout.Children.Add(BodyScroll);
        Child = layout;
        Refresh();
    }

    static Button Button(string text, Action clicked)
    {
        var button = new Button { Content = text, Padding = new Thickness(8, 5, 8, 5), MinHeight = 30,
            Margin = new Thickness(0, 0, 6, 2), FontSize = 12, Background = Brushes.Transparent,
            Foreground = GuideTheme.WhaleBlue, Cursor = Cursors.Hand };
        var border = new FrameworkElementFactory(typeof(Border));
        border.SetValue(Border.CornerRadiusProperty, new CornerRadius(8));
        border.SetBinding(Border.BackgroundProperty, new Binding(nameof(Background)) { RelativeSource = RelativeSource.TemplatedParent });
        border.SetBinding(Border.PaddingProperty, new Binding(nameof(Padding)) { RelativeSource = RelativeSource.TemplatedParent });
        border.AppendChild(new FrameworkElementFactory(typeof(ContentPresenter)));
        var template = new ControlTemplate(typeof(Button)) { VisualTree = border };
        template.Triggers.Add(new Trigger { Property = IsMouseOverProperty, Value = true,
            Setters = { new Setter(BackgroundProperty, new SolidColorBrush(Color.FromRgb(226, 242, 255))) } });
        button.Template = template;
        button.Click += (_, _) => clicked();
        return button;
    }

    void Submit()
    {
        if (HasQuestion && !string.IsNullOrWhiteSpace(_answer.Text)) AnswerSubmitted?.Invoke(_answer.Text);
    }

    internal void SetCloud(bool enabled)
    {
        _cloudEnabled = enabled;
        if (!enabled) { SetReferenceQuery(null); _referenceResult.Text = ""; if (View == GuideCardView.ReferenceConsent) View = GuideCardView.Session; }
        Refresh();
    }
    internal void SetReferenceQuery(string? query) { _referenceQuery = query; _referenceDisclosure.Text = ""; Refresh(); }
    internal void SetReferenceResult(string result) { _referenceResult.Text = result; Refresh(); }
    internal void SetStatus(string text) { _status.Text = text; BodyScroll.ScrollToTop(); _hasSession = true; _followupAvailable = false; IsBubble = IsCollapsed = false; View = GuideCardView.Session; Refresh(); }
    internal void SetNotice(string text) { Reset(); _status.Text = text; View = GuideCardView.Notice; Refresh(); }
    internal void DismissNotice()
    {
        if (View != GuideCardView.Notice) return;
        Reset(); Dismissed?.Invoke();
    }
    internal void SetFollowupAvailable(bool available) { _followupAvailable = available; Refresh(); }
    internal void SetPoint(bool visible) { IsBubble = visible && _hasSession && View == GuideCardView.Session; IsCollapsed = false; Refresh(); }
    internal void Collapse() { if (View != GuideCardView.Session) return; IsBubble = false; IsCollapsed = true; Refresh(); }
    internal void ShowMenu() { View = GuideCardView.Menu; Refresh(); }
    internal void ShowConsent() { View = GuideCardView.Consent; Refresh(); }
    internal void ShowQuestion() { _questionAvailable = true; Refresh(); }
    internal void SetRegionAvailable(bool available) { _regionAvailable = available; Refresh(); }
    internal void ClearQuestion() { _answer.Clear(); _questionAvailable = false; Refresh(); }
    internal void Reset()
    {
        _answer.Clear();
        _status.Text = "";
        BodyScroll.ScrollToTop();
        _referenceQuery = null;
        _referenceDisclosure.Text = _referenceResult.Text = "";
        _hasSession = _questionAvailable = _regionAvailable = false;
        _followupAvailable = false;
        IsBubble = IsCollapsed = false;
        View = GuideCardView.Menu;
        Refresh();
    }
    internal void SetKeyboardAccess(bool enabled)
    {
        _answer.Focusable = enabled;
        _submit.Focusable = enabled;
        DismissButton.Focusable = enabled;
        foreach (Button button in Actions.Children) button.Focusable = enabled;
    }

    void Refresh()
    {
        static void Visible(UIElement element, bool visible) => element.Visibility = visible ? Visibility.Visible : Visibility.Collapsed;
        var session = View == GuideCardView.Session;
        var notice = View == GuideCardView.Notice;
        var compact = session && IsCollapsed;
        var bubble = session && IsBubble;
        _caption.Text = bubble ? "就在这里 · 仅指路，未修改" : View == GuideCardView.ReferenceConsent ? "可选 · 单次公共资料查询" : View == GuideCardView.Consent ? "开始前，请了解发送与留存范围" :
            View == GuideCardView.Menu ? (_cloudEnabled ? "可以求助 · 由你操作目标软件" : "尚未同意 · 请先了解使用说明") : notice ? "" : "解释与指路 · 由你操作";
        Visible(_caption, !notice);
        Visible(DismissButton, notice);
        Visible(Actions, !notice);
        Visible(_disclosure, View == GuideCardView.Consent);
        Visible(_status, session || notice);
        Visible(_referenceDisclosure, View == GuideCardView.ReferenceConsent);
        Visible(_referenceResult, View == GuideCardView.Session && _referenceResult.Text.Length > 0);
        Visible(_reference, session && !compact && !bubble && _cloudEnabled && _referenceQuery is not null);
        Visible(_confirmReference, View == GuideCardView.ReferenceConsent && _cloudEnabled && _referenceQuery is not null);
        Visible(_question, HasQuestion && !compact && !bubble);
        Visible(_followup, HasFollowup && !compact);
        Visible(_region, HasRegion && !compact && !bubble);
        Visible(_cloud, View == GuideCardView.Menu);
        Visible(_clear, session && !compact && !bubble);
        Visible(_menu, false);
        Visible(_exit, View == GuideCardView.Menu);
        Visible(_back, !session && !notice);
        _back.Content = View == GuideCardView.Consent ? "关闭说明" : "返回";
        Visible(_details, session && (bubble || compact));
        _details.Content = compact ? "展开说明  ﹀" : "展开详情";
        Visible(_collapse, session && !compact);
        Visible(_header, !compact);
        Visible(BodyScroll, !compact);
        Actions.Margin = new Thickness(0, compact ? 0 : 10, 0, 0);
        Padding = compact ? new Thickness(10, 0, 10, 0) : GuideTheme.Padding;
        CornerRadius = session ? (bubble ? new CornerRadius(20, 20, 5, 20) : new CornerRadius(0, 0, 19, 19)) : new CornerRadius(16);
        _status.MaxHeight = bubble ? 92 : double.PositiveInfinity;
        _status.TextTrimming = bubble ? TextTrimming.CharacterEllipsis : TextTrimming.None;
        _status.FontSize = bubble ? 13 : 14;
        _status.LineHeight = bubble ? 22 : 23;
        BodyScroll.VerticalScrollBarVisibility = bubble ? ScrollBarVisibility.Disabled : ScrollBarVisibility.Auto;
        PresentationChanged?.Invoke();
    }
}
