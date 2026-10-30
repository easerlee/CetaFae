using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace CetaFae.Probe;

/// <summary>Local evidence only: file resources never establish authenticity or execution authority.</summary>
public sealed record SoftwareIdentity(uint ProcessId, long StartedAt, string? Product, string? Description,
    string? Version, string? Company, string? Package, string Signature, string? Certificate)
{
    internal static string? Clean(string? value, int max = 160)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        value = value.Trim();
        if (value.Length > max || value.Any(char.IsControl) || value.Any(c => c is '/' or '\\' or ':' or '@') ||
            value.Any(c => char.GetUnicodeCategory(c) == System.Globalization.UnicodeCategory.Format)) return null;
        try { GuidePrivacy.EnsureTextSafe(value); return value; }
        catch (InvalidDataException) { return null; }
    }

    internal bool IsValid(uint pid) => ProcessId == pid && StartedAt > 0 &&
        new[] { Product, Description, Version, Company, Package, Certificate }.All(s => s is null || Clean(s) == s) &&
        Signature is "offline-valid" or "unsigned" or "unverified" or "unavailable";

    public string Describe() =>
        $"本机文件自报：{Product ?? "产品名未知"}；{Description ?? "描述未知"}；版本 {Version ?? "未知"}；公司 {Company ?? "未知"}。\n" +
        $"包身份：{Package ?? "无或无法读取"}。\n" +
        "离线签名：" + (Signature switch { "offline-valid" => "本机缓存校验通过（未在线核验撤销状态，不代表安全）",
            "unsigned" => "未取得嵌入签名（可能使用目录签名）", "unverified" => "未通过离线校验", _ => "无法读取" }) +
        (Certificate is null ? "。" : $"；嵌入证书名称：{Certificate}（与资源自报公司独立）。");

    // Restrict public lookup to an explicitly confirmed product label; never fall back to filenames/titles.
    public string? ReferenceQuery => Product is { Length: >= 2 and <= 80 } product && Clean(product, 80) == product &&
        product.All(c => char.IsLetterOrDigit(c) || c is ' ' or '-' or '.' or '(' or ')') && product.Any(char.IsLetter)
        ? product + " software" : null;

    internal static SoftwareIdentity Read(uint pid, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        using var process = Process.GetProcessById(checked((int)pid));
        var started = process.StartTime.ToUniversalTime().Ticks;
        string? product = null, description = null, version = null, company = null, package = null, certificate = null;
        var signature = "unavailable";
        using var handle = OpenProcess(0x1000, false, pid);
        if (!handle.IsInvalid)
        {
            uint length = 512;
            var packageBuffer = new StringBuilder((int)length);
            if (GetPackageFullName(handle, ref length, packageBuffer) == 0) package = Clean(packageBuffer.ToString());
            uint capacity = 32768;
            var pathBuffer = new StringBuilder((int)capacity);
            if (QueryFullProcessImageName(handle, 0, pathBuffer, ref capacity))
            {
                var path = pathBuffer.ToString();
                try
                {
                    if (IsLocalRegularFile(path))
                    {
                        // Hold the file against writes/replacement while reading its independent evidence.
                        using var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
                        var info = FileVersionInfo.GetVersionInfo(path);
                        product = Clean(info.ProductName); description = Clean(info.FileDescription);
                        version = Clean(info.ProductVersion); company = Clean(info.CompanyName);
                        token.ThrowIfCancellationRequested();
                        signature = VerifyOffline(path);
                        try
                        {
                            using var embedded = X509Certificate.CreateFromSignedFile(path);
                            using var cert = new X509Certificate2(embedded);
                            certificate = Clean(cert.GetNameInfo(X509NameType.SimpleName, false));
                        }
                        catch (CryptographicException) { }
                    }
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.ComponentModel.Win32Exception or CryptographicException) { }
            }
        }
        token.ThrowIfCancellationRequested();
        using var current = Process.GetProcessById(checked((int)pid));
        if (current.StartTime.ToUniversalTime().Ticks != started) throw new InvalidDataException("target_changed");
        return new(pid, started, product, description, version, company, package, signature, certificate);
    }

    internal static bool IsLocalRegularFile(string path)
    {
        if (!Path.IsPathFullyQualified(path) || path.StartsWith(@"\\", StringComparison.Ordinal) || path.Length > 32767 ||
            path.AsSpan(2).Contains(':')) return false;
        var root = Path.GetPathRoot(path);
        if (root is null || new DriveInfo(root).DriveType != DriveType.Fixed) return false;
        for (var part = path; part is not null && part.Length > root.Length; part = Path.GetDirectoryName(part))
            if ((File.GetAttributes(part) & FileAttributes.ReparsePoint) != 0) return false;
        return (File.GetAttributes(path) & FileAttributes.Directory) == 0;
    }

    internal static string VerifyOffline(string path)
    {
        var file = new TrustFile { Size = (uint)Marshal.SizeOf<TrustFile>(), Path = path };
        var pointer = Marshal.AllocHGlobal(Marshal.SizeOf<TrustFile>());
        Marshal.StructureToPtr(file, pointer, false);
        var data = new TrustData { Size = (uint)Marshal.SizeOf<TrustData>(), Ui = 2, Union = 1, File = pointer,
            StateAction = 1, Flags = 0x1000 | 0x10 }; // Cache-only URL retrieval and no revocation network check.
        var action = new Guid("00AAC56B-CD44-11D0-8CC2-00C04FC295EE");
        try
        {
            var result = WinVerifyTrust(new IntPtr(-1), ref action, ref data);
            return result == 0 ? "offline-valid" : result == unchecked((int)0x800B0100) ? "unsigned" : "unverified";
        }
        finally
        {
            data.StateAction = 2;
            WinVerifyTrust(new IntPtr(-1), ref action, ref data);
            Marshal.DestroyStructure<TrustFile>(pointer);
            Marshal.FreeHGlobal(pointer);
        }
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    struct TrustFile { public uint Size; [MarshalAs(UnmanagedType.LPWStr)] public string Path; public IntPtr Handle, Subject; }
    [StructLayout(LayoutKind.Sequential)]
    struct TrustData
    {
        public uint Size; public IntPtr Policy, Sip; public uint Ui, Revocation, Union; public IntPtr File;
        public uint StateAction; public IntPtr State, Url; public uint Flags, Context; public IntPtr SignatureSettings;
    }
    [DllImport("kernel32.dll", SetLastError = true)]
    static extern SafeProcessHandle OpenProcess(uint access, bool inherit, uint pid);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    static extern bool QueryFullProcessImageName(SafeProcessHandle process, uint flags, StringBuilder path, ref uint size);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    static extern int GetPackageFullName(SafeProcessHandle process, ref uint length, StringBuilder name);
    [DllImport("wintrust.dll", ExactSpelling = true)]
    static extern int WinVerifyTrust(IntPtr window, ref Guid action, ref TrustData data);
}
