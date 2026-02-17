using MyDM.Core.Models;

namespace MyDM.Core.Parsers;

public static class MimeDetector
{
    private static readonly Dictionary<string, string> ExtensionToCategory = new(StringComparer.OrdinalIgnoreCase)
    {
        // Video
        { ".mp4", "Video" }, { ".mkv", "Video" }, { ".avi", "Video" }, { ".mov", "Video" },
        { ".wmv", "Video" }, { ".flv", "Video" }, { ".webm", "Video" }, { ".m4v", "Video" },
        { ".3gp", "Video" }, { ".ts", "Video" }, { ".m3u8", "Video" }, { ".mpd", "Video" },
        // Music
        { ".mp3", "Music" }, { ".flac", "Music" }, { ".wav", "Music" }, { ".aac", "Music" },
        { ".ogg", "Music" }, { ".wma", "Music" }, { ".m4a", "Music" }, { ".opus", "Music" },
        // Documents
        { ".pdf", "Documents" }, { ".doc", "Documents" }, { ".docx", "Documents" },
        { ".xls", "Documents" }, { ".xlsx", "Documents" }, { ".ppt", "Documents" },
        { ".pptx", "Documents" }, { ".txt", "Documents" }, { ".rtf", "Documents" },
        { ".odt", "Documents" }, { ".csv", "Documents" },
        // Programs
        { ".exe", "Programs" }, { ".msi", "Programs" }, { ".dmg", "Programs" },
        { ".deb", "Programs" }, { ".rpm", "Programs" }, { ".apk", "Programs" },
        { ".appx", "Programs" }, { ".bat", "Programs" }, { ".sh", "Programs" },
        // Compressed
        { ".zip", "Compressed" }, { ".rar", "Compressed" }, { ".7z", "Compressed" },
        { ".tar", "Compressed" }, { ".gz", "Compressed" }, { ".bz2", "Compressed" },
        { ".xz", "Compressed" }, { ".iso", "Compressed" }, { ".cab", "Compressed" },
    };

    private static readonly Dictionary<string, string> MimePrefixToCategory = new(StringComparer.OrdinalIgnoreCase)
    {
        { "video/", "Video" },
        { "audio/", "Music" },
        { "application/pdf", "Documents" },
        { "application/msword", "Documents" },
        { "application/vnd.openxmlformats", "Documents" },
        { "text/plain", "Documents" },
        { "text/csv", "Documents" },
        { "application/x-msdownload", "Programs" },
        { "application/x-executable", "Programs" },
        { "application/zip", "Compressed" },
        { "application/x-rar", "Compressed" },
        { "application/x-7z-compressed", "Compressed" },
        { "application/gzip", "Compressed" },
        { "application/x-tar", "Compressed" },
        { "application/x-iso9660-image", "Compressed" },
        { "application/vnd.apple.mpegurl", "Video" },
        { "application/x-mpegurl", "Video" },
        { "application/dash+xml", "Video" },
    };

    /// <summary>
    /// Detect category from file extension.
    /// </summary>
    public static string DetectFromExtension(string extension)
    {
        if (string.IsNullOrEmpty(extension)) return "Others";
        if (!extension.StartsWith('.')) extension = "." + extension;
        return ExtensionToCategory.TryGetValue(extension, out var cat) ? cat : "Others";
    }

    /// <summary>
    /// Detect category from MIME type.
    /// </summary>
    public static string DetectFromMime(string mimeType)
    {
        if (string.IsNullOrEmpty(mimeType)) return "Others";

        foreach (var kvp in MimePrefixToCategory)
        {
            if (mimeType.StartsWith(kvp.Key, StringComparison.OrdinalIgnoreCase))
                return kvp.Value;
        }
        return "Others";
    }

    /// <summary>
    /// Detect category using both extension and MIME type, preferring extension.
    /// </summary>
    public static string Detect(string? url, string? mimeType)
    {
        if (!string.IsNullOrEmpty(url))
        {
            try
            {
                var ext = Path.GetExtension(new Uri(url).AbsolutePath);
                var cat = DetectFromExtension(ext);
                if (cat != "Others") return cat;
            }
            catch { /* ignore */ }
        }

        if (!string.IsNullOrEmpty(mimeType))
        {
            var cat = DetectFromMime(mimeType);
            if (cat != "Others") return cat;
        }

        return "Others";
    }

    /// <summary>
    /// Check if the URL or MIME indicates a downloadable resource.
    /// </summary>
    public static bool IsDownloadable(string? url, string? mimeType)
    {
        return Detect(url, mimeType) != "Others"
               || (!string.IsNullOrEmpty(mimeType) && mimeType.StartsWith("application/octet-stream"));
    }
}
