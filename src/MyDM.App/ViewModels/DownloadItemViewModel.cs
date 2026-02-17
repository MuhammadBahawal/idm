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
    public string SpeedText => Item.TransferRate > 0 ? FileHelper.FormatSpeed(Item.TransferRate) : "—";
    public string TimeLeftText => Item.TimeLeft.HasValue ? FileHelper.FormatTimeLeft(Item.TimeLeft) : "—";
    public string LastTryText => Item.LastAttemptAt?.ToLocalTime().ToString("g") ?? "—";
    public string DescriptionText => Item.Description ?? "";
    public string Category => Item.Category;
    public string StatusText => Status switch
    {
        DownloadStatus.Queued => "Queued",
        DownloadStatus.Downloading => $"Downloading ({Progress:F1}%)",
        DownloadStatus.Paused => $"Paused ({Progress:F1}%)",
        DownloadStatus.Complete => "Complete",
        DownloadStatus.Error => $"Error: {Item.ErrorMessage}",
        DownloadStatus.Cancelled => "Cancelled",
        DownloadStatus.Merging => "Merging...",
        _ => "Unknown"
    };

    /// <summary>
    /// Called when the underlying Item properties change.
    /// </summary>
    public void Refresh()
    {
        OnPropertyChanged(nameof(SizeText));
        OnPropertyChanged(nameof(Status));
        OnPropertyChanged(nameof(Progress));
        OnPropertyChanged(nameof(SpeedText));
        OnPropertyChanged(nameof(TimeLeftText));
        OnPropertyChanged(nameof(StatusText));
        OnPropertyChanged(nameof(LastTryText));
    }
}
