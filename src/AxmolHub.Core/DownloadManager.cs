using System.Diagnostics;
using System.Security.Cryptography;
using System.Text.RegularExpressions;

namespace AxmolHub.Core;

public sealed record DownloadProgress(long Bytes, long? Total, double BytesPerSecond);
public sealed class DownloadManager(HttpClient client, Action<string> log)
{
    public async Task<string> DownloadAsync(Uri uri, string sha256, string cache,
        IProgress<DownloadProgress>? progress = null, CancellationToken cancellation = default)
    {
        if (uri.Scheme != Uri.UriSchemeHttps) throw new ArgumentException("Package downloads require HTTPS.");
        if (!Regex.IsMatch(sha256, "^[a-fA-F0-9]{64}$")) throw new ArgumentException("Package requires a SHA-256 digest.");
        Directory.CreateDirectory(cache);
        var target = Path.Combine(cache, sha256.ToLowerInvariant() + ".zip");
        if (File.Exists(target))
        {
            if (await ValidAsync(target, sha256, cancellation)) { log($"Verified cache: {target}"); return target; }
            File.Delete(target);
        }
        for (var attempt = 1; ; attempt++)
        {
            var temporary = target + "." + Guid.NewGuid().ToString("N") + ".partial";
            using var deadline = new CancellationTokenSource(TimeSpan.FromMinutes(20));
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellation, deadline.Token);
            try
            {
                log($"Download attempt {attempt}: {uri}");
                using var response = await client.GetAsync(uri, HttpCompletionOption.ResponseHeadersRead, linked.Token);
                response.EnsureSuccessStatusCode();
                if (response.RequestMessage?.RequestUri?.Scheme != Uri.UriSchemeHttps) throw new InvalidDataException("Download redirected to an insecure URL.");
                var total = response.Content.Headers.ContentLength;
                await using (var input = await response.Content.ReadAsStreamAsync(linked.Token))
                await using (var output = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None, 81920, true))
                {
                    var timer = Stopwatch.StartNew();
                    var buffer = new byte[81920];
                    long received = 0;
                    while (true)
                    {
                        var count = await input.ReadAsync(buffer, linked.Token);
                        if (count == 0) break;
                        await output.WriteAsync(buffer.AsMemory(0, count), linked.Token);
                        received += count;
                        progress?.Report(new(received, total, received / Math.Max(timer.Elapsed.TotalSeconds, .001)));
                    }
                    if (total.HasValue && received != total.Value) throw new IOException("Download length does not match Content-Length.");
                    await output.FlushAsync(linked.Token);
                }
                if (!await ValidAsync(temporary, sha256, linked.Token)) throw new InvalidDataException("SHA-256 verification failed.");
                File.Move(temporary, target, overwrite: true);
                log($"SHA-256 verified: {target}");
                return target;
            }
            catch (Exception ex) when (attempt < 3 && !cancellation.IsCancellationRequested && ex is HttpRequestException or IOException or OperationCanceledException)
            {
                log($"Download retry: {ex}");
                await Task.Delay(TimeSpan.FromSeconds(attempt), cancellation);
            }
            finally { if (File.Exists(temporary)) File.Delete(temporary); }
        }
    }
    private static async Task<bool> ValidAsync(string path, string sha256, CancellationToken cancellation)
    {
        await using var input = File.OpenRead(path);
        var digest = await SHA256.HashDataAsync(input, cancellation);
        return Convert.ToHexString(digest).Equals(sha256, StringComparison.OrdinalIgnoreCase);
    }
}
