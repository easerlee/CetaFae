using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Microsoft.Win32;

namespace CetaFae.Probe;

internal sealed class ChatAttachment
{
    internal const string DefaultQuestion = "请帮我看看这张图片。";
    readonly Image _thumbnail = new() { Width = 30, Height = 22, Stretch = Stretch.Uniform };
    readonly TextBlock _label = new()
    {
        FontSize = 11, Foreground = GuideTheme.Ink, VerticalAlignment = VerticalAlignment.Center,
        TextTrimming = TextTrimming.CharacterEllipsis, Margin = new Thickness(5, 0, 2, 0)
    };
    internal Button Add { get; } = WhalePrompt.Action("+", "添加图片", 24, GuideTheme.WhaleBlue, Brushes.Transparent, 19);
    internal Border Preview { get; }
    internal ChatImage? Image { get; private set; }
    internal bool Choosing { get; private set; }
    internal bool Active => Choosing || Add.ContextMenu.IsOpen;
    internal event Action? Changed;
    internal event Action? Opening;

    internal bool OwnsForeground(IntPtr window, IntPtr owner) => Active && window != IntPtr.Zero &&
        (window == owner || window == (PresentationSource.FromVisual(Add.ContextMenu) as HwndSource)?.Handle ||
         Choosing && owner != IntPtr.Zero && Native.GetAncestor(window, Native.GA_ROOTOWNER) == owner);

    internal void SetReceiver(Uri receiver) => Preview.ToolTip =
        $"图片会随本条消息发送到 {receiver.AbsoluteUri}。请先遮盖敏感内容。";

    internal ChatAttachment(TextBox editor)
    {
        var remove = WhalePrompt.Action("×", "移除图片", 22, GuideTheme.Ink, Brushes.Transparent, 15);
        remove.Click += (_, _) => SetImage(null);
        var contents = new DockPanel();
        DockPanel.SetDock(remove, Dock.Right);
        contents.Children.Add(remove);
        contents.Children.Add(_thumbnail);
        contents.Children.Add(_label);
        Preview = new Border
        {
            Height = 26, Background = GuideTheme.Surface, CornerRadius = new CornerRadius(6),
            Padding = new Thickness(3, 0, 2, 0), Child = contents, Visibility = Visibility.Collapsed,
            ToolTip = "图片会随本条消息发送到当前识别服务。请先遮盖敏感内容。"
        };
        Add.VerticalAlignment = VerticalAlignment.Center;
        var menu = new ContextMenu { Placement = PlacementMode.Bottom, PlacementTarget = Add };
        var file = new MenuItem { Header = "选择图片…" };
        var paste = new MenuItem { Header = "粘贴图片" };
        file.Click += (_, _) => ChooseFile();
        paste.Click += (_, _) => PasteImage();
        menu.Items.Add(file);
        menu.Items.Add(paste);
        Add.ContextMenu = menu;
        Add.Click += (_, _) => { Opening?.Invoke(); menu.IsOpen = true; };
        editor.PreviewKeyDown += (_, e) =>
        {
            if (Add.IsEnabled && e.Key == Key.V && Keyboard.Modifiers == ModifierKeys.Control)
                e.Handled = PasteImage(showMissing: false);
        };
    }

    internal void SetImage(ChatImage? image)
    {
        Image = image;
        _thumbnail.Source = image?.Preview;
        _thumbnail.Visibility = image is null ? Visibility.Collapsed : Visibility.Visible;
        _label.Text = image is null ? "" : "图片 · 发送时上传";
        Preview.Visibility = image is null ? Visibility.Collapsed : Visibility.Visible;
        Changed?.Invoke();
    }

    internal void Cancel()
    {
        Add.ContextMenu.IsOpen = false;
        SetImage(null);
    }

    bool PasteImage(bool showMissing = true)
    {
        if (!Add.IsEnabled) return false;
        return Read(() => Clipboard.GetImage() is { } bitmap ? ChatImage.FromBitmap(bitmap) : null, showMissing);
    }

    void ChooseFile()
    {
        if (!Add.IsEnabled || Choosing) return;
        var picker = new OpenFileDialog
        {
            Title = "选择要发送的图片", Filter = "图片 (*.jpg;*.jpeg;*.png;*.bmp)|*.jpg;*.jpeg;*.png;*.bmp",
            CheckFileExists = true, Multiselect = false
        };
        Choosing = true;
        try
        {
            if (picker.ShowDialog(Window.GetWindow(Add)) == true)
                Read(() => ChatImage.FromFile(picker.FileName));
        }
        finally { Choosing = false; }
    }

    bool Read(Func<ChatImage?> read, bool showMissing = false)
    {
        if (!Add.IsEnabled) return false;
        try
        {
            if (read() is { } image) SetImage(image);
            else if (showMissing) SetNotice("剪贴板里没有图片");
            else return false;
        }
        catch (InvalidDataException ex) when (ex.Message == "chat_image_size")
        {
            SetNotice("图片太大，请缩小后再试");
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException or NotSupportedException
            or ArgumentException or InvalidOperationException or FormatException or ExternalException)
        {
            SetNotice("无法读取图片，请试试 JPG、PNG 或 BMP");
        }
        return true;
    }

    void SetNotice(string text)
    {
        // Keep an existing draft and its send disclosure when a replacement fails.
        _label.Text = Image is null ? text : "保留原图 · " + text;
        Preview.Visibility = Visibility.Visible;
        Changed?.Invoke();
    }
}
