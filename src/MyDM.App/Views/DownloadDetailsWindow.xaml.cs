using MyDM.Core.Data;
using MyDM.Core.Engine;
using MyDM.Core.Models;
using MyDM.Core.Utilities;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Media;
using System.Windows.Threading;

namespace MyDM.App.Views;

public partial class DownloadDetailsWindow : Window, INotifyPropertyChanged
{
    private readonly DownloadRepository _repository;
    private readonly DownloadEngine _engine;
    private readonly DispatcherTimer _refreshTimer;
    private DateTime _lastLogRefresh = DateTime.MinValue;
    private DownloadItem _item;

    private string _windowTitle = "Download Monitor";
    private string _fileName = string.Empty;
    private string _url = string.Empty;
    private string _fileSizeText = "Unknown";
    private string _downloadedText = "0 B";
    private string _progressText = "0.0%";
    private double _progressPercent;
    private string _transferRateText = "0 B/s";
    private string _timeLeftText = "Unknown";
    private string _resumeCapabilityText = "Unknown";
    private string _connectionsText = "0";
    private string _categoryText = "Others";
    private string _statusText = "Queued";
    private string _speedLimitInput = "0";
    private string _speedLimitStatusText = "Current: Unlimited";
    private System.Windows.Media.Brush _statusBadgeBrush = System.Windows.Media.Brushes.Gray;

    public event PropertyChangedEventHandler? PropertyChanged;

    public ObservableCollection<SegmentProgressViewModel> SegmentRows { get; } = new();
    public ObservableCollection<string> LogEntries { get; } = new();

    public string WindowTitle
    {
        get => _windowTitle;
        private set => SetField(ref _windowTitle, value);
    }

    public string FileName
    {
        get => _fileName;
        private set => SetField(ref _fileName, value);
    }

    public string Url
    {
        get => _url;
        private set => SetField(ref _url, value);
    }

    public string FileSizeText
    {
        get => _fileSizeText;
        private set => SetField(ref _fileSizeText, value);
    }

    public string DownloadedText
    {
        get => _downloadedText;
        private set => SetField(ref _downloadedText, value);
    }

    public string ProgressText
    {
        get => _progressText;
        private set => SetField(ref _progressText, value);
    }

    public double ProgressPercent
    {
        get => _progressPercent;
        private set => SetField(ref _progressPercent, value);
    }

    public string TransferRateText
    {
        get => _transferRateText;
        private set => SetField(ref _transferRateText, value);
    }

    public string TimeLeftText
    {
        get => _timeLeftText;
        private set => SetField(ref _timeLeftText, value);
    }

    public string ResumeCapabilityText
    {
        get => _resumeCapabilityText;
        private set => SetField(ref _resumeCapabilityText, value);
    }

    public string ConnectionsText
    {
        get => _connectionsText;
        private set => SetField(ref _connectionsText, value);
    }

    public string CategoryText
    {
        get => _categoryText;
        private set => SetField(ref _categoryText, value);
    }

    public string StatusText
    {
        get => _statusText;
        private set
        {
            if (SetField(ref _statusText, value))
            {
                OnPropertyChanged(nameof(PauseResumeButtonText));
                OnPropertyChanged(nameof(CanPauseResume));
                OnPropertyChanged(nameof(CanCancel));
            }
        }
    }

    public System.Windows.Media.Brush StatusBadgeBrush
    {
        get => _statusBadgeBrush;
        private set => SetField(ref _statusBadgeBrush, value);
    }

    public string SpeedLimitInput
    {
        get => _speedLimitInput;
        set => SetField(ref _speedLimitInput, value);
    }

    public string SpeedLimitStatusText
    {
        get => _speedLimitStatusText;
        private set => SetField(ref _speedLimitStatusText, value);
    }

    public string PauseResumeButtonText =>
        _item.Status == DownloadStatus.Downloading || _item.Status == DownloadStatus.Merging
            ? "Pause"
            : "Resume";

    public bool CanPauseResume =>
        _item.Status is DownloadStatus.Downloading
        or DownloadStatus.Merging
        or DownloadStatus.Paused
        or DownloadStatus.Queued
        or DownloadStatus.Error;

    public bool CanCancel =>
        _item.Status is not DownloadStatus.Complete and not DownloadStatus.Cancelled;

    public DownloadDetailsWindow(DownloadItem item, DownloadRepository repository, DownloadEngine engine)
    {
        InitializeComponent();
        _item = item;
        _repository = repository;
        _engine = engine;
        DataContext = this;

        _engine.OnProgressUpdated += HandleEngineProgressUpdated;
        _engine.OnStatusChanged += HandleEngineStatusChanged;

        _refreshTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(150) };
        _refreshTimer.Tick += RefreshTimerTick;
        _refreshTimer.Start();

        RefreshSnapshot(forceLogs: true);
        SpeedLimitInput = _item.SpeedLimit > 0 ? (_item.SpeedLimit / 1024.0).ToString("F0") : "0";
        UpdateSpeedLimitStatus();
    }

    protected override void OnClosed(EventArgs e)
    {
        _refreshTimer.Stop();
        _engine.OnProgressUpdated -= HandleEngineProgressUpdated;
        _engine.OnStatusChanged -= HandleEngineStatusChanged;
        base.OnClosed(e);
    }

    private void RefreshTimerTick(object? sender, EventArgs e)
    {
        var refreshLogs = (DateTime.UtcNow - _lastLogRefresh).TotalSeconds >= 1;
        RefreshSnapshot(refreshLogs);
    }

    private void HandleEngineProgressUpdated(DownloadItem item)
    {
        if (item.Id != _item.Id) return;
        Dispatcher.InvokeAsync(() => RefreshSnapshot(false));
    }

    private void HandleEngineStatusChanged(DownloadItem item)
    {
        if (item.Id != _item.Id) return;
        Dispatcher.InvokeAsync(() => RefreshSnapshot(true));
    }

    private void RefreshSnapshot(bool forceLogs)
    {
        var latest = _engine.GetDownload(_item.Id) ?? _repository.GetById(_item.Id);
        if (latest != null)
        {
            _item = latest;
        }

        WindowTitle = $"Download - {_item.FileName}";
        FileName = _item.FileName;
        Url = _item.Url;
        FileSizeText = _item.TotalSize > 0 ? FileHelper.FormatSize(_item.TotalSize) : "Unknown";
        DownloadedText = _item.TotalSize > 0
            ? $"{FileHelper.FormatSize(_item.DownloadedSize)} / {FileHelper.FormatSize(_item.TotalSize)}"
            : FileHelper.FormatSize(_item.DownloadedSize);
        ProgressPercent = Math.Clamp(_item.ProgressPercent, 0, 100);
        ProgressText = $"{ProgressPercent:F1}%";
        TransferRateText = _item.TransferRate > 0 ? FileHelper.FormatSpeed(_item.TransferRate) : "0 B/s";
        TimeLeftText = _item.TimeLeft.HasValue ? FileHelper.FormatTimeLeft(_item.TimeLeft) : "Unknown";
        ResumeCapabilityText = _item.SupportsRange ? "Yes" : "No";
        ConnectionsText = _item.Connections.ToString();
        CategoryText = _item.Category;
        StatusText = _item.Status.ToString();
        StatusBadgeBrush = ResolveStatusBrush(_item.Status);
        UpdateSpeedLimitStatus();
        UpdateSegments();

        if (forceLogs)
        {
            UpdateLogs();
        }
    }

    private void UpdateSegments()
    {
        var segments = _item.Segments.Count > 0
            ? _item.Segments.OrderBy(s => s.Index).ToList()
            : _repository.GetSegments(_item.Id).OrderBy(s => s.Index).ToList();

        if (segments.Count == 0)
        {
            var syntheticTotal = _item.TotalSize > 0 ? _item.TotalSize : Math.Max(_item.DownloadedSize, 1);
            segments.Add(new DownloadSegment
            {
                Id = $"{_item.Id}_single",
                DownloadId = _item.Id,
                Index = 0,
                StartByte = 0,
                EndByte = syntheticTotal - 1,
                DownloadedBytes = _item.DownloadedSize,
                Status = MapSingleStreamStatus(_item.Status),
                TransferRate = _item.TransferRate
            });
        }

        SegmentRows.Clear();
        foreach (var segment in segments)
        {
            SegmentRows.Add(new SegmentProgressViewModel(segment));
        }
    }

    private void UpdateLogs()
    {
        _lastLogRefresh = DateTime.UtcNow;
        var logs = _repository.GetLogs(_item.Id, 60)
            .OrderBy(log => log.Id)
            .Select(log => $"[{log.Timestamp.ToLocalTime():HH:mm:ss}] [{log.Level}] {log.Message}")
            .ToList();

        LogEntries.Clear();
        foreach (var line in logs)
        {
            LogEntries.Add(line);
        }
    }

    private void UpdateSpeedLimitStatus()
    {
        SpeedLimitStatusText = _item.SpeedLimit > 0
            ? $"Current: {FileHelper.FormatSpeed(_item.SpeedLimit)}"
            : "Current: Unlimited";
    }

    private System.Windows.Media.Brush ResolveStatusBrush(DownloadStatus status)
    {
        var key = status switch
        {
            DownloadStatus.Downloading => "StatusDownloadingBrush",
            DownloadStatus.Merging => "StatusMergingBrush",
            DownloadStatus.Paused => "StatusPausedBrush",
            DownloadStatus.Error => "StatusErrorBrush",
            DownloadStatus.Complete => "StatusCompleteBrush",
            DownloadStatus.Cancelled => "StatusCancelledBrush",
            _ => "StatusQueuedBrush"
        };
        return (System.Windows.Media.Brush)(Application.Current.TryFindResource(key) ?? System.Windows.Media.Brushes.Gray);
    }

    private static SegmentStatus MapSingleStreamStatus(DownloadStatus status)
    {
        return status switch
        {
            DownloadStatus.Downloading => SegmentStatus.Active,
            DownloadStatus.Complete => SegmentStatus.Done,
            DownloadStatus.Error => SegmentStatus.Error,
            _ => SegmentStatus.Pending
        };
    }

    private async void PauseResume_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            if (_item.Status == DownloadStatus.Downloading || _item.Status == DownloadStatus.Merging)
            {
                _engine.PauseDownload(_item.Id);
                return;
            }

            if (!CanPauseResume)
            {
                return;
            }

            await _engine.StartDownloadAsync(_item.Id);
        }
        catch (Exception ex)
        {
            MessageBox.Show($"Failed to change download state: {ex.Message}", "Download Error", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void CancelDownload_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            _engine.CancelDownload(_item.Id);
        }
        catch (Exception ex)
        {
            MessageBox.Show($"Failed to cancel download: {ex.Message}", "Download Error", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void ApplySpeedLimit_Click(object sender, RoutedEventArgs e)
    {
        if (!double.TryParse(SpeedLimitInput, out var kbPerSecond) || kbPerSecond < 0)
        {
            MessageBox.Show("Enter a valid speed limit in KB/s.", "Invalid Value", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        var bytesPerSecond = (long)Math.Round(kbPerSecond * 1024);
        var current = _engine.GetDownload(_item.Id);
        if (current != null)
        {
            current.SpeedLimit = bytesPerSecond;
            _repository.Update(current);
            _item = current;
        }
        else
        {
            _item.SpeedLimit = bytesPerSecond;
            _repository.Update(_item);
        }

        UpdateSpeedLimitStatus();
        SpeedLimitInput = bytesPerSecond > 0 ? (bytesPerSecond / 1024.0).ToString("F0") : "0";
    }

    private void Close_Click(object sender, RoutedEventArgs e)
    {
        Close();
    }

    private bool SetField<T>(ref T field, T value, [CallerMemberName] string? propertyName = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value))
        {
            return false;
        }

        field = value;
        OnPropertyChanged(propertyName);
        return true;
    }

    private void OnPropertyChanged([CallerMemberName] string? propertyName = null)
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    }

    public sealed class SegmentProgressViewModel
    {
        public SegmentProgressViewModel(DownloadSegment segment)
        {
            Index = segment.Index + 1;
            SegmentLabel = $"Conn {Index}";
            RangeText = $"{FileHelper.FormatSize(segment.StartByte)} - {FileHelper.FormatSize(segment.EndByte)}";
            DownloadedText = FileHelper.FormatSize(segment.DownloadedBytes);
            SpeedText = segment.TransferRate > 0 ? FileHelper.FormatSpeed(segment.TransferRate) : "0 B/s";
            StatusText = segment.Status.ToString();
            ProgressPercent = Math.Clamp(segment.ProgressPercent, 0, 100);
            ProgressText = $"{ProgressPercent:F1}%";
        }

        public int Index { get; }
        public string SegmentLabel { get; }
        public string RangeText { get; }
        public string DownloadedText { get; }
        public string SpeedText { get; }
        public string StatusText { get; }
        public double ProgressPercent { get; }
        public string ProgressText { get; }
    }
}
