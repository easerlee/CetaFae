using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Security.Cryptography;
using System.Text;

namespace CetaFae.Probe;

/// <summary>Headless handoff from the installer, never called by window or model content.</summary>
internal static class InstallationAgreement
{
    internal static int StopRunning(string executable)
    {
        try
        {
            var path = Path.GetFullPath(executable);
            if (!string.Equals(Path.GetFileName(path), "CetaFae.Probe.exe", StringComparison.OrdinalIgnoreCase))
                return 2;
            if (!File.Exists(path)) return 0;
            using var current = Process.GetCurrentProcess();
            foreach (var process in Process.GetProcessesByName(Path.GetFileNameWithoutExtension(path)))
            {
                using (process)
                {
                    if (process.Id == current.Id || process.HasExited || process.SessionId != current.SessionId)
                        continue;
                    try
                    {
                        // Keep the process handle alive from path verification through termination.
                        _ = process.SafeHandle;
                        if (!string.Equals(process.MainModule?.FileName, path, StringComparison.OrdinalIgnoreCase))
                            continue;
                        process.Kill();
                        if (!process.WaitForExit(5000)) return 2;
                    }
                    catch (Exception ex) when ((ex is Win32Exception or InvalidOperationException) && process.HasExited)
                    { /* An instance that exits independently no longer holds the installed file. */ }
                }
            }
            return 0;
        }
        catch (Exception ex) when (ex is Win32Exception or InvalidOperationException or ArgumentException or IOException)
        { return 2; }
    }

    internal static string Text(GuideOptions options) =>
        "鲸汐（CetaFae）软件许可协议与隐私说明\n" +
        "条款版本 1.2（2026年11月3日）\n\n" +
        "请在安装前阅读本协议，尤其是下方的窗口识别、自由问答和留存说明。点击“同意协议并安装”即确认本协议及所列连接配置。不同意可取消安装。\n\n" +
        GuidePanel.Disclosure(options) + "\n\n" +
        "软件许可条款\n\n" +
        "一、安装与使用\n" +
        "本协议允许你在有权使用的 Windows 设备上安装和运行本安装包中的鲸汐客户端。请在你有权查看和发送的窗口中使用识别功能，并遵守适用法律及所选服务的条款。\n\n" +
        "二、知识产权\n" +
        "鲸汐及随附素材的权利归各自权利人所有。本协议仅授予上述安装与使用许可。随附的第三方组件适用各自的许可证。\n\n" +
        "三、第三方服务及识别结果\n" +
        "安装包包含运行所需的 .NET 桌面环境。模型服务、账号和使用额度需另行准备，已有连接配置在升级时沿用。鲸汐提供界面解释和位置提示，目标软件的操作由你完成。识别可能出错或超时，请核对实际界面后操作。本条款不限制你依法享有的权利。\n\n" +
        "四、停止使用与联系\n" +
        "你可以退出鲸汐，也可以通过 Windows 的已安装应用列表卸载。隐私说明和撤回入口位于“鲸汐设置 → 隐私”。使用、许可或隐私问题可通过 CetaFae 项目反馈渠道联系维护者。公开反馈时请勿附上私人窗口内容或密钥。\n" +
        "https://github.com/easerlee/CetaFae/issues";

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
