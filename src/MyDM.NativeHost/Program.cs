using System.Text;
using System.Text.Json;
using MyDM.Core.Data;
using MyDM.Core.Engine;
using MyDM.Core.Models;
using MyDM.Core.Utilities;

namespace MyDM.NativeHost;

/// <summary>
/// Native Messaging Host for Chrome/Edge extension communication.
/// Reads length-prefixed JSON from stdin, writes length-prefixed JSON to stdout.
/// </summary>
internal static class Program
{
    private static DownloadEngine? _engine;
    private static DownloadRepository? _repository;
    private static MyDMDatabase? _database;
    private static readonly SemaphoreSlim _logLock = new(1, 1);
    private static string _logPath = string.Empty;

    static async Task Main(string[] args)
    {
        var appDataDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "MyDM");
        Directory.CreateDirectory(appDataDir);
        _logPath = Path.Combine(appDataDir, "nativehost.log");

        try
        {
            // Initialize database and engine
            var dbPath = Path.Combine(appDataDir, "mydm.db");
            _database = new MyDMDatabase(dbPath);
            _database.Initialize();
            _repository = new DownloadRepository(_database);
            _engine = new DownloadEngine(_repository);
            _engine.RestoreState();

            _engine.OnLogMessage += (downloadId, message) => _ = LogAsync("engine", "engine.log", downloadId, new { message });
            _engine.OnStatusChanged += item => _ = LogAsync("engine", "engine.status", null, new
            {
                downloadId = item.Id,
                status = item.Status.ToString(),
                progress = Math.Round(item.ProgressPercent, 1)
            });

            await LogAsync("info", "host.start", null, new { pid = Environment.ProcessId });

            // Read messages from stdin
            var stdin = Console.OpenStandardInput();
            var stdout = Console.OpenStandardOutput();

            while (true)
            {
                NativeMessage? message = null;
                string? requestId = null;
                try
                {
                    message = await ReadMessageAsync(stdin);
                    if (message == null) break;

                    message.Payload ??= new Dictionary<string, object>();
                    requestId = GetPayloadString(message, "requestId") ?? Guid.NewGuid().ToString("N");
                    await LogAsync("info", "host.message.received", requestId, new
                    {
                        type = message.Type,
                        payloadKeys = message.Payload.Keys.ToArray()
                    });

                    var response = await HandleMessageAsync(message, requestId);
                    await WriteMessageAsync(stdout, response);
                    await LogAsync("info", "host.message.responded", requestId, new { responseType = response.Type });
                }
                catch (Exception ex)
                {
                    await LogAsync("error", "host.message.failed", requestId, new
                    {
                        exception = ex.GetType().Name,
                        ex.Message,
                        ex.StackTrace
                    });

                    var error = AddRequestId(new NativeMessage
                    {
                        Type = "error",
                        Payload = new Dictionary<string, object>
                        {
                            { "message", ex.Message },
                            { "code", "INTERNAL_ERROR" }
                        }
                    }, requestId);

                    await WriteMessageAsync(stdout, error);
                }
            }
        }
        catch (Exception ex)
        {
            await LogAsync("fatal", "host.crash", null, new
            {
                exception = ex.GetType().Name,
                ex.Message,
                ex.StackTrace
            });
            throw;
        }
        finally
        {
            _engine?.Dispose();
            _database?.Dispose();
            await LogAsync("info", "host.stop");
        }
    }

    private static async Task<NativeMessage?> ReadMessageAsync(Stream stdin)
    {
        // Read 4-byte length prefix (little-endian).
        var lengthBytes = new byte[4];
        var totalRead = 0;
        while (totalRead < 4)
        {
            var read = await stdin.ReadAsync(lengthBytes.AsMemory(totalRead, 4 - totalRead));
            if (read == 0)
            {
                if (totalRead == 0) return null;
                throw new Exception("Unexpected end of stream while reading length prefix");
            }
            totalRead += read;
        }

        var length = BitConverter.ToInt32(lengthBytes, 0);
        if (length <= 0 || length > 1024 * 1024) // 1MB max
            throw new Exception($"Invalid message length: {length}");

        // Read the message body.
        var buffer = new byte[length];
        totalRead = 0;
        while (totalRead < length)
        {
            var read = await stdin.ReadAsync(buffer.AsMemory(totalRead, length - totalRead));
            if (read == 0) throw new Exception("Unexpected end of stream while reading payload");
            totalRead += read;
        }

        var json = Encoding.UTF8.GetString(buffer);
        var message = JsonSerializer.Deserialize<NativeMessage>(json);
        if (message == null)
            throw new Exception("Invalid message JSON");

        message.Payload ??= new Dictionary<string, object>();
        return message;
    }

    private static async Task WriteMessageAsync(Stream stdout, NativeMessage message)
    {
        var json = JsonSerializer.Serialize(message);
        var bytes = Encoding.UTF8.GetBytes(json);
        var length = BitConverter.GetBytes(bytes.Length);

        await stdout.WriteAsync(length);
        await stdout.WriteAsync(bytes);
        await stdout.FlushAsync();
    }

    private static Task<NativeMessage> HandleMessageAsync(NativeMessage message, string requestId)
    {
        return message.Type switch
        {
            "ping" => Task.FromResult(AddRequestId(new NativeMessage
            {
                Type = "pong",
                Payload = new Dictionary<string, object>
                {
                    { "version", "1.1.0" },
                    { "status", "connected" },
                    { "pid", Environment.ProcessId }
                }
            }, requestId)),
            "healthcheck" => Task.FromResult(HandleHealthcheck(requestId)),
            "add_download" => HandleAddDownloadAsync(message, requestId),
            "add_media_download" => HandleAddMediaDownloadAsync(message, requestId),
            "get_status" => Task.FromResult(HandleGetStatus(message, requestId)),
            _ => Task.FromResult(AddRequestId(new NativeMessage
            {
                Type = "error",
                Payload = new Dictionary<string, object>
                {
                    { "message", $"Unknown message type: {message.Type}" },
                    { "code", "UNKNOWN_TYPE" }
                }
            }, requestId))
        };
    }

    private static async Task<NativeMessage> HandleAddDownloadAsync(NativeMessage message, string requestId)
    {
        var url = GetPayloadString(message, "url");
        if (string.IsNullOrEmpty(url) || !UrlHelper.IsValidUrl(url))
        {
            return AddRequestId(new NativeMessage
            {
                Type = "error",
                Payload = new Dictionary<string, object>
                {
                    { "message", "Invalid URL" },
                    { "code", "INVALID_URL" }
                }
            }, requestId);
        }

        var fileName = GetPayloadString(message, "filename");
        var category = GetPayloadString(message, "category");
        var headers = BuildRequestHeaders(message);

        try
        {
            var item = await _engine!.AddDownloadAsync(
                url,
                fileName: fileName,
                category: category,
                requestHeaders: headers);

            await _engine.StartDownloadAsync(item.Id);

            await LogAsync("info", "download.added", requestId, new
            {
                downloadId = item.Id,
                item.FileName,
                item.Url,
                item.SupportsRange
            });

            return AddRequestId(new NativeMessage
            {
                Type = "download_added",
                Payload = new Dictionary<string, object>
                {
                    { "downloadId", item.Id },
                    { "fileName", item.FileName },
                    { "size", item.TotalSize },
                    { "status", item.Status.ToString() }
                }
            }, requestId);
        }
        catch (Exception ex)
        {
            await LogAsync("error", "download.add.failed", requestId, new
            {
                url,
                exception = ex.GetType().Name,
                ex.Message
            });

            return AddRequestId(new NativeMessage
            {
                Type = "error",
                Payload = new Dictionary<string, object>
                {
                    { "message", ex.Message },
                    { "code", "ADD_DOWNLOAD_FAILED" }
                }
            }, requestId);
        }
    }

    private static async Task<NativeMessage> HandleAddMediaDownloadAsync(NativeMessage message, string requestId)
    {
        var manifestUrl = GetPayloadString(message, "manifestUrl");
        var mediaType = GetPayloadString(message, "mediaType");
        var quality = GetPayloadString(message, "quality");
        var title = GetPayloadString(message, "title");
        var headers = BuildRequestHeaders(message);

        if (string.IsNullOrWhiteSpace(manifestUrl))
        {
            return AddRequestId(new NativeMessage
            {
                Type = "error",
                Payload = new Dictionary<string, object>
                {
                    { "message", "Missing manifest URL" },
                    { "code", "MISSING_MANIFEST" }
                }
            }, requestId);
        }

        var manifestPath = manifestUrl.ToLowerInvariant();
        if (manifestPath.Contains(".m3u8") || manifestPath.Contains(".mpd"))
        {
            return AddRequestId(new NativeMessage
            {
                Type = "error",
                Payload = new Dictionary<string, object>
                {
                    { "message", "HLS/DASH manifest download pipeline is not enabled in desktop engine yet. Use a direct media URL when available." },
                    { "code", "MEDIA_PIPELINE_NOT_IMPLEMENTED" }
                }
            }, requestId);
        }

        try
        {
            var item = await _engine!.AddDownloadAsync(
                manifestUrl,
                fileName: title != null ? UrlHelper.SanitizeFileName(title) + ".mp4" : null,
                category: "Video",
                requestHeaders: headers);

            item.MediaType = mediaType?.ToLowerInvariant() == "dash" ? MediaType.Dash : MediaType.Hls;
            item.ManifestUrl = manifestUrl;
            item.SelectedQuality = quality;
            _repository!.Update(item);

            await _engine.StartDownloadAsync(item.Id);

            await LogAsync("info", "media.added", requestId, new
            {
                downloadId = item.Id,
                item.FileName,
                mediaType = item.MediaType.ToString(),
                item.SelectedQuality
            });

            return AddRequestId(new NativeMessage
            {
                Type = "download_added",
                Payload = new Dictionary<string, object>
                {
                    { "downloadId", item.Id },
                    { "fileName", item.FileName },
                    { "mediaType", item.MediaType.ToString() },
                    { "quality", item.SelectedQuality ?? string.Empty }
                }
            }, requestId);
        }
        catch (Exception ex)
        {
            await LogAsync("error", "media.add.failed", requestId, new
            {
                manifestUrl,
                exception = ex.GetType().Name,
                ex.Message
            });

            return AddRequestId(new NativeMessage
            {
                Type = "error",
                Payload = new Dictionary<string, object>
                {
                    { "message", ex.Message },
                    { "code", "ADD_MEDIA_FAILED" }
                }
            }, requestId);
        }
    }

    private static NativeMessage HandleGetStatus(NativeMessage message, string requestId)
    {
        var downloadId = GetPayloadString(message, "downloadId");

        if (!string.IsNullOrEmpty(downloadId))
        {
            var item = _engine!.GetDownload(downloadId);
            if (item == null)
            {
                return AddRequestId(new NativeMessage
                {
                    Type = "error",
                    Payload = new Dictionary<string, object>
                    {
                        { "message", "Download not found" },
                        { "code", "NOT_FOUND" }
                    }
                }, requestId);
            }

            return AddRequestId(new NativeMessage
            {
                Type = "status_update",
                Payload = new Dictionary<string, object>
                {
                    { "downloads", new[] { DownloadToDict(item) } }
                }
            }, requestId);
        }

        var downloads = _engine!.GetAllDownloads().Select(DownloadToDict).ToArray();
        return AddRequestId(new NativeMessage
        {
            Type = "status_update",
            Payload = new Dictionary<string, object>
            {
                { "downloads", downloads }
            }
        }, requestId);
    }

    private static NativeMessage HandleHealthcheck(string requestId)
    {
        var downloads = _engine!.GetAllDownloads();
        var active = downloads.Count(d => d.Status == DownloadStatus.Downloading);
        return AddRequestId(new NativeMessage
        {
            Type = "health_status",
            Payload = new Dictionary<string, object>
            {
                { "status", "ok" },
                { "pid", Environment.ProcessId },
                { "activeDownloads", active },
                { "totalDownloads", downloads.Count }
            }
        }, requestId);
    }

    private static Dictionary<string, object> DownloadToDict(DownloadItem item) => new()
    {
        { "id", item.Id },
        { "fileName", item.FileName },
        { "size", item.TotalSize },
        { "downloaded", item.DownloadedSize },
        { "status", item.Status.ToString() },
        { "progress", Math.Round(item.ProgressPercent, 1) },
        { "errorMessage", item.ErrorMessage ?? string.Empty },
        { "mediaType", item.MediaType.ToString() }
    };

    private static NativeMessage AddRequestId(NativeMessage message, string? requestId)
    {
        message.Payload ??= new Dictionary<string, object>();
        if (!string.IsNullOrWhiteSpace(requestId))
        {
            message.Payload["requestId"] = requestId;
        }
        message.Payload["timestamp"] = DateTime.UtcNow.ToString("O");
        return message;
    }

    private static string? GetPayloadString(NativeMessage message, string key)
    {
        if (!message.Payload.TryGetValue(key, out var value) || value == null)
            return null;

        if (value is JsonElement element)
        {
            return element.ValueKind switch
            {
                JsonValueKind.String => element.GetString(),
                JsonValueKind.Null => null,
                JsonValueKind.Undefined => null,
                _ => element.ToString()
            };
        }

        return value.ToString();
    }

    private static IReadOnlyDictionary<string, string>? BuildRequestHeaders(NativeMessage message)
    {
        var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        var referrer = GetPayloadString(message, "referrer");
        if (!string.IsNullOrWhiteSpace(referrer))
        {
            headers["Referer"] = referrer;
        }

        var cookies = GetPayloadString(message, "cookies");
        if (!string.IsNullOrWhiteSpace(cookies))
        {
            headers["Cookie"] = cookies;
        }

        var userAgent = GetPayloadString(message, "userAgent");
        if (!string.IsNullOrWhiteSpace(userAgent))
        {
            headers["User-Agent"] = userAgent;
        }

        if (message.Payload.TryGetValue("headers", out var rawHeaders) &&
            rawHeaders is JsonElement headersElement &&
            headersElement.ValueKind == JsonValueKind.Object)
        {
            foreach (var prop in headersElement.EnumerateObject())
            {
                var value = prop.Value.ValueKind == JsonValueKind.String
                    ? prop.Value.GetString()
                    : prop.Value.ToString();
                if (!string.IsNullOrWhiteSpace(value))
                {
                    headers[prop.Name] = value;
                }
            }
        }

        return headers.Count == 0 ? null : headers;
    }

    private static async Task LogAsync(string level, string eventName, string? requestId = null, object? data = null)
    {
        if (string.IsNullOrWhiteSpace(_logPath))
            return;

        var entry = new Dictionary<string, object?>
        {
            ["ts"] = DateTime.UtcNow.ToString("O"),
            ["level"] = level,
            ["event"] = eventName,
            ["requestId"] = requestId,
            ["data"] = data
        };

        var line = JsonSerializer.Serialize(entry);

        await _logLock.WaitAsync();
        try
        {
            await File.AppendAllTextAsync(_logPath, line + Environment.NewLine);
        }
        finally
        {
            _logLock.Release();
        }
    }
}
