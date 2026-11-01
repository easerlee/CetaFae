using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using CetaFae.Plugins;
using CetaFae.Probe;

namespace CetaFae.Checks;

static class SoftwareIdentityChecks
{
    internal static SoftwareIdentity Generated => new(12, 100, "Generated Editor", "Generated document tool", "1.2.3", "Example", null, "unsigned", null);

    internal static void Run(Action<bool, string> check)
    {
        var catalog = PluginCatalog.Load(Path.Combine(AppContext.BaseDirectory, "plugins"));
        var identity = catalog.IdentityProvider!;
        check(identity.Kind == "identity" && identity.SchemaVersion == 4 && identity.Targets.Count == 0 &&
            catalog.Resolve("unknown-generated-app", "").Kind == "general", "identity capability composes without capturing guide-plugin resolution");
        check(new PluginCatalog(catalog.Plugins.Where(p => p.Kind != "identity")).IdentityProvider is null,
            "disabling the identity package removes metadata/reference authority independently of general guidance");
        void Reject(Action action)
        {
            try { action(); } catch (Exception e) when (e is InvalidDataException or JsonException) { check(true, "invalid or conflicting identity capabilities reject"); return; }
            throw new InvalidOperationException("identity_capability_accepted");
        }
        var wire = JsonSerializer.Serialize(identity, ObservationWorker.Json);
        Reject(() => new PluginCatalog([identity, new GuidePlugin(4, "example.other-identity", "1.0", "Other", [], "control", ["identity"], [], [], "identity", "local-metadata-public-reference")]));
        Reject(() => JsonSerializer.Deserialize<GuidePlugin>(wire.Replace("local-metadata-public-reference", "download-and-execute"), ObservationWorker.Json));
        Reject(() => JsonSerializer.Deserialize<GuidePlugin>(wire.Replace("\"Kind\":\"identity\"", "\"Kind\":\"general\""), ObservationWorker.Json));
        var roundtrip = JsonSerializer.Deserialize<GuidePlugin>(wire, ObservationWorker.Json)!;
        check(roundtrip.IdentityPolicy == identity.IdentityPolicy, "bounded worker IPC retains the validated identity capability");
        foreach (var value in new[] { @"C:\private\file.exe", "https://example.test", "a@example.test", "forged\nlabel", "ab\u202ecd", "sk-" + new string('a', 48), new string('x', 161) })
            check(SoftwareIdentity.Clean(value) is null, "unsafe local metadata is withheld rather than logged, displayed or queried");
        check(Generated.IsValid(12) && !Generated.IsValid(13) && !(Generated with { Signature = "safe-to-run" }).IsValid(12),
            "identity IPC binds the selected PID and restricts evidence states without a safety verdict");
        check(Generated.ReferenceQuery == "Generated Editor software" &&
            (Generated with { Product = null }).ReferenceQuery is null &&
            (Generated with { Product = @"C:\private" }).ReferenceQuery is null &&
            (Generated with { Product = "sk-" + new string('a', 48) }).ReferenceQuery is null,
            "public queries use a qualified product label only, never an implicit path/title/process fallback");
        var answer = new GuideIdentification("Another visual brand", "Generated page", "想处理什么？").ToAnswer("generated", Generated);
        check(answer.Explanation.Contains("本机文件自报：Generated Editor") && answer.Explanation.Contains("软件（画面判断）：Another visual brand") &&
            answer.Explanation.Contains("当前界面：Generated page"), "local and conflicting visual claims remain separate evidence, not merged certainty");
        check(GuideIdentification.Unavailable("generated", "服务不可用", Generated).Contains("Generated Editor"),
            "a current model-only failure may retain independently collected local metadata");
        var observation = new GuideObservation(new IntPtr(1), 12, new(0, 0, 100, 100), [], null, Identity: Generated);
        var payloadJson = JsonSerializer.Serialize(GuidePayload.From(observation));
        check(!payloadJson.Contains("Generated Editor") && !payloadJson.Contains("Example") && !payloadJson.Contains("Identity"),
            "local software metadata is absent from the actual model payload projection");
        using var own = Process.GetCurrentProcess();
        var local = SoftwareIdentity.Read((uint)own.Id, CancellationToken.None);
        check(local.IsValid((uint)own.Id) && local.StartedAt == own.StartTime.ToUniversalTime().Ticks,
            "platform metadata/package/offline-trust path runs on this owned headless process without UI or external targets");
        var assembly = typeof(GuideOptions).Assembly.Location;
        check(FileVersionInfo.GetVersionInfo(assembly).ProductName == "CetaFae" &&
            FileVersionInfo.GetVersionInfo(assembly).ProductVersion == "0.2.0-preview.14" && SoftwareIdentity.IsLocalRegularFile(assembly),
            "generated preview assembly carries readable product resources on a regular local file");
        check(SoftwareIdentity.VerifyOffline(assembly) is "unsigned" or "unverified", "unsigned generated assembly never obtains a trusted-signature verdict");
        check(!SoftwareIdentity.IsLocalRegularFile(@"\\example.test\share\generated.exe") &&
            !SoftwareIdentity.IsLocalRegularFile("relative.exe") && !SoftwareIdentity.IsLocalRegularFile(assembly + ":stream"),
            "network, relative and alternate-stream identity file paths refuse before access");
    }

    internal static async Task RunTransportAsync(Action<bool, string> check)
    {
        var calls = 0;
        const string result = """{"batchcomplete":true,"query":{"pages":[{"pageid":42,"title":"Generated Editor","extract":"An example software tool."}]}}""";
        using var client = new SoftwareReference(new Handler(async (request, token) =>
        {
            calls++;
            var uri = request.RequestUri!;
            check(uri.Scheme == "https" && uri.Host == "en.wikipedia.org" && uri.AbsolutePath == "/w/api.php" &&
                uri.Query.Contains("gsrsearch=Generated%20Editor%20software") && !request.Headers.Contains("Authorization") &&
                request.Content is null, "reference transport sends only the disclosed product query to the fixed HTTPS receiver");
            await Task.CompletedTask;
            return new(HttpStatusCode.OK) { Content = new StringContent(result) };
        }));
        var found = await client.FindAsync(Generated.ReferenceQuery!, CancellationToken.None);
        check(calls == 1 && found.Contains("未确认与本机软件相同") && found.Contains("https://en.wikipedia.org/?curid=42"),
            "reference result is attributed candidate evidence, never binary verification or an automatic link launch");
        async Task Reject(Func<Task> action)
        {
            try { await action(); } catch (Exception e) when (e is InvalidDataException or HttpRequestException or JsonException or OperationCanceledException)
            { check(true, "invalid, cancelled or bounded reference request refuses"); return; }
            throw new InvalidOperationException("reference_accepted");
        }
        await Reject(() => client.FindAsync(@"C:\private\document software", CancellationToken.None));
        using var cancelled = new CancellationTokenSource(); cancelled.Cancel();
        await Reject(() => client.FindAsync(Generated.ReferenceQuery!, cancelled.Token));
        check(calls == 1, "invalid or pre-cancelled reference queries cannot reach the handler");
        foreach (var (status, body) in new[] { (HttpStatusCode.Redirect, result), (HttpStatusCode.TooManyRequests, ""),
                     (HttpStatusCode.OK, new string('x', 65537)), (HttpStatusCode.OK, "not json"),
                     (HttpStatusCode.OK, """{"query":{"pages":[{"pageid":-1,"title":"bad"}]}}"""),
                     (HttpStatusCode.OK, """{"query":{"pages":[false]}}"""), (HttpStatusCode.OK, "{}"),
                     (HttpStatusCode.OK, """{"query":{"pages":[{"pageid":"bad","title":"bad"}]}}""") })
        {
            using var failure = new SoftwareReference(new Handler((_, _) => Task.FromResult(new HttpResponseMessage(status)
                { Content = new StringContent(body) })));
            await Reject(() => failure.FindAsync(Generated.ReferenceQuery!, CancellationToken.None));
        }
        check(SoftwareReference.Parse(Encoding.UTF8.GetBytes("""{"batchcomplete":true}""")).Contains("未找到"),
            "empty public results remain unknown without changing local/visual evidence");
        var late = new TaskCompletionSource<HttpResponseMessage>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var delayed = new SoftwareReference(new Handler((_, _) => late.Task));
        using var stop = new CancellationTokenSource();
        var pending = delayed.FindAsync(Generated.ReferenceQuery!, stop.Token);
        stop.Cancel();
        late.SetResult(new(HttpStatusCode.OK) { Content = new StringContent(result) });
        await Reject(() => pending);
    }

    sealed class Handler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> run) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token) => run(request, token);
    }

    internal static async Task RunLiveAsync(Action<bool, string> check)
    {
        using var client = new SoftwareReference();
        var result = await client.FindAsync("7-Zip software", CancellationToken.None);
        check(result.Contains("7-Zip") && result.Contains("https://en.wikipedia.org/?curid="),
            "real public-reference request finds attributed software candidates from a generated public label, without any desktop content");
    }
}
