using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Microsoft.Win32;

namespace CetaFae.Probe;

/// <summary>Edits a local snapshot; exported pixels include opaque masks and annotations.</summary>
internal sealed class UtilityImageView : Border
{
    readonly Canvas _canvas = new();
    BitmapSource _source;
    readonly TextBlock _status = UtilityUi.Text("仅在本机处理。复制、保存和贴图均不会发送图片。");
    readonly List<(string Kind, Point Start, Point End)> _marks = [];
    string _mode = "rectangle";
    Point? _start;
    internal event Action<BitmapSource>? PinRequested;
    internal event Action<BitmapSource, bool>? TextRequested;
    internal BitmapSource Source => _source;
    internal UtilityImageView(BitmapSource source)
    {
        _source = source;
        CornerRadius = new(8); Background = Brushes.White; Padding = new(16);
        var layout = new DockPanel(); Child = layout;
        var top = new StackPanel(); top.Children.Add(UtilityUi.Title("截图工具"));
        var actions = new WrapPanel();
        foreach (var (id, name) in new[] { ("crop", "裁剪"), ("rectangle", "方框"), ("arrow", "箭头"), ("mask", "遮盖") })
            actions.Children.Add(UtilityUi.Button(name, () => { _mode = id; _status.Text = "按住左键拖动" + name + "范围。"; }));
        actions.Children.Add(UtilityUi.Button("撤销标注", () => { if (_marks.Count > 0) _marks.RemoveAt(_marks.Count - 1); Redraw(); }));
        actions.Children.Add(UtilityUi.Button("复制图片", () => Try(() => { Clipboard.SetImage(Export()); _status.Text = "已复制图片。"; })));
        actions.Children.Add(UtilityUi.Button("另存图片", () => Try(Save)));
        actions.Children.Add(UtilityUi.Button("贴在屏幕上", () => PinRequested?.Invoke(Export())));
        actions.Children.Add(UtilityUi.Button("提取文字", () => TextRequested?.Invoke(Export(), false)));
        actions.Children.Add(UtilityUi.Button("翻译文字", () => TextRequested?.Invoke(Export(), true)));
        top.Children.Add(actions); top.Children.Add(_status); DockPanel.SetDock(top, Dock.Top); layout.Children.Add(top);
        var box = new Viewbox { Stretch = Stretch.Uniform, Child = _canvas, Margin = new(0, 12, 0, 0) };
        layout.Children.Add(box);
        _canvas.MouseLeftButtonDown += (_, e) => { _start = e.GetPosition(_canvas); _canvas.CaptureMouse(); };
        _canvas.MouseMove += (_, e) => { if (_start is { } start) Redraw((_mode, start, Clamp(e.GetPosition(_canvas)))); };
        _canvas.MouseLeftButtonUp += (_, e) =>
        {
            if (_start is not { } start) return;
            _canvas.ReleaseMouseCapture(); _start = null;
            var end = Clamp(e.GetPosition(_canvas));
            if (_mode == "crop")
            {
                var r = new Rect(start, end);
                if (r.Width >= 4 && r.Height >= 4)
                {
                    _source = new CroppedBitmap(Export(), new Int32Rect((int)r.Left, (int)r.Top,
                        Math.Min((int)Math.Ceiling(r.Width), _source.PixelWidth - (int)r.Left),
                        Math.Min((int)Math.Ceiling(r.Height), _source.PixelHeight - (int)r.Top)));
                    _source.Freeze(); _marks.Clear();
                }
            }
            else if ((end - start).Length > 2) _marks.Add((_mode, start, end));
            Redraw();
        };
        Redraw();
    }
    Point Clamp(Point p) => new(Math.Clamp(p.X, 0, _source.PixelWidth), Math.Clamp(p.Y, 0, _source.PixelHeight));
    void Redraw((string Kind, Point Start, Point End)? preview = null)
    {
        _canvas.Width = _source.PixelWidth; _canvas.Height = _source.PixelHeight; _canvas.Children.Clear();
        _canvas.Children.Add(new Image { Source = _source, Width = _source.PixelWidth, Height = _source.PixelHeight });
        var drawing = new DrawingGroup();
        using (var context = drawing.Open())
        {
            context.DrawRectangle(Brushes.Transparent, null, new Rect(0, 0, _source.PixelWidth, _source.PixelHeight));
            foreach (var mark in _marks) Draw(context, mark);
            if (preview.HasValue) Draw(context, preview.Value);
        }
        var rendered = new DrawingImage(drawing); rendered.Freeze();
        _canvas.Children.Add(new Image { Source = rendered, Width = _source.PixelWidth, Height = _source.PixelHeight, IsHitTestVisible = false });
    }
    static void Draw(DrawingContext c, (string Kind, Point Start, Point End) m)
    {
        var pen = new Pen(GuideTheme.WhaleBlue, 3);
        if (m.Kind == "arrow")
        {
            c.DrawLine(pen, m.Start, m.End);
            var direction = m.Start - m.End;
            if (direction.Length < 1) return;
            direction.Normalize(); var normal = new Vector(-direction.Y, direction.X);
            c.DrawLine(pen, m.End, m.End + direction * 15 + normal * 7);
            c.DrawLine(pen, m.End, m.End + direction * 15 - normal * 7);
        }
        else c.DrawRectangle(m.Kind == "mask" ? Brushes.Black : null, m.Kind == "mask" ? null : pen, new Rect(m.Start, m.End));
    }
    internal BitmapSource Export()
    {
        var visual = new DrawingVisual();
        using (var c = visual.RenderOpen())
        {
            c.DrawImage(_source, new Rect(0, 0, _source.PixelWidth, _source.PixelHeight));
            foreach (var mark in _marks) Draw(c, mark);
        }
        var bitmap = new RenderTargetBitmap(_source.PixelWidth, _source.PixelHeight, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(visual); bitmap.Freeze(); return bitmap;
    }
    internal void AddMark(string kind, Point start, Point end)
    {
        if (kind is not ("rectangle" or "arrow" or "mask")) throw new ArgumentException("annotation_kind");
        _marks.Add((kind, Clamp(start), Clamp(end))); Redraw();
    }
    void Save()
    {
        var dialog = new SaveFileDialog { Title = "保存截图", Filter = "PNG 图片|*.png", FileName = "鲸汐截图.png" };
        if (dialog.ShowDialog(Window.GetWindow(this)) != true) return;
        var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(Export()));
        using var stream = File.Create(dialog.FileName); encoder.Save(stream); _status.Text = "已保存带有标注的图片。";
    }
    void Try(Action action)
    {
        try { action(); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Runtime.InteropServices.ExternalException)
        { _status.Text = "暂时无法完成，请检查文件位置或稍后再试。"; }
    }
}

internal static class UtilityUi
{
    internal static TextBlock Text(string text) => new() { Text = text, TextWrapping = TextWrapping.Wrap,
        Foreground = GuideTheme.Ink, FontSize = 15, Margin = new(0, 6, 0, 6) };
    internal static TextBlock Title(string text) => new() { Text = text, Foreground = GuideTheme.WhaleBlue,
        FontSize = 19, FontWeight = FontWeights.SemiBold, Margin = new(0, 0, 0, 8) };
    internal static Button Button(string label, Action action)
    {
        var button = new Button { Content = label, MinHeight = 34, Foreground = GuideTheme.WhaleBlue, Background = GuideTheme.Surface,
            FontSize = 14, Padding = new Thickness(12, 7, 12, 7), Margin = new(0, 3, 6, 3), Cursor = Cursors.Hand };
        var border = new FrameworkElementFactory(typeof(Border)); border.SetValue(Border.CornerRadiusProperty, new CornerRadius(8));
        border.SetBinding(Border.BackgroundProperty, new Binding(nameof(Control.Background)) { RelativeSource = RelativeSource.TemplatedParent });
        border.SetBinding(Border.PaddingProperty, new Binding(nameof(Control.Padding)) { RelativeSource = RelativeSource.TemplatedParent });
        var presenter = new FrameworkElementFactory(typeof(ContentPresenter));
        presenter.SetValue(FrameworkElement.HorizontalAlignmentProperty, HorizontalAlignment.Center);
        presenter.SetValue(FrameworkElement.VerticalAlignmentProperty, VerticalAlignment.Center); border.AppendChild(presenter);
        button.Template = new ControlTemplate(typeof(Button)) { VisualTree = border };
        button.Click += (_, _) => action(); return button;
    }
    internal static Window Window(string title, UIElement content, double width = 700, double height = 510)
    {
        var window = new Window { Title = title, Content = content, Width = Math.Min(width, SystemParameters.WorkArea.Width - 32),
            Height = Math.Min(height, SystemParameters.WorkArea.Height - 32), MinWidth = 300, MinHeight = 220,
            Background = Brushes.White, ShowInTaskbar = false, WindowStartupLocation = WindowStartupLocation.CenterScreen,
            FontFamily = new FontFamily("Microsoft YaHei UI") };
        window.Resources.MergedDictionaries.Add(new ResourceDictionary {
            Source = new Uri("/CetaFae.Probe;component/ChatScrollBar.xaml", UriKind.Relative) });
        return window;
    }
    internal static BitmapSource Decode(byte[] png)
    {
        using var stream = new MemoryStream(png);
        var bitmap = BitmapFrame.Create(stream, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad);
        bitmap.Freeze(); return bitmap;
    }
}
