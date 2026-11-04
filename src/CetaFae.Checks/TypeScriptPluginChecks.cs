using System.Text.Json;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Diagnostics;
using CetaFae.Plugins;
using CetaFae.Probe;

namespace CetaFae.Checks;

internal static class TypeScriptPluginChecks
{
    internal static void Run(Action<bool, string> check)
    {
        var definition = new PluginProgram(1, "function invoke(input) { return JSON.stringify({reply: JSON.parse(input).input.question}); }");
        using var result = JsonDocument.Parse(PluginExecutor.Invoke(definition, "chat", new { question = "generated" }));
        check(result.RootElement.GetProperty("reply").GetString() == "generated", "a TS-compatible program receives only JSON and returns a structured result");
        foreach (var (version, source) in new[] { (2, "function invoke(){}"), (1, ""), (1, new string('中', 10000)) })
        {
            var rejected = false;
            try { _ = new PluginProgram(version, source); }
            catch (InvalidDataException) { rejected = true; }
            check(rejected, "invalid API, empty source and UTF8 source limits reject before execution");
        }
        void Reject(string source, string name)
        {
            try { PluginExecutor.Invoke(new PluginProgram(1, source), "generated", new { }); }
            catch (InvalidDataException ex) { check(ex.Message == "plugin_execution_failed", name); return; }
            throw new InvalidOperationException(name);
        }
        foreach (var expression in new[] { "require('fs')", "process.env", "fetch('https://example.invalid')", "System.IO.File", "importNamespace('System')",
                     "eval('1')", "new Function('return 1')()", "({}).constructor.constructor('return 1')()" })
            Reject("function invoke() { return JSON.stringify({value: " + expression + "}); }", "program cannot reach a host capability or compile strings: " + expression.Split('(')[0]);
        Reject("function invoke() { while(true) {} }", "statement budget stops an infinite loop");
        Reject("function recurse() { return recurse(); } function invoke() { return recurse(); }", "recursion is bounded");
        Reject("function invoke() { return JSON.stringify({values: new Array(100001)}); }", "array allocation has a finite size bound");
        Reject("function invoke() { let a=[]; while(true) { a.push(new Array(1000).fill('generated')); } }", "managed allocation budget rejects an accumulating script");
        Reject("function invoke() { return JSON.stringify({text: 'x'.repeat(270000)}); }", "oversized program output is rejected");
        Reject("function invoke() { return '[]'; }", "nonobject JSON results are rejected");
        Reject("function invoke() { return '{broken'; }", "invalid JSON results are rejected");
        Reject("function invoke() { throw new Error('generated-private-text'); }", "errors never expose program text or user data");
        var isolated = new PluginProgram(1, "let count=0; function invoke() { return JSON.stringify({count: ++count}); }");
        check(PluginExecutor.Invoke(isolated, "generated", new { }) == PluginExecutor.Invoke(isolated, "generated", new { }),
            "invocations do not retain mutable script state");
        var environment = PluginWorker.StartInfo("generated.exe", "").Environment;
        check(!environment.ContainsKey("CETAFAE_API_KEY") && environment.Keys.All(key => new[] { "SystemRoot", "WINDIR", "PATH", "TEMP", "TMP", "DOTNET_ROOT" }.Contains(key)),
            "feature workers inherit only the explicit runtime environment allowlist");
        var shipped = GuideFeature.DefaultCatalog();
        check(shipped.Plugins.All(plugin => plugin.Program is { ApiVersion: 1 }), "every bundled feature and adapter carries a compiled program");
        try { GuideFeature.Select(new PluginCatalog([])); throw new InvalidOperationException("hidden_provider"); }
        catch (InvalidDataException) { check(true, "a missing general feature cannot reactivate a disabled first-party provider"); }
    }

    static GuidePlugin Example() => PluginCatalog.Import(Path.Combine(AppContext.BaseDirectory, "examples", "example.chat.json"));

    internal static async Task RunAsync(Action<bool, string> check)
    {
        var shipped = GuideFeature.DefaultCatalog();
        var example = Example();
        var utf8Start = PluginWorker.StartInfo(Environment.ProcessPath!, typeof(GuideModel).Assembly.Location);
        check(utf8Start.StandardInputEncoding!.CodePage == 65001 && utf8Start.StandardOutputEncoding!.CodePage == 65001,
            "feature pipes explicitly use UTF8 independently of the GUI process console code page");
        var unicodeRequest = JsonSerializer.Serialize(new { program = example.Program, operation = "chat", input = new { question = "生成问题：中文与水花" } },
            new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase, Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping });
        using (var unicodeReply = JsonDocument.Parse(await ObservationWorker.ExchangeAsync(utf8Start, unicodeRequest, default)))
            check(unicodeReply.RootElement.GetProperty("result").GetProperty("messages")[1].GetProperty("content").GetString() == "生成问题：中文与水花",
                "raw Unicode JSON survives both sides of the actual feature process boundary");
        var largeContext = new { candidates = Enumerable.Range(1, 128).Select(id => new
            { id, name = new string('界', 200), controlType = "ControlType.Button", bounds = new { x = 1, y = 1, width = 20, height = 20 } }) };
        var largeMessages = await GuideFeature.MessagesAsync(shipped.GeneralProvider!, "window",
            new { identifyWindow = false, interpretationPolicy = shipped.GeneralProvider!.InterpretationPolicy, context = largeContext }, default);
        check(largeMessages.Count == 2 && largeMessages[1].Content.Contains(new string('界', 200)),
            "maximum collected candidate counts and Unicode names fit the feature boundary without truncation");
        var sensitiveRejected = false;
        try
        {
            await GuideFeature.MessagesAsync(shipped.GeneralProvider!, "window", new { context = new
                { candidates = largeContext.candidates, credential = "sk-" + new string('a', 48) } }, default);
        }
        catch (InvalidDataException ex) when (ex.Message == "sensitive_content") { sensitiveRejected = true; }
        check(sensitiveRejected, "large structured context still rejects a generated credential in an individual field");
        var imported = GuidePreferences.Default.ImportPlugin(example, shipped);
        check(imported.Filter(shipped).ChatProvider!.Id != example.Id, "an imported code feature starts disabled, without executing on import");
        var enabled = imported with { DisabledPlugins = [shipped.ChatProvider!.Id] };
        var reloaded = GuidePreferences.Parse(enabled.Serialize()).Filter(shipped);
        check(reloaded.ChatProvider!.Id == example.Id && reloaded.ChatProvider.Program!.Source == example.Program!.Source,
            "an imported TS feature survives the existing atomic preference lifecycle and replaces the selected capability");
        var calls = 0;
        using var model = new GuideModel(GuideOptions.Create("http://127.0.0.1:8787/v1", "generated"), new Handler(async request =>
        {
            using var body = JsonDocument.Parse(await request.Content!.ReadAsStringAsync());
            var messages = body.RootElement.GetProperty("messages");
            check(messages[0].GetProperty("content").GetString()!.StartsWith("You are an example software helper.") &&
                  messages[1].GetProperty("content").GetString() == "生成问题", "production transport uses the imported program rather than a bundled-ID fallback");
            calls++;
            return new(HttpStatusCode.OK) { Content = new StringContent("{\"choices\":[{\"finish_reason\":\"stop\",\"message\":{\"content\":\"{\\\"reply\\\":\\\"生成回答\\\"}\"}}]}") };
        }), plugins: reloaded);
        check(await model.ChatAsync([], "生成问题", default) == "生成回答" && calls == 1,
            "third-party generated TS package completes the same validated chat path as first-party features");
        var clock = Stopwatch.StartNew();
        var rejected = false;
        try { await PluginWorker.InvokeAsync(new PluginProgram(1, "function invoke() { while(true) {} }"), "generated", new { }, default); }
        catch (InvalidDataException) { rejected = true; }
        check(rejected && clock.Elapsed < TimeSpan.FromSeconds(5), "disposable process rejects runaway programs within its external deadline");
        using var interrupted = new CancellationTokenSource();
        var child = 0;
        var request = JsonSerializer.Serialize(new { program = example.Program, operation = "chat", input = new { question = "unused" } },
            new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase });
        rejected = false;
        try
        {
            await ObservationWorker.ExchangeAsync(PluginWorker.StartInfo(Environment.ProcessPath!, typeof(GuideModel).Assembly.Location), request,
                interrupted.Token, pid => { child = pid; interrupted.Cancel(); });
        }
        catch (OperationCanceledException) { rejected = true; }
        var exited = false;
        try { using var process = Process.GetProcessById(child); exited = process.HasExited; }
        catch (ArgumentException) { exited = true; }
        check(rejected && child > 0 && exited, "cancellation reaps the actual owned feature process before returning");
        using var cancelled = new CancellationTokenSource(); cancelled.Cancel();
        rejected = false;
        try { await PluginWorker.InvokeAsync(example.Program!, "chat", new { question = "unused" }, cancelled.Token); }
        catch (OperationCanceledException) { rejected = true; }
        check(rejected, "cancelled invocation cannot start a new feature request");
        foreach (var output in new[] { "{\"messages\":[]}", "{\"messages\":[{\"role\":\"tool\",\"content\":\"invalid\"},{\"role\":\"user\",\"content\":\"invalid\"}]}",
                     "{\"messages\":[{\"role\":\"system\",\"content\":\"generated\"},{\"role\":\"user\",\"content\":\"generated\"}],\"endpoint\":\"https://example.invalid\"}" })
        {
            var invalid = new GuidePlugin(7, "example.invalid", "0.1", "Generated", [], "control", [], [], kind: "chat",
                program: new PluginProgram(1, "function invoke() { return " + JsonSerializer.Serialize(output) + "; }"));
            rejected = false;
            try { await GuideFeature.MessagesAsync(invalid, "chat", new { }, default); }
            catch (InvalidDataException) { rejected = true; }
            check(rejected, "host rejects invalid messages or an attempted receiver override before transport");
        }
        var recovered = await GuideFeature.MessagesAsync(example, "chat", new { question = "恢复" }, default);
        check(recovered[1].Content == "恢复", "a failed or cancelled program does not poison the next worker");
    }

    sealed class Handler(Func<HttpRequestMessage, Task<HttpResponseMessage>> reply) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token) => reply(request);
    }
}
