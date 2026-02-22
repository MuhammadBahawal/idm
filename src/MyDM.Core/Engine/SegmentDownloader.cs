using MyDM.Core.Models;
using System.Net;
using System.Net.Http.Headers;

namespace MyDM.Core.Engine;

/// <summary>
/// Downloads a single segment of a file using HTTP Range requests.
/// </summary>
public sealed class RangeNotSupportedException : Exception
{
    public RangeNotSupportedException(string message) : base(message) { }
}

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
        IReadOnlyDictionary<string, string>? headers = null,
        Action<DownloadSegment, long>? onProgress = null,
        CancellationToken ct = default)
    {
        segment.Status = SegmentStatus.Active;

        var tempFile = segment.TempFile ?? Path.Combine(
            Path.GetTempPath(), "MyDM", $"{segment.DownloadId}_{segment.Index}.part");

        Directory.CreateDirectory(Path.GetDirectoryName(tempFile)!);
        segment.TempFile = tempFile;
        var existingBytes = File.Exists(tempFile) ? new FileInfo(tempFile).Length : 0L;
        if (existingBytes != segment.DownloadedBytes)
        {
            // Keep persisted/downloaded byte offsets aligned with the on-disk temp file.
            segment.DownloadedBytes = Math.Max(0, Math.Min(existingBytes, segment.TotalBytes));
        }

        var rangeStart = segment.StartByte + segment.DownloadedBytes;
        if (rangeStart > segment.EndByte)
        {
            segment.Status = SegmentStatus.Done;
            return;
        }

        var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.Range = new RangeHeaderValue(rangeStart, segment.EndByte);
        ApplyHeaders(request, headers);

        using var response = await _httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);

        if (response.StatusCode == HttpStatusCode.OK)
        {
            throw new RangeNotSupportedException("Server ignored Range request (HTTP 200 for a ranged segment).");
        }
        if (response.StatusCode != HttpStatusCode.PartialContent)
        {
            throw new HttpRequestException($"Server returned {response.StatusCode}", null, response.StatusCode);
        }

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
        IReadOnlyDictionary<string, string>? headers = null,
        Action<long, long>? onProgress = null,
        CancellationToken ct = default)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(outputPath)!);
        var existingBytes = File.Exists(outputPath) ? new FileInfo(outputPath).Length : 0L;

        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        if (existingBytes > 0)
        {
            request.Headers.Range = new RangeHeaderValue(existingBytes, null);
        }
        ApplyHeaders(request, headers);

        using var response = await _httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
        if (response.StatusCode == HttpStatusCode.RequestedRangeNotSatisfiable && existingBytes > 0)
        {
            onProgress?.Invoke(existingBytes, existingBytes);
            return;
        }
        response.EnsureSuccessStatusCode();

        var append = existingBytes > 0 && response.StatusCode == HttpStatusCode.PartialContent;
        if (!append)
        {
            existingBytes = 0;
        }

        var totalSize = response.Content.Headers.ContentRange?.Length
            ?? response.Content.Headers.ContentLength
            ?? 0;

        using var contentStream = await response.Content.ReadAsStreamAsync(ct);
        using var fileStream = new FileStream(
            outputPath,
            append ? FileMode.Append : FileMode.Create,
            FileAccess.Write,
            FileShare.None,
            BufferSize);

        var buffer = new byte[BufferSize];
        long totalRead = existingBytes;
        int bytesRead;

        onProgress?.Invoke(totalRead, totalSize);
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

    private static void ApplyHeaders(HttpRequestMessage request, IReadOnlyDictionary<string, string>? headers)
    {
        if (headers == null || headers.Count == 0) return;

        foreach (var (key, value) in headers)
        {
            if (string.IsNullOrWhiteSpace(key) || string.IsNullOrWhiteSpace(value))
                continue;

            if (string.Equals(key, "Referrer", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(key, "Referer", StringComparison.OrdinalIgnoreCase))
            {
                if (Uri.TryCreate(value, UriKind.Absolute, out var refUri))
                {
                    request.Headers.Referrer = refUri;
                }
                continue;
            }

            if (!request.Headers.TryAddWithoutValidation(key, value))
            {
                request.Content ??= new ByteArrayContent(Array.Empty<byte>());
                request.Content.Headers.TryAddWithoutValidation(key, value);
            }
        }
    }
}
