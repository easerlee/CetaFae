using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Input;
using CetaFae.Plugins;

namespace CetaFae.Probe;

internal static class GuideDiagnostics
{
    internal static byte[] Create(PluginCatalog shipped, PluginCatalog enabled, bool cloud, ConnectionStatus connection) =>
        JsonSerializer.SerializeToUtf8Bytes(new
        {
            schemaVersion = 1,
            productVersion = Program.VersionLabel,
            runtimeVersion = Environment.Version.ToString(),
            cloudEnabled = cloud,
            connection = connection.ToString(),
            plugins = shipped.Plugins.Select(p => new { id = p.Id, version = p.Version,
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
    int _generation;
    bool _closed;
    ConnectionStatus _result;

    internal GuideSettingsWindow(GuidePreferences preferences, PluginCatalog shipped, Func<bool> cloudEnabled,
        Func<GuidePreferences, string?> apply, Action cloudAction, Func<ConnectionStatus, byte[]> diagnostics, bool invalid)
    {
        _saved = preferences; _cloudEnabled = cloudEnabled; _apply = apply; _diagnostics = diagnostics;
        Title = "鲸汐设置"; Width = 660; Height = 660; MinWidth = 500; MinHeight = 420;
        MaxHeight = SystemParameters.WorkArea.Height; MaxWidth = SystemParameters.WorkArea.Width;
        Width = Math.Min(Width, MaxWidth); Height = Math.Min(Height, MaxHeight);
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        Background = GuideTheme.Surface;
        Content = _view = new GuideSettingsView(preferences, shipped, cloudEnabled(), invalid);
        _view.Changed += () => CancelDiagnostic();
        _view.TestRequested += Test;
        _view.TestCancelled += () => CancelDiagnostic(ConnectionStatus.Cancelled);
        _view.CloseRequested += Close;
        _view.CloudRequested += () => { Close(); cloudAction(); };
        _view.ExportRequested += Export;
        _view.SaveRequested += Apply;
        PreviewKeyDown += (_, e) => { if (e.Key == Key.Escape) { Close(); e.Handled = true; } };
        Closed += (_, _) => { _closed = true; CancelDiagnostic(); };
    }

    void CancelDiagnostic(ConnectionStatus status = ConnectionStatus.Unchecked)
    {
        _generation++; _test?.Cancel(); _test = null; _result = status;
        if (!_closed) _view.SetConnection(status);
    }

    async void Test()
    {
        GuideOptions options;
        try { options = _view.Read().Options; }
        catch (Exception ex) when (ex is ArgumentException or InvalidDataException)
        { _view.SetNotice("请先核对服务地址与模型名称。远程服务必须使用 HTTPS，不能在地址中填写密钥。"); return; }
        CancelDiagnostic();
        var version = _generation;
        using var request = new CancellationTokenSource(); _test = request;
        _view.SetConnection(ConnectionStatus.Checking);
        var result = await GuideConnection.TestAsync(options, request.Token);
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
        _view.SetNotice("设置已保存。配置变更已结束旧求助；尚未同意时请到隐私页确认，再重新拖入。");
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
