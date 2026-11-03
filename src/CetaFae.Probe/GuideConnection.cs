using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Globalization;
using System.Security.Cryptography;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace CetaFae.Probe;

internal enum ConnectionStatus { Unchecked, Checking, Reachable, ModelMissing, Authentication, Quota, Limited,
    Unavailable, InvalidResponse, Cancelled, ImageVerified, ImageUnconfirmed }

/// <summary>Explicit service diagnostics use either a model list or a generated image, never a target window.</summary>
internal static class GuideConnection
{
    internal static string Message(ConnectionStatus status) => status switch
    {
        ConnectionStatus.Checking => "正在检测所选服务；不读取你的窗口。",
        ConnectionStatus.Reachable => "服务可达，且列出了所选模型。图片理解与指路能力尚未验证。",
        ConnectionStatus.ModelMissing => "服务可达，但未列出所选模型。请核对模型名称；部分兼容服务不提供完整列表。",
        ConnectionStatus.Authentication => "服务拒绝了身份验证。请检查服务端或运行时密钥配置。",
        ConnectionStatus.Quota => "服务报告额度不足。请到所用服务检查额度。",
        ConnectionStatus.Limited => "服务正在限流，请稍后手动检测。",
        ConnectionStatus.Unavailable => "暂时无法连接服务。请检查服务是否运行、地址和网络，再手动检测。",
        ConnectionStatus.InvalidResponse => "服务返回的内容无法用于这次检测。请核对接口兼容性；这不代表你的窗口有问题。",
        ConnectionStatus.ImageVerified => "已读对本次生成图片中的数字，图片传输和基础读图可用；不代表所有窗口或指路都准确。",
        ConnectionStatus.ImageUnconfirmed => "服务返回了结果，但没有读对测试图片；暂未确认图片识别可用。不会发送你的窗口重试。",
        ConnectionStatus.Cancelled => "检测已取消；未读取你的窗口，已经发出的检测请求无法撤回。",
        _ => "尚未检测。同意使用不代表模型服务已经可用。"
    };

    internal static byte[] CreateImage(string digits)
    {
        var drawing = new DrawingVisual();
        using (var dc = drawing.RenderOpen())
        {
            dc.DrawRectangle(Brushes.White, null, new Rect(0, 0, 360, 100));
            dc.DrawText(new FormattedText(digits, CultureInfo.InvariantCulture, FlowDirection.LeftToRight,
                new Typeface("Arial"), 48, Brushes.Black, 1), new Point(30, 20));
        }
        var bitmap = new RenderTargetBitmap(360, 100, 96, 96, PixelFormats.Pbgra32); bitmap.Render(drawing);
        var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var bytes = new MemoryStream(); encoder.Save(bytes); return bytes.ToArray();
    }

    internal static async Task<ConnectionStatus> TestImageAsync(GuideOptions options, CancellationToken token,
        HttpMessageHandler? handler = null)
    {
        if (token.IsCancellationRequested) return ConnectionStatus.Cancelled;
        var digits = RandomNumberGenerator.GetInt32(100000, 1000000).ToString(CultureInfo.InvariantCulture);
        var png = CreateImage(digits);
        using var model = new GuideModel(options with { IncludeImage = true, Timeout = TimeSpan.FromSeconds(15) }, handler);
        try
        {
            var result = await model.AskAsync("只读出图片中的六位数字。返回explain，explanation仅含这六位数字，candidateId为null。", [], png, token);
            return result.Mode == "explain" && result.Explanation == digits ? ConnectionStatus.ImageVerified : ConnectionStatus.ImageUnconfirmed;
        }
        catch (OperationCanceledException) { return token.IsCancellationRequested ? ConnectionStatus.Cancelled : ConnectionStatus.Unavailable; }
        catch (HttpRequestException ex) { return HttpFailure(ex.StatusCode); }
        catch (Exception ex) when (ex is InvalidDataException or JsonException or InvalidOperationException or KeyNotFoundException or FormatException or IOException)
        { return ConnectionStatus.InvalidResponse; }
    }

    internal static ConnectionStatus HttpFailure(HttpStatusCode? status) => status switch
    {
        HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden => ConnectionStatus.Authentication,
        HttpStatusCode.PaymentRequired => ConnectionStatus.Quota,
        HttpStatusCode.TooManyRequests => ConnectionStatus.Limited,
        >= HttpStatusCode.InternalServerError or null => ConnectionStatus.Unavailable,
        _ => ConnectionStatus.InvalidResponse
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
            if (!response.IsSuccessStatusCode) return HttpFailure(response.StatusCode);
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
