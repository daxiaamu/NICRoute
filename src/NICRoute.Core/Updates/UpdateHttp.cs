using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;

namespace NICRoute.Core.Updates;

public interface IUpdateTransport
{
    Task<byte[]> GetBytesAsync(Uri url, int maximumBytes, CancellationToken cancellationToken);
    Task DownloadAsync(Uri url, string destination, long expectedSize, IProgress<long>? progress, CancellationToken cancellationToken);
}

/// <summary>No credentials are sent to mirrors. Every redirect is HTTPS-checked.</summary>
public sealed class UpdateHttp : IUpdateTransport, IDisposable
{
    private readonly HttpClient client;
    public UpdateHttp(HttpMessageHandler? handler = null)
    {
        client = new(handler ?? new SocketsHttpHandler { AllowAutoRedirect = false, ConnectTimeout = TimeSpan.FromSeconds(8), AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate });
        client.Timeout = Timeout.InfiniteTimeSpan;
        client.DefaultRequestHeaders.UserAgent.ParseAdd("NICRoute-Updater/0.2");
    }
    private async Task<HttpResponseMessage> OpenAsync(Uri url, CancellationToken token)
    {
        for (var i = 0; i < 6; i++)
        {
            UpdateJson.Https(url.AbsoluteUri);
            using var request = new HttpRequestMessage(HttpMethod.Get, url);
            request.Headers.CacheControl = new CacheControlHeaderValue { NoCache = true, NoStore = true };
            var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, token);
            if ((int)response.StatusCode is >= 300 and < 400 && response.Headers.Location is { } location)
            {
                var next = location.IsAbsoluteUri ? location : new Uri(url, location);
                response.Dispose();
                UpdateJson.Https(next.AbsoluteUri);
                url = next; continue;
            }
            if (!response.IsSuccessStatusCode) { var code = response.StatusCode; response.Dispose(); throw new HttpRequestException($"HTTP {(int)code}"); }
            return response;
        }
        throw new HttpRequestException("更新地址重定向次数过多。");
    }
    public async Task<Uri> ResolveHttpsAsync(Uri url, CancellationToken token)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
        timeout.CancelAfter(TimeSpan.FromSeconds(10));
        using var response = await OpenAsync(url, timeout.Token);
        var final = response.RequestMessage?.RequestUri ?? throw new HttpRequestException("无法确定下载地址。");
        return UpdateJson.Https(final.AbsoluteUri);
    }
    public async Task<byte[]> GetBytesAsync(Uri url, int maximumBytes, CancellationToken cancellationToken)
    {
        using var response = await OpenAsync(url, cancellationToken);
        if (response.Content.Headers.ContentLength > maximumBytes) throw new InvalidDataException("更新信息超过大小限制。");
        await using var input = await response.Content.ReadAsStreamAsync(cancellationToken);
        using var memory = new MemoryStream();
        var buffer = new byte[16384];
        int read;
        while ((read = await input.ReadAsync(buffer, cancellationToken)) > 0)
        {
            if (memory.Length + read > maximumBytes) throw new InvalidDataException("更新信息超过大小限制。");
            memory.Write(buffer, 0, read);
        }
        return memory.ToArray();
    }
    public async Task DownloadAsync(Uri url, string destination, long expectedSize, IProgress<long>? progress, CancellationToken cancellationToken)
    {
        using var response = await OpenAsync(url, cancellationToken);
        if (response.Content.Headers.ContentType?.MediaType?.Contains("html", StringComparison.OrdinalIgnoreCase) == true)
            throw new InvalidDataException("下载源返回了网页。");
        if (response.Content.Headers.ContentLength is { } length && length != expectedSize)
            throw new InvalidDataException("更新包大小与清单不符。");
        await using var input = await response.Content.ReadAsStreamAsync(cancellationToken);
        await using var file = new FileStream(destination, FileMode.CreateNew, FileAccess.Write, FileShare.None, 65536, FileOptions.Asynchronous);
        var buffer = new byte[65536]; long count = 0;
        int read;
        var lastProgress = Environment.TickCount64;
        while (true)
        {
            using var stall = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            stall.CancelAfter(TimeSpan.FromSeconds(25));
            read = await input.ReadAsync(buffer, stall.Token);
            if (read == 0) break;
            count += read;
            if (count > expectedSize) throw new InvalidDataException("更新包超过声明大小。");
            await file.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
            if (Environment.TickCount64 - lastProgress >= 250) { progress?.Report(count); lastProgress = Environment.TickCount64; }
        }
        if (count != expectedSize) throw new InvalidDataException("更新包下载不完整。");
        await file.FlushAsync(cancellationToken); file.Flush(true);
        progress?.Report(count);
    }
    public void Dispose() => client.Dispose();
}

public sealed class UpdateDownloader(IUpdateDownloadEngine engine)
{
    private readonly SemaphoreSlim gate = new(1);
    public async Task<string> DownloadAsync(UpdateManifest manifest, string directory, int connections, IProgress<DownloadProgress>? progress, CancellationToken token)
    {
        await gate.WaitAsync(token);
        try
        {
            Directory.CreateDirectory(directory);
            var final = Path.Combine(directory, $"NICRoute-{manifest.VersionCode}-{manifest.Sha256}.zip");
            if (File.Exists(final) && await MatchesAsync(final, manifest, token)) return final;
            if (File.Exists(final)) File.Delete(final);
            var errors = new List<string>();
            // First use all mirrors concurrently. If a mixed download fails integrity or
            // source negotiation, retry individual declared sources without accepting bytes.
            var plans = new List<IReadOnlyList<string>> { manifest.Urls };
            if (manifest.Urls.Length > 1) plans.AddRange(manifest.Urls.Select(u => (IReadOnlyList<string>)new[] { u }));
            foreach (var urls in plans)
            {
                token.ThrowIfCancellationRequested();
                var temp = final + ".download";
                try
                {
                    using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
                    timeout.CancelAfter(TimeSpan.FromMinutes(12));
                    await engine.DownloadAsync(urls, temp, connections, manifest.Sha256, manifest.Size, progress, timeout.Token);
                    if (!await MatchesAsync(temp, manifest, token)) throw new InvalidDataException("SHA256 校验失败。");
                    File.Move(temp, final, true);
                    return final;
                }
                catch (Exception e) when (e is IOException or HttpRequestException or InvalidDataException or OperationCanceledException)
                {
                    if (token.IsCancellationRequested) throw;
                    errors.Add(string.Join(", ", urls.Select(u => UpdateJson.Https(u).Host)) + "：" + e.Message);
                }
                finally { if (!token.IsCancellationRequested && File.Exists(temp)) File.Delete(temp); }
            }
            throw new IOException("所有下载源均失败。\n" + string.Join("\n", errors));
        }
        finally { gate.Release(); }
    }
    public static async Task<bool> MatchesAsync(string file, UpdateManifest manifest, CancellationToken token = default)
    {
        if (!File.Exists(file) || new FileInfo(file).Length != manifest.Size) return false;
        await using var stream = File.OpenRead(file);
        var hash = await SHA256.HashDataAsync(stream, token);
        return CryptographicOperations.FixedTimeEquals(hash, Convert.FromHexString(manifest.Sha256));
    }
}
