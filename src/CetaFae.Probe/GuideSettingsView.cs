using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;
using CetaFae.Plugins;

namespace CetaFae.Probe;

/// <summary>One settings visual for generated previews and the user-opened native window.</summary>
internal sealed class GuideSettingsView : Border
{
    readonly StackPanel[] _pages = [new(), new(), new(), new()];
    readonly Button[] _tabs;
    readonly TextBlock _connection = Text(GuideConnection.Message(ConnectionStatus.Unchecked));
    readonly TextBlock _notice = Text("更改设置后点击保存。不会自动读取窗口或发送识别请求。");
    readonly TextBlock _cloud = Text("");
    readonly TextBlock _disclosure = Text("");
    readonly TextBox _endpoint = Input("模型服务地址");
    readonly TextBox _model = Input("模型名称");
    readonly CheckBox _image = new() { Content = "允许发送授权范围内的图片", Margin = new Thickness(0, 10, 0, 10) };
    readonly Dictionary<string, CheckBox> _plugins = new(StringComparer.Ordinal);
    readonly ScrollViewer _scroll;
    readonly Button _cloudAction;
    internal Button SaveButton { get; }
    internal Button TestButton { get; }
    internal Button CancelTestButton { get; }
    internal Expander Advanced { get; }
    internal ScrollViewer BodyScroll => _scroll;
    internal TextBox Endpoint => _endpoint;
    internal TextBox Model => _model;
    internal event Action? Changed;
    internal event Action? TestRequested;
    internal event Action? TestCancelled;
    internal event Action<GuidePreferences>? SaveRequested;
    internal event Action? CloudRequested;
    internal event Action? ExportRequested;
    internal event Action? CloseRequested;

    internal GuideSettingsView(GuidePreferences preferences, PluginCatalog shipped, bool cloudEnabled, bool invalid = false)
    {
        Background = GuideTheme.Surface;
        Padding = new Thickness(20);
        TextElement.SetFontFamily(this, new FontFamily("Microsoft YaHei UI"));
        TextElement.SetFontSize(this, 13);
        TextElement.SetForeground(this, GuideTheme.Ink);
        var layout = new DockPanel(); Child = layout;
        var header = new StackPanel();
        header.Children.Add(new TextBlock { Text = "鲸汐设置", FontSize = 23, FontWeight = FontWeights.SemiBold,
            Foreground = GuideTheme.WhaleBlue });
        header.Children.Add(Text("管理连接与权限。日常求助仍然从拖动小鲸鱼开始。"));
        DockPanel.SetDock(header, Dock.Top); layout.Children.Add(header);
        var tabs = new WrapPanel { Margin = new Thickness(0, 10, 0, 14) };
        _tabs = new[] { "连接", "隐私", "功能", "帮助" }.Select((name, index) => Action(name, () => SelectPage(index))).ToArray();
        foreach (var tab in _tabs) tabs.Children.Add(tab);
        DockPanel.SetDock(tabs, Dock.Top); layout.Children.Add(tabs);
        var footer = new StackPanel { Margin = new Thickness(0, 12, 0, 0) };
        _notice.FontSize = 12; footer.Children.Add(_notice);
        var buttons = new WrapPanel { HorizontalAlignment = HorizontalAlignment.Right };
        SaveButton = Action("保存设置", Submit); SaveButton.Background = GuideTheme.WhaleBlue; SaveButton.Foreground = Brushes.White;
        buttons.Children.Add(SaveButton); buttons.Children.Add(Action("关闭", () => CloseRequested?.Invoke()));
        footer.Children.Add(buttons); DockPanel.SetDock(footer, Dock.Bottom); layout.Children.Add(footer);
        var body = new Grid(); foreach (var page in _pages) body.Children.Add(page);
        _scroll = new ScrollViewer { Content = body, VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled };
        layout.Children.Add(_scroll);

        _pages[0].Children.Add(Title("模型服务"));
        _pages[0].Children.Add(Text("使用你选择的兼容模型服务。鲸汐目前不附带模型、代理或免费额度。"));
        _pages[0].Children.Add(_cloud); _pages[0].Children.Add(_connection);
        _pages[0].Children.Add(Text("检测仅访问下方服务地址的模型列表，可能携带运行时配置的凭证；不读取屏幕，不验证图片理解。"));
        var testActions = new WrapPanel();
        TestButton = Action("检测连接", () => TestRequested?.Invoke());
        CancelTestButton = Action("取消检测", () => TestCancelled?.Invoke()); CancelTestButton.IsEnabled = false;
        testActions.Children.Add(TestButton); testActions.Children.Add(CancelTestButton); _pages[0].Children.Add(testActions);
        var advanced = new StackPanel();
        advanced.Children.Add(Text("服务地址（远程服务须使用 HTTPS）")); advanced.Children.Add(_endpoint);
        advanced.Children.Add(Text("模型名称")); advanced.Children.Add(_model); advanced.Children.Add(_image);
        advanced.Children.Add(Text("鉴权仍从运行时环境获取；此窗口不接收、不显示或保存密钥。修改地址、模型或图片范围后须重新授权。"));
        Advanced = new Expander { Header = "高级连接设置", IsExpanded = true, Content = advanced,
            Margin = new Thickness(0, 12, 0, 0) };
        _pages[0].Children.Add(Advanced);

        _pages[1].Children.Add(Title("你决定鲸汐何时看窗口"));
        _pages[1].Children.Add(Text("只有你拖入的所选窗口才会用于求助。关闭云端后，不再发起新的窗口读取或识别请求；已发送内容无法从上游撤回。"));
        _cloudAction = Action("查看云端授权", () => CloudRequested?.Invoke()); _pages[1].Children.Add(_cloudAction);
        _pages[1].Children.Add(Text("以下为当前已保存配置的完整说明。高级设置尚未保存的编辑不会改变授权。"));
        _pages[1].Children.Add(_disclosure);

        _pages[2].Children.Add(Title("已随包提供的功能"));
        _pages[2].Children.Add(Text("关闭适配只移除它的增强。通用理解仍可独立工作；关闭全部相关能力后不再识别。这里不下载或运行第三方代码。"));
        foreach (var plugin in shipped.Plugins)
        {
            var enabled = new CheckBox { Content = plugin.DisplayName, IsChecked = !preferences.DisabledPlugins.Contains(plugin.Id, StringComparer.OrdinalIgnoreCase),
                FontSize = 14, Margin = new Thickness(0, 4, 0, 4) };
            _plugins.Add(plugin.Id, enabled);
            var item = new StackPanel(); item.Children.Add(enabled);
            var kind = plugin.Kind switch { "general" => "通用界面理解", "identity" => "本机软件身份", _ => "软件适配" };
            item.Children.Add(Text($"{kind} · 版本 {plugin.Version} · {plugin.TaskIds.Count} 项声明任务"));
            _pages[2].Children.Add(new Border { Child = item, Padding = new Thickness(12), Margin = new Thickness(0, 5, 0, 5),
                BorderBrush = GuideTheme.Outline, BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(8) });
        }

        _pages[3].Children.Add(Title("从拖动开始"));
        _pages[3].Children.Add(Text("1. 配置可用的模型服务，阅读并亲自同意云端授权。\n2. 将小鲸鱼拖到要了解的窗口。\n3. 需要时回答问题或框选区域，再阅读说明并自行操作。\n4. 点击对话条内的 × 结束；右键鲸鱼可再次打开设置。"));
        _pages[3].Children.Add(Title("遇到问题"));
        _pages[3].Children.Add(Text("先在连接页手动检测服务。隐私检查失败不代表检测到了敏感内容；鲸汐会停止这次识别，而不是绕过检查。\n诊断导出仅含产品/运行时版本、云端状态、连接结果类别和插件状态，不包含密钥、地址、路径、截图、控件文字或问答。"));
        _pages[3].Children.Add(Text($"版本：{Program.VersionLabel} · 开发预览，尚未完成正式性能及平台验收。"));
        _pages[3].Children.Add(Action("导出安全诊断", () => ExportRequested?.Invoke()));

        _endpoint.Text = preferences.BaseUrl; _model.Text = preferences.Model; _image.IsChecked = preferences.IncludeImage;
        SetCloud(cloudEnabled, preferences.Options);
        if (invalid) SetNotice("本机设置损坏或无法读取，云端保持关闭。请核对连接配置并保存，再亲自授权。");
        _endpoint.TextChanged += (_, _) => Edited(); _model.TextChanged += (_, _) => Edited();
        _image.Checked += (_, _) => Edited(); _image.Unchecked += (_, _) => Edited();
        foreach (var toggle in _plugins.Values) { toggle.Checked += (_, _) => Edited(); toggle.Unchecked += (_, _) => Edited(); }
        SelectPage(0);
    }

    internal GuidePreferences Read() => new GuidePreferences(_endpoint.Text.Trim(), _model.Text.Trim(), _image.IsChecked == true,
        _plugins.Where(p => p.Value.IsChecked != true).Select(p => p.Key).ToArray()).Validate();
    internal void SetNotice(string message) => _notice.Text = message;
    internal void SetConnection(ConnectionStatus status)
    {
        _connection.Text = GuideConnection.Message(status);
        TestButton.IsEnabled = status != ConnectionStatus.Checking; CancelTestButton.IsEnabled = !TestButton.IsEnabled;
    }
    internal void SetCloud(bool enabled, GuideOptions options)
    {
        _cloud.Text = enabled ? "云端已启用；尚未检测服务状态。" : "云端已关闭；不会自动识别。";
        _cloudAction.Content = enabled ? "关闭云端" : "查看并确认云端授权";
        _disclosure.Text = GuidePanel.Disclosure(options);
    }
    internal void SelectPage(int index)
    {
        for (var i = 0; i < _pages.Length; i++)
        {
            _pages[i].Visibility = i == index ? Visibility.Visible : Visibility.Collapsed;
            _tabs[i].Background = i == index ? GuideTheme.WhaleBlue : Brushes.White;
            _tabs[i].Foreground = i == index ? Brushes.White : GuideTheme.Ink;
        }
        _scroll.ScrollToTop();
    }
    void Edited() { SetConnection(ConnectionStatus.Unchecked); SetNotice("有未保存的更改。保存前，原配置与授权仍然有效。"); Changed?.Invoke(); }
    void Submit()
    {
        try { SaveRequested?.Invoke(Read()); }
        catch (Exception ex) when (ex is ArgumentException or System.IO.InvalidDataException)
        { SetNotice("请核对服务地址和模型名称：远程服务须用 HTTPS，地址不能包含账号、密码、查询参数或片段。"); }
    }
    static TextBlock Text(string text) => new() { Text = text, TextWrapping = TextWrapping.Wrap, LineHeight = 21,
        Margin = new Thickness(0, 5, 0, 5) };
    static TextBlock Title(string text) => new() { Text = text, FontSize = 16, FontWeight = FontWeights.SemiBold,
        TextWrapping = TextWrapping.Wrap, Foreground = GuideTheme.Ink, Margin = new Thickness(0, 5, 0, 10) };
    static TextBox Input(string name)
    {
        var input = new TextBox { MaxLength = name == "模型名称" ? 100 : 2048, MinHeight = 34,
            Padding = new Thickness(8, 4, 8, 4), VerticalContentAlignment = VerticalAlignment.Center };
        System.Windows.Automation.AutomationProperties.SetName(input, name); return input;
    }
    static Button Action(string text, Action action)
    {
        var button = new Button { Content = text, Padding = new Thickness(14, 7, 14, 7), MinHeight = 34,
            Margin = new Thickness(0, 4, 8, 4), BorderBrush = GuideTheme.Outline, Background = Brushes.White };
        button.Click += (_, _) => action(); return button;
    }
}
