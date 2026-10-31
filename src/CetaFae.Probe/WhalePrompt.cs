using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;

namespace CetaFae.Probe;

/// <summary>Native controls replace the illustrated input; no raster text or second clarification.</summary>
internal sealed class WhalePrompt : Border
{
    internal readonly TextBox Answer = new() { MaxLength = 300, AcceptsReturn = false, BorderThickness = new Thickness(0),
        Background = Brushes.Transparent, Padding = new Thickness(3, 7, 3, 7), VerticalContentAlignment = VerticalAlignment.Center,
        FontSize = 13, MinWidth = 60, ToolTip = "点击输入，按 Enter 回答；最多300字" };
    readonly TextBlock _hint = new() { Text = "正在识别当前窗口…", VerticalAlignment = VerticalAlignment.Center,
        FontSize = 12, Foreground = GuideTheme.Muted, IsHitTestVisible = false };
    readonly Button _submit;
    internal readonly Border Grip;
    internal bool CanAnswer { get; private set; }
    internal event Action? FocusRequested;
    internal event Action? MenuRequested;
    internal event Action<string>? Submitted;
    internal WhalePrompt()
    {
        Height = 40; VerticalAlignment = VerticalAlignment.Center;
        Margin = new Thickness(2, 0, 2, 0); CornerRadius = new CornerRadius(12);
        BorderBrush = GuideTheme.Outline; BorderThickness = new Thickness(1.5); Background = GuideTheme.Surface;
        TextElement.SetForeground(this, GuideTheme.Ink);
        var row = new DockPanel();
        Grip = new Border { Width = 24, Background = Brushes.Transparent, Cursor = Cursors.SizeAll,
            ToolTip = "拖动到另一个窗口", Child = new TextBlock { Text = "≈", Foreground = GuideTheme.Accent,
                HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center, FontSize = 19 } };
        DockPanel.SetDock(Grip, Dock.Left); row.Children.Add(Grip);
        var menu = Action("⋯", "鲸汐菜单、云端与清除");
        menu.Click += (_, _) => MenuRequested?.Invoke(); DockPanel.SetDock(menu, Dock.Right); row.Children.Add(menu);
        _submit = Action("→", "提交回答");
        _submit.Click += (_, _) => Submit(); DockPanel.SetDock(_submit, Dock.Right); row.Children.Add(_submit);
        var input = new Grid(); input.Children.Add(Answer); input.Children.Add(_hint); row.Children.Add(input); Child = row;
        Answer.PreviewMouseLeftButtonDown += (_, _) => { if (CanAnswer) FocusRequested?.Invoke(); };
        Answer.PreviewKeyDown += (_, e) => { if (e.Key == Key.Enter && CanAnswer) { Submit(); e.Handled = true; } };
        Answer.TextChanged += (_, _) => UpdateHint();
        SetQuestion(false);
    }
    static Button Action(string text, string hint) => new() { Content = text, Width = 28, ToolTip = hint,
        Focusable = false, Background = Brushes.Transparent, BorderThickness = new Thickness(0),
        Foreground = GuideTheme.Accent, FontSize = 16, Padding = new Thickness(0) };
    void UpdateHint() => _hint.Visibility = Answer.Text.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
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
        Answer.Focusable = available; _submit.IsEnabled = available;
        _hint.Text = available ? "你想在这个窗口处理什么？" : "正在识别当前窗口…";
        UpdateHint();
    }
    internal void SetResult() { if (!CanAnswer) _hint.Text = "说明已就绪 · 可拖动继续求助"; }
}
