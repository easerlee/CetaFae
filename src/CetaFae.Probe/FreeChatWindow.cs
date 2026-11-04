using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Media.Animation;
using System.Windows.Shapes;

namespace CetaFae.Probe;

internal sealed class FreeChatWindow : Window
{
    internal const double OuterRadius = 8;
    internal const double InputHeight = 32;
    internal const double FramePadding = 8;
    internal const double RestingWidth = 240;
    internal const double ConversationWidth = 380;
    internal const double ConversationMaxHeight = 480;
    internal const double ConversationFontSize = 16;
    internal const double InputRadius = OuterRadius * InputHeight / (InputHeight + FramePadding * 2);
    readonly StackPanel _turns = new() { Margin = new Thickness(12) };
    readonly ScrollViewer _transcript;
    readonly Border _transcriptSurface;
    readonly TextBlock _status = new() { Foreground = GuideTheme.Ink, TextWrapping = TextWrapping.Wrap };
    readonly TextBlock _hint = new() { Text = "输入你的问题", Foreground = GuideTheme.Muted,
        VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(6, 0, 0, 0), IsHitTestVisible = false };
    readonly Border _statusSurface;
    readonly StackPanel _waiting = new() { Orientation = Orientation.Horizontal, Height = 44,
        Margin = new Thickness(12, 0, 12, 0), Visibility = Visibility.Collapsed };
    readonly TextBox _input = new() { MaxLength = FreeChatSession.MaxQuestionLength, MinHeight = 30,
        BorderThickness = new Thickness(0), Padding = new Thickness(0), Background = Brushes.Transparent, VerticalContentAlignment = VerticalAlignment.Center,
        Foreground = GuideTheme.Ink };
    readonly Button _send;
    readonly Button _clear;
    bool _busy;
    bool _started;
    internal event Action<string>? Submitted;
    internal event Action? Dismissed;
    internal event Action? Cleared;
    internal IntPtr Handle => new WindowInteropHelper(this).Handle;
    internal TextBox Input => _input;
    internal Button Send => _send;
    internal Button ClearButton => _clear;
    internal ScrollViewer Transcript => _transcript;
    internal StackPanel Waiting => _waiting;

    internal FreeChatWindow()
    {
        WindowStyle = WindowStyle.None;
        AllowsTransparency = true;
        Background = Brushes.Transparent;
        ShowInTaskbar = false;
        Topmost = true;
        ResizeMode = ResizeMode.NoResize;
        WindowStartupLocation = WindowStartupLocation.Manual;
        FontSize = ConversationFontSize;
        Resources.MergedDictionaries.Add(new ResourceDictionary
        {
            Source = new Uri("/CetaFae.Probe;component/ChatScrollBar.xaml", UriKind.Relative)
        });
        var body = new StackPanel();
        // Include the outer right gutter in the clear action's blue region.
        var row = new DockPanel { Margin = new Thickness(0, 0, -FramePadding, 0) };
        var clearIcon = new BitmapImage();
        clearIcon.BeginInit();
        clearIcon.UriSource = new Uri("pack://application:,,,/CetaFae.Probe;component/Art/Ui/clear-chat.png", UriKind.Absolute);
        clearIcon.DecodePixelWidth = 64;
        clearIcon.CacheOption = BitmapCacheOption.OnLoad;
        clearIcon.EndInit();
        clearIcon.Freeze();
        _clear = WhalePrompt.Action("", "清除对话", 26, Brushes.White, Brushes.Transparent, 20);
        _clear.Width = 26 + FramePadding;
        _clear.Height = InputHeight;
        _clear.Content = new Border { Width = 18, Height = 18, Background = Brushes.White,
            OpacityMask = new ImageBrush(clearIcon) { Stretch = Stretch.Uniform } };
        _clear.VerticalAlignment = VerticalAlignment.Center;
        _clear.Click += (_, _) => ClearConversation();
        DockPanel.SetDock(_clear, Dock.Right);
        row.Children.Add(_clear);
        var inputRow = new DockPanel();
        _transcript = new ScrollViewer { Content = _turns, VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled };
        for (var index = 0; index < 3; index++)
            _waiting.Children.Add(new Ellipse { Width = 6, Height = 6, Fill = GuideTheme.Muted,
                Margin = new Thickness(0, 0, 5, 0), VerticalAlignment = VerticalAlignment.Center, Opacity = .25 });
        AutomationProperties.SetName(_waiting, "正在回答");
        var transcriptBody = new StackPanel();
        transcriptBody.Children.Add(_transcript);
        transcriptBody.Children.Add(_waiting);
        _transcriptSurface = new Border { Child = transcriptBody, Background = GuideTheme.Surface,
            CornerRadius = new CornerRadius(InputRadius), Visibility = Visibility.Collapsed,
            Margin = new Thickness(0, 0, 0, 8) };
        _send = WhalePrompt.Action("▶", "发送 · Enter", 26, GuideTheme.WhaleBlue, Brushes.Transparent, 15);
        _send.VerticalAlignment = VerticalAlignment.Center;
        AutomationProperties.SetName(_input, "自由问答");
        _send.Click += (_, _) => Submit();
        DockPanel.SetDock(_send, Dock.Right);
        inputRow.Children.Add(_send);
        var editor = new Grid();
        editor.Children.Add(_input);
        editor.Children.Add(_hint);
        inputRow.Children.Add(editor);
        row.Children.Add(new Border { Child = inputRow, Height = InputHeight, Background = Brushes.White,
            CornerRadius = new CornerRadius(InputRadius), Padding = new Thickness(12, 0, 4, 0) });
        body.Children.Add(_transcriptSurface);
        _statusSurface = new Border { Child = _status, Background = Brushes.White, CornerRadius = new CornerRadius(InputRadius),
            Padding = new Thickness(10, 6, 10, 6), Margin = new Thickness(0, 0, 0, 6) };
        body.Children.Add(_statusSurface);
        body.Children.Add(row);
        Content = new Border { Child = body, Background = GuideTheme.WhaleBlue,
            CornerRadius = new CornerRadius(OuterRadius), Padding = new Thickness(FramePadding) };
        _input.TextChanged += (_, _) => UpdateSend();
        _input.PreviewKeyDown += (_, e) =>
        {
            if (e.Key != Key.Enter || e.IsRepeat) return;
            Submit();
            e.Handled = true;
        };
        PreviewKeyDown += (_, e) =>
        {
            if (e.Key != Key.Escape) return;
            e.Handled = true;
            Dismiss();
        };
        Deactivated += (_, _) => { if (IsVisible) Dismiss(); };
        Loaded += (_, _) => _input.Focus();
        Closed += (_, _) => SetWaiting(false);
        SetStatus(null);
    }

    internal void Dismiss()
    {
        if (IsVisible) Hide();
        SetStatus(null);
        Dismissed?.Invoke();
    }

    internal void ClearConversation()
    {
        _turns.Children.Clear();
        _transcriptSurface.Visibility = Visibility.Collapsed;
        _transcript.ScrollToTop();
        _input.Clear();
        _hint.Text = "输入你的问题";
        _started = false;
        SetStatus(null);
        Cleared?.Invoke();
        _input.Focus();
    }

    internal void Place(Rect anchor, Rect work)
    {
        const double gap = 6;
        const double inputFrameHeight = InputHeight + FramePadding * 2;
        Width = Math.Max(1, Math.Min(_started ? ConversationWidth : RestingWidth, work.Width));
        // Choose the input's resting edge once per anchor, independently of reply length.
        var inputTop = anchor.Top - gap - inputFrameHeight;
        if (inputTop < work.Top + inputFrameHeight * 2) inputTop = anchor.Bottom + gap;
        var position = BallWindow.ConstrainPosition(new Point(anchor.Right - Width, inputTop), work,
            new Size(Width, inputFrameHeight));
        var bottom = Math.Min(work.Bottom, position.Y + inputFrameHeight);
        var available = Math.Min(ConversationMaxHeight, bottom - work.Top);
        var fixedHeight = inputFrameHeight;
        if (_statusSurface.Visibility == Visibility.Visible)
        {
            _statusSurface.Measure(new Size(Math.Max(1, Width - FramePadding * 2), double.PositiveInfinity));
            fixedHeight += _statusSurface.DesiredSize.Height;
        }
        var transcriptWidth = Math.Max(1, Width - FramePadding * 2);
        var transcriptLimit = Math.Max(0, available - fixedHeight - _transcriptSurface.Margin.Bottom - (_busy ? _waiting.Height : 0));
        // Measure messages independently of the previous ScrollViewer viewport and its queued scroll commands.
        _turns.Measure(new Size(transcriptWidth, double.PositiveInfinity));
        if (_turns.DesiredSize.Height > transcriptLimit)
            _turns.Measure(new Size(Math.Max(1, transcriptWidth - 10), double.PositiveInfinity));
        _transcript.Height = Math.Min(transcriptLimit, _turns.DesiredSize.Height);
        _transcript.MaxHeight = transcriptLimit;
        var surface = (Border)Content;
        // A reply can replace the status at the same width before WPF's next layout pass.
        _transcript.InvalidateMeasure();
        _transcriptSurface.Child.InvalidateMeasure();
        _transcriptSurface.InvalidateMeasure();
        surface.Child.InvalidateMeasure();
        surface.InvalidateMeasure();
        surface.Measure(new Size(Width, double.PositiveInfinity));
        // WPF must use the same height for the HWND and for placement.
        var height = Math.Min(available, surface.DesiredSize.Height);
        Left = position.X;
        Top = bottom - height;
        Height = height;
    }

    void Submit()
    {
        if (_busy || string.IsNullOrWhiteSpace(_input.Text)) return;
        Submitted?.Invoke(_input.Text);
    }

    internal void SetStatus(string? message, bool busy = false)
    {
        _busy = busy;
        _started |= busy;
        _input.IsReadOnly = busy;
        _status.Text = busy ? null : message;
        _statusSurface.Visibility = busy || message is null ? Visibility.Collapsed : Visibility.Visible;
        SetWaiting(busy);
        _transcriptSurface.Visibility = busy || _turns.Children.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        UpdateSend();
    }

    void SetWaiting(bool waiting)
    {
        _waiting.Visibility = waiting ? Visibility.Visible : Visibility.Collapsed;
        for (var index = 0; index < _waiting.Children.Count; index++)
        {
            var dot = (Ellipse)_waiting.Children[index];
            dot.BeginAnimation(OpacityProperty, waiting ? WaitingAnimation(index) : null);
        }
    }

    internal static DoubleAnimation WaitingAnimation(int index) => new(.25, 1, TimeSpan.FromMilliseconds(360))
    {
        AutoReverse = true, RepeatBehavior = RepeatBehavior.Forever,
        BeginTime = TimeSpan.FromMilliseconds(index * 140)
    };

    void UpdateSend()
    {
        _hint.Visibility = _input.Text.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
        _send.IsEnabled = !_busy && !string.IsNullOrWhiteSpace(_input.Text);
    }

    internal void ShowTurn(ChatTurn turn)
    {
        _started = true;
        if (_turns.Children.Count == FreeChatSession.MaxTurns) _turns.Children.RemoveAt(0);
        var exchange = new Grid { Margin = new Thickness(0, 0, 0, 12) };
        exchange.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(2, GridUnitType.Star) });
        exchange.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(3, GridUnitType.Star) });
        exchange.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        exchange.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        var question = new Border
        {
            Background = GuideTheme.ChatBubble, CornerRadius = new CornerRadius(8),
            HorizontalAlignment = HorizontalAlignment.Right, Padding = new Thickness(12, 8, 12, 8),
            Child = new TextBlock { Text = turn.Question,
                FontSize = ConversationFontSize, VerticalAlignment = VerticalAlignment.Center,
                Foreground = GuideTheme.Ink, TextWrapping = TextWrapping.Wrap }
        };
        Grid.SetColumn(question, 1);
        exchange.Children.Add(question);
        var reply = new TextBlock { Text = turn.Reply, FontSize = ConversationFontSize,
            LineHeight = 26, Foreground = GuideTheme.Ink, TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 12, 0, 0) };
        Grid.SetRow(reply, 1);
        Grid.SetColumnSpan(reply, 2);
        exchange.Children.Add(reply);
        _turns.Children.Add(exchange);
        _transcriptSurface.Visibility = Visibility.Visible;
        _transcript.ScrollToEnd();
        _input.Clear();
        _hint.Text = "可以接着问";
        SetStatus(null);
        _input.Focus();
    }
}
