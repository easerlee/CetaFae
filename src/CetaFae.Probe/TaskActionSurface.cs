using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace CetaFae.Probe;

/// <summary>One inline decision surface shared by attached and resting conversations.</summary>
internal sealed class TaskActionSurface : Border
{
    readonly StackPanel _body = new();
    readonly TextBlock _message = new() { TextWrapping = TextWrapping.Wrap, FontSize = 15, LineHeight = 24, Foreground = GuideTheme.Ink };
    readonly WrapPanel _actions = new();
    internal TaskActionSurface()
    {
        Margin = new Thickness(0, 10, 0, 8);
        Background = GuideTheme.Surface;
        CornerRadius = new CornerRadius(8);
        Padding = new Thickness(12);
        Child = _body;
        Visibility = Visibility.Collapsed;
        Resources.MergedDictionaries.Add(new ResourceDictionary { Source = new Uri("/CetaFae.Probe;component/ChatScrollBar.xaml", UriKind.Relative) });
        _body.Children.Add(new ScrollViewer { Content = _message, MaxHeight = 220, VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled });
        _body.Children.Add(_actions);
    }

    internal void Present(string message, string? action, Action? proceed, Action? stop)
    {
        _message.Text = message;
        _actions.Children.Clear();
        if (action is not null && proceed is not null) AddAction(action, proceed);
        if (stop is not null) AddAction(action is null ? "停止" : "先不处理", stop);
        Visibility = Visibility.Visible;
    }

    internal void AddAction(string label, Action callback)
    {
        var button = WhalePrompt.Action(label, label, 32, GuideTheme.WhaleBlue, Brushes.Transparent, 14);
        button.Width = double.NaN; button.MinWidth = 56; button.Padding = new Thickness(10, 6, 10, 6);
        button.Margin = new Thickness(0, 6, 8, 0);
        button.Click += (_, _) => callback();
        _actions.Children.Add(button);
    }

    internal void Clear() { _message.Text = ""; _actions.Children.Clear(); Visibility = Visibility.Collapsed; }
}
