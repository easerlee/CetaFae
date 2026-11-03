using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;

namespace CetaFae.Probe;

internal sealed class FreeChatWindow : Window
{
    internal const double OuterRadius = 8;
    internal const double InputHeight = 32;
    internal const double FramePadding = 4;
    internal const double InputRadius = OuterRadius * InputHeight / (InputHeight + FramePadding * 2);
    readonly StackPanel _turns = new();
    readonly ScrollViewer _transcript;
    readonly TextBlock _status = new() { Foreground = GuideTheme.Ink, TextWrapping = TextWrapping.Wrap };
    readonly TextBlock _hint = new() { Text = "输入你的问题", Foreground = GuideTheme.Muted,
        VerticalAlignment = VerticalAlignment.Center, IsHitTestVisible = false };
    readonly Border _statusSurface;
    readonly TextBox _input = new() { MaxLength = FreeChatSession.MaxQuestionLength, MinHeight = 30,
        BorderThickness = new Thickness(0), Background = Brushes.Transparent, VerticalContentAlignment = VerticalAlignment.Center,
        Foreground = GuideTheme.Ink };
    readonly Button _send;
    bool _busy;
    internal event Action<string>? Submitted;
    internal IntPtr Handle => new WindowInteropHelper(this).Handle;
    internal TextBox Input => _input;
    internal Button Send => _send;

    internal FreeChatWindow()
    {
        WindowStyle = WindowStyle.None;
        AllowsTransparency = true;
        Background = Brushes.Transparent;
        ShowInTaskbar = false;
        Topmost = true;
        ResizeMode = ResizeMode.NoResize;
        SizeToContent = SizeToContent.Height;
        var body = new StackPanel();
        var row = new DockPanel();
        var close = WhalePrompt.Action("×", "关闭对话", 26, Brushes.White, Brushes.Transparent, 20);
        close.VerticalAlignment = VerticalAlignment.Center;
        close.Click += (_, _) => Close();
        DockPanel.SetDock(close, Dock.Right);
        row.Children.Add(close);
        var inputRow = new DockPanel();
        _transcript = new ScrollViewer { Content = _turns, VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            Visibility = Visibility.Collapsed, Margin = new Thickness(0, 6, 0, 0) };
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
        body.Children.Add(row);
        body.Children.Add(_transcript);
        _statusSurface = new Border { Child = _status, Background = Brushes.White, CornerRadius = new CornerRadius(InputRadius),
            Padding = new Thickness(8, 4, 8, 4), Margin = new Thickness(0, 5, 0, 0) };
        body.Children.Add(_statusSurface);
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
            Close();
        };
        Loaded += (_, _) => _input.Focus();
        SetStatus(null);
    }

    internal static Point Position(Rect anchor, Rect work, Size size)
    {
        const double gap = 6;
        var above = anchor.Top - size.Height - gap;
        var below = anchor.Bottom + gap;
        var y = above >= work.Top ? above : below;
        var x = anchor.Right - size.Width;
        return BallWindow.ConstrainPosition(new Point(x, y), work, size);
    }

    internal void Place(Rect anchor, Rect work)
    {
        Width = Math.Max(1, Math.Min(360, work.Width));
        _transcript.MaxHeight = Math.Clamp(work.Height - 150, 0, 270);
        ((UIElement)Content).Measure(new Size(Width, double.PositiveInfinity));
        var point = Position(anchor, work, new Size(Width, ((UIElement)Content).DesiredSize.Height));
        Left = point.X;
        Top = point.Y;
    }

    void Submit()
    {
        if (_busy || string.IsNullOrWhiteSpace(_input.Text)) return;
        Submitted?.Invoke(_input.Text);
    }

    internal void SetStatus(string? message, bool busy = false)
    {
        _busy = busy;
        _input.IsReadOnly = busy;
        _status.Text = message;
        _statusSurface.Visibility = message is null ? Visibility.Collapsed : Visibility.Visible;
        UpdateSend();
    }

    void UpdateSend()
    {
        _hint.Visibility = _input.Text.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
        _send.IsEnabled = !_busy && !string.IsNullOrWhiteSpace(_input.Text);
    }

    internal void ShowTurn(ChatTurn turn)
    {
        if (_turns.Children.Count == FreeChatSession.MaxTurns) _turns.Children.RemoveAt(0);
        _turns.Children.Add(new Border
        {
            Background = Brushes.White, CornerRadius = new CornerRadius(InputRadius),
            Padding = new Thickness(10, 8, 10, 8), Margin = new Thickness(0, 0, 0, 4),
            Child = new TextBlock { Text = "你  " + turn.Question + "\n\n" + turn.Reply,
                Foreground = GuideTheme.Ink, TextWrapping = TextWrapping.Wrap }
        });
        _transcript.Visibility = Visibility.Visible;
        _transcript.ScrollToEnd();
        _input.Clear();
        _hint.Text = "可以接着问";
        SetStatus(null);
        _input.Focus();
    }
}
