using System.IO;
using System.Diagnostics;
using System.Text;
using System.Text.Json;
using CetaFae.Plugins;
using CetaFae.Probe;

namespace CetaFae.Checks;

static class ThirdPartyPluginChecks
{
    internal static GuidePlugin Example() => PluginCatalog.Import(Path.Combine(AppContext.BaseDirectory, "examples", "example.editor.json"));

    internal static void Run(Action<bool, string> check)
    {
        void Reject(Action action, string name)
        {
            try { action(); }
            catch (Exception ex) when (ex is InvalidDataException or IOException or JsonException or ArgumentException)
            { check(true, name); return; }
            throw new InvalidOperationException(name);
        }
        var shipped = PluginCatalog.Load(Path.Combine(AppContext.BaseDirectory, "plugins"));
        var plugin = Example();
        Reject(() => PluginCatalog.Import(@"\\unreachable.invalid\share\plugin.json"), "network paths are rejected before any import file read");
        var original = GuidePreferences.Default.Validate();
        var imported = original.ImportPlugin(plugin, shipped);
        check(imported.CustomPlugins.Count == 1 && imported.DisabledPlugins.Contains(plugin.Id) &&
            imported.Filter(shipped).Resolve("generated-editor", "Generated editor fixture").Kind == "general",
            "importing a documented external package stages it disabled without claiming target support");
        check(original.CustomPlugins.Count == 0 && original.Filter(shipped).Plugins.Count == shipped.Plugins.Count,
            "import does not mutate the caller or its active runtime snapshot");
        var enabled = imported with { DisabledPlugins = [] };
        var active = enabled.Filter(shipped);
        check(active.Resolve("generated-editor", "Generated editor fixture").Id == plugin.Id &&
            active.Resolve("generated-editor", "other page").Kind == "general" &&
            !active.Get(plugin.Id).AllowsPoint("keep", "delete"),
            "external data uses the same target, scope and opposite-action guard as bundled plugins");
        var transferred = JsonSerializer.Deserialize<GuidePlugin>(JsonSerializer.Serialize(active.Get(plugin.Id), ObservationWorker.Json), ObservationWorker.Json)!;
        var scope = GuideScope.ForQuestion(new System.Windows.Rect(0, 0, 800, 600), transferred, "destination", null,
            [new GuideScopeAnchor("Destination", "ControlType.Edit", new System.Windows.Rect(100, 100, 200, 30), [1])]);
        check(scope?.Region == new System.Windows.Rect(88, 88, 224, 54) && !transferred.AllowsPoint("keep", "delete"),
            "an imported definition survives collector IPC and uses the production scope resolver without a host branch");
        check(GuidePreferences.Parse(enabled.Serialize()).SameAs(enabled) && !enabled.RequiresNewConsent(original),
            "package inventory and activation round trip together without granting additional authority");
        check(!enabled.SameAs(original) && !enabled.SameAs(imported), "manifest or activation changes require a new runtime snapshot");
        var old = Encoding.UTF8.GetBytes("""{"version":1,"baseUrl":"http://127.0.0.1:8787/v1","model":"deepseek-v4.1-flash","includeImage":true,"disabledPlugins":[]}""");
        check(GuidePreferences.Parse(old).SameAs(original), "v1 preferences migrate without an installation or grant change");
        foreach (var malformed in new[] { "{}", "{\"version\":null}", "[]", "{\"version\":999}" })
            Reject(() => GuidePreferences.Parse(Encoding.UTF8.GetBytes(malformed)), "malformed settings version is a controlled rejection");
        var serialized = PluginCatalog.SerializePlugin(plugin);
        var updated = PluginCatalog.ParsePlugin(serialized.Replace("0.1.0", "0.2.0"));
        var upgraded = enabled.ImportPlugin(updated, shipped);
        check(upgraded.CustomPlugins.Single().Version == "0.2.0" && upgraded.DisabledPlugins.Contains(plugin.Id) &&
            enabled.CustomPlugins.Single().Version == "0.1.0", "higher-version update is staged disabled while the prior snapshot remains unchanged");
        Reject(() => enabled.ImportPlugin(plugin, shipped), "same-version custom overwrite is refused");
        Reject(() => upgraded.ImportPlugin(plugin, shipped), "custom downgrade is refused");
        Reject(() => original.ImportPlugin(shipped.Plugins[0], shipped), "bundled package identity cannot be overwritten by imported data");
        Reject(() => original.ImportPlugin(PluginCatalog.ParsePlugin(serialized.Replace(plugin.Id, shipped.Plugins[0].Id.ToUpperInvariant())), shipped),
            "bundled identity collision is case insensitive");
        var removed = upgraded.RemovePlugin(plugin.Id);
        check(removed.CustomPlugins.Count == 0 && !removed.DisabledPlugins.Contains(plugin.Id) &&
            removed.Filter(shipped).Resolve("generated-editor", "Generated editor fixture").Kind == "general",
            "removal discards only the custom definition and its disabled flag");
        var overlap = PluginCatalog.ParsePlugin(serialized.Replace("generated-editor", "msedge").Replace("Generated editor fixture", "Downloads"));
        var pendingConflict = original.ImportPlugin(overlap, shipped);
        check(pendingConflict.Filter(shipped).Resolve("msedge", "Downloads").Id == "cetafae.edge",
            "a conflicting imported adapter is safe while disabled");
        Reject(() => (pendingConflict with { DisabledPlugins = [] }).Filter(shipped), "overlapping active application adapters cannot be saved");
        Reject(() => new PluginCatalog(new[] { overlap }.Concat(shipped.Plugins)).ValidateActivation(),
            "application overlap cannot be resolved by reversing package order");
        var disjointA = PluginCatalog.ParsePlugin(serialized.Replace("Generated editor fixture", "Page A"));
        var disjointB = PluginCatalog.ParsePlugin(serialized.Replace(plugin.Id, "example.other").Replace("Generated editor fixture", "Page B"));
        var disjoint = new PluginCatalog([disjointA, disjointB]); disjoint.ValidateActivation();
        check(disjoint.Resolve("generated-editor", "Page B").Id == "example.other", "disjoint exact titles on one process remain compatible");
        foreach (var provider in shipped.Plugins.Where(p => p.Kind != "application"))
        {
            var alternative = PluginCatalog.ParsePlugin(PluginCatalog.SerializePlugin(provider).Replace(provider.Id, "example." + provider.Kind));
            var alternativeDraft = original.ImportPlugin(alternative, shipped);
            Reject(() => (alternativeDraft with { DisabledPlugins = [] }).Filter(shipped), "alternative capability provider requires disabling the existing provider");
            check((alternativeDraft with { DisabledPlugins = new[] { provider.Id } }).Filter(shipped).Get(alternative.Id).Kind == provider.Kind,
                "external general, identity and chat packages share the same capability contract");
        }
        var full = original;
        for (var i = shipped.Plugins.Count; i < 32; i++)
            full = full.ImportPlugin(PluginCatalog.ParsePlugin(serialized.Replace(plugin.Id, "example.p" + i)), shipped);
        check(full.AvailablePlugins(shipped).Count == 32, "combined package inventory has one bounded capacity");
        Reject(() => full.ImportPlugin(plugin, shipped), "disabled packages cannot bypass the total inventory limit");

        var artifacts = Path.GetFullPath(".artifacts/checks");
        var directory = Path.Combine(artifacts, "third-party-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            var file = Path.Combine(directory, "plugin.json");
            foreach (var invalid in new[] { "{}", serialized.Replace("\"id\":", "\"endpoint\":\"https://invalid.example\",\"id\":"),
                         serialized.Replace("\"id\":", "\"command\":\"run\",\"id\":"), serialized.Replace("\"schemaVersion\":2", "\"schemaVersion\":999"),
                         serialized.Replace("\"id\":", "\"schemaVersion\":2,\"id\":"), new string(' ', PluginCatalog.MaxFileBytes + 1) })
            {
                File.WriteAllText(file, invalid); Reject(() => PluginCatalog.Import(file), "invalid external package is rejected without runtime activation");
            }
            File.WriteAllBytes(file, [0xff, 0xff]); Reject(() => PluginCatalog.Import(file), "non-UTF8 package is refused");
            var executable = Path.Combine(directory, "plugin.exe"); File.WriteAllText(executable, serialized);
            Reject(() => PluginCatalog.Import(executable), "a valid manifest disguised as executable is not an importable package");
            Reject(() => PluginCatalog.Import(file + ":alternate.json"), "alternate data stream names cannot bypass the single-file import boundary");
            File.WriteAllText(file, serialized);
            var link = Path.Combine(directory, "linked");
            try { Directory.CreateSymbolicLink(link, directory); }
            catch (IOException)
            {
                var start = new ProcessStartInfo("cmd.exe") { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
                foreach (var argument in new[] { "/d", "/c", "mklink", "/J", link, directory }) start.ArgumentList.Add(argument);
                using var junction = Process.Start(start)!; junction.WaitForExit();
                if (junction.ExitCode != 0) throw new InvalidOperationException("generated_junction_failed");
            }
            try { Reject(() => PluginCatalog.Import(Path.Combine(link, "plugin.json")), "linked package ancestors cannot redirect the import reader"); }
            finally { Directory.Delete(link); }
            var store = new GuidePreferencesStore(Path.Combine(directory, "preferences.json"));
            check(store.Save(enabled) && store.Read().Value.SameAs(enabled), "installed package survives an atomic settings save and reload");
            File.WriteAllText(file, "source changed after import");
            check(store.Read().Value.Filter(shipped).Get(plugin.Id).Version == plugin.Version,
                "saved extension definitions no longer depend on or reread the author source file");
            var before = File.ReadAllBytes(Path.Combine(directory, "preferences.json"));
            using (var locked = new FileStream(Path.Combine(directory, "preferences.json"), FileMode.Open, FileAccess.Read, FileShare.Read))
                check(!store.Save(upgraded), "failed replacement cannot partially update a plugin or its activation");
            check(File.ReadAllBytes(Path.Combine(directory, "preferences.json")).SequenceEqual(before) && store.Read().Value.SameAs(enabled),
                "failed plugin save retains the old complete package and settings");
            using var diagnostics = JsonDocument.Parse(GuideDiagnostics.Create(enabled.AvailablePlugins(shipped), active, false, ConnectionStatus.Unchecked));
            check(diagnostics.RootElement.GetProperty("plugins").EnumerateArray().Any(p => p.GetProperty("id").GetString() == plugin.Id) &&
                !diagnostics.RootElement.ToString().Contains("generated-editor"), "custom diagnostics export metadata without target rules or paths");
        }
        finally
        {
            if (!Path.GetFullPath(directory).StartsWith(artifacts + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("fixture_path");
            Directory.Delete(directory, true);
        }
    }
}
