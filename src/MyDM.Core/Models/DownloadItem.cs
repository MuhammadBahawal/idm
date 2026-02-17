namespace MyDM.Core.Models;

public class DownloadItem
{
    public string Id { get; set; } = Guid.NewGuid().ToString();
    public string Url { get; set; } = string.Empty;
    public string FileName { get; set; } = string.Empty;
    public string SavePath { get; set; } = string.Empty;
    public string Category { get; set; } = "Others";
    public DownloadStatus Status { get; set; } = DownloadStatus.Queued;
    public long TotalSize { get; set; }
    public long DownloadedSize { get; set; }
    public int Connections { get; set; } = 8;
    public long SpeedLimit { get; set; } // bytes/sec, 0 = unlimited
    public string? Checksum { get; set; }
    public bool ChecksumVerified { get; set; }
    public string? Description { get; set; }
    public MediaType MediaType { get; set; } = MediaType.Direct;
    public string? ManifestUrl { get; set; }
    public string? SelectedQuality { get; set; }
    public string? ErrorMessage { get; set; }
    public int RetryCount { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime? CompletedAt { get; set; }
    public DateTime? LastAttemptAt { get; set; }
    public bool SupportsRange { get; set; } = true;
    public double TransferRate { get; set; } // bytes/sec current
    public TimeSpan? TimeLeft { get; set; }

    public string FullPath => Path.Combine(SavePath, FileName);
    public string PartFilePath => FullPath + ".part";
    public double ProgressPercent => TotalSize > 0 ? (double)DownloadedSize / TotalSize * 100 : 0;

    public List<DownloadSegment> Segments { get; set; } = new();
}
