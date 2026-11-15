using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Input;
using CetaFae.Plugins;

namespace CetaFae.Probe;

internal static class GuideDiagnostics
{
    internal static byte[] Create(PluginCatalog shipped, PluginCatalog enabled, bool cloud, ConnectionStatus connection) =>
        Create(shipped.Plugins, enabled, cloud, connection);

    internal static byte[] Create(IReadOnlyList<GuidePlugin> available, PluginCatalog enabled, bool cloud, ConnectionStatus connection) =>
        JsonSerializer.SerializeToUtf8Bytes(new
        {
            schemaVersion = 1,
            productVersion = Program.VersionLabel,
            runtimeVersion = Environment.Version.ToString(),
            cloudEnabled = cloud,
            connection = connection.ToString(),
            plugins = available.Select(p => new { id = p.Id, version = p.Version,
                enabled = enabled.Plugins.Any(active => active.Id == p.Id) })
        }, new JsonSerializerOptions { WriteIndented = true });
}

/// <summary>Lazily opened by the user; holds no screen or conversation data.</summary>
internal sealed class GuideSettingsWindow : Window
{
    readonly GuideSettingsView _view;
    readonly Func<bool> _cloudEnabled;
    readonly Func<GuidePreferences, string?> _apply;
    readonly Func<ConnectionStatus, byte[]> _diagnostics;
    GuidePreferences _saved;
    CancellationTokenSource? _test;
    CancellationTokenSource? _bundleInstall;
    readonly bool _allowBundleInstall;
    int _generation;
    bool _closed;
    ConnectionStatus _result;

    internal GuideSettingsWindow(GuidePreferences preferences, PluginCatalog shipped, Func<bool> cloudEnabled,
        Func<GuidePreferences, string?> apply, Action cloudAction, Func<ConnectionStatus, byte[]> diagnostics, bool invalid,
        bool allowBundleInstall)
    {
        _saved = preferences; _cloudEnabled = cloudEnabled; _apply = apply; _diagnostics = diagnostics;
        _allowBundleInstall = allowBundleInstall;
        Title = "鲸汐设置"; Width = 660; Height = 660; MinWidth = 500; MinHeight = 420;
        MaxHeight = SystemParameters.WorkArea.Height; MaxWidth = SystemParameters.WorkArea.Width;
        Width = Math.Min(Width, MaxWidth); Height = Math.Min(Height, MaxHeight);
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        Background = GuideTheme.Surface;
        Content = _view = new GuideSettingsView(preferences, shipped, cloudEnabled(), invalid);
        _view.Changed += () => CancelDiagnostic();
        _view.TestRequested += () => Test(false);
        _view.ImageTestRequested += () => Test(true);
        _view.TestCancelled += () => CancelDiagnostic(ConnectionStatus.Cancelled);
        _view.CloseRequested += Close;
        _view.CloudRequested += () => { Close(); cloudAction(); };
        _view.ExportRequested += Export;
        _view.SaveRequested += Apply;
        _view.ImportRequested += Import;
        PreviewKeyDown += (_, e) => { if (e.Key == Key.Escape) { Close(); e.Handled = true; } };
        Closed += (_, _) => { _closed = true; _bundleInstall?.Cancel(); CancelDiagnostic(); };
    }

    void CancelDiagnostic(ConnectionStatus status = ConnectionStatus.Unchecked)
    {
        _generation++; _test?.Cancel(); _test = null; _result = status;
        if (!_closed) _view.SetConnection(status);
    }

    async void Test(bool image)
    {
        GuideOptions options;
        try { options = _view.Read().Options; }
        catch (Exception ex) when (ex is ArgumentException or InvalidDataException)
        { _view.SetNotice("请先核对服务地址与模型名称。远程服务必须使用 HTTPS，不能在地址中填写密钥。"); return; }
        CancelDiagnostic();
        var version = _generation;
        using var request = new CancellationTokenSource(); _test = request;
        _view.SetConnection(ConnectionStatus.Checking);
        var result = image ? await GuideConnection.TestImageAsync(options, request.Token) :
            await GuideConnection.TestAsync(options, request.Token);
        if (!_closed && version == _generation && !request.IsCancellationRequested)
        { _result = result; _view.SetConnection(result); }
        if (ReferenceEquals(_test, request)) _test = null;
    }

    void Apply(GuidePreferences preferences)
    {
        CancelDiagnostic();
        var error = _apply(preferences);
        if (error is not null) { _view.SetCloud(_cloudEnabled(), _saved.Options); _view.SetNotice(error); return; }
        _saved = preferences;
        _view.SetCloud(_cloudEnabled(), _saved.Options);
        _view.SetNotice("设置已保存。配置变更已结束旧求助；使用范围改变后须重新运行安装程序确认协议，再重新拖入。");
    }

    async void Import()
    {
        if (_bundleInstall is not null) return;
        var dialog = new Microsoft.Win32.OpenFileDialog { Title = "导入鲸汐插件",
            Filter = "鲸汐插件 (*.json;*.cetaplugin)|*.json;*.cetaplugin|软件适配与功能 (*.json)|*.json|转换组件 (*.cetaplugin)|*.cetaplugin",
            Multiselect = false, CheckFileExists = true };
        if (dialog.ShowDialog(this) != true) return;
        if (string.Equals(Path.GetExtension(dialog.FileName), FixedToolBundle.Extension, StringComparison.OrdinalIgnoreCase))
        {
            await InstallBundle(dialog.FileName);
            return;
        }
        try { _view.ImportPlugin(PluginCatalog.Import(dialog.FileName)); }
        catch (InvalidDataException ex)
        {
            _view.SetNotice(ex.Message switch
            {
                "plugin_shipped_identity" => "不能用导入文件覆盖随软件提供的插件。可在功能列表停用它。",
                "plugin_update_version" => "已存在同标识插件。更新文件的版本必须高于当前版本；原插件保持不变。",
                _ => "无法导入：文件无效、不兼容、过大或路径不可读取。请选择作者提供的 JSON 插件文件；原列表保持不变。"
            });
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or ArgumentException)
        { _view.SetNotice("无法读取这个插件文件。请核对文件和读取权限；原列表保持不变。"); }
    }

    async Task InstallBundle(string file)
    {
        if (!_allowBundleInstall || !FixedToolBundle.Supported)
        {
            _view.SetNotice("当前无法安装 Filee 转换组件。此安装包适用于 Windows x64 正常运行模式。");
            return;
        }
        using var install = new CancellationTokenSource();
        _bundleInstall = install;
        _view.SetConversionBundle(FixedTool.ImageConverter.Installed, busy: true);
        string message;
        try
        {
            var result = await Task.Run(() => FixedToolBundle.ImageConverter.Install(file, FixedTool.ToolsRoot, install.Token), install.Token);
            message = result switch
            {
                BundleInstallResult.Repaired => "Filee 转换组件已修复。",
                BundleInstallResult.AlreadyInstalled => "此版本的 Filee 转换组件已经安装。",
                _ => "Filee 转换组件已安装。可在功能列表启用图片格式转换。"
            };
        }
        catch (OperationCanceledException) { message = "已取消安装。"; }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or InvalidDataException or PlatformFailure)
        { message = "未能安装转换组件。请确认安装包与当前鲸汐版本匹配，并检查磁盘空间和文件权限。"; }
        finally
        {
            _bundleInstall = null;
            if (!_closed) _view.SetConversionBundle(FixedTool.ImageConverter.Installed);
        }
        if (!_closed) _view.SetNotice(message);
    }

    void Export()
    {
        var dialog = new Microsoft.Win32.SaveFileDialog { Title = "导出鲸汐安全诊断", FileName = "CetaFae-diagnostics.json",
            Filter = "JSON 诊断文件 (*.json)|*.json", DefaultExt = ".json", AddExtension = true };
        if (dialog.ShowDialog(this) != true) return;
        try
        {
            File.WriteAllBytes(dialog.FileName, _diagnostics(_result));
            _view.SetNotice("已导出白名单诊断；不包含密钥、地址、路径、画面或问答。");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        { _view.SetNotice("无法写入诊断文件。请选择可写的位置后重试。"); }
    }
}
