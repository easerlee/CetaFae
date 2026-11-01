using System.IO;
using CetaFae.Probe;

namespace CetaFae.Checks;

static class InstallationChecks
{
    internal static void Run(Action<bool, string> check)
    {
        var root = Path.GetFullPath(Path.Combine(".artifacts", "checks", "installation-" + Guid.NewGuid().ToString("N")));
        Directory.CreateDirectory(root);
        var preferences = new GuidePreferencesStore(Path.Combine(root, "settings.json"));
        var grant = Path.Combine(root, "consent.dat");
        var prefix = Path.Combine(root, "agreement");
        try
        {
            var bundledWorker = ObservationWorker.CreateStartInfo("generated-app.exe", "");
            var sourceWorker = ObservationWorker.CreateStartInfo("generated-checks.exe", "generated-probe.dll");
            check(bundledWorker.FileName == "generated-app.exe" && bundledWorker.ArgumentList.SequenceEqual(new[] { "--observe-worker" }) &&
                  !bundledWorker.Environment.ContainsKey("CETAFAE_API_KEY") && sourceWorker.FileName == "dotnet" &&
                  sourceWorker.ArgumentList.SequenceEqual(new[] { "generated-probe.dll", "--observe-worker" }),
                "self-contained collectors launch the app itself without requiring dotnet or inheriting model credentials");
            check(InstallationAgreement.Export(prefix, preferences) == 0 && !File.Exists(grant),
                "installer can export fresh-machine disclosure without granting consent");
            var text = File.ReadAllText(prefix + ".txt");
            var scope = File.ReadAllText(prefix + ".scope");
            check(text.Contains("同意协议并安装") && text.Contains(GuidePreferences.Default.BaseUrl) &&
                  text.Contains(GuidePreferences.Default.Model) && scope.Length == 64,
                "installer displays actual default receiver/model and exact agreement scope");
            check(InstallationAgreement.Complete("", preferences, grant) != 0 &&
                  InstallationAgreement.Complete(new string('0', 64), preferences, grant) != 0 && !File.Exists(grant),
                "missing or mismatched installation acceptance never grants consent");
            check(InstallationAgreement.Complete(scope, preferences, grant) == 0 &&
                  new CloudConsentStore(GuidePreferences.Default.Options, grant).Read().Enabled,
                "completed matching installation restores permission without in-app confirmation");
            File.Delete(grant);
            var changed = GuidePreferences.Default with { BaseUrl = "https://example.invalid/v1", Model = "generated-model", IncludeImage = false };
            check(preferences.Save(changed) && InstallationAgreement.Complete(scope, preferences, grant) != 0 && !File.Exists(grant),
                "configuration change after showing the installer agreement prevents a stale grant");
            check(InstallationAgreement.Export(prefix, preferences) == 0 && File.ReadAllText(prefix + ".txt").Contains(changed.BaseUrl) &&
                  File.ReadAllText(prefix + ".txt").Contains(changed.Model),
                "upgrade agreement preserves and truthfully discloses existing non-secret connection choices");
            scope = File.ReadAllText(prefix + ".scope");
            Directory.CreateDirectory(grant);
            check(InstallationAgreement.Complete(scope, preferences, grant) == 3,
                "installer completion reports protected grant storage failure");
            Directory.Delete(grant);
            check(InstallationAgreement.Complete(scope, preferences, grant) == 0 &&
                  new CloudConsentStore(changed.Options, grant).Revoke() && !File.Exists(grant),
                "installed agreement remains revocable without a routine cloud switch");
            File.WriteAllText(Path.Combine(root, "settings.json"), "invalid generated configuration");
            check(InstallationAgreement.Export(prefix, preferences) != 0 &&
                  InstallationAgreement.Complete(scope, preferences, grant) != 0 && !File.Exists(grant),
                "invalid existing configuration cannot be silently replaced with default consent");
        }
        finally { Directory.Delete(root, recursive: true); }
    }
}
