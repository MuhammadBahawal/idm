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
    private static readonly TimeSpan RepositorySyncInterval = TimeSpan.FromMilliseconds(900);

    private readonly DownloadEngine _engine;
    private readonly DownloadRepository _repository;
    private readonly QueueManager _queueManager;
    private readonly DispatcherTimer _refreshTimer;
    private readonly Dictionary<string, Views.DownloadDetailsWindow> _detailWindows = new();
    private readonly Dictionary<string, DownloadStatus> _lastKnownStatuses = new(StringComparer.Ordinal);
    private DateTime _lastRepositorySyncUtc = DateTime.MinValue;

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
        _refreshTimer.Tick += RefreshTimerTick;
        _refreshTimer.Start();

        LoadDownloads();
    }

    [RelayCommand]
    private void AddUrl()
    {
        var dialog = new Views.AddUrlDialog(_engine, _repository)
        {
            Owner = Application.Current.MainWindow
        };

        if (dialog.ShowDialog() == true)
        {
            LoadDownloads();
            if (dialog.StartedImmediately && !string.IsNullOrWhiteSpace(dialog.CreatedDownloadId))
            {
                var started = _repository.GetById(dialog.CreatedDownloadId);
                if (started != null)
                {
                    HandleDownloadStateTransition(started);
                }
            }
        }
    }

    [RelayCommand]
    private async Task ResumeSelected()
    {
        if (SelectedDownload == null) return;

        var item = SelectedDownload.Item;
        await _engine.StartDownloadAsync(item.Id);
        HandleDownloadStateTransition(item);
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
        if (SelectedDownload == null)
        {
            return;
        }

        DeleteDownloads(new[] { SelectedDownload });
    }

    public void DeleteDownloads(IReadOnlyCollection<DownloadItemViewModel> selections)
    {
        if (selections.Count == 0)
        {
            return;
        }

        var targets = selections
            .Where(item => item != null)
            .Distinct()
            .ToList();

        if (targets.Count == 0)
        {
            return;
        }

        var prompt = targets.Count == 1
            ? $"Delete '{targets[0].FileName}'?"
            : $"Delete {targets.Count} selected downloads?";
        var result = MessageBox.Show(
            prompt,
            "Confirm Delete",
            MessageBoxButton.YesNo,
            MessageBoxImage.Question);
        if (result != MessageBoxResult.Yes)
        {
            return;
        }

        foreach (var download in targets)
        {
            var downloadId = download.Item.Id;
            _engine.DeleteDownload(downloadId);
            Downloads.Remove(download);
            _lastKnownStatuses.Remove(downloadId);

            if (_detailWindows.TryGetValue(downloadId, out var window))
            {
                window.Close();
            }
        }

        if (SelectedDownload != null && targets.Contains(SelectedDownload))
        {
            SelectedDownload = Downloads.FirstOrDefault();
        }

        StatusBarText = targets.Count == 1
            ? "Download deleted"
            : $"{targets.Count} downloads deleted";
    }

    [RelayCommand]
    private void OpenSettings()
    {
        var settings = new Views.SettingsWindow(_repository, _engine)
        {
            Owner = Application.Current.MainWindow
        };
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
        EnsureDetailsWindow(SelectedDownload.Item, forceOpen: true, autoCloseOnCompletion: false);
    }

    public void FilterByCategory(string category)
    {
        SelectedCategory = category;
        LoadDownloads();
    }

    private void OnEngineProgressUpdated(DownloadItem item)
    {
        Application.Current.Dispatcher.Invoke(() => UpdateDownloadInList(item, updateOrder: false));
    }

    private void OnEngineStatusChanged(DownloadItem item)
    {
        Application.Current.Dispatcher.Invoke(() =>
        {
            UpdateDownloadInList(item, updateOrder: true);
            HandleDownloadStateTransition(item);
        });
    }

    private void LoadDownloads()
    {
        var all = _repository.GetAll();
        var filtered = GetFilteredItems(all);

        Downloads.Clear();
        foreach (var item in filtered)
        {
            Downloads.Add(new DownloadItemViewModel(item));
        }

        foreach (var item in all)
        {
            _lastKnownStatuses[item.Id] = item.Status;
        }
    }

    private void UpdateDownloadInList(DownloadItem item, bool updateOrder)
    {
        if (!IsVisibleInCurrentCategory(item))
        {
            var hidden = Downloads.FirstOrDefault(d => d.Item.Id == item.Id);
            if (hidden != null)
            {
                Downloads.Remove(hidden);
            }
            return;
        }

        var existing = Downloads.FirstOrDefault(d => d.Item.Id == item.Id);
        if (existing != null)
        {
            existing.UpdateFrom(item);
            if (updateOrder)
            {
                var currentIndex = Downloads.IndexOf(existing);
                if (currentIndex > 0)
                {
                    Downloads.Move(currentIndex, 0);
                }
            }
        }
        else
        {
            Downloads.Insert(0, new DownloadItemViewModel(item));
        }
    }

    private void EnsureDetailsWindow(DownloadItem item, bool forceOpen, bool autoCloseOnCompletion)
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
            if (autoCloseOnCompletion)
            {
                existingWindow.EnableAutoCloseOnCompletion();
            }
            BringWindowToFront(existingWindow);
            return;
        }

        var details = new Views.DownloadDetailsWindow(item, _repository, _engine, autoCloseOnCompletion);
        var owner = Application.Current.MainWindow;
        if (owner != null && owner.IsVisible && owner.WindowState != WindowState.Minimized)
        {
            details.Owner = owner;
        }

        details.Closed += (_, _) => _detailWindows.Remove(item.Id);
        _detailWindows[item.Id] = details;
        details.Show();
        BringWindowToFront(details);
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

    /// <summary>
    /// Called from the IPC pipe listener when NativeHost signals a new download.
    /// Reads the download from the shared database and opens the details window.
    /// </summary>
    public void HandleExternalDownload(string downloadId)
    {
        try
        {
            var item = _repository.GetById(downloadId);
            if (item == null)
            {
                return;
            }

            UpdateDownloadInList(item, updateOrder: true);
            if (item.Status is DownloadStatus.Queued or DownloadStatus.Downloading or DownloadStatus.Merging)
            {
                EnsureDetailsWindow(item, forceOpen: true, autoCloseOnCompletion: true);
            }
            HandleDownloadStateTransition(item);
        }
        catch
        {
            // Ignore transient DB access issues while the host is writing.
        }
    }

    private void RefreshTimerTick(object? sender, EventArgs e)
    {
        if (DateTime.UtcNow - _lastRepositorySyncUtc >= RepositorySyncInterval)
        {
            SyncDownloadsFromRepository();
            _lastRepositorySyncUtc = DateTime.UtcNow;
        }

        RefreshStats();
    }

    private void SyncDownloadsFromRepository()
    {
        var all = _repository.GetAll();
        var filtered = GetFilteredItems(all);
        var visibleIds = filtered.Select(item => item.Id).ToHashSet(StringComparer.Ordinal);

        foreach (var item in all)
        {
            HandleDownloadStateTransition(item);
        }

        foreach (var item in filtered)
        {
            UpdateDownloadInList(item, updateOrder: false);
        }

        for (var i = Downloads.Count - 1; i >= 0; i--)
        {
            if (!visibleIds.Contains(Downloads[i].Item.Id))
            {
                Downloads.RemoveAt(i);
            }
        }
    }

    private List<DownloadItem> GetFilteredItems(List<DownloadItem> source)
    {
        return SelectedCategory switch
        {
            "All Downloads" => source,
            "Unfinished" => source.Where(d => d.Status != DownloadStatus.Complete).ToList(),
            "Finished" => source.Where(d => d.Status == DownloadStatus.Complete).ToList(),
            "Queues" => source.Where(d => d.Status == DownloadStatus.Queued).ToList(),
            CategorySeparator => source,
            _ => source.Where(d => string.Equals(d.Category, SelectedCategory, StringComparison.OrdinalIgnoreCase)).ToList()
        };
    }

    private bool IsVisibleInCurrentCategory(DownloadItem item)
    {
        return SelectedCategory switch
        {
            "All Downloads" => true,
            "Unfinished" => item.Status != DownloadStatus.Complete,
            "Finished" => item.Status == DownloadStatus.Complete,
            "Queues" => item.Status == DownloadStatus.Queued,
            CategorySeparator => true,
            _ => string.Equals(item.Category, SelectedCategory, StringComparison.OrdinalIgnoreCase)
        };
    }

    private void HandleDownloadStateTransition(DownloadItem item)
    {
        _lastKnownStatuses.TryGetValue(item.Id, out var previousStatus);
        _lastKnownStatuses[item.Id] = item.Status;

        if (item.Status == DownloadStatus.Downloading && previousStatus != DownloadStatus.Downloading)
        {
            EnsureDetailsWindow(item, forceOpen: true, autoCloseOnCompletion: true);
            StatusBarText = $"Downloading: {item.FileName}";
        }
    }

    private static void BringWindowToFront(Window window)
    {
        if (window.WindowState == WindowState.Minimized)
        {
            window.WindowState = WindowState.Normal;
        }

        window.Topmost = true;
        window.Activate();
        window.Topmost = false;
        window.Focus();
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
