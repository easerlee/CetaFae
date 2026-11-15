using System.IO;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using CetaFae.Probe;

namespace CetaFae.Checks;

internal static class ConversionBundleChecks
{
    internal static void Run(Action<bool, string> check, string? officialPackage = null)
    {
        var root = Path.GetFullPath(Path.Combine(".artifacts", "checks", "bundle-" + Guid.NewGuid().ToString("N")));
        Directory.CreateDirectory(root);
        var tree = Directory.CreateDirectory(Path.Combine(root, "fixture")).FullName;
        File.WriteAllText(Path.Combine(tree, "codec.exe"), "generated, never executable");
        var payload = Path.Combine(root, "fixture.zip");
        ZipFile.CreateFromDirectory(tree, payload);
        var license = Encoding.UTF8.GetBytes("Generated license");
        var notices = Encoding.UTF8.GetBytes("Generated notices");
        string Hash(byte[] data) => Convert.ToHexString(SHA256.HashData(data));
        var tool = FixedTool.ImageConverter with
        {
            Id = "test.bundle.1", DirectoryName = "test-bundle-1", Executable = "codec.exe",
            TreeHash = FixedTool.DirectoryHash(tree, default)
        };
        var bundle = new FixedToolBundle(tool, "https://example.invalid/generated", Hash(File.ReadAllBytes(payload)), Hash(license), Hash(notices));
        var tools = Path.Combine(root, "tools");
        var bundles = Path.Combine(tools, "bundles");
        var target = Path.Combine(bundles, tool.DirectoryName, "payload");
        var file = Path.Combine(root, "test.cetaplugin");

        void Write(string id = "test.bundle.1", bool extra = false, bool badArchive = false, bool missingLicense = false, bool badLicense = false)
        {
            File.Delete(file);
            using var zip = ZipFile.Open(file, ZipArchiveMode.Create);
            void Put(string name, byte[] content)
            {
                using var entry = zip.CreateEntry(name).Open();
                entry.Write(content);
            }
            Put("bundle-id.txt", Encoding.UTF8.GetBytes(id));
            Put("filee-portable.zip", badArchive ? [0] : File.ReadAllBytes(payload));
            if (!missingLicense) Put("LICENSE", badLicense ? [0] : license);
            Put("THIRD-PARTY-NOTICES.md", notices);
            Put("SOURCE.txt", Encoding.UTF8.GetBytes(bundle.Source));
            if (extra) Put("../rogue.exe", [1]);
        }
        void Reject(Action attempt, string message)
        {
            try { attempt(); }
            catch (Exception ex) when (ex is InvalidDataException or OperationCanceledException or PlatformFailure)
            { check(true, message); return; }
            check(false, message);
        }

        try
        {
            Write(id: "test.bundle.2");
            Reject(() => bundle.Install(file, tools, default), "unknown package version cannot be installed");
            Write(extra: true);
            Reject(() => bundle.Install(file, tools, default), "archive traversal and extra files are rejected before extraction");
            Write(missingLicense: true);
            Reject(() => bundle.Install(file, tools, default), "package missing upstream license is rejected");
            Write(badLicense: true);
            Reject(() => bundle.Install(file, tools, default), "package with altered upstream license is rejected");
            Write(badArchive: true);
            Reject(() => bundle.Install(file, tools, default), "altered upstream archive cannot reach extraction");
            check(!Directory.Exists(target), "rejected packages leave no active directory");
            Write();
            check(tool.FindInstallation(tools) == Path.Combine(tools, tool.DirectoryName),
                "an existing portable location remains usable before installing a bundle");
            Reject(() => bundle.Install(file, tools, new CancellationToken(true)), "cancelled import does not activate a tool");
            check(bundle.Install(file, tools, default) == BundleInstallResult.Installed &&
                FixedTool.DirectoryHash(target, default) == tool.TreeHash && tool.FindInstallation(tools) == target,
                "verified bundle activates the exact expected dependency tree");
            check(bundle.Install(file, tools, default) == BundleInstallResult.AlreadyInstalled,
                "repeat import preserves the active directory");
            Write(badArchive: true);
            Reject(() => bundle.Install(file, tools, default), "failed update preserves the active version");
            check(FixedTool.DirectoryHash(target, default) == tool.TreeHash, "active installation survives a rejected replacement");
            Write();
            File.WriteAllText(Path.Combine(target, tool.Executable), "damaged");
            check(bundle.Install(file, tools, default) == BundleInstallResult.Repaired &&
                FixedTool.DirectoryHash(target, default) == tool.TreeHash,
                "verified reimport repairs a damaged component");
            check(!Directory.EnumerateDirectories(bundles, ".install-*").Any() &&
                Directory.GetDirectories(bundles, "test-bundle-1.previous-*").Length == 1,
                "installer removes staging and retains one repaired original");
            if (officialPackage is null) return;

            var official = FixedToolBundle.ImageConverter;
            var cleanProfileTools = Path.Combine(root, "official");
            check(official.Install(officialPackage, cleanProfileTools, default) == BundleInstallResult.Installed,
                "official optional archive installs on a clean profile");
            var installed = Path.Combine(cleanProfileTools, "bundles", official.Tool.DirectoryName);
            check(official.Tool.FindInstallation(cleanProfileTools) == Path.Combine(installed, "payload") &&
                FixedTool.DirectoryHash(official.Tool.FindInstallation(cleanProfileTools), default) == official.Tool.TreeHash,
                "installed optional payload matches the real runtime recipe");
            check(File.Exists(Path.Combine(installed, "LICENSE")) && File.Exists(Path.Combine(installed, "THIRD-PARTY-NOTICES.md")) &&
                File.ReadAllText(Path.Combine(installed, "SOURCE.txt")) == official.Source,
                "offline import retains both verified third-party declarations and the upstream source link");
        }
        finally
        {
            PlatformScope.CheckAncestors(root);
            Directory.Delete(root, recursive: true);
        }
    }
}
