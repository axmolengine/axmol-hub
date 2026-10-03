using System.Diagnostics;
using System.Net;
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
        var partial = target + ".partial";
        if (File.Exists(target))
        {
            if (await ValidAsync(target, sha256, cancellation)) { log($"Verified cache: {target}"); return target; }
            File.Delete(target);
        }
        for (var attempt = 1; ; attempt++)
        {
            using var deadline = new CancellationTokenSource(TimeSpan.FromMinutes(20));
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellation, deadline.Token);
            try
            {
                log($"Download attempt {attempt}: {uri}");
                using var request = new HttpRequestMessage(HttpMethod.Get, uri);
                var offset = File.Exists(partial) ? new FileInfo(partial).Length : 0;
                if (offset > 0)
                {
                    request.Headers.Range = new System.Net.Http.Headers.RangeHeaderValue(offset, null);
                    log($"Resuming from byte {offset}.");
                }
                using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, linked.Token);
                response.EnsureSuccessStatusCode();
                if (response.RequestMessage?.RequestUri?.Scheme != Uri.UriSchemeHttps) throw new InvalidDataException("Download redirected to an insecure URL.");

                var append = response.StatusCode == HttpStatusCode.PartialContent && offset > 0;
                // The whole-file size: on a 206 the Content-Length is the remaining byte count, so
                // recover the total from Content-Range; on a 200 it is the full length.
                var total = response.Content.Headers.ContentLength;
                if (append && response.Content.Headers.ContentRange is { } range && range.Length.HasValue)
                {
                    total = range.Length;
                }
                if (!append)
                {
                    // Server ignored the Range header (full 200) or there was nothing to resume.
                    offset = 0;
                    if (File.Exists(partial)) File.Delete(partial);
                }

                using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
                // On resume the incremental hash must cover the bytes already on disk too.
                if (append) await AppendExistingToHashAsync(partial, hash, linked.Token);

                await using (var input = await response.Content.ReadAsStreamAsync(linked.Token))
                await using (var output = new FileStream(partial, append ? FileMode.Append : FileMode.Create, FileAccess.Write, FileShare.None, 81920, true))
                {
                    var timer = Stopwatch.StartNew();
                    var buffer = new byte[81920];
                    long received = 0;
                    while (true)
                    {
                        var count = await input.ReadAsync(buffer, linked.Token);
                        if (count == 0) break;
                        await output.WriteAsync(buffer.AsMemory(0, count), linked.Token);
                        hash.AppendData(buffer.AsSpan(0, count));
                        received += count;
                        progress?.Report(new(offset + received, total, received / Math.Max(timer.Elapsed.TotalSeconds, .001)));
                    }
                    // total is the whole-file size; the stream must deliver exactly the remaining bytes.
                    if (total.HasValue && received != total.Value - offset) throw new IOException("Download length does not match Content-Length.");
                    await output.FlushAsync(linked.Token);
                }
                var digest = Convert.ToHexString(hash.GetHashAndReset());
                if (!digest.Equals(sha256, StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("SHA-256 verification failed.");
                File.Move(partial, target, overwrite: true);
                log($"SHA-256 verified: {target}");
                return target;
            }
            catch (Exception ex) when (attempt < 3 && !cancellation.IsCancellationRequested && ex is HttpRequestException or IOException or OperationCanceledException)
            {
                log($"Download retry: {ex}");
                await Task.Delay(TimeSpan.FromSeconds(attempt), cancellation);
            }
            catch (InvalidDataException ex) when (ex.Message == "SHA-256 verification failed.")
            {
                // Content was corrupted (or a stale partial no longer matches the server);
                // drop the partial, then either restart from byte zero or surface the failure.
                if (File.Exists(partial)) File.Delete(partial);
                if (attempt < 3)
                {
                    log($"Download content corrupted, restarting from scratch: {ex.Message}");
                    await Task.Delay(TimeSpan.FromSeconds(attempt), cancellation);
                }
                else
                {
                    throw;
                }
            }
        }
    }

    private static async Task AppendExistingToHashAsync(string partial, IncrementalHash hash, CancellationToken cancellation)
    {
        await using var input = new FileStream(partial, FileMode.Open, FileAccess.Read, FileShare.Read, 81920, true);
        var buffer = new byte[81920];
        int count;
        while ((count = await input.ReadAsync(buffer, cancellation)) != 0)
        {
            hash.AppendData(buffer.AsSpan(0, count));
        }
    }

    private static async Task<bool> ValidAsync(string path, string sha256, CancellationToken cancellation)
    {
        await using var input = File.OpenRead(path);
        var digest = await SHA256.HashDataAsync(input, cancellation);
        return Convert.ToHexString(digest).Equals(sha256, StringComparison.OrdinalIgnoreCase);
    }
}
