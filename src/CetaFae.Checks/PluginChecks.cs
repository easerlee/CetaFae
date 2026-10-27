using System.IO;
using System.Text.Json;
using CetaFae.Plugins;
using CetaFae.Probe;

namespace CetaFae.Checks;

static class PluginChecks
{
    public static void Run(Action<bool, string> check)
    {
        void Reject(Action action, string name)
        {
            try { action(); }
            catch (Exception ex) when (ex is InvalidDataException or JsonException or ArgumentException)
            { check(true, name); return; }
            throw new InvalidOperationException(name);
        }

        var installed = PluginCatalog.Load(Path.Combine(AppContext.BaseDirectory, "plugins"));
        check(installed.Plugins.Count == 2, "both first-party packages are deployed with the check runner");
        var settings = installed.Resolve("ApplicationFrameHost", "设置");
        var edge = installed.Resolve("msedge", "Downloads");
        var transferred = JsonSerializer.Deserialize<GuidePlugin>(JsonSerializer.Serialize(settings, ObservationWorker.Json),
            ObservationWorker.Json)!;
        check(transferred.Id == settings.Id && transferred.Matches("SystemSettings", "Settings") &&
              !transferred.AllowsPoint("暂停更新", "恢复更新"),
            "collector IPC preserves and validates the session's plugin definition and task guard");
        check(settings.Id != edge.Id && !settings.UsesRawView && edge.UsesRawView,
            "the same resolver selects first-party identity and collection strategy from data");
        Reject(() => installed.Resolve("notepad", "设置"), "a familiar title cannot grant application support");
        Reject(() => installed.Resolve("ApplicationFrameHost", "other application"), "hosted windows require their plugin title constraint");
        check(installed.Resolve("MSEDGE", "Downloads").Id == edge.Id, "process matching uses Windows case-insensitive identity");
        check(!settings.AllowsPoint("暂停更新", "继续更新") && !settings.AllowsPoint("pause updates", "Resume updates") &&
              settings.AllowsPoint("暂停更新", "暂停更新"), "the first-party task plugin preserves the opposite-action guard");
        check(edge.AllowsPoint("暂停更新", "继续更新"), "one application's semantic rules do not leak into another plugin");
        check(!settings.AllowsPoint(null, "暂停更新"), "plugin data cannot permit a speculative initial marker");

        const string generated = """
            {"schemaVersion":1,"id":"example.editor","version":"0.1.0","displayName":"Generated editor",
             "targets":[{"processNames":["generated-editor"]}],"uiaView":"control","taskIds":["example.find"],
             "blockedPoints":[{"intentContainsAny":["keep"],"candidateNameContainsAny":["delete"]}]}
            """;
        var third = PluginCatalog.ParsePlugin(generated);
        var extended = new PluginCatalog(installed.Plugins.Append(third));
        check(extended.Resolve("generated-editor", "generated page").Id == third.Id &&
              !third.AllowsPoint("keep the object", "delete object") && third.AllowsPoint("find the object", "search"),
            "a third application and its task rule work through data without a host application branch");
        Reject(() => new PluginCatalog([]).Resolve("msedge", "Downloads"), "an empty catalog has no hidden first-party fallback");
        Reject(() => new PluginCatalog([settings]).Resolve("msedge", "Downloads"), "disabling the first-party package removes its support");
        Reject(() => new PluginCatalog([edge, edge]), "duplicate package identity rejects the catalog");
        var overlapping = PluginCatalog.ParsePlugin(generated.Replace("generated-editor", "msedge"));
        Reject(() => new PluginCatalog([edge, overlapping]).Resolve("msedge", "Downloads"),
            "overlapping packages fail rather than choosing by load order");
        foreach (var (json, name) in new[]
                 {
                     (generated.Replace("\"schemaVersion\":1", "\"schemaVersion\":2"), "incompatible contract"),
                     (generated.Replace("\"id\":\"example.editor\"", "\"id\":\"../outside\""), "path traversal identity"),
                     (generated.Replace("\"uiaView\":\"control\"", "\"uiaView\":\"execute\""), "unsupported collection capability"),
                     (generated.Replace("\"schemaVersion\":1", "\"command\":\"run\",\"schemaVersion\":1"), "executable field"),
                     (generated.Replace("\"schemaVersion\":1", "\"endpoint\":\"https://invalid.example\",\"schemaVersion\":1"), "recipient override"),
                     (generated.Replace("\"schemaVersion\":1", "\"schemaVersion\":1,\"schemaVersion\":1"), "duplicate fields"),
                     (generated.Replace("\"version\":\"0.1.0\"", "\"version\":\"future\""), "invalid package version"),
                     (generated.Replace("\"processNames\":[\"generated-editor\"]", "\"processNames\":[]"), "empty target"),
                     (generated.Replace("\"intentContainsAny\":[\"keep\"]", "\"intentContainsAny\":[]"), "empty rule"),
                     (generated.Replace("\"blockedPoints\":[{\"intentContainsAny\":[\"keep\"],\"candidateNameContainsAny\":[\"delete\"]}",
                         "\"blockedPoints\":[null"), "null rule"),
                     (new string(' ', PluginCatalog.MaxFileBytes + 1), "oversized package")
                 }) Reject(() => PluginCatalog.ParsePlugin(json), $"plugin contract rejects {name}");

        var names = new List<string> { "initial-process" };
        var frozen = new PluginTarget(names);
        names[0] = "changed-process";
        check(frozen.Matches("initial-process", "") && !frozen.Matches("changed-process", ""),
            "validated target data is a snapshot, not a caller-owned mutable list");
        check(!typeof(PluginCatalog).Assembly.GetReferencedAssemblies().Any(a =>
                a.Name is "PresentationFramework" or "PresentationCore" or "WindowsBase" or "CetaFae.Probe"),
            "the plugin contract has no dependency on WPF or the application host");

        var artifacts = Path.GetFullPath(Path.Combine(".artifacts", "checks"));
        var directory = Path.Combine(artifacts, "plugins-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(directory, third.Id));
        try
        {
            File.WriteAllText(Path.Combine(directory, third.Id, "plugin.json"), generated);
            var catalogPath = Path.Combine(directory, "catalog.json");
            File.WriteAllText(catalogPath, """{"schemaVersion":1,"enabled":["example.editor"]}""");
            var snapshot = PluginCatalog.Load(directory);
            check(snapshot.Resolve("generated-editor", "generated page").Id == third.Id,
                "a separately supplied package loads through the same installed-catalog path");
            File.WriteAllText(catalogPath, """{"schemaVersion":1,"enabled":[]}""");
            Reject(() => PluginCatalog.Load(directory).Resolve("generated-editor", ""),
                "removing the enabled entry disables the package on restart");
            File.WriteAllText(Path.Combine(directory, third.Id, "plugin.json"), "invalid disabled package");
            check(PluginCatalog.Load(directory).Plugins.Count == 0 && snapshot.Resolve("generated-editor", "").Id == third.Id,
                "disabled files are unread and an existing session catalog is unaffected by disk edits");
            File.WriteAllText(catalogPath, """{"schemaVersion":1,"enabled":["../outside"]}""");
            Reject(() => PluginCatalog.Load(directory), "installed-catalog paths cannot leave the package root");
            File.WriteAllText(catalogPath, """{"schemaVersion":1,"enabled":["example.editor"]}""");
            File.WriteAllText(Path.Combine(directory, third.Id, "plugin.json"), generated.Replace("example.editor", "other.id"));
            Reject(() => PluginCatalog.Load(directory), "package identity must match its enabled directory");
        }
        finally
        {
            if (!Path.GetFullPath(directory).StartsWith(artifacts + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("fixture_path");
            Directory.Delete(directory, true);
        }
    }
}
