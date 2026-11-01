using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Interop;

namespace CetaFae.Probe;

/// <summary>One management menu shared by the whale and notification-area entry.</summary>
internal sealed class GuideMenu : IDisposable
{
    internal ContextMenu Menu { get; } = new();
    readonly System.Windows.Forms.NotifyIcon _tray;
    readonly System.Drawing.Icon _icon;
    readonly BallWindow _ball;
    bool _opening;
    internal bool OwnsForeground(IntPtr window) => (_opening || Menu.IsOpen) &&
        (window == _ball.Handle || window == (PresentationSource.FromVisual(Menu) as HwndSource)?.Handle);

    internal GuideMenu(BallWindow ball, Action clear, bool registerTray = true, Action? settings = null)
    {
        _ball = ball;
        if (settings is not null) Menu.Items.Add(Item("鲸汐设置", settings));
        Menu.Items.Add(Item("清除标记", clear));
        Menu.Items.Add(new Separator());
        Menu.Items.Add(Item("退出鲸汐", () => Application.Current.Shutdown()));
        _icon = CreateIcon();
        _tray = new System.Windows.Forms.NotifyIcon { Icon = _icon, Text = "鲸汐 · 尚未同意", Visible = registerTray };
        _tray.MouseUp += (_, e) =>
        {
            if (e.Button is System.Windows.Forms.MouseButtons.Left or System.Windows.Forms.MouseButtons.Right)
                ball.Dispatcher.InvokeAsync(Show);
        };
    }

    static MenuItem Item(string text, Action action)
    {
        var item = new MenuItem { Header = text };
        item.Click += (_, _) => action();
        return item;
    }
    internal void SetCloud(bool enabled)
    {
        _tray.Text = enabled ? "鲸汐 · 可以求助" : "鲸汐 · 尚未同意";
    }
    internal void Show()
    {
        _opening = true;
        try
        {
            Native.SetForegroundWindow(_ball.Handle);
            Menu.Placement = PlacementMode.MousePoint;
            Menu.IsOpen = true;
        }
        finally { _opening = false; }
    }
    static System.Drawing.Icon CreateIcon()
    {
        using var ico = typeof(GuideMenu).Assembly.GetManifestResourceStream("CetaFae.jingxi.ico")
            ?? throw new InvalidDataException("application_icon_missing");
        using var icon = new System.Drawing.Icon(ico);
        return (System.Drawing.Icon)icon.Clone();
    }
    public void Dispose() { Menu.IsOpen = false; _tray.Visible = false; _tray.Dispose(); _icon.Dispose(); }
}
