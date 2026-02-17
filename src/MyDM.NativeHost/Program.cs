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
class Program
{
    private static DownloadEngine? _engine;
    private static DownloadRepository? _repository;
    private static MyDMDatabase? _database;

    static async Task Main(string[] args)
    {
        // Initialize database and engine
        var dbPath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "MyDM", "mydm.db");
        Directory.CreateDirectory(Path.GetDirectoryName(dbPath)!);

        _database = new MyDMDatabase(dbPath);
        _database.Initialize();
        _repository = new DownloadRepository(_database);
        _engine = new DownloadEngine(_repository);
        _engine.RestoreState();

        // Log startup
        var logPath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "MyDM", "nativehost.log");
        await File.AppendAllTextAsync(logPath, $"[{DateTime.UtcNow:o}] Native Host started\n");

        // Read messages from stdin
        var stdin = Console.OpenStandardInput();
        var stdout = Console.OpenStandardOutput();

        while (true)
        {
            try
            {
                var message = await ReadMessageAsync(stdin);
                if (message == null) break;

                var response = await HandleMessageAsync(message);
                await WriteMessageAsync(stdout, response);
            }
            catch (Exception ex)
            {
                var error = new NativeMessage
                {
                    Type = "error",
                    Payload = new Dictionary<string, object> { { "message", ex.Message }, { "code", "INTERNAL_ERROR" } }
                };
                await WriteMessageAsync(stdout, error);
            }
        }

        _engine.Dispose();
        _database.Dispose();
    }

    private static async Task<NativeMessage?> ReadMessageAsync(Stream stdin)
    {
        // Read 4-byte length prefix (little-endian)
        var lengthBytes = new byte[4];
        var bytesRead = await stdin.ReadAsync(lengthBytes);
        if (bytesRead == 0) return null;
        if (bytesRead < 4) throw new Exception("Invalid message length prefix");

        var length = BitConverter.ToInt32(lengthBytes, 0);
        if (length <= 0 || length > 1024 * 1024) // 1MB max
            throw new Exception($"Invalid message length: {length}");

        // Read the message body
        var buffer = new byte[length];
        var totalRead = 0;
        while (totalRead < length)
        {
            var read = await stdin.ReadAsync(buffer.AsMemory(totalRead, length - totalRead));
            if (read == 0) throw new Exception("Unexpected end of stream");
            totalRead += read;
        }

        var json = Encoding.UTF8.GetString(buffer);
        return JsonSerializer.Deserialize<NativeMessage>(json);
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

    private static async Task<NativeMessage> HandleMessageAsync(NativeMessage message)
    {
        return message.Type switch
        {
            "ping" => new NativeMessage
            {
                Type = "pong",
                Payload = new Dictionary<string, object> { { "version", "1.0.0" }, { "status", "connected" } }
            },
            "add_download" => await HandleAddDownloadAsync(message),
            "add_media_download" => await HandleAddMediaDownloadAsync(message),
            "get_status" => HandleGetStatus(message),
            _ => new NativeMessage
            {
                Type = "error",
                Payload = new Dictionary<string, object> { { "message", $"Unknown message type: {message.Type}" }, { "code", "UNKNOWN_TYPE" } }
            }
        };
    }

    private static async Task<NativeMessage> HandleAddDownloadAsync(NativeMessage message)
    {
        var url = message.Payload.GetValueOrDefault("url")?.ToString();
        if (string.IsNullOrEmpty(url) || !UrlHelper.IsValidUrl(url))
        {
            return new NativeMessage
            {
                Type = "error",
                Payload = new Dictionary<string, object> { { "message", "Invalid URL" }, { "code", "INVALID_URL" } }
            };
        }

        var fileName = message.Payload.GetValueOrDefault("filename")?.ToString();
        var category = message.Payload.GetValueOrDefault("category")?.ToString();

        var item = await _engine!.AddDownloadAsync(url, fileName: fileName, category: category);
        await _engine.StartDownloadAsync(item.Id);

        return new NativeMessage
        {
            Type = "download_added",
            Payload = new Dictionary<string, object>
            {
                { "downloadId", item.Id },
                { "fileName", item.FileName },
                { "size", item.TotalSize }
            }
        };
    }

    private static async Task<NativeMessage> HandleAddMediaDownloadAsync(NativeMessage message)
    {
        var manifestUrl = message.Payload.GetValueOrDefault("manifestUrl")?.ToString();
        var mediaType = message.Payload.GetValueOrDefault("mediaType")?.ToString();
        var quality = message.Payload.GetValueOrDefault("quality")?.ToString();
        var title = message.Payload.GetValueOrDefault("title")?.ToString();

        if (string.IsNullOrEmpty(manifestUrl))
        {
            return new NativeMessage
            {
                Type = "error",
                Payload = new Dictionary<string, object> { { "message", "Missing manifest URL" }, { "code", "MISSING_MANIFEST" } }
            };
        }

        var item = await _engine!.AddDownloadAsync(
            manifestUrl,
            fileName: title != null ? UrlHelper.SanitizeFileName(title) + ".mp4" : null,
            category: "Video");

        item.MediaType = mediaType?.ToLower() == "dash" ? MediaType.Dash : MediaType.Hls;
        item.ManifestUrl = manifestUrl;
        item.SelectedQuality = quality;
        _repository!.Update(item);

        await _engine.StartDownloadAsync(item.Id);

        return new NativeMessage
        {
            Type = "download_added",
            Payload = new Dictionary<string, object>
            {
                { "downloadId", item.Id },
                { "fileName", item.FileName }
            }
        };
    }

    private static NativeMessage HandleGetStatus(NativeMessage message)
    {
        var downloadId = message.Payload.GetValueOrDefault("downloadId")?.ToString();

        if (!string.IsNullOrEmpty(downloadId))
        {
            var item = _engine!.GetDownload(downloadId);
            if (item == null)
            {
                return new NativeMessage
                {
                    Type = "error",
                    Payload = new Dictionary<string, object> { { "message", "Download not found" }, { "code", "NOT_FOUND" } }
                };
            }

            return new NativeMessage
            {
                Type = "status_update",
                Payload = new Dictionary<string, object>
                {
                    { "downloads", new[] { DownloadToDict(item) } }
                }
            };
        }

        var downloads = _engine!.GetAllDownloads().Select(DownloadToDict).ToArray();
        return new NativeMessage
        {
            Type = "status_update",
            Payload = new Dictionary<string, object> { { "downloads", downloads } }
        };
    }

    private static Dictionary<string, object> DownloadToDict(DownloadItem item) => new()
    {
        { "id", item.Id },
        { "fileName", item.FileName },
        { "size", item.TotalSize },
        { "downloaded", item.DownloadedSize },
        { "status", item.Status.ToString() },
        { "progress", Math.Round(item.ProgressPercent, 1) }
    };
}
