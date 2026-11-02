using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Data;

namespace CetaFae.Probe;

internal enum GuideCardView { Session, Menu, Consent, Notice }

/// <summary>The same fixed-data visual is rendered offscreen and hosted in the live window.</summary>
internal sealed class GuideCard : Border
{
    readonly TextBlock _title = new() { Text = "鲸汐解读", FontSize = 14, FontWeight = FontWeights.SemiBold, Foreground = Brushes.White, VerticalAlignment = VerticalAlignment.Center };
    readonly TextBlock _status = new() { TextWrapping = TextWrapping.Wrap, FontSize = 14, LineHeight = 23 };
    readonly TextBlock _disclosure;
    readonly DockPanel _question = new() { Margin = new Thickness(0, 12, 0, 0) };
    readonly TextBox _answer = new() { MaxLength = 300, AcceptsReturn = false, MinWidth = 120,
        Padding = new Thickness(8), VerticalContentAlignment = VerticalAlignment.Center,
        ToolTip = "点击输入，按 Enter 回答；最多300字" };
    readonly Button _submit;
    readonly Button _menu;
    readonly Button _cloud;
    readonly Button _region;
    readonly Button _exit;
    readonly Button _back;
    readonly Button _details;
    readonly Button _followup;
    readonly Border _header;
    readonly StackPanel _body = new();
    readonly StackPanel _pagination = new() { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Visibility = Visibility.Collapsed };
    readonly TextBlock _pageLabel = new() { VerticalAlignment = VerticalAlignment.Center, Foreground = GuideTheme.Muted, FontSize = 12 };
    readonly List<string> _pages = [];
    string _fullText = "";
    int _pageIndex;
    internal int PageCount => _pages.Count;
    internal int PageIndex => _pageIndex;
    internal string DisplayedText => _status.Text;
    internal TextBlock Heading => _title;
    internal Border Header => _header;
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
    internal string FullText => _fullText;
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
    internal event Action? FollowupRequested;

    internal GuideCard(GuideOptions options)
    {
        Background = GuideTheme.Surface;
        BorderBrush = GuideTheme.WhaleBlue;
        BorderThickness = new Thickness(2);
        CornerRadius = new CornerRadius(0, 0, 19, 19);
        Padding = new Thickness(0);
        TextElement.SetForeground(this, GuideTheme.Ink);
        TextElement.SetFontFamily(this, new FontFamily("Microsoft YaHei UI"));
        _disclosure = new TextBlock { Text = InstallationAgreement.Text(options), TextWrapping = TextWrapping.Wrap,
            FontSize = 12, LineHeight = 19, Foreground = GuideTheme.Muted };
        var layout = new DockPanel();
        var heading = new DockPanel();
        DismissButton = Button("×", () =>
        {
            if (View == GuideCardView.Session) Cancelled?.Invoke();
            else DismissNotice();
        });
        DismissButton.FontSize = 22;
        DismissButton.Padding = new Thickness(5, 0, 5, 0);
        DismissButton.Foreground = Brushes.White;
        DismissButton.Margin = new Thickness(8, 0, 0, 0);
        DismissButton.VerticalAlignment = VerticalAlignment.Center;
        DismissButton.ToolTip = "关闭提示";
        System.Windows.Automation.AutomationProperties.SetName(DismissButton, "关闭提示");
        DockPanel.SetDock(DismissButton, Dock.Right);
        heading.Children.Add(DismissButton);
        heading.Children.Add(_title);
        _header = new Border { Background = GuideTheme.CardHeader, MinHeight = 36, Padding = new Thickness(16, 6, 16, 6), Child = heading };
        DockPanel.SetDock(_header, Dock.Top);
        layout.Children.Add(_header);
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
        var body = _body;
        body.Children.Add(_disclosure);
        body.Children.Add(_status);
        body.Children.Add(_question);
        BodyScroll = new ScrollViewer { Content = body, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            VerticalScrollBarVisibility = ScrollBarVisibility.Disabled, Margin = new Thickness(16, 12, 16, 14) };
        _menu = Button("菜单", () => MenuRequested?.Invoke());
        _cloud = Button("使用与隐私说明", () =>
        {
            if (View != GuideCardView.Menu) return;
            ShowConsent();
        });
        _region = Button("选择识别区域", () => { if (HasRegion) RegionRequested?.Invoke(); });
        _exit = Button("退出", () => ExitRequested?.Invoke());
        _back = Button("返回", () =>
        {
            if (_hasSession) { View = GuideCardView.Session; Refresh(); }
            else { Reset(); Dismissed?.Invoke(); }
        });
        _details = Button("展开详情", () => { IsBubble = IsCollapsed = false; Refresh(); });
        _followup = Button("继续提问", () => { if (HasFollowup) FollowupRequested?.Invoke(); });
        foreach (var button in new[] { _details, _followup, _region, _cloud, _menu, _exit, _back }) Actions.Children.Add(button);
        var previous = Button("‹", () => ChangePage(-1));
        previous.ToolTip = "上一页";
        var next = Button("›", () => ChangePage(1));
        next.ToolTip = "下一页";
        _pagination.Children.Add(previous); _pagination.Children.Add(_pageLabel); _pagination.Children.Add(next);
        _pagination.Margin = new Thickness(12, 0, 10, 6);
        DockPanel.SetDock(_pagination, Dock.Bottom); layout.Children.Add(_pagination);
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
            Setters = { new Setter(BackgroundProperty, new SolidColorBrush(text == "×" ? Color.FromArgb(45, 255, 255, 255) : Color.FromRgb(226, 242, 255))) } });
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
        Refresh();
    }
    internal void SetStatus(string text) { _fullText = _status.Text = text; _pageIndex = 0; BodyScroll.ScrollToTop(); _hasSession = true; _followupAvailable = false; IsBubble = IsCollapsed = false; View = GuideCardView.Session; Refresh(); }
    internal void SetNotice(string text) { Reset(); _fullText = _status.Text = text; View = GuideCardView.Notice; Refresh(); }
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
        _fullText = _status.Text = "";
        _pages.Clear(); _pageIndex = 0; _pagination.Visibility = Visibility.Collapsed;
        BodyScroll.ScrollToTop();
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
        foreach (var button in _pagination.Children.OfType<Button>()) button.Focusable = enabled;
    }

    void ChangePage(int delta)
    {
        var next = Math.Clamp(_pageIndex + delta, 0, Math.Max(0, _pages.Count - 1));
        if (next == _pageIndex) return;
        _pageIndex = next;
        _status.Text = _pages[_pageIndex];
        UpdatePageLabel();
        PresentationChanged?.Invoke();
    }

    void UpdatePageLabel()
    {
        _pageLabel.Text = $"{_pageIndex + 1} / {_pages.Count}";
        ((Button)_pagination.Children[0]).IsEnabled = _pageIndex > 0;
        ((Button)_pagination.Children[2]).IsEnabled = _pageIndex + 1 < _pages.Count;
    }

    /// <summary>Grow width before paginating at the physical screen limit; never discard response text.</summary>
    internal Size Fit(Size available)
    {
        var maximumWidth = Math.Max(1, Math.Min(GuideTheme.CardMaxWidth, available.Width));
        var width = Math.Min(GuideTheme.CardWidth, maximumWidth);
        var maximumHeight = Math.Max(1, Math.Min(GuideTheme.CardMaxHeight, available.Height));
        double NaturalHeight()
        {
            _status.InvalidateMeasure(); _body.InvalidateMeasure();
            _header.InvalidateMeasure(); Actions.InvalidateMeasure(); _pagination.InvalidateMeasure();
            var innerWidth = Math.Max(1, width - BorderThickness.Left - BorderThickness.Right);
            _body.Measure(new Size(Math.Max(1, innerWidth - BodyScroll.Margin.Left - BodyScroll.Margin.Right), double.PositiveInfinity));
            _header.Measure(new Size(innerWidth, double.PositiveInfinity));
            Actions.Measure(new Size(innerWidth, double.PositiveInfinity));
            _pagination.Measure(new Size(innerWidth, double.PositiveInfinity));
            return BorderThickness.Top + BorderThickness.Bottom + _header.DesiredSize.Height + Actions.DesiredSize.Height +
                _pagination.DesiredSize.Height + (IsCollapsed ? 0 : _body.DesiredSize.Height + BodyScroll.Margin.Top + BodyScroll.Margin.Bottom);
        }
        _status.Text = _fullText;
        _pagination.Visibility = Visibility.Collapsed;
        _pages.Clear();
        var height = NaturalHeight();
        if (IsCollapsed || View == GuideCardView.Consent) return new Size(width, Math.Min(maximumHeight, height));
        while (height > maximumHeight && width < maximumWidth)
        {
            width = Math.Min(maximumWidth, width + 72);
            height = NaturalHeight();
        }
        if (height > maximumHeight && _fullText.Length > 0)
        {
            _pagination.Visibility = Visibility.Visible;
            var elements = StringInfo.ParseCombiningCharacters(_fullText);
            var start = 0;
            while (start < elements.Length)
            {
                var low = start + 1; var high = elements.Length; var end = low;
                while (low <= high)
                {
                    var middle = (low + high) / 2;
                    _status.Text = _fullText[elements[start]..(middle < elements.Length ? elements[middle] : _fullText.Length)];
                    if (NaturalHeight() <= maximumHeight) { end = middle; low = middle + 1; }
                    else high = middle - 1;
                }
                _pages.Add(_fullText[elements[start]..(end < elements.Length ? elements[end] : _fullText.Length)]);
                start = end;
            }
            _pageIndex = Math.Clamp(_pageIndex, 0, _pages.Count - 1);
            _status.Text = _pages[_pageIndex]; UpdatePageLabel();
            height = NaturalHeight();
        }
        else { _pageIndex = 0; _pages.Add(_fullText); }
        return new Size(width, Math.Min(maximumHeight, height));
    }

    void Refresh()
    {
        static void Visible(UIElement element, bool visible) => element.Visibility = visible ? Visibility.Visible : Visibility.Collapsed;
        var session = View == GuideCardView.Session;
        var notice = View == GuideCardView.Notice;
        var compact = session && IsCollapsed;
        var bubble = session && IsBubble;
        _title.Text = View == GuideCardView.Consent ? "使用与隐私" : "鲸汐解读";
        Visible(DismissButton, notice);
        DismissButton.ToolTip = session ? "结束本次求助" : "关闭提示";
        System.Windows.Automation.AutomationProperties.SetName(DismissButton, session ? "结束本次求助" : "关闭提示");
        Visible(_disclosure, View == GuideCardView.Consent);
        Visible(_status, session || notice);
        Visible(_question, HasQuestion && !compact && !bubble);
        Visible(_followup, HasFollowup && !compact && !bubble);
        Visible(_region, HasRegion && !compact && !bubble);
        Visible(_cloud, View == GuideCardView.Menu);
        Visible(_menu, false);
        Visible(_exit, View == GuideCardView.Menu);
        Visible(_back, !session && !notice);
        _back.Content = View == GuideCardView.Consent ? "关闭说明" : "返回";
        Visible(_details, session && (bubble || compact));
        _details.Content = compact ? "展开说明  ﹀" : "展开详情";
        Visible(_header, !compact);
        Visible(BodyScroll, !compact);
        Visible(Actions, !notice && Actions.Children.OfType<Button>().Any(button => button.Visibility == Visibility.Visible));
        Actions.Margin = compact ? new Thickness(10, 0, 10, 0) : new Thickness(10, 0, 10, 10);
        Padding = new Thickness(0);
        CornerRadius = session ? (bubble ? new CornerRadius(20, 20, 5, 20) : new CornerRadius(0, 0, 19, 19)) : new CornerRadius(16);
        _header.CornerRadius = session && !bubble ? new CornerRadius(0) : new CornerRadius(14, 14, 0, 0);
        _status.MaxHeight = double.PositiveInfinity;
        _status.TextTrimming = TextTrimming.None;
        _status.FontSize = 14;
        _status.LineHeight = 23;
        BodyScroll.VerticalScrollBarVisibility = View == GuideCardView.Consent ? ScrollBarVisibility.Auto : ScrollBarVisibility.Disabled;
        if (compact) _pagination.Visibility = Visibility.Collapsed;
        PresentationChanged?.Invoke();
    }
}
