using System.IO;
using System.IO.Compression;
using System.Net;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;

namespace CetaFae.Probe;

/// <summary>Only host-reviewed releases can enter the existing offline installer. No plugin URLs or credentials.</summary>
internal static class ToolDownload
{
    internal const long MaximumBytes = 256L * 1024 * 1024;
    internal static bool AllowedUri(Uri uri) => uri.Scheme == "https" && uri.Port == 443 && uri.UserInfo.Length == 0 &&
        uri.Fragment.Length == 0 && uri.Host is "github.com" or "release-assets.githubusercontent.com" or "objects.githubusercontent.com";

    internal static async Task DownloadAsync(HttpClient http, Uri address, string destination, long size, string hash,
        Action<long, long>? progress, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        if (!AllowedUri(address) || size is <= 0 or > MaximumBytes || hash.Length != 64) throw new InvalidDataException("tool_download_source");
        for (var redirects = 0; redirects <= 5; redirects++)
        {
            if (!AllowedUri(address)) throw new InvalidDataException("tool_download_redirect");
            using var request = new HttpRequestMessage(HttpMethod.Get, address);
            request.Headers.UserAgent.ParseAdd("CetaFae/0.3");
            using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, token);
            if (response.StatusCode is HttpStatusCode.Moved or HttpStatusCode.Redirect or HttpStatusCode.RedirectMethod or HttpStatusCode.TemporaryRedirect or HttpStatusCode.PermanentRedirect)
            {
                var location = response.Headers.Location ?? throw new InvalidDataException("tool_download_redirect");
                address = location.IsAbsoluteUri ? location : new Uri(address, location);
                continue;
            }
            response.EnsureSuccessStatusCode();
            if (response.Content.Headers.ContentLength is { } length && length != size) throw new InvalidDataException("tool_download_size");
            token.ThrowIfCancellationRequested();
            PlatformScope.CheckAncestors(destination);
            using var output = new FileStream(destination, FileMode.CreateNew, FileAccess.Write, FileShare.None, 65536, true);
            using var input = await response.Content.ReadAsStreamAsync(token);
            using var sha = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            var buffer = new byte[65536]; long received = 0; var reported = 0L;
            int count;
            while ((count = await input.ReadAsync(buffer, token)) > 0)
            {
                received += count;
                if (received > size) throw new InvalidDataException("tool_download_size");
                sha.AppendData(buffer, 0, count); await output.WriteAsync(buffer.AsMemory(0, count), token);
                if (received - reported >= 1024 * 1024) { progress?.Invoke(received, size); reported = received; }
            }
            if (received != size || Convert.ToHexString(sha.GetHashAndReset()) != hash) throw new InvalidDataException("tool_download_hash");
            await output.FlushAsync(token); progress?.Invoke(received, size); return;
        }
        throw new InvalidDataException("tool_download_redirect");
    }

    internal static async Task<BundleInstallResult> InstallAsync(FixedToolBundle bundle, Action<long, long>? progress, CancellationToken token)
    {
        if (!FixedToolBundle.Supported || bundle.DownloadUrl is null) throw new InvalidDataException("tool_download_unavailable");
        var parent = Path.Combine(FixedTool.ToolsRoot, "downloads"); PlatformScope.CheckAncestors(parent); Directory.CreateDirectory(parent);
        var stage = Path.Combine(parent, Guid.NewGuid().ToString("N")); Directory.CreateDirectory(stage);
        try
        {
            using var http = new HttpClient(new HttpClientHandler { AllowAutoRedirect = false, UseCookies = false,
                UseDefaultCredentials = false, UseProxy = false }) { Timeout = Timeout.InfiniteTimeSpan };
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token); timeout.CancelAfter(TimeSpan.FromMinutes(8));
            var payload = Path.Combine(stage, "portable.zip");
            await DownloadAsync(http, new Uri(bundle.DownloadUrl), payload, bundle.DownloadBytes, bundle.ArchiveHash, progress, timeout.Token);
            var package = Path.Combine(stage, "download.cetaplugin");
            await Task.Run(() =>
            {
                using (var zip = ZipFile.Open(package, ZipArchiveMode.Create))
                {
                    void Add(string name, byte[] bytes) { using var stream = zip.CreateEntry(name).Open(); stream.Write(bytes); }
                    Add("bundle-id.txt", Encoding.UTF8.GetBytes(bundle.Tool.Id));
                    zip.CreateEntryFromFile(payload, "filee-portable.zip", CompressionLevel.NoCompression);
                    Add("SOURCE.txt", Encoding.UTF8.GetBytes(bundle.Source));
                    Add("LICENSE", Notice("CetaFae.FileeLicense")); Add("THIRD-PARTY-NOTICES.md", Notice("CetaFae.FileeNotices"));
                }
            }, timeout.Token);
            return await Task.Run(() => bundle.Install(package, FixedTool.ToolsRoot, timeout.Token), timeout.Token);
        }
        catch (HttpRequestException ex) { throw new InvalidDataException("tool_download_network", ex); }
        finally
        {
            try { PlatformScope.CheckAncestors(stage); if (Directory.Exists(stage)) Directory.Delete(stage, true); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or PlatformFailure) { ProbeLog.Log("tool_download_cleanup_failed"); }
        }
    }
    static byte[] Notice(string name)
    {
        using var stream = typeof(ToolDownload).Assembly.GetManifestResourceStream(name) ?? throw new InvalidDataException("tool_notice");
        using var reader = new StreamReader(stream, new UTF8Encoding(false, true), true);
        return Encoding.UTF8.GetBytes(reader.ReadToEnd().Replace("\r\n", "\n", StringComparison.Ordinal));
    }
}
