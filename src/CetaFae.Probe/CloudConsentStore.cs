using System.IO;
using System.Security.Cryptography;
using System.Text.Json;

namespace CetaFae.Probe;

internal sealed class CloudConsentStore(GuideOptions options, string? file = null)
{
    internal readonly record struct State(bool Granted, bool Enabled);
    sealed record Record(int Version, string Disclosure, bool Enabled);
    readonly string _file = file ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "CetaFae", "consent.dat");
    readonly string _scope = InstallationAgreement.Scope(options);
    static bool StorageFailure(Exception ex) => ex is IOException or UnauthorizedAccessException or CryptographicException or JsonException or ArgumentException;
    void CheckPath()
    {
        for (var path = Path.GetFullPath(_file); path is not null; path = Path.GetDirectoryName(path))
            if ((File.Exists(path) || Directory.Exists(path)) && (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
                throw new IOException("consent_path_not_regular");
    }

    internal State Read()
    {
        try
        {
            CheckPath();
            using var stream = new FileStream(_file, FileMode.Open, FileAccess.Read, FileShare.Read);
            if (stream.Length is <= 0 or > 8192) return default;
            var bytes = new byte[(int)stream.Length]; stream.ReadExactly(bytes);
            var plain = ProtectedData.Unprotect(bytes, null, DataProtectionScope.CurrentUser);
            try
            {
                var record = JsonSerializer.Deserialize<Record>(plain);
                return record is { Version: 1 } && record.Disclosure == _scope ? new(true, record.Enabled) : default;
            }
            finally { CryptographicOperations.ZeroMemory(plain); }
        }
        catch (Exception ex) when (StorageFailure(ex)) { return default; }
    }

    internal bool Forget()
    {
        try { CheckPath(); File.Delete(_file); return true; }
        catch (Exception ex) when (StorageFailure(ex)) { return false; }
    }

    // A disabled tombstone is a safe fallback when the old grant cannot be removed.
    internal bool Revoke() => Forget() || Save(false);

    internal bool Save(bool enabled)
    {
        var temporary = _file + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            CheckPath();
            var plain = JsonSerializer.SerializeToUtf8Bytes(new Record(1, _scope, enabled));
            byte[] bytes;
            try { bytes = ProtectedData.Protect(plain, null, DataProtectionScope.CurrentUser); }
            finally { CryptographicOperations.ZeroMemory(plain); }
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(_file))!);
            File.WriteAllBytes(temporary, bytes);
            File.Move(temporary, _file, overwrite: true);
            return true;
        }
        catch (Exception ex) when (StorageFailure(ex)) { return false; }
        finally { try { File.Delete(temporary); } catch (Exception ex) when (StorageFailure(ex)) { } }
    }
}
