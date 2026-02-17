using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using MyDM.Core.Data;
using MyDM.Core.Engine;
using MyDM.Core.Models;
using MyDM.Core.Queue;
using MyDM.Core.Utilities;
using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Threading;

namespace MyDM.App.ViewModels;

public partial class MainViewModel : ObservableObject
{
    private readonly DownloadEngine _engine;
    private readonly DownloadRepository _repository;
    private readonly QueueManager _queueManager;
    private readonly DispatcherTimer _refreshTimer;

    [ObservableProperty] private string _selectedCategory = "All Downloads";
    [ObservableProperty] private DownloadItemViewModel? _selectedDownload;
    [ObservableProperty] private string _statusBarText = "Ready";
    [ObservableProperty] private string _activeDownloadsText = "0 active";
    [ObservableProperty] private string _totalSpeedText = "0 B/s";

    public ObservableCollection<DownloadItemViewModel> Downloads { get; } = new();
    public ObservableCollection<string> Categories { get; } = new()
    {
        "All Downloads", "Video", "Music", "Documents", "Programs", "Compressed", "Others",
        "─────────", "Unfinished", "Finished", "Queues"
    };

    public MainViewModel(DownloadEngine engine, DownloadRepository repository, QueueManager queueManager)
    {
        _engine = engine;
        _repository = repository;
        _queueManager = queueManager;

        _engine.OnProgressUpdated += item => Application.Current.Dispatcher.Invoke(() => UpdateDownloadInList(item));
        _engine.OnStatusChanged += item => Application.Current.Dispatcher.Invoke(() => UpdateDownloadInList(item));

        _refreshTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(500) };
        _refreshTimer.Tick += (_, _) => RefreshStats();
        _refreshTimer.Start();

        LoadDownloads();
    }

    [RelayCommand]
    private void AddUrl()
    {
        var dialog = new Views.AddUrlDialog(_engine, _repository);
        dialog.Owner = Application.Current.MainWindow;
        if (dialog.ShowDialog() == true)
        {
            LoadDownloads();
        }
    }

    [RelayCommand]
    private async Task ResumeSelected()
    {
        if (SelectedDownload == null) return;
        await _engine.StartDownloadAsync(SelectedDownload.Item.Id);
        StatusBarText = $"Resuming {SelectedDownload.FileName}";
    }

    [RelayCommand]
    private void StopSelected()
    {
        if (SelectedDownload == null) return;
        _engine.PauseDownload(SelectedDownload.Item.Id);
        StatusBarText = $"Paused {SelectedDownload.FileName}";
    }

    [RelayCommand]
    private void StopAll()
    {
        _engine.StopAll();
        StatusBarText = "All downloads stopped";
    }

    [RelayCommand]
    private void DeleteSelected()
    {
        if (SelectedDownload == null) return;
        var result = MessageBox.Show(
            $"Delete '{SelectedDownload.FileName}'?",
            "Confirm Delete", MessageBoxButton.YesNo, MessageBoxImage.Question);

        if (result == MessageBoxResult.Yes)
        {
            _engine.DeleteDownload(SelectedDownload.Item.Id);
            Downloads.Remove(SelectedDownload);
            StatusBarText = "Download deleted";
        }
    }

    [RelayCommand]
    private void OpenSettings()
    {
        var settings = new Views.SettingsWindow(_repository);
        settings.Owner = Application.Current.MainWindow;
        settings.ShowDialog();
    }

    [RelayCommand]
    private void StartQueue()
    {
        _queueManager.Start();
        StatusBarText = "Queue started";
    }

    [RelayCommand]
    private void StopQueue()
    {
        _queueManager.Stop();
        StatusBarText = "Queue stopped";
    }

    [RelayCommand]
    private void ShowDetails()
    {
        if (SelectedDownload == null) return;
        var details = new Views.DownloadDetailsWindow(SelectedDownload.Item, _repository);
        details.Owner = Application.Current.MainWindow;
        details.Show();
    }

    public void FilterByCategory(string category)
    {
        SelectedCategory = category;
        LoadDownloads();
    }

    private void LoadDownloads()
    {
        Downloads.Clear();
        var items = SelectedCategory switch
        {
            "All Downloads" => _engine.GetAllDownloads(),
            "Unfinished" => _engine.GetAllDownloads().Where(d => d.Status != DownloadStatus.Complete).ToList(),
            "Finished" => _engine.GetAllDownloads().Where(d => d.Status == DownloadStatus.Complete).ToList(),
            "Queues" => _engine.GetAllDownloads().Where(d => d.Status == DownloadStatus.Queued).ToList(),
            "─────────" => _engine.GetAllDownloads(),
            _ => _engine.GetAllDownloads().Where(d => d.Category == SelectedCategory).ToList()
        };

        foreach (var item in items)
        {
            Downloads.Add(new DownloadItemViewModel(item));
        }
    }

    private void UpdateDownloadInList(DownloadItem item)
    {
        var existing = Downloads.FirstOrDefault(d => d.Item.Id == item.Id);
        if (existing != null)
        {
            existing.Refresh();
        }
        else
        {
            Downloads.Insert(0, new DownloadItemViewModel(item));
        }
    }

    private void RefreshStats()
    {
        var all = Downloads.ToList();
        var activeCount = all.Count(d => d.Status == DownloadStatus.Downloading);
        var totalSpeed = all.Where(d => d.Status == DownloadStatus.Downloading).Sum(d => d.Item.TransferRate);

        ActiveDownloadsText = $"{activeCount} active";
        TotalSpeedText = FileHelper.FormatSpeed(totalSpeed);
    }
}
