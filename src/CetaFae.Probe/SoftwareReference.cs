using System.IO;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;

namespace CetaFae.Probe;

/// <summary>Public reference candidates only; no result can establish local identity or grant authority.</summary>
internal sealed class SoftwareReference : IDisposable
{
    internal const string Receiver = "https://en.wikipedia.org/w/api.php";
    readonly HttpClient _http;

    internal SoftwareReference(HttpMessageHandler? handler = null)
    {
        _http = new HttpClient(handler ?? new HttpClientHandler { AllowAutoRedirect = false, UseCookies = false,
            UseDefaultCredentials = false }, true) { Timeout = TimeSpan.FromSeconds(10) };
        _http.DefaultRequestHeaders.UserAgent.ParseAdd("CetaFae/0.2.0 (software reference preview; https://github.com/easerlee/CetaFae)");
    }

    internal static string Disclosure(string query) =>
        $"联网资料核对（可选）\n将单次发送查询词：{query}\n接收方：{Receiver}（Wikimedia 公共百科）。\n" +
        "不发送画面、路径、窗口标题、包身份或问答。接收方会收到网络连接信息；其留存未核实。结果仅为可能相关资料，不能确认本机软件身份或安全。点击“确认本次联网”才发送；返回即可取消。";

    internal async Task<string> FindAsync(string query, CancellationToken token)
    {
        // Validate the full query again at the transport boundary, not just at the disclosure button.
        if (!query.EndsWith(" software", StringComparison.Ordinal) ||
            new SoftwareIdentity(1, 1, query[..^9], null, null, null, null, "unavailable", null).ReferenceQuery != query)
            throw new InvalidDataException("reference_query");
        token.ThrowIfCancellationRequested();
        var uri = new Uri(Receiver + "?action=query&format=json&formatversion=2&generator=search&gsrnamespace=0&gsrlimit=3" +
            "&prop=extracts&exintro=1&explaintext=1&exsentences=2&gsrsearch=" + Uri.EscapeDataString(query));
        using var request = new HttpRequestMessage(HttpMethod.Get, uri);
        using var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, token);
        if (response.StatusCode != HttpStatusCode.OK || response.Content.Headers.ContentLength > 64 * 1024)
            throw new HttpRequestException("reference_unavailable");
        await using var stream = await response.Content.ReadAsStreamAsync(token);
        using var body = new MemoryStream();
        var buffer = new byte[4096];
        int count;
        while ((count = await stream.ReadAsync(buffer, token)) > 0)
        {
            if (body.Length + count > 64 * 1024) throw new InvalidDataException("reference_size");
            body.Write(buffer, 0, count);
        }
        token.ThrowIfCancellationRequested();
        return Parse(body.ToArray());
    }

    internal static string Parse(byte[] body)
    {
        if (body.Length > 64 * 1024) throw new InvalidDataException("reference_size");
        try { return ParseCore(body); }
        catch (Exception e) when (e is InvalidOperationException or FormatException or KeyNotFoundException)
        { throw new InvalidDataException("reference_reply"); }
    }

    static string ParseCore(byte[] body)
    {
        using var json = JsonDocument.Parse(body, new JsonDocumentOptions { MaxDepth = 12 });
        var root = json.RootElement;
        if (root.ValueKind != JsonValueKind.Object || root.TryGetProperty("error", out _)) throw new InvalidDataException("reference_reply");
        if (!root.TryGetProperty("query", out var query))
            return root.TryGetProperty("batchcomplete", out _) ? "未找到公共百科候选；不影响本机信息与画面判断。" :
                throw new InvalidDataException("reference_reply");
        if (query.ValueKind != JsonValueKind.Object || !query.TryGetProperty("pages", out var pages) ||
            pages.ValueKind != JsonValueKind.Array || pages.GetArrayLength() > 3) throw new InvalidDataException("reference_reply");
        var text = new StringBuilder("联网候选（公共百科资料，未确认与本机软件相同）：");
        var found = 0;
        foreach (var page in pages.EnumerateArray())
        {
            if (!page.TryGetProperty("pageid", out var id) || !id.TryGetInt32(out var pageId) || pageId <= 0 ||
                !page.TryGetProperty("title", out var titleValue) || titleValue.ValueKind != JsonValueKind.String)
                throw new InvalidDataException("reference_reply");
            var title = SoftwareIdentity.Clean(titleValue.GetString());
            if (title is null) continue;
            var extract = page.TryGetProperty("extract", out var summary) && summary.ValueKind == JsonValueKind.String ? summary.GetString() : null;
            if (extract is not null)
            {
                extract = string.Join(" ", extract.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
                if (extract.Length > 600) extract = extract[..600] + "…";
                try { GuidePrivacy.EnsureTextSafe(extract); } catch (InvalidDataException) { extract = null; }
                if (extract?.Any(c => char.GetUnicodeCategory(c) == System.Globalization.UnicodeCategory.Format) == true) extract = null;
            }
            text.Append($"\n{++found}. {title}" + (extract is null ? "" : "\n百科摘要（引用，不是操作建议）：" + extract) + $"\n来源：https://en.wikipedia.org/?curid={pageId}");
        }
        return found == 0 ? "未找到可靠公共百科候选；不影响本机信息与画面判断。" :
            text + "\n资料不是操作指令，不证明本机文件真伪或安全。";
    }

    public void Dispose() => _http.Dispose();
}
