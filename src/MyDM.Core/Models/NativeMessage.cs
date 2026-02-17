using System.Text.Json.Serialization;

namespace MyDM.Core.Models;

public class NativeMessage
{
    [JsonPropertyName("type")]
    public string Type { get; set; } = string.Empty;

    [JsonPropertyName("payload")]
    public Dictionary<string, object> Payload { get; set; } = new();
}

public class AddDownloadPayload
{
    [JsonPropertyName("url")]
    public string Url { get; set; } = string.Empty;

    [JsonPropertyName("filename")]
    public string? FileName { get; set; }

    [JsonPropertyName("category")]
    public string? Category { get; set; }

    [JsonPropertyName("referrer")]
    public string? Referrer { get; set; }

    [JsonPropertyName("cookies")]
    public string? Cookies { get; set; }
}

public class AddMediaDownloadPayload
{
    [JsonPropertyName("manifestUrl")]
    public string ManifestUrl { get; set; } = string.Empty;

    [JsonPropertyName("mediaType")]
    public string MediaType { get; set; } = string.Empty;

    [JsonPropertyName("quality")]
    public string? Quality { get; set; }

    [JsonPropertyName("title")]
    public string? Title { get; set; }

    [JsonPropertyName("referrer")]
    public string? Referrer { get; set; }
}

public class MediaQuality
{
    [JsonPropertyName("resolution")]
    public string Resolution { get; set; } = string.Empty;

    [JsonPropertyName("bandwidth")]
    public long Bandwidth { get; set; }

    [JsonPropertyName("codecs")]
    public string? Codecs { get; set; }

    [JsonPropertyName("url")]
    public string Url { get; set; } = string.Empty;

    public override string ToString() => $"{Resolution} ({Bandwidth / 1000}kbps)";
}

public class DownloadLog
{
    public int Id { get; set; }
    public string DownloadId { get; set; } = string.Empty;
    public DateTime Timestamp { get; set; } = DateTime.UtcNow;
    public string Level { get; set; } = "Info";
    public string Message { get; set; } = string.Empty;
}
