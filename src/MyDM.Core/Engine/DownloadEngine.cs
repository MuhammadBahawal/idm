using MyDM.Core.Data;
using MyDM.Core.Models;
using MyDM.Core.Parsers;
using MyDM.Core.Utilities;
using System.Collections.Concurrent;
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
        _httpClient.DefaultRequestHeaders.UserAgent.ParseAdd("MyDM/1.0");

        _segmentDownloader = new SegmentDownloader(_httpClient, _speedLimiter);
    }

    public SpeedLimiter SpeedLimiter => _speedLimiter;

    /// <summary>
    /// Add a new download and start fetching file info.
    /// </summary>
    public async Task<DownloadItem> AddDownloadAsync(string url, string? savePath = null,
        string? fileName = null, int connections = 8, string? category = null)
    {
        if (!UrlHelper.IsValidUrl(url))
            throw new ArgumentException("Invalid URL", nameof(url));

        var item = new DownloadItem
        {
            Url = url,
            FileName = fileName ?? UrlHelper.ExtractFileName(url),
            SavePath = savePath ?? Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads", "MyDM"),
            Connections = connections,
            Category = category ?? MimeDetector.Detect(url, null)
        };

        // Probe the URL for file info
        try
        {
            var info = await ProbeUrlAsync(url);
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
            Log(item.Id, "Warning", $"Could not probe URL: {ex.Message}");
        }

        FileHelper.EnsureDirectory(item.SavePath);
        _repository.Insert(item);
        _downloadCache[item.Id] = item;
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

        item.Status = DownloadStatus.Downloading;
        item.LastAttemptAt = DateTime.UtcNow;
        _repository.Update(item);
        OnStatusChanged?.Invoke(item);

        _ = Task.Run(async () =>
        {
            try
            {
                if (item.SupportsRange && item.TotalSize > 0 && item.Connections > 1)
                {
                    await DownloadWithSegmentsAsync(item, cts.Token);
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
                }

                item.Status = DownloadStatus.Complete;
                item.CompletedAt = DateTime.UtcNow;
                item.DownloadedSize = item.TotalSize;
                _repository.Update(item);
                Log(downloadId, "Info", "Download completed successfully");
                OnStatusChanged?.Invoke(item);
            }
            catch (OperationCanceledException)
            {
                item.Status = DownloadStatus.Paused;
                _repository.Update(item);
                Log(downloadId, "Info", "Download paused");
                OnStatusChanged?.Invoke(item);
            }
            catch (Exception ex)
            {
                item.Status = DownloadStatus.Error;
                item.ErrorMessage = ex.Message;
                item.RetryCount++;
                _repository.Update(item);
                Log(downloadId, "Error", $"Download failed: {ex.Message}");
                OnStatusChanged?.Invoke(item);

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
            _repository.Update(item);
            SaveSegmentProgress(item);
            OnStatusChanged?.Invoke(item);
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
            _repository.Update(item);
            CleanupTempFiles(item);
            OnStatusChanged?.Invoke(item);
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
            item.Segments = _repository.GetSegments(item.Id);
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

        var tasks = pendingSegments.Select(segment => Task.Run(async () =>
        {
            await _segmentDownloader.DownloadSegmentAsync(
                segment, item.Url, item.SpeedLimit,
                onProgress: (seg, bytesRead) =>
                {
                    lock (progressLock)
                    {
                        item.DownloadedSize = item.Segments.Sum(s => s.DownloadedBytes);
                        var now = DateTime.UtcNow;
                        if ((now - lastProgressUpdate).TotalMilliseconds > 250)
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

        await _segmentDownloader.DownloadSingleStreamAsync(
            item.Url, item.PartFilePath, item.SpeedLimit,
            onProgress: (downloaded, total) =>
            {
                item.DownloadedSize = downloaded;
                if (total > 0) item.TotalSize = total;
                var now = DateTime.UtcNow;
                if ((now - lastUpdate).TotalMilliseconds > 250)
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

    private async Task<ProbeResult> ProbeUrlAsync(string url)
    {
        var request = new HttpRequestMessage(HttpMethod.Head, url);
        var response = await _httpClient.SendAsync(request);
        response.EnsureSuccessStatusCode();

        var contentLength = response.Content.Headers.ContentLength ?? 0;
        var contentType = response.Content.Headers.ContentType?.MediaType;
        var supportsRange = response.Headers.AcceptRanges?.Contains("bytes") == true
                           || response.StatusCode == System.Net.HttpStatusCode.PartialContent;

        // Try to get filename from Content-Disposition
        var disposition = response.Content.Headers.ContentDisposition;
        var fileName = disposition?.FileNameStar ?? disposition?.FileName;
        fileName = fileName?.Trim('"');

        return new ProbeResult
        {
            ContentLength = contentLength,
            ContentType = contentType,
            SupportsRange = supportsRange,
            FileName = fileName
        };
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
