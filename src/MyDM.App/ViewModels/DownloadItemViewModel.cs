using CommunityToolkit.Mvvm.ComponentModel;
using MyDM.Core.Models;
using MyDM.Core.Utilities;

namespace MyDM.App.ViewModels;

public partial class DownloadItemViewModel : ObservableObject
{
    public DownloadItem Item { get; }

    public DownloadItemViewModel(DownloadItem item)
    {
        Item = item;
    }

    public string FileName => Item.FileName;
    public string SizeText => Item.TotalSize > 0 ? FileHelper.FormatSize(Item.TotalSize) : "Unknown";
    public DownloadStatus Status => Item.Status;
    public double Progress => Item.ProgressPercent;
    public string SpeedText => Item.TransferRate > 0 ? FileHelper.FormatSpeed(Item.TransferRate) : "0 B/s";
    public string TimeLeftText => Item.TimeLeft.HasValue ? FileHelper.FormatTimeLeft(Item.TimeLeft) : "Unknown";
    public string LastTryText => Item.LastAttemptAt?.ToLocalTime().ToString("g") ?? "-";
    public string DescriptionText => Item.Description ?? string.Empty;
    public string Category => Item.Category;
    public string StatusText => Status switch
    {
        DownloadStatus.Queued => "Queued",
        DownloadStatus.Downloading => $"Downloading ({Progress:F1}%)",
        DownloadStatus.Paused => $"Paused ({Progress:F1}%)",
        DownloadStatus.Complete => "Complete",
        DownloadStatus.Error => $"Error: {Item.ErrorMessage}",
        DownloadStatus.Cancelled => "Cancelled",
        DownloadStatus.Merging => "Merging",
        _ => "Unknown"
    };

    /// <summary>
    /// Called when the underlying item properties change.
    /// </summary>
    public void Refresh()
    {
        OnPropertyChanged(nameof(FileName));
        OnPropertyChanged(nameof(SizeText));
        OnPropertyChanged(nameof(Status));
        OnPropertyChanged(nameof(Progress));
        OnPropertyChanged(nameof(SpeedText));
        OnPropertyChanged(nameof(TimeLeftText));
        OnPropertyChanged(nameof(StatusText));
        OnPropertyChanged(nameof(LastTryText));
    }

    public void UpdateFrom(DownloadItem source)
    {
        if (ReferenceEquals(Item, source))
        {
            Refresh();
            return;
        }

        Item.Url = source.Url;
        Item.FileName = source.FileName;
        Item.SavePath = source.SavePath;
        Item.Category = source.Category;
        Item.Status = source.Status;
        Item.TotalSize = source.TotalSize;
        Item.DownloadedSize = source.DownloadedSize;
        Item.Connections = source.Connections;
        Item.SpeedLimit = source.SpeedLimit;
        Item.ErrorMessage = source.ErrorMessage;
        Item.RetryCount = source.RetryCount;
        Item.CompletedAt = source.CompletedAt;
        Item.LastAttemptAt = source.LastAttemptAt;
        Item.SupportsRange = source.SupportsRange;
        Item.TransferRate = source.TransferRate;
        Item.TimeLeft = source.TimeLeft;
        Item.Description = source.Description;
        Item.Segments = source.Segments.Select(CloneSegment).ToList();
        Refresh();
    }

    private static DownloadSegment CloneSegment(DownloadSegment segment)
    {
        return new DownloadSegment
        {
            Id = segment.Id,
            DownloadId = segment.DownloadId,
            Index = segment.Index,
            StartByte = segment.StartByte,
            EndByte = segment.EndByte,
            DownloadedBytes = segment.DownloadedBytes,
            Status = segment.Status,
            TempFile = segment.TempFile,
            TransferRate = segment.TransferRate
        };
    }
}
