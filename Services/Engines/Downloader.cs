using System;
using System.IO;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;

namespace Herald.Services.Engines;

/// <summary>Downloads engine files (programs, models, voices) with progress in the install log.</summary>
internal static class Downloader
{
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromMinutes(30) };

    /// <summary>
    /// Downloads into a ".part" file first and renames it when complete, so an interrupted
    /// download never looks like a finished one. Returns false (and says why) on failure.
    /// </summary>
    public static async Task<bool> DownloadAsync(string url, string target, string label, IProgress<string> log, CancellationToken ct)
    {
        log.Report($"Downloading {label} ...");
        var partial = target + ".part";
        try
        {
            using var response = await Http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct);
            if (!response.IsSuccessStatusCode)
            {
                log.Report($"Download failed: HTTP {(int)response.StatusCode} for {url}");
                return false;
            }

            var total = response.Content.Headers.ContentLength;
            await using (var input = await response.Content.ReadAsStreamAsync(ct))
            await using (var output = File.Create(partial))
            {
                var buffer = new byte[1 << 16];
                long done = 0;
                var lastReported = -1;
                int read;
                while ((read = await input.ReadAsync(buffer, ct)) > 0)
                {
                    await output.WriteAsync(buffer.AsMemory(0, read), ct);
                    done += read;

                    // Every 10%, and only for files big enough to take a while.
                    if (total > 1_000_000)
                    {
                        var percent = (int)(done * 100 / total.Value);
                        if (percent / 10 != lastReported / 10)
                        {
                            lastReported = percent;
                            log.Report($"  {label}: {percent}%");
                        }
                    }
                }
            }

            File.Move(partial, target, overwrite: true);
            return true;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            log.Report($"Download failed: {ex.Message}");
            SafeFile.TryDelete(partial);
            return false;
        }
    }
}
