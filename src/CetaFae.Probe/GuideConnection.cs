using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text.Json;

namespace CetaFae.Probe;

internal enum ConnectionStatus { Unchecked, Checking, Reachable, ModelMissing, Authentication, Quota, Limited,
    Unavailable, InvalidResponse, Cancelled }

/// <summary>Explicit model-list diagnostics only; never collects a target or proves inference support.</summary>
internal static class GuideConnection
{
    internal static string Message(ConnectionStatus status) => status switch
    {
        ConnectionStatus.Checking => "正在检测连接；不读取窗口，也不发送问答。",
        ConnectionStatus.Reachable => "服务可达，且列出了所选模型。图片理解与指路能力尚未验证。",
        ConnectionStatus.ModelMissing => "服务可达，但未列出所选模型。请核对模型名称；部分兼容服务不提供完整列表。",
        ConnectionStatus.Authentication => "服务拒绝了身份验证。请检查服务端或运行时密钥配置。",
        ConnectionStatus.Quota => "服务报告额度不足。请到所用服务检查额度。",
        ConnectionStatus.Limited => "服务正在限流，请稍后手动检测。",
        ConnectionStatus.Unavailable => "暂时无法连接服务。请检查服务是否运行、地址和网络，再手动检测。",
        ConnectionStatus.InvalidResponse => "未取得可用的模型列表；部分兼容服务不支持此检测，不能据此判断识别能力。",
        ConnectionStatus.Cancelled => "检测已取消；没有读取窗口或发送问答。",
        _ => "尚未检测。同意使用不代表模型服务已经可用。"
    };

    internal static async Task<ConnectionStatus> TestAsync(GuideOptions options, CancellationToken token,
        HttpMessageHandler? handler = null)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
        timeout.CancelAfter(TimeSpan.FromSeconds(10));
        using var http = new HttpClient(handler ?? new HttpClientHandler
        { AllowAutoRedirect = false, UseProxy = false, UseCookies = false, UseDefaultCredentials = false })
        { Timeout = Timeout.InfiniteTimeSpan };
        try
        {
            timeout.Token.ThrowIfCancellationRequested();
            using var request = new HttpRequestMessage(HttpMethod.Get, new Uri(options.BaseUrl, "models"));
            var key = Environment.GetEnvironmentVariable("CETAFAE_API_KEY");
            if (!string.IsNullOrWhiteSpace(key)) request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", key);
            using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token);
            if (!response.IsSuccessStatusCode) return response.StatusCode switch
            {
                HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden => ConnectionStatus.Authentication,
                HttpStatusCode.PaymentRequired => ConnectionStatus.Quota,
                HttpStatusCode.TooManyRequests => ConnectionStatus.Limited,
                >= HttpStatusCode.InternalServerError => ConnectionStatus.Unavailable,
                _ => ConnectionStatus.InvalidResponse
            };
            if (response.Content.Headers.ContentLength > 32 * 1024) return ConnectionStatus.InvalidResponse;
            await using var stream = await response.Content.ReadAsStreamAsync(timeout.Token);
            using var body = new MemoryStream();
            var buffer = new byte[4096];
            int count;
            while ((count = await stream.ReadAsync(buffer, timeout.Token)) > 0)
            {
                if (body.Length + count > 32 * 1024) return ConnectionStatus.InvalidResponse;
                body.Write(buffer, 0, count);
            }
            timeout.Token.ThrowIfCancellationRequested();
            using var json = JsonDocument.Parse(body.ToArray(), new JsonDocumentOptions { MaxDepth = 8 });
            var data = json.RootElement.GetProperty("data");
            if (data.ValueKind != JsonValueKind.Array || data.GetArrayLength() > 256) return ConnectionStatus.InvalidResponse;
            return data.EnumerateArray().Any(m => m.GetProperty("id").GetString() == options.Model)
                ? ConnectionStatus.Reachable : ConnectionStatus.ModelMissing;
        }
        catch (OperationCanceledException) { return token.IsCancellationRequested ? ConnectionStatus.Cancelled : ConnectionStatus.Unavailable; }
        catch (HttpRequestException) { return ConnectionStatus.Unavailable; }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException or KeyNotFoundException or
                                      IOException or FormatException)
        { return ConnectionStatus.InvalidResponse; }
    }
}
