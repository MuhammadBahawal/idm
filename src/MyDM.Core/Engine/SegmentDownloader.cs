using MyDM.Core.Models;

namespace MyDM.Core.Engine;

/// <summary>
/// Downloads a single segment of a file using HTTP Range requests.
/// </summary>
public class SegmentDownloader
{
    private readonly HttpClient _httpClient;
    private readonly SpeedLimiter _speedLimiter;
    private const int BufferSize = 65536; // 64KB

    public SegmentDownloader(HttpClient httpClient, SpeedLimiter speedLimiter)
    {
        _httpClient = httpClient;
        _speedLimiter = speedLimiter;
    }

    /// <summary>
    /// Download a segment, supporting resume from the current position.
    /// </summary>
    public async Task DownloadSegmentAsync(
        DownloadSegment segment,
        string url,
        long perDownloadSpeedLimit,
        Action<DownloadSegment, long>? onProgress = null,
        CancellationToken ct = default)
    {
        segment.Status = SegmentStatus.Active;

        var request = new HttpRequestMessage(HttpMethod.Get, url);
        var rangeStart = segment.StartByte + segment.DownloadedBytes;
        request.Headers.Range = new System.Net.Http.Headers.RangeHeaderValue(rangeStart, segment.EndByte);

        using var response = await _httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);

        if (!response.IsSuccessStatusCode && response.StatusCode != System.Net.HttpStatusCode.PartialContent)
        {
            throw new HttpRequestException($"Server returned {response.StatusCode}", null, response.StatusCode);
        }

        var tempFile = segment.TempFile ?? Path.Combine(
            Path.GetTempPath(), "MyDM", $"{segment.DownloadId}_{segment.Index}.part");

        Directory.CreateDirectory(Path.GetDirectoryName(tempFile)!);
        segment.TempFile = tempFile;

        using var contentStream = await response.Content.ReadAsStreamAsync(ct);
        using var fileStream = new FileStream(tempFile, FileMode.Append, FileAccess.Write, FileShare.None, BufferSize);

        var buffer = new byte[BufferSize];
        int bytesRead;

        while ((bytesRead = await contentStream.ReadAsync(buffer, ct)) > 0)
        {
            ct.ThrowIfCancellationRequested();

            // Apply speed limiting (delay only, never truncate data)
            await _speedLimiter.RequestBytesAsync(bytesRead, perDownloadSpeedLimit, ct);
            await fileStream.WriteAsync(buffer.AsMemory(0, bytesRead), ct);

            segment.DownloadedBytes += bytesRead;
            onProgress?.Invoke(segment, bytesRead);

            if (segment.DownloadedBytes >= segment.TotalBytes)
                break;
        }

        segment.Status = SegmentStatus.Done;
    }

    /// <summary>
    /// Download a file as a single stream (no Range support).
    /// </summary>
    public async Task DownloadSingleStreamAsync(
        string url,
        string outputPath,
        long perDownloadSpeedLimit,
        Action<long, long>? onProgress = null,
        CancellationToken ct = default)
    {
        using var response = await _httpClient.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct);
        response.EnsureSuccessStatusCode();

        var totalSize = response.Content.Headers.ContentLength ?? 0;

        Directory.CreateDirectory(Path.GetDirectoryName(outputPath)!);
        using var contentStream = await response.Content.ReadAsStreamAsync(ct);
        using var fileStream = new FileStream(outputPath, FileMode.Create, FileAccess.Write, FileShare.None, BufferSize);

        var buffer = new byte[BufferSize];
        long totalRead = 0;
        int bytesRead;

        while ((bytesRead = await contentStream.ReadAsync(buffer, ct)) > 0)
        {
            ct.ThrowIfCancellationRequested();

            // Apply speed limiting (delay only, never truncate data)
            await _speedLimiter.RequestBytesAsync(bytesRead, perDownloadSpeedLimit, ct);
            await fileStream.WriteAsync(buffer.AsMemory(0, bytesRead), ct);

            totalRead += bytesRead;
            onProgress?.Invoke(totalRead, totalSize);
        }
    }
}
