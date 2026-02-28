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
    private const string CategorySeparator = "------------";
    private readonly DownloadEngine _engine;
    private readonly DownloadRepository _repository;
    private readonly QueueManager _queueManager;
    private readonly DispatcherTimer _refreshTimer;
    private readonly Dictionary<string, Views.DownloadDetailsWindow> _detailWindows = new();

    [ObservableProperty] private string _selectedCategory = "All Downloads";
    [ObservableProperty] private DownloadItemViewModel? _selectedDownload;
    [ObservableProperty] private string _statusBarText = "Ready";
    [ObservableProperty] private string _activeDownloadsText = "0 active";
    [ObservableProperty] private string _totalSpeedText = "0 B/s";

    public ObservableCollection<DownloadItemViewModel> Downloads { get; } = new();
    public ObservableCollection<string> Categories { get; } = new()
    {
        "All Downloads", "Video", "Music", "Documents", "Programs", "Compressed", "Others",
        CategorySeparator, "Unfinished", "Finished", "Queues"
    };

    public MainViewModel(DownloadEngine engine, DownloadRepository repository, QueueManager queueManager)
    {
        _engine = engine;
        _repository = repository;
        _queueManager = queueManager;

        _engine.OnProgressUpdated += OnEngineProgressUpdated;
        _engine.OnStatusChanged += OnEngineStatusChanged;

        _refreshTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(250) };
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
            var downloadId = SelectedDownload.Item.Id;
            _engine.DeleteDownload(downloadId);
            Downloads.Remove(SelectedDownload);
            if (_detailWindows.TryGetValue(downloadId, out var window))
            {
                window.Close();
            }
            StatusBarText = "Download deleted";
        }
    }

    [RelayCommand]
    private void OpenSettings()
    {
        var settings = new Views.SettingsWindow(_repository, _engine);
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
        EnsureDetailsWindow(SelectedDownload.Item, forceOpen: true);
    }

    public void FilterByCategory(string category)
    {
        SelectedCategory = category;
        LoadDownloads();
    }

    private void OnEngineProgressUpdated(DownloadItem item)
    {
        Application.Current.Dispatcher.Invoke(() => UpdateDownloadInList(item));
    }

    private void OnEngineStatusChanged(DownloadItem item)
    {
        Application.Current.Dispatcher.Invoke(() =>
        {
            UpdateDownloadInList(item);
            if (item.Status == DownloadStatus.Downloading)
            {
                EnsureDetailsWindow(item, forceOpen: false);
            }
        });
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
            CategorySeparator => _engine.GetAllDownloads(),
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
            existing.UpdateFrom(item);
        }
        else
        {
            Downloads.Insert(0, new DownloadItemViewModel(item));
        }
    }

    private void EnsureDetailsWindow(DownloadItem item, bool forceOpen)
    {
        if (!forceOpen && !IsAutoDetailsPopupEnabled())
        {
            return;
        }

        if (_detailWindows.TryGetValue(item.Id, out var existingWindow))
        {
            if (!existingWindow.IsVisible)
            {
                existingWindow.Show();
            }
            existingWindow.Activate();
            return;
        }

        var details = new Views.DownloadDetailsWindow(item, _repository, _engine);
        details.Owner = Application.Current.MainWindow;
        details.Closed += (_, _) => _detailWindows.Remove(item.Id);
        _detailWindows[item.Id] = details;
        details.Show();
        details.Activate();
    }

    private bool IsAutoDetailsPopupEnabled()
    {
        var setting = _repository.GetSetting("AutoShowDownloadWindow");
        if (string.IsNullOrWhiteSpace(setting))
        {
            return true;
        }

        return !string.Equals(setting, "0", StringComparison.OrdinalIgnoreCase)
            && !string.Equals(setting, "false", StringComparison.OrdinalIgnoreCase);
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
