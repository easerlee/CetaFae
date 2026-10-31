using System.Windows;
using System.Windows.Controls;
using System.Windows.Automation;
using System.Windows.Data;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;

namespace CetaFae.Probe;

/// <summary>Native controls replace the illustrated input; no raster text or second clarification.</summary>
internal sealed class WhalePrompt : Border
{
    internal const double WidthDip = 360;
    internal const double HeightDip = 64;
    internal const double DockFoot = 76;
    internal readonly TextBox Answer = new() { MaxLength = 300, AcceptsReturn = false, BorderThickness = new Thickness(0),
        Background = Brushes.Transparent, Padding = new Thickness(0), VerticalContentAlignment = VerticalAlignment.Center,
        Foreground = GuideTheme.Ink, CaretBrush = GuideTheme.WhaleBlue,
        FontSize = 13, MinWidth = 60, ToolTip = "点击输入，按 Enter 回答；最多300字" };
    readonly TextBlock _hint = new() { Text = "正在识别当前窗口…", VerticalAlignment = VerticalAlignment.Center,
        FontSize = 12, Foreground = GuideTheme.Muted, IsHitTestVisible = false, TextTrimming = TextTrimming.CharacterEllipsis };
    internal readonly Button Send;
    internal readonly Button Dismiss;
    internal readonly PromptShell DragSurface;
    internal bool CanAnswer { get; private set; }
    internal event Action? FocusRequested;
    internal event Action? DismissRequested;
    internal event Action<string>? Submitted;
    internal WhalePrompt()
    {
        Height = HeightDip; VerticalAlignment = VerticalAlignment.Center;
        Background = Brushes.Transparent;
        TextElement.SetForeground(this, GuideTheme.Ink);
        var root = new Grid();
        var body = new Grid { Margin = new Thickness(0, 14, 0, 0) };
        DragSurface = new PromptShell { Cursor = Cursors.SizeAll, ToolTip = "拖动蓝色背景换窗口；单击打开鲸汐菜单" };
        AutomationProperties.SetName(DragSurface, "拖动蓝色背景换窗口或打开鲸汐菜单");
        body.Children.Add(DragSurface);
        var row = new DockPanel { Margin = new Thickness(18, 6, 48, 8) };
        Send = Action("→", "发送回答 · Enter", 32, GuideTheme.WhaleBlue, Brushes.White, 21);
        Send.VerticalAlignment = VerticalAlignment.Bottom;
        Send.Margin = new Thickness(6, 0, 0, 0);
        Send.Click += (_, _) => Submit(); DockPanel.SetDock(Send, Dock.Right); row.Children.Add(Send);
        var input = new Grid(); input.Children.Add(Answer); input.Children.Add(_hint);
        row.Children.Add(new Border { Background = Brushes.White, CornerRadius = new CornerRadius(12),
            Padding = new Thickness(10, 0, 10, 0), Child = input });
        body.Children.Add(row); root.Children.Add(body);
        Dismiss = Action("×", "结束本次指路，恢复鲸鱼", 20, Brushes.White, Brushes.Transparent, 14);
        Dismiss.Opacity = .7; Dismiss.Margin = new Thickness(0, 10, 16, 0);
        Dismiss.HorizontalAlignment = HorizontalAlignment.Right; Dismiss.VerticalAlignment = VerticalAlignment.Top;
        Dismiss.Click += (_, _) => DismissRequested?.Invoke(); body.Children.Add(Dismiss); Child = root;
        AutomationProperties.SetName(Answer, "本次指路的回答");
        Answer.PreviewMouseLeftButtonDown += (_, _) => { if (CanAnswer) FocusRequested?.Invoke(); };
        Answer.PreviewKeyDown += (_, e) => { if (e.Key == Key.Enter && CanAnswer) { Submit(); e.Handled = true; } };
        Answer.TextChanged += (_, _) => UpdateHint();
        SetQuestion(false);
    }
    static Button Action(string text, string hint, double size, Brush foreground, Brush background, double fontSize)
    {
        var button = new Button { Content = text, Width = size, Height = size, ToolTip = hint, Focusable = false,
            Background = background, Foreground = foreground, FontSize = fontSize, Padding = new Thickness(0), Cursor = Cursors.Hand };
        var border = new FrameworkElementFactory(typeof(Border));
        border.SetValue(Border.CornerRadiusProperty, new CornerRadius(size / 2));
        border.SetBinding(Border.BackgroundProperty, new Binding(nameof(Background)) { RelativeSource = RelativeSource.TemplatedParent });
        var content = new FrameworkElementFactory(typeof(ContentPresenter));
        content.SetValue(HorizontalAlignmentProperty, HorizontalAlignment.Center);
        content.SetValue(VerticalAlignmentProperty, VerticalAlignment.Center); border.AppendChild(content);
        var template = new ControlTemplate(typeof(Button)) { VisualTree = border };
        foreach (var (property, value, opacity) in new[] { (IsMouseOverProperty, true, .85), (Button.IsPressedProperty, true, .65), (IsEnabledProperty, false, .3) })
        {
            var trigger = new Trigger { Property = property, Value = value }; trigger.Setters.Add(new Setter(OpacityProperty, opacity));
            template.Triggers.Add(trigger);
        }
        button.Template = template;
        AutomationProperties.SetName(button, hint); return button;
    }
    void UpdateHint()
    {
        _hint.Visibility = Answer.Text.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
        Send.IsEnabled = CanAnswer && !string.IsNullOrWhiteSpace(Answer.Text);
    }
    void Submit()
    {
        if (!CanAnswer || string.IsNullOrWhiteSpace(Answer.Text)) return;
        var text = Answer.Text;
        SetQuestion(false);
        Submitted?.Invoke(text);
    }
    internal void SetQuestion(bool available)
    {
        CanAnswer = available; Answer.Clear(); Answer.IsReadOnly = !available;
        Answer.Focusable = available; Send.Visibility = available ? Visibility.Visible : Visibility.Collapsed;
        _hint.Text = available ? "告诉我你想做什么" : "正在识别当前窗口…";
        UpdateHint();
    }
    internal void SetResult() { if (!CanAnswer) _hint.Text = "说明已就绪 · 可拖动继续求助"; }
}

internal sealed class PromptShell : FrameworkElement
{
    internal static StreamGeometry Shape(double width, double height)
    {
        var shape = new StreamGeometry();
        using (var p = shape.Open())
        {
            p.BeginFigure(new Point(0, height), true, true);
            p.BezierTo(new Point(10, height), new Point(12, height - 6), new Point(12, height - 15), true, false);
            p.LineTo(new Point(12, 18), true, false); p.QuadraticBezierTo(new Point(12, 0), new Point(30, 0), true, false);
            p.LineTo(new Point(width - 30, 0), true, false);
            p.QuadraticBezierTo(new Point(width - 12, 0), new Point(width - 12, 18), true, false);
            p.LineTo(new Point(width - 12, height - 15), true, false);
            p.BezierTo(new Point(width - 12, height - 6), new Point(width - 10, height), new Point(width, height), true, false);
        }
        shape.Freeze(); return shape;
    }
    protected override void OnRender(DrawingContext draw)
    {
        if (ActualWidth >= 60 && ActualHeight >= 32) draw.DrawGeometry(GuideTheme.WhaleBlue, null, Shape(ActualWidth, ActualHeight));
    }
}
