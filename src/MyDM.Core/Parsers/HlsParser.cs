using System.Text.RegularExpressions;
using MyDM.Core.Models;

namespace MyDM.Core.Parsers;

public static class HlsParser
{
    /// <summary>
    /// Parse an HLS master playlist and extract variant streams with their quality info.
    /// </summary>
    public static List<MediaQuality> ParseMasterPlaylist(string content, string baseUrl)
    {
        var qualities = new List<MediaQuality>();
        var lines = content.Split('\n', StringSplitOptions.RemoveEmptyEntries);

        for (int i = 0; i < lines.Length; i++)
        {
            var line = lines[i].Trim();
            if (!line.StartsWith("#EXT-X-STREAM-INF:")) continue;

            var bandwidth = ExtractAttribute(line, "BANDWIDTH");
            var resolution = ExtractAttribute(line, "RESOLUTION");
            var codecs = ExtractAttribute(line, "CODECS");

            // Next non-comment line is the URL
            string? url = null;
            for (int j = i + 1; j < lines.Length; j++)
            {
                var nextLine = lines[j].Trim();
                if (string.IsNullOrEmpty(nextLine) || nextLine.StartsWith("#")) continue;
                url = ResolveUrl(nextLine, baseUrl);
                break;
            }

            if (url != null)
            {
                qualities.Add(new MediaQuality
                {
                    Resolution = resolution ?? "Unknown",
                    Bandwidth = long.TryParse(bandwidth, out var bw) ? bw : 0,
                    Codecs = codecs,
                    Url = url
                });
            }
        }

        return qualities.OrderByDescending(q => q.Bandwidth).ToList();
    }

    /// <summary>
    /// Parse a media playlist and extract segment URLs.
    /// </summary>
    public static List<string> ParseMediaPlaylist(string content, string baseUrl)
    {
        var segments = new List<string>();
        var lines = content.Split('\n', StringSplitOptions.RemoveEmptyEntries);

        foreach (var rawLine in lines)
        {
            var line = rawLine.Trim();
            if (string.IsNullOrEmpty(line) || line.StartsWith("#"))
                continue;

            segments.Add(ResolveUrl(line, baseUrl));
        }

        return segments;
    }

    /// <summary>
    /// Checks if content is a master playlist (contains stream info tags).
    /// </summary>
    public static bool IsMasterPlaylist(string content)
    {
        return content.Contains("#EXT-X-STREAM-INF:");
    }

    /// <summary>
    /// Checks if content is a valid HLS playlist.
    /// </summary>
    public static bool IsHlsPlaylist(string content)
    {
        return content.TrimStart().StartsWith("#EXTM3U");
    }

    private static string? ExtractAttribute(string line, string attribute)
    {
        // Handle quoted values like CODECS="avc1.4d401e,mp4a.40.2"
        var quotedPattern = $@"{attribute}=""([^""]*)""";
        var quotedMatch = Regex.Match(line, quotedPattern);
        if (quotedMatch.Success) return quotedMatch.Groups[1].Value;

        // Handle unquoted values like BANDWIDTH=2560000
        var pattern = $@"{attribute}=([^,\s]+)";
        var match = Regex.Match(line, pattern);
        return match.Success ? match.Groups[1].Value : null;
    }

    private static string ResolveUrl(string url, string baseUrl)
    {
        if (Uri.IsWellFormedUriString(url, UriKind.Absolute))
            return url;

        try
        {
            var baseUri = new Uri(baseUrl);
            return new Uri(baseUri, url).AbsoluteUri;
        }
        catch
        {
            return url;
        }
    }
}
