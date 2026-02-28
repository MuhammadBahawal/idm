using System.Text;
using System.Text.Json;
using System.ComponentModel;
using System.Diagnostics;
using MyDM.Core.Data;
using MyDM.Core.Engine;
using MyDM.Core.Media;
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
    private static FfmpegMuxer? _muxer;
    private static readonly SemaphoreSlim _logLock = new(1, 1);
    private static string _logPath = string.Empty;
    private static string _ytDebugLogPath = string.Empty;
    private const int YtDlpTimeoutMs = 30 * 60 * 1000;

    static async Task Main(string[] args)
    {
        var appDataDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "MyDM");
        Directory.CreateDirectory(appDataDir);
        _logPath = Path.Combine(appDataDir, "nativehost.log");
        _ytDebugLogPath = Path.Combine(appDataDir, "yt-dlp-debug.log");

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
            "add_video_download" => HandleAddVideoDownloadAsync(message, requestId),
            "add_youtube_download" => HandleAddYouTubeDownloadAsync(message, requestId),
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
        var isManifest = manifestPath.Contains(".m3u8") || manifestPath.Contains(".mpd");
        if (isManifest)
        {
            await LogAsync("warn", "media.manifest_direct_download", requestId, new
            {
                manifestUrl,
                note = "HLS/DASH manifest pipeline not implemented. Attempting direct download of the URL."
            });
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

    private static async Task<NativeMessage> HandleAddVideoDownloadAsync(NativeMessage message, string requestId)
    {
        var videoUrl = GetPayloadString(message, "videoUrl");
        var audioUrl = GetPayloadString(message, "audioUrl");
        var fileName = GetPayloadString(message, "filename");
        var title = GetPayloadString(message, "title");
        var quality = GetPayloadString(message, "quality");
        var headers = BuildRequestHeaders(message);

        if (string.IsNullOrWhiteSpace(videoUrl) || !UrlHelper.IsValidUrl(videoUrl))
        {
            return AddRequestId(new NativeMessage
            {
                Type = "error",
                Payload = new Dictionary<string, object>
                {
                    { "message", "Invalid or missing video URL" },
                    { "code", "INVALID_VIDEO_URL" }
                }
            }, requestId);
        }

        // Determine final file name
        var baseName = !string.IsNullOrWhiteSpace(fileName)
            ? fileName
            : !string.IsNullOrWhiteSpace(title)
                ? UrlHelper.SanitizeFileName(title) + ".mp4"
                : UrlHelper.ExtractFileName(videoUrl);

        // Ensure .mp4 extension for muxed output
        if (!string.IsNullOrWhiteSpace(audioUrl) && !baseName.EndsWith(".mp4", StringComparison.OrdinalIgnoreCase))
        {
            baseName = Path.ChangeExtension(baseName, ".mp4");
        }

        try
        {
            var needsMux = !string.IsNullOrWhiteSpace(audioUrl) && UrlHelper.IsValidUrl(audioUrl!);

            if (!needsMux)
            {
                // Simple case: muxed stream or no audio — just download directly
                var item = await _engine!.AddDownloadAsync(
                    videoUrl,
                    fileName: baseName,
                    category: "Video",
                    requestHeaders: headers);

                await _engine.StartDownloadAsync(item.Id);

                await LogAsync("info", "video.download.added", requestId, new
                {
                    downloadId = item.Id,
                    item.FileName,
                    quality,
                    muxed = true
                });

                return AddRequestId(new NativeMessage
                {
                    Type = "download_added",
                    Payload = new Dictionary<string, object>
                    {
                        { "downloadId", item.Id },
                        { "fileName", item.FileName },
                        { "quality", quality ?? string.Empty },
                        { "status", item.Status.ToString() }
                    }
                }, requestId);
            }

            // Split streams: download video + audio in parallel, then mux
            await LogAsync("info", "video.download.split_start", requestId, new
            {
                videoUrl,
                audioUrl,
                quality,
                fileName = baseName
            });

            var videoItem = await _engine!.AddDownloadAsync(
                videoUrl,
                fileName: "_video_" + baseName,
                category: "Video",
                requestHeaders: headers);

            var audioItem = await _engine.AddDownloadAsync(
                audioUrl!,
                fileName: "_audio_" + baseName,
                category: "Video",
                requestHeaders: headers);

            // Start both downloads
            var videoTask = _engine.StartDownloadAsync(videoItem.Id);
            var audioTask = _engine.StartDownloadAsync(audioItem.Id);
            await Task.WhenAll(videoTask, audioTask);

            // Wait for both to complete (poll status)
            var maxWaitMs = 600_000; // 10 minutes max
            var pollMs = 1000;
            var elapsed = 0;

            while (elapsed < maxWaitMs)
            {
                await Task.Delay(pollMs);
                elapsed += pollMs;

                var v = _repository!.GetById(videoItem.Id);
                var a = _repository.GetById(audioItem.Id);

                if (v?.Status == DownloadStatus.Error || a?.Status == DownloadStatus.Error)
                {
                    var failedPart = v?.Status == DownloadStatus.Error ? "video" : "audio";
                    var errorMsg = v?.Status == DownloadStatus.Error ? v.ErrorMessage : a?.ErrorMessage;
                    throw new Exception($"Failed to download {failedPart} stream: {errorMsg}");
                }

                if (v?.Status == DownloadStatus.Complete && a?.Status == DownloadStatus.Complete)
                    break;
            }

            var videoFinal = _repository!.GetById(videoItem.Id);
            var audioFinal = _repository.GetById(audioItem.Id);

            if (videoFinal?.Status != DownloadStatus.Complete || audioFinal?.Status != DownloadStatus.Complete)
            {
                throw new Exception("Download timed out before both streams completed");
            }

            var videoPath = videoFinal.SavePath;
            var audioPath = audioFinal.SavePath;
            var outputDir = Path.GetDirectoryName(videoPath) ?? Environment.GetFolderPath(Environment.SpecialFolder.MyVideos);
            var outputPath = Path.Combine(outputDir, baseName);

            // Mux with ffmpeg
            await LogAsync("info", "video.mux.start", requestId, new { videoPath, audioPath, outputPath });

            var muxSuccess = false;
            if (_muxer == null) _muxer = new FfmpegMuxer();

            if (_muxer.IsAvailable)
            {
                muxSuccess = await _muxer.MuxAsync(videoPath, audioPath, outputPath);
            }

            if (muxSuccess)
            {
                // Clean up temp files
                try { File.Delete(videoPath); } catch { /* ignore */ }
                try { File.Delete(audioPath); } catch { /* ignore */ }
                _repository.Delete(videoItem.Id);
                _repository.Delete(audioItem.Id);

                await LogAsync("info", "video.mux.complete", requestId, new
                {
                    outputPath,
                    quality
                });

                return AddRequestId(new NativeMessage
                {
                    Type = "download_added",
                    Payload = new Dictionary<string, object>
                    {
                        { "downloadId", videoItem.Id },
                        { "fileName", baseName },
                        { "quality", quality ?? string.Empty },
                        { "status", "Complete" },
                        { "muxed", true },
                        { "outputPath", outputPath }
                    }
                }, requestId);
            }
            else
            {
                await LogAsync("warn", "video.mux.failed", requestId, new
                {
                    videoPath,
                    audioPath,
                    note = "ffmpeg not found or mux failed. Video file saved without audio."
                });

                return AddRequestId(new NativeMessage
                {
                    Type = "download_added",
                    Payload = new Dictionary<string, object>
                    {
                        { "downloadId", videoItem.Id },
                        { "fileName", videoFinal.FileName },
                        { "quality", quality ?? string.Empty },
                        { "status", "Complete" },
                        { "muxed", false },
                        { "warning", "ffmpeg not available. Video saved without audio. Install ffmpeg and add to PATH for automatic muxing." }
                    }
                }, requestId);
            }
        }
        catch (Exception ex)
        {
            await LogAsync("error", "video.download.failed", requestId, new
            {
                videoUrl,
                audioUrl,
                exception = ex.GetType().Name,
                ex.Message
            });

            return AddRequestId(new NativeMessage
            {
                Type = "error",
                Payload = new Dictionary<string, object>
                {
                    { "message", ex.Message },
                    { "code", "ADD_VIDEO_DOWNLOAD_FAILED" }
                }
            }, requestId);
        }
    }

    private static async Task<NativeMessage> HandleAddYouTubeDownloadAsync(NativeMessage message, string requestId)
    {
        var rawVideoUrl = GetPayloadString(message, "videoUrl") ?? GetPayloadString(message, "url");
        var fileName = GetPayloadString(message, "filename");
        var title = GetPayloadString(message, "title");
        var quality = GetPayloadString(message, "quality");
        var referrer = GetPayloadString(message, "referrer");
        var pageUrl = GetPayloadString(message, "pageUrl");
        var clickTsIso = GetPayloadString(message, "clickTsIso");
        var detectedVideoUrl = GetPayloadString(message, "detectedVideoUrl");
        var canonicalFromGui = GetPayloadString(message, "canonicalYoutubeUrl");
        var requestSource = GetPayloadString(message, "requestSource") ?? "unknown";
        var headers = BuildRequestHeaders(message);

        if (string.IsNullOrWhiteSpace(rawVideoUrl) || !UrlHelper.IsValidUrl(rawVideoUrl))
        {
            return AddRequestId(new NativeMessage
            {
                Type = "error",
                Payload = new Dictionary<string, object>
                {
                    { "message", "Invalid or missing YouTube URL" },
                    { "code", "INVALID_YOUTUBE_URL" }
                }
            }, requestId);
        }

        var videoUrl = NormalizeYouTubeUrl(rawVideoUrl)
            ?? NormalizeYouTubeUrl(canonicalFromGui)
            ?? NormalizeYouTubeUrl(pageUrl);

        if (string.IsNullOrWhiteSpace(videoUrl) || !IsYouTubePageUrl(videoUrl))
        {
            return AddRequestId(new NativeMessage
            {
                Type = "error",
                Payload = new Dictionary<string, object>
                {
                    { "message", "URL must be a YouTube page URL (youtube.com / youtu.be)" },
                    { "code", "UNSUPPORTED_YOUTUBE_URL" }
                }
            }, requestId);
        }

        try
        {
            await LogAsync("info", "youtube.gui.click_received", requestId, new
            {
                clickTsIso = clickTsIso ?? string.Empty,
                requestSource,
                pageUrl = pageUrl ?? string.Empty,
                detectedVideoUrl = detectedVideoUrl ?? string.Empty,
                rawVideoUrl = rawVideoUrl ?? string.Empty,
                canonicalFromGui = canonicalFromGui ?? string.Empty,
                normalizedVideoUrl = videoUrl
            });
            await LogYouTubeDebugAsync("youtube.gui.click_received", requestId, new
            {
                clickTsIso = clickTsIso ?? string.Empty,
                requestSource,
                pageUrl = pageUrl ?? string.Empty,
                detectedVideoUrl = detectedVideoUrl ?? string.Empty,
                rawVideoUrl = rawVideoUrl ?? string.Empty,
                canonicalFromGui = canonicalFromGui ?? string.Empty,
                normalizedVideoUrl = videoUrl
            });

            var saveDir = ResolveVideoSaveDirectory();
            Directory.CreateDirectory(saveDir);

            if (!IsDirectoryWritable(saveDir, out var writeProbeError))
            {
                await LogAsync("error", "youtube.output.dir_not_writable", requestId, new
                {
                    saveDir,
                    writeProbeError
                });
                await LogYouTubeDebugAsync("youtube.output.dir_not_writable", requestId, new
                {
                    saveDir,
                    writeProbeError
                });
                return AddRequestId(new NativeMessage
                {
                    Type = "error",
                    Payload = new Dictionary<string, object>
                    {
                        { "message", $"Download folder is not writable: {saveDir}. {writeProbeError}" },
                        { "code", "OUTPUT_DIR_NOT_WRITABLE" }
                    }
                }, requestId);
            }

            var outputTemplate = BuildYouTubeOutputTemplate(saveDir, fileName, title);
            var formatSelector = BuildYouTubeFormatSelector(quality);

            var headersWithoutCookie = headers != null
                ? new Dictionary<string, string>(headers, StringComparer.OrdinalIgnoreCase)
                : null;
            headersWithoutCookie?.Remove("Cookie");

            var runnerSpecs = ResolveYtDlpRunnerSpecs();
            await LogAsync("info", "youtube.runtime.environment", requestId, new
            {
                path = Environment.GetEnvironmentVariable("PATH") ?? string.Empty,
                appBase = AppContext.BaseDirectory,
                cwd = Environment.CurrentDirectory,
                discoveredRunners = runnerSpecs.Select(r => r.DisplayName).ToArray()
            });
            await LogYouTubeDebugAsync("youtube.runtime.environment", requestId, new
            {
                path = Environment.GetEnvironmentVariable("PATH") ?? string.Empty,
                appBase = AppContext.BaseDirectory,
                cwd = Environment.CurrentDirectory,
                discoveredRunners = runnerSpecs.Select(r => r.DisplayName).ToArray()
            });

            var attempts = new List<(string FileName, IReadOnlyList<string> Args, string Runner, string Mode, bool CliParity, bool IncludesCookieHeader, string? CookiesFromBrowser)>();
            void AddAttemptsForMode(
                string mode,
                bool cliParity,
                bool includesCookieHeader,
                string? cookiesFromBrowser,
                IReadOnlyList<string> modeArgs)
            {
                foreach (var runner in runnerSpecs)
                {
                    var allArgs = new List<string>(runner.PrefixArgs.Count + modeArgs.Count);
                    allArgs.AddRange(runner.PrefixArgs);
                    allArgs.AddRange(modeArgs);
                    attempts.Add((runner.Executable, allArgs, runner.DisplayName, mode, cliParity, includesCookieHeader, cookiesFromBrowser));
                }
            }

            var cliParityArgs = BuildYtDlpArgs(
                videoUrl,
                outputTemplate,
                formatSelector,
                referrer,
                headersWithoutCookie,
                includeCookieHeader: false,
                cookiesFromBrowser: null);
            AddAttemptsForMode("cli_parity", cliParity: true, includesCookieHeader: false, cookiesFromBrowser: null, cliParityArgs);

            var chromeProfileCookieArgs = BuildYtDlpArgs(
                videoUrl,
                outputTemplate,
                formatSelector,
                referrer,
                headersWithoutCookie,
                includeCookieHeader: false,
                cookiesFromBrowser: "chrome");
            AddAttemptsForMode("with_cookies_from_browser_chrome", cliParity: false, includesCookieHeader: false, cookiesFromBrowser: "chrome", chromeProfileCookieArgs);

            var chromiumProfileCookieArgs = BuildYtDlpArgs(
                videoUrl,
                outputTemplate,
                formatSelector,
                referrer,
                headersWithoutCookie,
                includeCookieHeader: false,
                cookiesFromBrowser: "chromium");
            AddAttemptsForMode("with_cookies_from_browser_chromium", cliParity: false, includesCookieHeader: false, cookiesFromBrowser: "chromium", chromiumProfileCookieArgs);

            if (headers != null &&
                headers.TryGetValue("Cookie", out var cookieHeader) &&
                !string.IsNullOrWhiteSpace(cookieHeader))
            {
                var cookieAwareArgs = BuildYtDlpArgs(
                    videoUrl,
                    outputTemplate,
                    formatSelector,
                    referrer,
                    headers,
                    includeCookieHeader: true,
                    cookiesFromBrowser: null);
                AddAttemptsForMode("with_browser_cookie_header", cliParity: false, includesCookieHeader: true, cookiesFromBrowser: null, cookieAwareArgs);
            }

            ProcessExecutionResult? lastResult = null;
            string? runnerUsed = null;
            string? modeUsed = null;

            foreach (var attempt in attempts)
            {
                await LogAsync("info", "youtube.ytdlp.command", requestId, new
                {
                    mode = attempt.Mode,
                    cliParity = attempt.CliParity,
                    includesCookieHeader = attempt.IncludesCookieHeader,
                    cookiesFromBrowser = attempt.CookiesFromBrowser ?? string.Empty,
                    runner = attempt.Runner,
                    executable = attempt.FileName,
                    arguments = attempt.Args,
                    commandLine = BuildCommandLineForLog(attempt.FileName, attempt.Args),
                    outputTemplate,
                    saveDir,
                    formatSelector,
                    referrer = referrer ?? string.Empty,
                    headers
                });
                await LogYouTubeDebugAsync("youtube.ytdlp.command", requestId, new
                {
                    mode = attempt.Mode,
                    cliParity = attempt.CliParity,
                    includesCookieHeader = attempt.IncludesCookieHeader,
                    cookiesFromBrowser = attempt.CookiesFromBrowser ?? string.Empty,
                    runner = attempt.Runner,
                    executable = attempt.FileName,
                    arguments = attempt.Args,
                    commandLine = BuildCommandLineForLog(attempt.FileName, attempt.Args),
                    outputTemplate,
                    saveDir,
                    formatSelector,
                    referrer = referrer ?? string.Empty,
                    headers
                });

                await LogAsync("info", "youtube.ytdlp.process.start", requestId, new
                {
                    mode = attempt.Mode,
                    cliParity = attempt.CliParity,
                    includesCookieHeader = attempt.IncludesCookieHeader,
                    cookiesFromBrowser = attempt.CookiesFromBrowser ?? string.Empty,
                    runner = attempt.Runner,
                    executable = attempt.FileName
                });
                await LogYouTubeDebugAsync("youtube.ytdlp.process.start", requestId, new
                {
                    mode = attempt.Mode,
                    cliParity = attempt.CliParity,
                    includesCookieHeader = attempt.IncludesCookieHeader,
                    cookiesFromBrowser = attempt.CookiesFromBrowser ?? string.Empty,
                    runner = attempt.Runner,
                    executable = attempt.FileName
                });

                var result = await ExecuteProcessAsync(attempt.FileName, attempt.Args, YtDlpTimeoutMs);
                if (!result.Started)
                {
                    await LogAsync("warn", "youtube.runner.unavailable", requestId, new
                    {
                        mode = attempt.Mode,
                        runner = attempt.Runner,
                        executable = attempt.FileName,
                        result.StartError
                    });
                    await LogYouTubeDebugAsync("youtube.runner.unavailable", requestId, new
                    {
                        mode = attempt.Mode,
                        runner = attempt.Runner,
                        executable = attempt.FileName,
                        result.StartError
                    });
                    lastResult = result;
                    continue;
                }

                await LogAsync("info", "youtube.ytdlp.process.exit", requestId, new
                {
                    mode = attempt.Mode,
                    cliParity = attempt.CliParity,
                    includesCookieHeader = attempt.IncludesCookieHeader,
                    cookiesFromBrowser = attempt.CookiesFromBrowser ?? string.Empty,
                    runner = attempt.Runner,
                    result.ExitCode,
                    stdout = result.Stdout,
                    stderr = result.Stderr
                });
                await LogYouTubeDebugAsync("youtube.ytdlp.process.exit", requestId, new
                {
                    mode = attempt.Mode,
                    cliParity = attempt.CliParity,
                    includesCookieHeader = attempt.IncludesCookieHeader,
                    cookiesFromBrowser = attempt.CookiesFromBrowser ?? string.Empty,
                    runner = attempt.Runner,
                    result.ExitCode,
                    stdout = result.Stdout,
                    stderr = result.Stderr
                });

                if (result.ExitCode == 0)
                {
                    lastResult = result;
                    runnerUsed = attempt.Runner;
                    modeUsed = attempt.Mode;
                    break;
                }

                await LogAsync("warn", "youtube.runner.failed", requestId, new
                {
                    mode = attempt.Mode,
                    cliParity = attempt.CliParity,
                    includesCookieHeader = attempt.IncludesCookieHeader,
                    cookiesFromBrowser = attempt.CookiesFromBrowser ?? string.Empty,
                    runner = attempt.Runner,
                    result.ExitCode,
                    output = Tail(result.CombinedOutput, 3000)
                });
                lastResult = result;
            }

            if (lastResult == null || (!lastResult.Value.Started && string.IsNullOrWhiteSpace(runnerUsed)))
            {
                return AddRequestId(new NativeMessage
                {
                    Type = "error",
                    Payload = new Dictionary<string, object>
                    {
                        { "message", "yt-dlp runtime was not found. Install yt-dlp or python/py + yt_dlp, then restart Chrome." },
                        { "code", "YTDLP_NOT_FOUND" },
                        { "triedRunners", runnerSpecs.Select(r => r.DisplayName).ToArray() }
                    }
                }, requestId);
            }

            var finalResult = lastResult.Value;

            if (string.IsNullOrWhiteSpace(runnerUsed))
            {
                return AddRequestId(new NativeMessage
                {
                    Type = "error",
                    Payload = new Dictionary<string, object>
                    {
                        { "message", $"yt-dlp download failed. {Tail(finalResult.CombinedOutput, 2200)}" },
                        { "code", "YTDLP_DOWNLOAD_FAILED" }
                    }
                }, requestId);
            }

            var outputPath = TryExtractOutputPath(finalResult.CombinedOutput, saveDir)
                ?? ResolveLikelyOutputPath(saveDir, fileName, title);
            var resolvedFileName = !string.IsNullOrWhiteSpace(outputPath)
                ? Path.GetFileName(outputPath)
                : BuildFallbackFileName(fileName, title);

            var fileExists = !string.IsNullOrWhiteSpace(outputPath) && File.Exists(outputPath);
            var fileSize = fileExists ? new FileInfo(outputPath!).Length : 0L;
            string? writeFileError = null;
            var fileWritable = fileExists && IsFileWritable(outputPath!, out writeFileError);

            await LogAsync("info", "youtube.output.verify", requestId, new
            {
                outputPath = outputPath ?? string.Empty,
                fileExists,
                fileSize,
                fileWritable,
                writeFileError = writeFileError ?? string.Empty
            });
            await LogYouTubeDebugAsync("youtube.output.verify", requestId, new
            {
                outputPath = outputPath ?? string.Empty,
                fileExists,
                fileSize,
                fileWritable,
                writeFileError = writeFileError ?? string.Empty
            });

            if (!fileExists)
            {
                return AddRequestId(new NativeMessage
                {
                    Type = "error",
                    Payload = new Dictionary<string, object>
                    {
                        { "message", "yt-dlp reported success but output file was not found at expected path." },
                        { "code", "YTDLP_OUTPUT_NOT_FOUND" },
                        { "expectedPath", outputPath ?? string.Empty }
                    }
                }, requestId);
            }

            await LogAsync("info", "youtube.download.complete", requestId, new
            {
                videoUrl,
                runner = runnerUsed,
                mode = modeUsed ?? string.Empty,
                outputPath = outputPath ?? string.Empty,
                quality = quality ?? string.Empty
            });

            return AddRequestId(new NativeMessage
            {
                Type = "download_added",
                Payload = new Dictionary<string, object>
                {
                    { "downloadId", Guid.NewGuid().ToString("N") },
                    { "fileName", resolvedFileName },
                    { "status", "Complete" },
                    { "quality", quality ?? string.Empty },
                    { "source", "yt-dlp" },
                    { "runner", runnerUsed },
                    { "mode", modeUsed ?? string.Empty },
                    { "outputPath", outputPath ?? string.Empty },
                    { "fileExists", fileExists },
                    { "fileSize", fileSize },
                    { "fileWritable", fileWritable }
                }
            }, requestId);
        }
        catch (Exception ex)
        {
            await LogAsync("error", "youtube.download.failed", requestId, new
            {
                videoUrl,
                exception = ex.GetType().Name,
                ex.Message
            });

            return AddRequestId(new NativeMessage
            {
                Type = "error",
                Payload = new Dictionary<string, object>
                {
                    { "message", ex.Message },
                    { "code", "YOUTUBE_DOWNLOAD_FAILED" }
                }
            }, requestId);
        }
    }

    private static bool IsYouTubePageUrl(string url)
    {
        try
        {
            var host = new Uri(url).Host.ToLowerInvariant();
            return host.EndsWith("youtube.com")
                || host == "youtu.be"
                || host == "m.youtube.com"
                || host.EndsWith("youtube-nocookie.com");
        }
        catch
        {
            return false;
        }
    }

    private static string? NormalizeYouTubeUrl(string? inputUrl)
    {
        if (string.IsNullOrWhiteSpace(inputUrl))
        {
            return null;
        }

        Uri parsed;
        try
        {
            parsed = new Uri(inputUrl);
        }
        catch
        {
            return null;
        }

        var host = parsed.Host.ToLowerInvariant();
        var isYoutubeHost = host.EndsWith("youtube.com")
            || host == "youtu.be"
            || host == "m.youtube.com"
            || host.EndsWith("youtube-nocookie.com");
        if (!isYoutubeHost)
        {
            return null;
        }

        static string ToWatchUrl(string id) => $"https://www.youtube.com/watch?v={Uri.EscapeDataString(id)}";

        if (host == "youtu.be")
        {
            var id = parsed.AbsolutePath.Trim('/').Split('/').FirstOrDefault();
            return string.IsNullOrWhiteSpace(id) ? null : ToWatchUrl(id);
        }

        if (parsed.AbsolutePath.Equals("/redirect", StringComparison.OrdinalIgnoreCase))
        {
            var q = GetQueryParam(parsed.Query, "q");
            return NormalizeYouTubeUrl(q);
        }

        if (parsed.AbsolutePath.Equals("/watch", StringComparison.OrdinalIgnoreCase))
        {
            var id = GetQueryParam(parsed.Query, "v");
            return string.IsNullOrWhiteSpace(id) ? null : ToWatchUrl(id);
        }

        var parts = parsed.AbsolutePath.Trim('/').Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length >= 2 &&
            (parts[0].Equals("shorts", StringComparison.OrdinalIgnoreCase)
            || parts[0].Equals("embed", StringComparison.OrdinalIgnoreCase)
            || parts[0].Equals("live", StringComparison.OrdinalIgnoreCase)))
        {
            return ToWatchUrl(parts[1]);
        }

        // Keep unknown YouTube URL shapes (playlist, channel live pages, etc.) so yt-dlp can try them.
        return parsed.GetComponents(UriComponents.SchemeAndServer | UriComponents.PathAndQuery, UriFormat.UriEscaped);
    }

    private static string? GetQueryParam(string query, string key)
    {
        if (string.IsNullOrWhiteSpace(query))
        {
            return null;
        }

        var trim = query.TrimStart('?');
        var pairs = trim.Split('&', StringSplitOptions.RemoveEmptyEntries);
        foreach (var pair in pairs)
        {
            var idx = pair.IndexOf('=');
            if (idx <= 0)
            {
                continue;
            }

            var k = Uri.UnescapeDataString(pair[..idx]);
            if (!k.Equals(key, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            return Uri.UnescapeDataString(pair[(idx + 1)..]);
        }

        return null;
    }

    private static bool IsDirectoryWritable(string directoryPath, out string? error)
    {
        try
        {
            Directory.CreateDirectory(directoryPath);
            var probePath = Path.Combine(directoryPath, $".mydm_write_probe_{Guid.NewGuid():N}.tmp");
            File.WriteAllText(probePath, "probe");
            File.Delete(probePath);
            error = null;
            return true;
        }
        catch (Exception ex)
        {
            error = ex.Message;
            return false;
        }
    }

    private static bool IsFileWritable(string filePath, out string? error)
    {
        try
        {
            using var stream = new FileStream(filePath, FileMode.Open, FileAccess.ReadWrite, FileShare.Read);
            error = null;
            return stream.CanWrite;
        }
        catch (Exception ex)
        {
            error = ex.Message;
            return false;
        }
    }

    private static string ResolveVideoSaveDirectory()
    {
        var basePath = _repository?.GetSetting("DefaultSavePath")
            ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads", "MyDM");

        var videoFolder = _repository?
            .GetCategories()
            .FirstOrDefault(c => string.Equals(c.Name, "Video", StringComparison.OrdinalIgnoreCase))
            ?.SaveFolder;

        var targetFolder = string.IsNullOrWhiteSpace(videoFolder) ? "Videos" : videoFolder;
        var invalidChars = Path.GetInvalidFileNameChars();
        var safeFolder = new string(targetFolder.Select(ch => invalidChars.Contains(ch) ? '_' : ch).ToArray());
        return Path.Combine(basePath, safeFolder);
    }

    private static string BuildYouTubeOutputTemplate(string saveDir, string? fileName, string? title)
    {
        var preferredName = !string.IsNullOrWhiteSpace(fileName)
            ? Path.GetFileNameWithoutExtension(fileName)
            : !string.IsNullOrWhiteSpace(title)
                ? title
                : null;

        if (!string.IsNullOrWhiteSpace(preferredName))
        {
            var safeBase = UrlHelper.SanitizeFileName(preferredName).Replace("%", "_");
            return Path.Combine(saveDir, $"{safeBase}.%(ext)s");
        }

        return Path.Combine(saveDir, "%(title).180B [%(id)s].%(ext)s");
    }

    private static string BuildYouTubeFormatSelector(string? quality)
    {
        if (string.IsNullOrWhiteSpace(quality))
        {
            return "best[ext=mp4][vcodec!=none][acodec!=none]/best[vcodec!=none][acodec!=none]/best";
        }

        var digits = new string(quality.Where(char.IsDigit).ToArray());
        if (!int.TryParse(digits, out var maxHeight) || maxHeight <= 0)
        {
            return "best[ext=mp4][vcodec!=none][acodec!=none]/best[vcodec!=none][acodec!=none]/best";
        }

        return $"best[height<={maxHeight}][ext=mp4][vcodec!=none][acodec!=none]/best[height<={maxHeight}][vcodec!=none][acodec!=none]/best[ext=mp4][vcodec!=none][acodec!=none]/best[vcodec!=none][acodec!=none]/best";
    }

    private static List<string> BuildYtDlpArgs(
        string videoUrl,
        string outputTemplate,
        string formatSelector,
        string? referrer,
        IReadOnlyDictionary<string, string>? headers,
        bool includeCookieHeader,
        string? cookiesFromBrowser)
    {
        var args = new List<string>
        {
            "--no-playlist",
            "--ignore-config",
            "--verbose",
            "--no-progress",
            "--newline",
            "--restrict-filenames",
            "--print", "after_move:filepath",
            "-o", outputTemplate,
            "-f", formatSelector
        };

        if (!string.IsNullOrWhiteSpace(cookiesFromBrowser))
        {
            args.Add("--cookies-from-browser");
            args.Add(cookiesFromBrowser);
        }

        if (!string.IsNullOrWhiteSpace(referrer))
        {
            args.Add("--referer");
            args.Add(referrer);
        }
        else
        {
            args.Add("--referer");
            args.Add("https://www.youtube.com/");
        }

        var allHeaders = headers != null
            ? new Dictionary<string, string>(headers, StringComparer.OrdinalIgnoreCase)
            : new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        if (!allHeaders.ContainsKey("Origin"))
        {
            allHeaders["Origin"] = "https://www.youtube.com";
        }

        foreach (var header in allHeaders)
        {
            if (string.IsNullOrWhiteSpace(header.Value))
            {
                continue;
            }

            if (string.Equals(header.Key, "User-Agent", StringComparison.OrdinalIgnoreCase))
            {
                args.Add("--user-agent");
                args.Add(header.Value);
                continue;
            }

            if (string.Equals(header.Key, "Referer", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (!includeCookieHeader &&
                string.Equals(header.Key, "Cookie", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            args.Add("--add-header");
            args.Add($"{header.Key}:{header.Value}");
        }

        args.Add(videoUrl);
        return args;
    }

    private static IReadOnlyList<YtDlpRunnerSpec> ResolveYtDlpRunnerSpecs()
    {
        var runners = new List<YtDlpRunnerSpec>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        static string MakeKey(string executable, IReadOnlyList<string> prefixArgs)
            => $"{executable}|{string.Join(" ", prefixArgs)}";

        void AddRunner(string executable, IReadOnlyList<string> prefixArgs, string displayName)
        {
            if (string.IsNullOrWhiteSpace(executable))
            {
                return;
            }

            var key = MakeKey(executable, prefixArgs);
            if (!seen.Add(key))
            {
                return;
            }

            runners.Add(new YtDlpRunnerSpec(executable, prefixArgs, displayName));
        }

        AddRunner("yt-dlp", Array.Empty<string>(), "yt-dlp");
        AddRunner("yt-dlp.exe", Array.Empty<string>(), "yt-dlp.exe");
        AddRunner("python", new[] { "-m", "yt_dlp" }, "python -m yt_dlp");
        AddRunner("py", new[] { "-m", "yt_dlp" }, "py -m yt_dlp");

        foreach (var discovered in DiscoverYtDlpExecutables())
        {
            AddRunner(discovered, Array.Empty<string>(), discovered);
        }

        return runners;
    }

    private static IReadOnlyList<string> DiscoverYtDlpExecutables()
    {
        var found = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        void AddIfExists(string? path)
        {
            if (string.IsNullOrWhiteSpace(path))
            {
                return;
            }

            try
            {
                if (File.Exists(path))
                {
                    found.Add(path);
                }
            }
            catch
            {
                // ignore bad candidate path
            }
        }

        var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        var appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        var userProfile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);

        AddIfExists(Environment.GetEnvironmentVariable("YTDLP_PATH"));
        AddIfExists(Path.Combine(AppContext.BaseDirectory, "yt-dlp.exe"));
        AddIfExists(Path.Combine(localAppData, "Microsoft", "WinGet", "Links", "yt-dlp.exe"));
        AddIfExists(Path.Combine(userProfile, "scoop", "shims", "yt-dlp.exe"));

        void AddPythonScriptsCandidates(string rootDir)
        {
            if (string.IsNullOrWhiteSpace(rootDir) || !Directory.Exists(rootDir))
            {
                return;
            }

            try
            {
                foreach (var pythonDir in Directory.EnumerateDirectories(rootDir, "Python*"))
                {
                    AddIfExists(Path.Combine(pythonDir, "Scripts", "yt-dlp.exe"));
                }
            }
            catch
            {
                // ignore directory scan failures
            }
        }

        AddPythonScriptsCandidates(Path.Combine(localAppData, "Programs", "Python"));
        AddPythonScriptsCandidates(Path.Combine(appData, "Python"));

        return found.ToArray();
    }

    private static string BuildFallbackFileName(string? fileName, string? title)
    {
        if (!string.IsNullOrWhiteSpace(fileName))
        {
            return UrlHelper.SanitizeFileName(Path.GetFileName(fileName));
        }

        if (!string.IsNullOrWhiteSpace(title))
        {
            return UrlHelper.SanitizeFileName(title) + ".mp4";
        }

        return "youtube_video.mp4";
    }

    private static string? ResolveLikelyOutputPath(string saveDir, string? fileName, string? title)
    {
        try
        {
            if (!Directory.Exists(saveDir))
            {
                return null;
            }

            var preferredName = !string.IsNullOrWhiteSpace(fileName)
                ? Path.GetFileNameWithoutExtension(fileName)
                : !string.IsNullOrWhiteSpace(title)
                    ? title
                    : null;

            if (!string.IsNullOrWhiteSpace(preferredName))
            {
                var safeBase = UrlHelper.SanitizeFileName(preferredName).Replace("%", "_");
                var files = Directory.GetFiles(saveDir, $"{safeBase}.*", SearchOption.TopDirectoryOnly);
                var newest = files
                    .Select(path => new FileInfo(path))
                    .OrderByDescending(info => info.LastWriteTimeUtc)
                    .FirstOrDefault();
                if (newest != null)
                {
                    return newest.FullName;
                }
            }

            var fallbackNewest = Directory.GetFiles(saveDir, "*.*", SearchOption.TopDirectoryOnly)
                .Select(path => new FileInfo(path))
                .OrderByDescending(info => info.LastWriteTimeUtc)
                .FirstOrDefault();
            return fallbackNewest?.FullName;
        }
        catch
        {
            return null;
        }
    }

    private static string? TryExtractOutputPath(string processOutput, string saveDir)
    {
        var lines = processOutput
            .Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries)
            .Select(line => line.Trim())
            .Where(line => !string.IsNullOrWhiteSpace(line))
            .ToArray();

        for (var i = lines.Length - 1; i >= 0; i--)
        {
            var line = lines[i];

            // after_move:filepath prints the final absolute path as a plain line
            if (Path.IsPathRooted(line))
            {
                if (File.Exists(line))
                {
                    return line;
                }

                continue;
            }

            var combined = Path.Combine(saveDir, line);
            if (File.Exists(combined))
            {
                return combined;
            }
        }

        return null;
    }

    private static string BuildCommandLineForLog(string fileName, IReadOnlyList<string> args)
    {
        var quotedArgs = args.Select(arg =>
        {
            if (string.IsNullOrEmpty(arg))
            {
                return "\"\"";
            }

            if (arg.Contains(' ') || arg.Contains('"'))
            {
                return $"\"{arg.Replace("\"", "\\\"")}\"";
            }

            return arg;
        });
        return $"{fileName} {string.Join(" ", quotedArgs)}";
    }

    private static string Tail(string value, int maxLength)
    {
        if (string.IsNullOrEmpty(value))
        {
            return string.Empty;
        }

        return value.Length <= maxLength
            ? value
            : value[^maxLength..];
    }

    private static async Task<ProcessExecutionResult> ExecuteProcessAsync(
        string fileName,
        IReadOnlyList<string> args,
        int timeoutMs)
    {
        var stdout = new StringBuilder();
        var stderr = new StringBuilder();
        using var process = new Process();
        process.StartInfo = new ProcessStartInfo
        {
            FileName = fileName,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true
        };

        foreach (var arg in args)
        {
            process.StartInfo.ArgumentList.Add(arg);
        }

        process.OutputDataReceived += (_, eventArgs) =>
        {
            if (!string.IsNullOrWhiteSpace(eventArgs.Data))
            {
                stdout.AppendLine(eventArgs.Data);
            }
        };
        process.ErrorDataReceived += (_, eventArgs) =>
        {
            if (!string.IsNullOrWhiteSpace(eventArgs.Data))
            {
                stderr.AppendLine(eventArgs.Data);
            }
        };

        try
        {
            if (!process.Start())
            {
                return new ProcessExecutionResult(false, -1, stdout.ToString(), stderr.ToString(), "Process failed to start.");
            }
        }
        catch (Win32Exception ex)
        {
            return new ProcessExecutionResult(false, -1, stdout.ToString(), stderr.ToString(), ex.Message);
        }

        process.BeginOutputReadLine();
        process.BeginErrorReadLine();

        using var timeoutCts = new CancellationTokenSource(timeoutMs);
        try
        {
            await process.WaitForExitAsync(timeoutCts.Token);
        }
        catch (OperationCanceledException)
        {
            try
            {
                if (!process.HasExited)
                {
                    process.Kill(entireProcessTree: true);
                }
            }
            catch
            {
                // ignore kill errors
            }

            return new ProcessExecutionResult(true, -1, stdout.ToString(), stderr.ToString(), $"Process timed out after {timeoutMs / 1000}s.");
        }

        return new ProcessExecutionResult(true, process.ExitCode, stdout.ToString(), stderr.ToString(), null);
    }

    private readonly record struct YtDlpRunnerSpec(
        string Executable,
        IReadOnlyList<string> PrefixArgs,
        string DisplayName);

    private readonly record struct ProcessExecutionResult(
        bool Started,
        int ExitCode,
        string Stdout,
        string Stderr,
        string? StartError)
    {
        public string CombinedOutput => string.Join(
            Environment.NewLine,
            new[] { Stdout, Stderr }.Where(part => !string.IsNullOrWhiteSpace(part)));
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

    private static async Task LogYouTubeDebugAsync(string eventName, string? requestId = null, object? data = null)
    {
        if (string.IsNullOrWhiteSpace(_ytDebugLogPath))
        {
            return;
        }

        var entry = new Dictionary<string, object?>
        {
            ["ts"] = DateTime.UtcNow.ToString("O"),
            ["event"] = eventName,
            ["requestId"] = requestId,
            ["data"] = data
        };

        var line = JsonSerializer.Serialize(entry);
        await _logLock.WaitAsync();
        try
        {
            await File.AppendAllTextAsync(_ytDebugLogPath, line + Environment.NewLine);
        }
        finally
        {
            _logLock.Release();
        }
    }
}
