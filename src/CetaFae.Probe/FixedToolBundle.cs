using System.Buffers.Binary;
using System.IO;
using System.IO.Compression;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using CetaFae.Plugins;

namespace CetaFae.Probe;

internal enum BundleInstallResult { Installed, AlreadyInstalled, Repaired }

/// <summary>Imports a host-approved, versioned tool bundle without executing its contents.</summary>
internal sealed record FixedToolBundle(FixedTool Tool, string Source, string ArchiveHash, string LicenseHash, string NoticesHash)
{
    internal const string Extension = ".cetaplugin";
    const int ArchiveLimit = 256 * 1024 * 1024;
    const int NoticeLimit = 128 * 1024;
    const string MarkerName = "bundle-id.txt";
    const string ArchiveName = "filee-portable.zip";
    const string LicenseName = "LICENSE";
    const string NoticesName = "THIRD-PARTY-NOTICES.md";
    const string SourceName = "SOURCE.txt";
    static readonly string[] Entries = [MarkerName, ArchiveName, LicenseName, NoticesName, SourceName];
    internal static bool Supported => RuntimeInformation.OSArchitecture == Architecture.X64;
    internal static FixedToolBundle ImageConverter { get; } = LoadImageConverter();

    sealed record Release(string Id, string Source, string ArchiveHash, string LicenseHash, string NoticesHash);

    static FixedToolBundle LoadImageConverter()
    {
        using var resource = typeof(FixedToolBundle).Assembly.GetManifestResourceStream("CetaFae.FileeBundle.json")
            ?? throw new InvalidDataException("conversion_release");
        var release = JsonSerializer.Deserialize<Release>(resource, PlatformRequest.Json)
            ?? throw new InvalidDataException("conversion_release");
        var tool = FixedTool.Resolve(release.Id) ?? throw new InvalidDataException("conversion_release");
        return new(tool, release.Source, release.ArchiveHash, release.LicenseHash, release.NoticesHash);
    }

    internal BundleInstallResult Install(string file, string toolRoot, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        if (!Supported || !string.Equals(Path.GetExtension(file), Extension, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("conversion_package");
        file = PlatformScope.LocalPath(file);
        PlatformScope.CheckAncestors(file);
        using var source = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.Read);
        CheckCentralDirectory(source);
        using var bundle = new ZipArchive(source, ZipArchiveMode.Read);
        if (!bundle.Entries.Select(entry => entry.FullName).Order(StringComparer.Ordinal)
            .SequenceEqual(Entries.Order(StringComparer.Ordinal), StringComparer.Ordinal))
            throw new InvalidDataException("conversion_package");
        var marker = ReadEntry(bundle.GetEntry(MarkerName)!, 128);
        if (!marker.AsSpan().SequenceEqual(Encoding.UTF8.GetBytes(Tool.Id)))
            throw new InvalidDataException("conversion_release");
        var license = ReadEntry(bundle.GetEntry(LicenseName)!, NoticeLimit);
        var notices = ReadEntry(bundle.GetEntry(NoticesName)!, NoticeLimit);
        var attribution = ReadEntry(bundle.GetEntry(SourceName)!, 256);
        if (Convert.ToHexString(SHA256.HashData(license)) != LicenseHash ||
            Convert.ToHexString(SHA256.HashData(notices)) != NoticesHash ||
            !attribution.AsSpan().SequenceEqual(Encoding.UTF8.GetBytes(Source)))
            throw new InvalidDataException("conversion_notices");

        var root = Path.Combine(PlatformScope.LocalPath(toolRoot), "bundles");
        var target = Path.Combine(root, Tool.DirectoryName);
        var stage = Path.Combine(root, ".install-" + Guid.NewGuid().ToString("N"));
        var installLock = Path.Combine(root, Tool.DirectoryName + ".lock");
        PlatformScope.CheckAncestors(installLock);
        Directory.CreateDirectory(root);
        using var owned = new FileStream(installLock, FileMode.OpenOrCreate, FileAccess.Write, FileShare.None);
        PlatformScope.CheckAncestors(target);
        Directory.CreateDirectory(stage);
        try
        {
            var archive = Path.Combine(stage, ArchiveName);
            CopyArchive(bundle.GetEntry(ArchiveName)!, archive, token);
            using (var bytes = new FileStream(archive, FileMode.Open, FileAccess.Read, FileShare.Read))
            {
                if (Convert.ToHexString(SHA256.HashData(bytes)) != ArchiveHash)
                    throw new InvalidDataException("conversion_archive");
                token.ThrowIfCancellationRequested();
                bytes.Position = 0;
                // Extraction sees only the exact official archive that was just verified.
                using var pinned = new ZipArchive(bytes, ZipArchiveMode.Read);
                pinned.ExtractToDirectory(Path.Combine(stage, "payload"));
            }
            File.Delete(archive);
            File.WriteAllBytes(Path.Combine(stage, LicenseName), license);
            File.WriteAllBytes(Path.Combine(stage, NoticesName), notices);
            File.WriteAllBytes(Path.Combine(stage, SourceName), attribution);
            if (!Matches(stage, token)) throw new InvalidDataException("conversion_tree");

            var replaced = Directory.Exists(target);
            if (replaced && Matches(target, token)) return BundleInstallResult.AlreadyInstalled;
            token.ThrowIfCancellationRequested();
            var backup = target + ".previous-" + Guid.NewGuid().ToString("N");
            if (replaced) Directory.Move(target, backup);
            try { Directory.Move(stage, target); }
            catch
            {
                if (replaced) Directory.Move(backup, target);
                throw;
            }
            return replaced ? BundleInstallResult.Repaired : BundleInstallResult.Installed;
        }
        finally
        {
            PlatformScope.CheckAncestors(stage);
            if (Directory.Exists(stage)) Directory.Delete(stage, recursive: true);
        }
    }

    bool Matches(string directory, CancellationToken token)
    {
        var payload = Path.Combine(directory, "payload");
        try
        {
            return File.Exists(Path.Combine(payload, Tool.Executable)) &&
                FixedTool.DirectoryHash(payload, token) == Tool.TreeHash &&
                MatchesNotice(Path.Combine(directory, LicenseName), LicenseHash) &&
                MatchesNotice(Path.Combine(directory, NoticesName), NoticesHash) &&
                MatchesNotice(Path.Combine(directory, SourceName), Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(Source))));
        }
        catch (InvalidDataException) { return false; }
    }

    static bool MatchesNotice(string file, string hash)
    {
        PlatformScope.CheckAncestors(file);
        if (!File.Exists(file)) return false;
        using var stream = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.Read);
        return stream.Length is > 0 and <= NoticeLimit && Convert.ToHexString(SHA256.HashData(stream)) == hash;
    }

    static byte[] ReadEntry(ZipArchiveEntry entry, int limit)
    {
        if (entry.Length is <= 0 || entry.Length > limit) throw new InvalidDataException("conversion_package");
        var bytes = new byte[(int)entry.Length];
        using var stream = entry.Open();
        stream.ReadExactly(bytes);
        if (stream.ReadByte() != -1) throw new InvalidDataException("conversion_package");
        return bytes;
    }

    static void CheckCentralDirectory(FileStream file)
    {
        // Our outer ZIP has a fixed entry count and no comment. Bound the directory before ZipArchive allocates entries.
        Span<byte> footer = stackalloc byte[22];
        if (file.Length is < 22 or > ArchiveLimit) throw new InvalidDataException("conversion_package");
        file.Position = file.Length - footer.Length;
        file.ReadExactly(footer);
        var size = BinaryPrimitives.ReadUInt32LittleEndian(footer[12..]);
        var start = BinaryPrimitives.ReadUInt32LittleEndian(footer[16..]);
        if (BinaryPrimitives.ReadUInt32LittleEndian(footer) != 0x06054B50 ||
            BinaryPrimitives.ReadUInt32LittleEndian(footer[4..]) != 0 ||
            BinaryPrimitives.ReadUInt16LittleEndian(footer[8..]) != Entries.Length ||
            BinaryPrimitives.ReadUInt16LittleEndian(footer[10..]) != Entries.Length ||
            BinaryPrimitives.ReadUInt16LittleEndian(footer[20..]) != 0 ||
            size > 4096 || (long)start + size != file.Length - footer.Length)
            throw new InvalidDataException("conversion_package");
        file.Position = 0;
    }

    static void CopyArchive(ZipArchiveEntry entry, string target, CancellationToken token)
    {
        if (entry.Length is <= 0 or > ArchiveLimit) throw new InvalidDataException("conversion_package");
        using var input = entry.Open();
        using var output = new FileStream(target, FileMode.CreateNew, FileAccess.Write, FileShare.None);
        var buffer = new byte[128 * 1024];
        var remaining = entry.Length;
        while (remaining > 0)
        {
            token.ThrowIfCancellationRequested();
            var size = (int)Math.Min(buffer.Length, remaining);
            input.ReadExactly(buffer.AsSpan(0, size));
            output.Write(buffer, 0, size);
            remaining -= size;
        }
        if (input.ReadByte() != -1) throw new InvalidDataException("conversion_package");
    }
}
