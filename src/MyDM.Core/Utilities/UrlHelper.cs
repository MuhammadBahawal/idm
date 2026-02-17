namespace MyDM.Core.Utilities;

public static class UrlHelper
{
    /// <summary>
    /// Extracts a clean filename from a URL, stripping query strings and fragments.
    /// </summary>
    public static string ExtractFileName(string url)
    {
        try
        {
            var uri = new Uri(url);
            var path = uri.AbsolutePath;
            var fileName = Path.GetFileName(path);

            if (string.IsNullOrWhiteSpace(fileName) || !fileName.Contains('.'))
            {
                // Generate a name from URL hash
                fileName = $"download_{Math.Abs(url.GetHashCode()):X8}";
            }

            return SanitizeFileName(Uri.UnescapeDataString(fileName));
        }
        catch
        {
            return $"download_{DateTime.UtcNow:yyyyMMddHHmmss}";
        }
    }

    /// <summary>
    /// Sanitizes a filename by removing invalid characters and preventing path traversal.
    /// </summary>
    public static string SanitizeFileName(string fileName)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var sanitized = string.Concat(fileName.Where(c => !invalid.Contains(c)));

        // Prevent path traversal
        sanitized = sanitized.Replace("..", "")
                             .Replace("/", "")
                             .Replace("\\", "");

        // Limit length
        if (sanitized.Length > 200)
        {
            var ext = Path.GetExtension(sanitized);
            sanitized = sanitized[..(200 - ext.Length)] + ext;
        }

        return string.IsNullOrWhiteSpace(sanitized) ? $"download_{DateTime.UtcNow.Ticks}" : sanitized;
    }

    /// <summary>
    /// Validates whether a string is a well-formed HTTP/HTTPS URL.
    /// </summary>
    public static bool IsValidUrl(string url)
    {
        return Uri.TryCreate(url, UriKind.Absolute, out var uri)
               && (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps);
    }

    /// <summary>
    /// Extracts file extension from a URL.
    /// </summary>
    public static string GetExtension(string url)
    {
        try
        {
            var uri = new Uri(url);
            var ext = Path.GetExtension(uri.AbsolutePath);
            return ext.ToLowerInvariant();
        }
        catch
        {
            return string.Empty;
        }
    }
}
