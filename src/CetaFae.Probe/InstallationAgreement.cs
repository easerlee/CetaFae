using System.IO;
using System.Security.Cryptography;
using System.Text;

namespace CetaFae.Probe;

/// <summary>Headless handoff from the installer, never called by window or model content.</summary>
internal static class InstallationAgreement
{
    internal static string Text(GuideOptions options) =>
        "鲸汐使用协议与隐私说明 · 开发预览版\n\n" +
        "点击“同意协议并安装”，表示你同意下列使用方式。不同意请关闭安装程序。\n" +
        "鲸汐解释和指路，目标软件的操作由你完成。模型可能判断错误，请核对后操作；本预览尚未完成正式兼容性与性能验收。\n" +
        "安装不附带模型服务、账号或额度；现有连接配置会沿用。安装后直接拖动鲸鱼求助，不再重复确认。可从设置查看本说明或撤回同意。\n\n" +
        GuidePanel.Disclosure(options);

    internal static string Scope(GuideOptions options) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(Text(options))));

    static GuideOptions? ReadOptions(GuidePreferencesStore? preferences)
    {
        var stored = (preferences ?? new GuidePreferencesStore()).Read();
        return stored.Status == PreferencesStatus.Invalid ? null : stored.Value.Options;
    }

    internal static int Export(string prefix, GuidePreferencesStore? preferences = null)
    {
        try
        {
            var options = ReadOptions(preferences);
            if (options is null) return 2;
            var path = Path.GetFullPath(prefix);
            CheckPath(path + ".txt"); CheckPath(path + ".scope");
            File.WriteAllText(path + ".txt", Text(options), new UTF8Encoding(true));
            File.WriteAllText(path + ".scope", Scope(options), Encoding.ASCII);
            return 0;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException) { return 2; }
    }

    // The installer calls this only after its visible agreement and successful file copy.
    // A process already running as this user can create its own DPAPI data; this is not a local-user security boundary.
    internal static int Complete(string acceptedScope, GuidePreferencesStore? preferences = null, string? consentFile = null)
    {
        if (acceptedScope.Length != 64 || acceptedScope.Any(c => !Uri.IsHexDigit(c))) return 2;
        var options = ReadOptions(preferences);
        if (options is null || !string.Equals(acceptedScope, Scope(options), StringComparison.Ordinal)) return 2;
        return new CloudConsentStore(options, consentFile).Save(true) ? 0 : 3;
    }

    static void CheckPath(string file)
    {
        for (var path = file; path is not null; path = Path.GetDirectoryName(path))
            if ((File.Exists(path) || Directory.Exists(path)) && (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
                throw new IOException("installation_output_not_regular");
    }
}
