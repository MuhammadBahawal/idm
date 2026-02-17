namespace MyDM.Core.Models;

public class DownloadSegment
{
    public string Id { get; set; } = Guid.NewGuid().ToString();
    public string DownloadId { get; set; } = string.Empty;
    public int Index { get; set; }
    public long StartByte { get; set; }
    public long EndByte { get; set; }
    public long DownloadedBytes { get; set; }
    public SegmentStatus Status { get; set; } = SegmentStatus.Pending;
    public string? TempFile { get; set; }

    public long TotalBytes => EndByte - StartByte + 1;
    public long RemainingBytes => TotalBytes - DownloadedBytes;
    public double ProgressPercent => TotalBytes > 0 ? (double)DownloadedBytes / TotalBytes * 100 : 0;
    public long CurrentPosition => StartByte + DownloadedBytes;
}
