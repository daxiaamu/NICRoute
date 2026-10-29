using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.Json;

namespace NICRoute.Core.Updates;

public sealed record DownloadProgress(long Downloaded, long Total, long BytesPerSecond, int Connections);
public interface IUpdateDownloadEngine
{
    Task DownloadAsync(IReadOnlyList<string> urls, string output, int connections, string sha256, long expectedSize,
        IProgress<DownloadProgress>? progress, CancellationToken cancellationToken);
}

public sealed class MudlDownloadEngine(string executable, Func<Uri, CancellationToken, Task<Uri>> resolveHttps) : IUpdateDownloadEngine
{
    public const string Version = "0.5.14";
    public const string ExecutableSha256 = "ed47df4b630840e76dde1345fad9ddb0831ac7784b077fe520702f6774cb1a1d";
    public static string[] Arguments(IReadOnlyList<string> urls, string output, int connections, string sha256)
    {
        if (connections is < 1 or > 32 || urls.Count is < 1 or > 16) throw new ArgumentOutOfRangeException(nameof(connections));
        UpdateJson.RequireHash(sha256);
        foreach (var url in urls) UpdateJson.Https(url);
        var args = new List<string> { "-c", connections.ToString(CultureInfo.InvariantCulture), "-d", Path.GetDirectoryName(Path.GetFullPath(output))!,
            "-o", Path.GetFileName(output), "--progress", "json", "--timeout", "15", "--retries", "2", "--checksum=sha-256=" + sha256 };
        args.AddRange(urls);
        if (args.Sum(a => a.Length + 3) > 30000) throw new InvalidDataException("下载地址过长。");
        return args.ToArray();
    }
    public static DownloadProgress? ParseProgress(string line)
    {
        if (!line.StartsWith('{') || line.Length > 4096) return null;
        try
        {
            using var document = JsonDocument.Parse(line); var root = document.RootElement;
            if (!root.TryGetProperty("dl", out var downloaded) || !root.TryGetProperty("sz", out var total) ||
                !downloaded.TryGetInt64(out var count) || !total.TryGetInt64(out var size) || count < 0 || size < 0) return null;
            var speed = root.TryGetProperty("sp", out var sp) && sp.TryGetInt64(out var s) ? Math.Max(0, s) : 0;
            var connections = root.TryGetProperty("th", out var th) && th.TryGetInt32(out var n) ? Math.Clamp(n, 0, 32) : 0;
            return new(count, size, speed, connections);
        }
        catch (JsonException) { return null; }
    }
    public async Task DownloadAsync(IReadOnlyList<string> urls, string output, int connections, string sha256, long expectedSize,
        IProgress<DownloadProgress>? progress, CancellationToken cancellationToken)
    {
        if (!File.Exists(executable) || await PackageVerifier.FileHashAsync(executable, cancellationToken) != ExecutableSha256)
            throw new InvalidDataException("MUDL 下载引擎缺失或校验失败，请重新获取完整程序包。");
        // Resolve redirects before handing sources to MUDL. Mirrors are public, credential-free
        // URLs; a downgrade discovered during this preflight is rejected, never followed.
        var resolved = await Task.WhenAll(urls.Select(async url =>
        {
            try { return (await resolveHttps(UpdateJson.Https(url), cancellationToken)).AbsoluteUri; }
            catch(InvalidDataException) { return null; } // A known protocol downgrade is never retried.
            catch (Exception e) when (e is HttpRequestException or IOException or OperationCanceledException)
            {
                if (cancellationToken.IsCancellationRequested) throw;
                // Let native SChannel probe a declared HTTPS source when .NET's optional
                // preflight has a transport failure. Its independent checksum is still mandatory.
                return UpdateJson.Https(url).AbsoluteUri;
            }
        }));
        var valid = resolved.OfType<string>().Distinct(StringComparer.Ordinal).Take(16).ToArray();
        if (valid.Length == 0) throw new IOException("所有更新下载源均不可用。");
        var start = new ProcessStartInfo(executable) { UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardOutput = true, RedirectStandardError = true, StandardOutputEncoding = Encoding.UTF8, StandardErrorEncoding = Encoding.UTF8 };
        foreach (var arg in Arguments(valid, output, connections, sha256)) start.ArgumentList.Add(arg);
        using var process = new Process { StartInfo = start };
        using var job = new NativeLifetime();
        if (!process.Start()) throw new IOException("无法启动 MUDL 下载引擎。");
        try
        {
            job.Add(process);
            var errors = new Queue<string>();
            var readOutput = Task.Run(async () =>
            {
                while (await process.StandardOutput.ReadLineAsync(cancellationToken) is { } line)
                    if (ParseProgress(line) is { } update)
                    {
                        if (update.Total > 0 && update.Total != expectedSize || update.Downloaded > expectedSize)
                            throw new InvalidDataException("MUDL 下载的文件大小与更新清单不符。");
                        progress?.Report(update);
                    }
            }, CancellationToken.None);
            var readErrors = Task.Run(async () =>
            {
                while (await process.StandardError.ReadLineAsync(cancellationToken) is { } line)
                { errors.Enqueue(line.Length > 400 ? line[..400] : line); while (errors.Count > 6) errors.Dequeue(); }
            }, CancellationToken.None);
            var exit = process.WaitForExitAsync(cancellationToken);
            // A malformed progress stream must not leave the downloader running indefinitely.
            var first = await Task.WhenAny(exit, readOutput);
            if (first == readOutput && readOutput.IsFaulted) await readOutput;
            await exit;
            await Task.WhenAll(readOutput, readErrors);
            if (process.ExitCode != 0) throw new IOException("MUDL 下载失败，可重试或切换网络。\n" + string.Join("\n", errors));
        }
        finally
        {
            if (!process.HasExited) { process.Kill(true); await process.WaitForExitAsync(); }
        }
    }
}
