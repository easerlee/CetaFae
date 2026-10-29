using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Text.Json;
using System.Windows;
using CetaFae.Probe;

namespace CetaFae.Checks;

static class TransportChecks
{
    static readonly GuideOptions Options = GuideOptions.Create("http://127.0.0.1:8787/v1", "generated-transport");

    public static async Task RunAsync(Action<bool, string> check)
    {
        var image = new byte[256 * 1024];
        new Random(42).NextBytes(image);
        const string task = "生成目标：定位\"选项\"，不修改。\\test\nSecond line";
        const string name = "生成选项 <value> & \"quoted\"";
        GuideCandidate[] candidates = [new(7, name, "", "ControlType.CheckBox", new Rect(3, 4, 50, 20), [], "Off")];
        HttpContent? sentContent = null;
        using (var model = new GuideModel(Options, new Handler(async (request, token) =>
        {
            sentContent = request.Content;
            var declaredLength = request.Content!.Headers.ContentLength;
            var bytes = await request.Content.ReadAsByteArrayAsync(token);
            check(declaredLength == bytes.Length &&
                  request.Content.Headers.ContentType?.CharSet == "utf-8",
                "request declares its UTF8 byte length before gateway transmission");
            using var body = JsonDocument.Parse(bytes);
            var root = body.RootElement;
            check(request.Content.Headers.ContentType?.MediaType == "application/json" &&
                  root.GetProperty("model").GetString() == Options.Model &&
                  !root.GetProperty("stream").GetBoolean() &&
                  root.GetProperty("response_format").GetProperty("type").GetString() == "json_object",
                "large request preserves JSON media type, selected model and response contract");
            var content = root.GetProperty("messages")[1].GetProperty("content");
            var url = content[1].GetProperty("image_url").GetProperty("url").GetString()!;
            check(url.StartsWith("data:image/png;base64,", StringComparison.Ordinal) &&
                  Convert.FromBase64String(url[22..]).SequenceEqual(image),
                "large generated image payload survives JSON escaping without altered bytes");
            using var context = JsonDocument.Parse(content[0].GetProperty("text").GetString()!);
            var candidate = context.RootElement.GetProperty("candidates")[0];
            check(context.RootElement.GetProperty("task").GetString() == task &&
                  context.RootElement.GetProperty("visibleContext").GetString() == "生成上下文" &&
                  candidate.GetProperty("name").GetString() == name &&
                  candidate.GetProperty("toggleState").GetString() == "Off" &&
                  candidate.GetProperty("bounds").GetProperty("x").GetDouble() == 3,
                "large request preserves Chinese, quotes, backslashes, state and scoped coordinates");
            return Completion();
        })))
            check((await model.AskAsync(task, candidates, image, CancellationToken.None, "生成上下文")).Mode == "explain",
                "large generated request accepts its bounded explanation response");
        var disposed = false;
        try { await sentContent!.ReadAsStringAsync(); }
        catch (ObjectDisposedException) { disposed = true; }
        check(disposed, "request content is disposed after the model response");
    }

    public static async Task MeasureAsync()
    {
        // This is an allocation diagnostic, not native idle-memory or model-latency acceptance.
        var image = new byte[1024 * 1024];
        new Random(42).NextBytes(image);
        using var model = new GuideModel(Options, new Handler(async (request, token) =>
        {
            await request.Content!.CopyToAsync(Stream.Null, token);
            return Completion();
        }));
        for (var i = 0; i < 3; i++) await model.AskAsync("Generated allocation warmup", [], image, CancellationToken.None);
        var before = GC.GetTotalAllocatedBytes(true);
        var clock = Stopwatch.StartNew();
        const int iterations = 5;
        for (var i = 0; i < iterations; i++) await model.AskAsync("Generated allocation sample", [], image, CancellationToken.None);
        var allocated = GC.GetTotalAllocatedBytes(true) - before;
        Console.WriteLine(JsonSerializer.Serialize(new
        {
            diagnostic = "generated_transport_allocation", imageBytes = image.Length, iterations,
            allocatedBytes = allocated, allocatedBytesPerRequest = allocated / iterations,
            elapsedMs = clock.Elapsed.TotalMilliseconds, forcedGc = false, network = false, desktop = false
        }));
    }

    static HttpResponseMessage Completion() => new(HttpStatusCode.OK)
    {
        Content = new StringContent("""{"choices":[{"finish_reason":"stop","message":{"content":"{\"mode\":\"explain\",\"candidateId\":null,\"explanation\":\"Generated result\"}"}}]}""")
    };

    sealed class Handler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> send) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token) => send(request, token);
    }
}
