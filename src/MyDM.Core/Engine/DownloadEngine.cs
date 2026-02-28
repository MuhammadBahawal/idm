using MyDM.Core.Data;
using MyDM.Core.Models;
using MyDM.Core.Parsers;
using MyDM.Core.Utilities;
using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Headers;

namespace MyDM.Core.Engine;

/// <summary>
/// Main download engine that orchestrates multi-segment downloads.
/// </summary>
public class DownloadEngine : IDisposable
{
    private readonly HttpClient _httpClient;
    private readonly DownloadRepository _repository;
    private readonly SpeedLimiter _speedLimiter;
    private readonly RetryPolicy _retryPolicy;
    private readonly ConcurrentDictionary<string, CancellationTokenSource> _activeDownloads = new();
    private readonly ConcurrentDictionary<string, DownloadItem> _downloadCache = new();
    private readonly ConcurrentDictionary<string, IReadOnlyDictionary<string, string>> _requestHeaders = new();
    private readonly ConcurrentDictionary<string, RuntimeTelemetry> _telemetry = new();
    private readonly SegmentDownloader _segmentDownloader;

    public event Action<DownloadItem>? OnProgressUpdated;
    public event Action<DownloadItem>? OnStatusChanged;
    public event Action<string, string>? OnLogMessage;

    public DownloadEngine(DownloadRepository repository, SpeedLimiter? speedLimiter = null)
    {
        _repository = repository;
        _speedLimiter = speedLimiter ?? new SpeedLimiter();
        _retryPolicy = new RetryPolicy();

        var handler = new SocketsHttpHandler
        {
            MaxConnectionsPerServer = 32,
            PooledConnectionLifetime = TimeSpan.FromMinutes(5),
            PooledConnectionIdleTimeout = TimeSpan.FromMinutes(2),
            AutomaticDecompression = System.Net.DecompressionMethods.All,
            ConnectTimeout = TimeSpan.FromSeconds(30)
        };

        _httpClient = new HttpClient(handler)
        {
            Timeout = TimeSpan.FromMinutes(30)
        };
        // CRITICAL: Use a real browser User-Agent. googlevideo.com returns 403 for non-browser UAs.
    _httpClient.DefaultRequestHeaders.UserAgent.ParseAdd(
        "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/131.0.0.0 Safari/537.36");

        _segmentDownloader = new SegmentDownloader(_httpClient, _speedLimiter);
    }

    public SpeedLimiter SpeedLimiter => _speedLimiter;

    /// <summary>
    /// Add a new download and start fetching file info.
    /// </summary>
    public async Task<DownloadItem> AddDownloadAsync(string url, string? savePath = null,
        string? fileName = null, int connections = 8, string? category = null,
        IReadOnlyDictionary<string, string>? requestHeaders = null)
    {
        if (!UrlHelper.IsValidUrl(url))
            throw new ArgumentException("Invalid URL", nameof(url));

        var defaultBasePath = _repository.GetSetting("DefaultSavePath")
            ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads", "MyDM");
        var selectedBasePath = string.IsNullOrWhiteSpace(savePath) ? defaultBasePath : savePath;
        var autoCategorizePath = string.IsNullOrWhiteSpace(savePath);
        var initialCategory = category ?? MimeDetector.Detect(url, null);

        var item = new DownloadItem
        {
            Url = url,
            FileName = fileName ?? UrlHelper.ExtractFileName(url),
            SavePath = ResolveCategorySavePath(selectedBasePath, initialCategory, autoCategorizePath),
            Connections = Math.Clamp(connections, 1, 32),
            Category = initialCategory
        };

        string? probeWarning = null;

        // Probe the URL for file info.
        try
        {
            var info = await ProbeUrlAsync(url, AutoInjectYouTubeHeaders(url, requestHeaders));
            if (info.ContentLength > 0) item.TotalSize = info.ContentLength;
            if (!string.IsNullOrEmpty(info.FileName)) item.FileName = UrlHelper.SanitizeFileName(info.FileName);
            if (!string.IsNullOrEmpty(info.ContentType))
            {
                var detectedCat = MimeDetector.DetectFromMime(info.ContentType);
                if (detectedCat != "Others" && category == null) item.Category = detectedCat;
            }
            item.SupportsRange = info.SupportsRange;
        }
        catch (Exception ex)
        {
            probeWarning = $"Could not probe URL: {ex.Message}";
        }

        item.SavePath = ResolveCategorySavePath(selectedBasePath, item.Category, autoCategorizePath);

        FileHelper.EnsureDirectory(item.SavePath);
        _repository.Insert(item);
        _downloadCache[item.Id] = item;
        // Auto-inject YouTube/googlevideo headers (Referer + Origin) if needed
        var finalHeaders = AutoInjectYouTubeHeaders(url, requestHeaders);
        if (finalHeaders != null && finalHeaders.Count > 0)
        {
            _requestHeaders[item.Id] = new Dictionary<string, string>(finalHeaders, StringComparer.OrdinalIgnoreCase);
        }
        if (!string.IsNullOrEmpty(probeWarning))
        {
            Log(item.Id, "Warning", probeWarning);
        }
        Log(item.Id, "Info", $"Download added: {item.FileName} ({FileHelper.FormatSize(item.TotalSize)})");

        return item;
    }

    /// <summary>
    /// Start or resume a download.
    /// </summary>
    public async Task StartDownloadAsync(string downloadId)
    {
        var item = GetDownload(downloadId);
        if (item == null) return;

        if (_activeDownloads.ContainsKey(downloadId))
        {
            Log(downloadId, "Warning", "Download is already active");
            return;
        }

        var cts = new CancellationTokenSource();
        _activeDownloads[downloadId] = cts;
        InitializeTelemetry(item);

        item.Status = DownloadStatus.Downloading;
        item.ErrorMessage = null;
        item.LastAttemptAt = DateTime.UtcNow;
        _repository.Update(item);
        OnStatusChanged?.Invoke(item);

        _ = Task.Run(async () =>
        {
            try
            {
                if (item.SupportsRange && item.TotalSize > 0 && item.Connections > 1)
                {
                    try
                    {
                        await DownloadWithSegmentsAsync(item, cts.Token);
                    }
                    catch (RangeNotSupportedException ex)
                    {
                        Log(item.Id, "Warning", $"{ex.Message} Falling back to single-stream mode.");
                        item.SupportsRange = false;
                        item.Segments.Clear();
                        _repository.DeleteSegments(item.Id);
                        _repository.Update(item);
                        await DownloadSingleStreamAsync(item, cts.Token);
                    }
                    catch (HttpRequestException ex) when (ex.StatusCode == HttpStatusCode.RequestedRangeNotSatisfiable)
                    {
                        Log(item.Id, "Warning", "Range request became invalid during segmented download. Falling back to single-stream mode.");
                        item.SupportsRange = false;
                        item.Segments.Clear();
                        _repository.DeleteSegments(item.Id);
                        _repository.Update(item);
                        await DownloadSingleStreamAsync(item, cts.Token);
                    }
                }
                else
                {
                    await DownloadSingleStreamAsync(item, cts.Token);
                }

                // Download complete — atomic rename
                if (File.Exists(item.PartFilePath))
                {
                    var finalPath = FileHelper.AtomicRename(item.PartFilePath, item.FullPath);
                    item.FileName = Path.GetFileName(finalPath);
                    var actualSize = new FileInfo(finalPath).Length;
                    if (item.TotalSize <= 0)
                    {
                        item.TotalSize = actualSize;
                    }
                    item.DownloadedSize = actualSize;
                }

                item.Status = DownloadStatus.Complete;
                item.CompletedAt = DateTime.UtcNow;
                item.TransferRate = 0;
                item.TimeLeft = TimeSpan.Zero;
                foreach (var seg in item.Segments)
                {
                    seg.TransferRate = 0;
                }
                _repository.Update(item);
                Log(downloadId, "Info", "Download completed successfully");
                OnProgressUpdated?.Invoke(item);
                OnStatusChanged?.Invoke(item);
                ResetTelemetry(item.Id);
            }
            catch (OperationCanceledException)
            {
                item.Status = DownloadStatus.Paused;
                item.TransferRate = 0;
                item.TimeLeft = null;
                foreach (var seg in item.Segments)
                {
                    seg.TransferRate = 0;
                }
                _repository.Update(item);
                Log(downloadId, "Info", "Download paused");
                OnProgressUpdated?.Invoke(item);
                OnStatusChanged?.Invoke(item);
                ResetTelemetry(item.Id);
            }
            catch (Exception ex)
            {
                item.Status = DownloadStatus.Error;
                item.ErrorMessage = ex.Message;
                item.RetryCount++;
                item.TransferRate = 0;
                item.TimeLeft = null;
                foreach (var seg in item.Segments)
                {
                    seg.TransferRate = 0;
                }
                _repository.Update(item);
                Log(downloadId, "Error", $"Download failed: {ex.Message}");
                OnProgressUpdated?.Invoke(item);
                OnStatusChanged?.Invoke(item);
                ResetTelemetry(item.Id);

                // Auto-retry if retryable
                if (RetryPolicy.IsRetryable(ex) && item.RetryCount <= _retryPolicy.MaxRetries)
                {
                    var delay = _retryPolicy.GetDelay(item.RetryCount);
                    Log(downloadId, "Info", $"Retrying in {delay.TotalSeconds:F0}s (attempt {item.RetryCount}/{_retryPolicy.MaxRetries})");
                    await Task.Delay(delay);
                    if (!cts.Token.IsCancellationRequested)
                    {
                        _activeDownloads.TryRemove(downloadId, out _);
                        await StartDownloadAsync(downloadId);
                    }
                }
            }
            finally
            {
                _activeDownloads.TryRemove(downloadId, out _);
            }
        }, cts.Token);
    }

    /// <summary>
    /// Pause a download.
    /// </summary>
    public void PauseDownload(string downloadId)
    {
        if (_activeDownloads.TryRemove(downloadId, out var cts))
        {
            cts.Cancel();
            cts.Dispose();
        }
        var item = GetDownload(downloadId);
        if (item != null)
        {
            item.Status = DownloadStatus.Paused;
            item.TransferRate = 0;
            item.TimeLeft = null;
            foreach (var seg in item.Segments)
            {
                seg.TransferRate = 0;
            }
            _repository.Update(item);
            SaveSegmentProgress(item);
            OnProgressUpdated?.Invoke(item);
            OnStatusChanged?.Invoke(item);
            ResetTelemetry(downloadId);
        }
    }

    /// <summary>
    /// Cancel and remove a download.
    /// </summary>
    public void CancelDownload(string downloadId)
    {
        PauseDownload(downloadId);
        var item = GetDownload(downloadId);
        if (item != null)
        {
            item.Status = DownloadStatus.Cancelled;
            item.TransferRate = 0;
            item.TimeLeft = null;
            foreach (var seg in item.Segments)
            {
                seg.TransferRate = 0;
            }
            _repository.Update(item);
            CleanupTempFiles(item);
            OnProgressUpdated?.Invoke(item);
            OnStatusChanged?.Invoke(item);
            ResetTelemetry(downloadId);
        }
    }

    /// <summary>
    /// Stop all active downloads.
    /// </summary>
    public void StopAll()
    {
        foreach (var kvp in _activeDownloads.ToArray())
        {
            PauseDownload(kvp.Key);
        }
    }

    /// <summary>
    /// Delete a download and optionally delete the file.
    /// </summary>
    public void DeleteDownload(string downloadId, bool deleteFile = false)
    {
        CancelDownload(downloadId);
        var item = GetDownload(downloadId);
        if (item != null)
        {
            if (deleteFile)
            {
                FileHelper.SafeDelete(item.FullPath);
            }
            CleanupTempFiles(item);
            _repository.DeleteSegments(downloadId);
            _repository.Delete(downloadId);
            _downloadCache.TryRemove(downloadId, out _);
            _requestHeaders.TryRemove(downloadId, out _);
            _telemetry.TryRemove(downloadId, out _);
        }
    }

    /// <summary>
    /// Get a download by ID.
    /// </summary>
    public DownloadItem? GetDownload(string downloadId)
    {
        if (_downloadCache.TryGetValue(downloadId, out var cached))
            return cached;
        var item = _repository.GetById(downloadId);
        if (item != null) _downloadCache[downloadId] = item;
        return item;
    }

    /// <summary>
    /// Get all downloads.
    /// </summary>
    public List<DownloadItem> GetAllDownloads()
    {
        return _repository.GetAll();
    }

    /// <summary>
    /// Load unfinished downloads from DB on startup.
    /// </summary>
    public void RestoreState()
    {
        var items = _repository.GetAll();
        foreach (var item in items)
        {
            _downloadCache[item.Id] = item;
            if (item.Status == DownloadStatus.Downloading)
            {
                // Was downloading when app closed — mark as paused for resume
                item.Status = DownloadStatus.Paused;
                _repository.Update(item);
            }
            item.TransferRate = 0;
            item.TimeLeft = null;
            item.Segments = _repository.GetSegments(item.Id);
            foreach (var seg in item.Segments)
            {
                seg.TransferRate = 0;
            }
        }
    }

    // ──── Private Methods ────

    private async Task DownloadWithSegmentsAsync(DownloadItem item, CancellationToken ct)
    {
        // Create or restore segments
        if (item.Segments.Count == 0)
        {
            item.Segments = CreateSegments(item);
            _repository.InsertSegments(item.Segments);
        }
        else
        {
            var dbSegments = _repository.GetSegments(item.Id);
            if (dbSegments.Count > 0)
                item.Segments = dbSegments;
        }

        FileHelper.EnsureDirectory(item.SavePath);

        var pendingSegments = item.Segments
            .Where(s => s.Status != SegmentStatus.Done)
            .ToList();

        if (pendingSegments.Count == 0)
        {
            // All segments done, just need to merge
            await MergeAndFinalizeAsync(item, ct);
            return;
        }

        var progressLock = new object();
        var lastProgressUpdate = DateTime.MinValue;
        var runtime = _telemetry.GetOrAdd(item.Id, _ => new RuntimeTelemetry(item));
        var headers = GetHeaders(item.Id);

        var tasks = pendingSegments.Select(segment => Task.Run(async () =>
        {
            await _segmentDownloader.DownloadSegmentAsync(
                segment, item.Url, item.SpeedLimit,
                speedLimitProvider: () => item.SpeedLimit,
                headers: headers,
                onProgress: (seg, bytesRead) =>
                {
                    lock (progressLock)
                    {
                        item.DownloadedSize = item.Segments.Sum(s => s.DownloadedBytes);
                        var now = DateTime.UtcNow;
                        UpdateRealtimeStats(item, runtime, now);
                        if ((now - lastProgressUpdate).TotalMilliseconds > 120)
                        {
                            lastProgressUpdate = now;
                            _repository.UpdateSegment(seg);
                            _repository.UpdateProgress(item.Id, item.DownloadedSize, DownloadStatus.Downloading);
                            OnProgressUpdated?.Invoke(item);
                        }
                    }
                },
                ct: ct);

            _repository.UpdateSegment(segment);
        }, ct)).ToArray();

        await Task.WhenAll(tasks);

        // Merge segments
        await MergeAndFinalizeAsync(item, ct);
    }

    private async Task MergeAndFinalizeAsync(DownloadItem item, CancellationToken ct)
    {
        item.Status = DownloadStatus.Merging;
        OnStatusChanged?.Invoke(item);

        var segmentFiles = item.Segments.OrderBy(s => s.Index).Select(s => s.TempFile!).ToList();
        await FileHelper.MergeSegmentFilesAsync(segmentFiles, item.PartFilePath, ct);

        // Cleanup segment temp files
        foreach (var seg in item.Segments)
        {
            FileHelper.SafeDelete(seg.TempFile);
        }
        _repository.DeleteSegments(item.Id);
    }

    private async Task DownloadSingleStreamAsync(DownloadItem item, CancellationToken ct)
    {
        Log(item.Id, "Info", "Server does not support Range. Downloading as single stream.");
        var lastUpdate = DateTime.MinValue;
        var runtime = _telemetry.GetOrAdd(item.Id, _ => new RuntimeTelemetry(item));
        var headers = GetHeaders(item.Id);

        await _segmentDownloader.DownloadSingleStreamAsync(
            item.Url, item.PartFilePath, item.SpeedLimit,
            speedLimitProvider: () => item.SpeedLimit,
            headers: headers,
            onProgress: (downloaded, total) =>
            {
                item.DownloadedSize = downloaded;
                if (total > 0) item.TotalSize = total;
                var now = DateTime.UtcNow;
                UpdateRealtimeStats(item, runtime, now);
                if ((now - lastUpdate).TotalMilliseconds > 120)
                {
                    lastUpdate = now;
                    _repository.UpdateProgress(item.Id, downloaded, DownloadStatus.Downloading);
                    OnProgressUpdated?.Invoke(item);
                }
            },
            ct: ct);
    }

    private List<DownloadSegment> CreateSegments(DownloadItem item)
    {
        var segments = new List<DownloadSegment>();
        var segmentSize = item.TotalSize / item.Connections;
        var remainder = item.TotalSize % item.Connections;

        long position = 0;
        for (int i = 0; i < item.Connections; i++)
        {
            var size = segmentSize + (i < remainder ? 1 : 0);
            var tempFile = Path.Combine(Path.GetTempPath(), "MyDM", $"{item.Id}_{i}.part");

            segments.Add(new DownloadSegment
            {
                DownloadId = item.Id,
                Index = i,
                StartByte = position,
                EndByte = position + size - 1,
                TempFile = tempFile
            });

            position += size;
        }

        Log(item.Id, "Info", $"Created {segments.Count} segments of ~{FileHelper.FormatSize(segmentSize)} each");
        return segments;
    }

    private async Task<ProbeResult> ProbeUrlAsync(string url, IReadOnlyDictionary<string, string>? headers = null)
    {
        // HEAD first for fast metadata, then fall back to a ranged GET for servers that reject HEAD.
        try
        {
            using var headRequest = new HttpRequestMessage(HttpMethod.Head, url);
            ApplyHeaders(headRequest, headers);
            using var headResponse = await _httpClient.SendAsync(headRequest, HttpCompletionOption.ResponseHeadersRead);
            headResponse.EnsureSuccessStatusCode();

            return BuildProbeResultFromResponse(headResponse, url);
        }
        catch
        {
            using var getRequest = new HttpRequestMessage(HttpMethod.Get, url);
            getRequest.Headers.Range = new RangeHeaderValue(0, 0);
            ApplyHeaders(getRequest, headers);
            using var getResponse = await _httpClient.SendAsync(getRequest, HttpCompletionOption.ResponseHeadersRead);
            getResponse.EnsureSuccessStatusCode();

            return BuildProbeResultFromResponse(getResponse, url);
        }
    }

    private void SaveSegmentProgress(DownloadItem item)
    {
        foreach (var seg in item.Segments)
        {
            _repository.UpdateSegment(seg);
        }
    }

    private void CleanupTempFiles(DownloadItem item)
    {
        FileHelper.SafeDelete(item.PartFilePath);
        foreach (var seg in item.Segments)
        {
            FileHelper.SafeDelete(seg.TempFile);
        }
    }

    private void Log(string downloadId, string level, string message)
    {
        _repository.AddLog(downloadId, level, message);
        OnLogMessage?.Invoke(downloadId, $"[{level}] {message}");
    }

    private IReadOnlyDictionary<string, string>? GetHeaders(string downloadId)
    {
        return _requestHeaders.TryGetValue(downloadId, out var headers) ? headers : null;
    }

    private string ResolveCategorySavePath(string basePath, string category, bool autoCategorizePath)
    {
        if (!autoCategorizePath)
        {
            return basePath;
        }

        var rule = _repository
            .GetCategories()
            .FirstOrDefault(cat => string.Equals(cat.Name, category, StringComparison.OrdinalIgnoreCase));

        var targetFolder = !string.IsNullOrWhiteSpace(rule?.SaveFolder)
            ? rule.SaveFolder
            : category;

        if (string.IsNullOrWhiteSpace(targetFolder))
        {
            targetFolder = "Others";
        }

        var invalidChars = Path.GetInvalidFileNameChars();
        var sanitizedFolder = new string(targetFolder.Select(ch => invalidChars.Contains(ch) ? '_' : ch).ToArray());
        return Path.Combine(basePath, sanitizedFolder);
    }

    private void InitializeTelemetry(DownloadItem item)
    {
        _telemetry[item.Id] = new RuntimeTelemetry(item);
    }

    private void ResetTelemetry(string downloadId)
    {
        _telemetry.TryRemove(downloadId, out _);
    }

    private static void UpdateRealtimeStats(DownloadItem item, RuntimeTelemetry telemetry, DateTime now)
    {
        var elapsedSeconds = (now - telemetry.LastSampleUtc).TotalSeconds;
        if (elapsedSeconds <= 0)
        {
            return;
        }

        var downloadedDelta = item.DownloadedSize - telemetry.LastDownloadedSize;
        var instantRate = downloadedDelta > 0 ? downloadedDelta / elapsedSeconds : 0;

        item.TransferRate = instantRate <= 0
            ? item.TransferRate * 0.80
            : item.TransferRate <= 0
                ? instantRate
                : (item.TransferRate * 0.65) + (instantRate * 0.35);

        if (item.TransferRate < 1)
        {
            item.TransferRate = 0;
        }

        if (item.TotalSize > 0 && item.TransferRate > 0)
        {
            var remainingBytes = Math.Max(0, item.TotalSize - item.DownloadedSize);
            item.TimeLeft = TimeSpan.FromSeconds(remainingBytes / item.TransferRate);
        }
        else
        {
            item.TimeLeft = null;
        }

        foreach (var segment in item.Segments)
        {
            var previousSegmentBytes = telemetry.LastSegmentBytes.TryGetValue(segment.Index, out var value)
                ? value
                : segment.DownloadedBytes;
            var segmentDelta = segment.DownloadedBytes - previousSegmentBytes;
            var segmentInstantRate = segmentDelta > 0 ? segmentDelta / elapsedSeconds : 0;

            segment.TransferRate = segmentInstantRate <= 0
                ? segment.TransferRate * 0.75
                : segment.TransferRate <= 0
                    ? segmentInstantRate
                    : (segment.TransferRate * 0.60) + (segmentInstantRate * 0.40);

            if (segment.TransferRate < 1)
            {
                segment.TransferRate = 0;
            }

            telemetry.LastSegmentBytes[segment.Index] = segment.DownloadedBytes;
        }

        telemetry.LastDownloadedSize = item.DownloadedSize;
        telemetry.LastSampleUtc = now;
    }

    /// <summary>
    /// Automatically inject Referer and Origin headers for googlevideo.com URLs.
    /// These headers are REQUIRED — without them, YouTube returns 403 Forbidden.
    /// </summary>
    private static IReadOnlyDictionary<string, string>? AutoInjectYouTubeHeaders(
        string url, IReadOnlyDictionary<string, string>? existing)
    {
        try
        {
            var host = new Uri(url).Host.ToLowerInvariant();
            if (!host.Contains("googlevideo.com") && !host.Contains("youtube.com"))
                return existing;
        }
        catch
        {
            return existing;
        }

        var headers = existing != null
            ? new Dictionary<string, string>(existing, StringComparer.OrdinalIgnoreCase)
            : new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        if (!headers.ContainsKey("Referer"))
            headers["Referer"] = "https://www.youtube.com/";

        if (!headers.ContainsKey("Origin"))
            headers["Origin"] = "https://www.youtube.com";

        return headers;
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

            if (string.Equals(key, "Origin", StringComparison.OrdinalIgnoreCase))
            {
                request.Headers.TryAddWithoutValidation("Origin", value);
                continue;
            }

            request.Headers.TryAddWithoutValidation(key, value);
        }
    }

    private static ProbeResult BuildProbeResultFromResponse(HttpResponseMessage response, string fallbackUrl)
    {
        var contentLength = response.Content.Headers.ContentRange?.Length
            ?? response.Content.Headers.ContentLength
            ?? 0;
        var contentType = response.Content.Headers.ContentType?.MediaType;
        var supportsRange = response.StatusCode == HttpStatusCode.PartialContent
            || response.Headers.AcceptRanges?.Contains("bytes") == true
            || response.Content.Headers.ContentRange != null;

        var disposition = response.Content.Headers.ContentDisposition;
        var fileName = disposition?.FileNameStar ?? disposition?.FileName;
        fileName = fileName?.Trim('"');
        if (string.IsNullOrWhiteSpace(fileName))
        {
            fileName = UrlHelper.ExtractFileName(fallbackUrl);
        }

        return new ProbeResult
        {
            ContentLength = contentLength,
            ContentType = contentType,
            SupportsRange = supportsRange,
            FileName = fileName
        };
    }

    private sealed class RuntimeTelemetry
    {
        public RuntimeTelemetry(DownloadItem item)
        {
            LastDownloadedSize = item.DownloadedSize;
            LastSampleUtc = DateTime.UtcNow;
            foreach (var segment in item.Segments)
            {
                LastSegmentBytes[segment.Index] = segment.DownloadedBytes;
            }
        }

        public long LastDownloadedSize { get; set; }
        public DateTime LastSampleUtc { get; set; }
        public Dictionary<int, long> LastSegmentBytes { get; } = new();
    }

    public void Dispose()
    {
        StopAll();
        _httpClient.Dispose();
    }

    private class ProbeResult
    {
        public long ContentLength { get; set; }
        public string? ContentType { get; set; }
        public bool SupportsRange { get; set; }
        public string? FileName { get; set; }
    }
}
