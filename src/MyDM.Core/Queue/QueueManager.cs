using MyDM.Core.Data;
using MyDM.Core.Engine;
using MyDM.Core.Models;
using System.Collections.Concurrent;

namespace MyDM.Core.Queue;

/// <summary>
/// Manages download queues with concurrency limits and optional scheduling windows.
/// </summary>
public class QueueManager : IDisposable
{
    private readonly DownloadEngine _engine;
    private readonly DownloadRepository _repository;
    private readonly ConcurrentDictionary<string, QueueConfig> _queues = new();
    private readonly Timer _schedulerTimer;
    private bool _isRunning;

    public QueueManager(DownloadEngine engine, DownloadRepository repository)
    {
        _engine = engine;
        _repository = repository;
        _schedulerTimer = new Timer(SchedulerTick, null, Timeout.Infinite, Timeout.Infinite);
    }

    /// <summary>
    /// Start the queue manager, processing queued downloads.
    /// </summary>
    public void Start()
    {
        EnsureDefaultQueueFromSettings();
        _isRunning = true;
        _schedulerTimer.Change(TimeSpan.Zero, TimeSpan.FromSeconds(5));
    }

    /// <summary>
    /// Stop the queue manager.
    /// </summary>
    public void Stop()
    {
        _isRunning = false;
        _schedulerTimer.Change(Timeout.Infinite, Timeout.Infinite);
    }

    /// <summary>
    /// Create a new queue.
    /// </summary>
    public QueueConfig CreateQueue(string name, int maxConcurrent = 3)
    {
        var queue = new QueueConfig
        {
            Name = name,
            MaxConcurrent = Math.Max(1, maxConcurrent),
            IsActive = true
        };
        _queues[queue.Id] = queue;
        return queue;
    }

    /// <summary>
    /// Get the default queue, creating it if needed.
    /// </summary>
    public QueueConfig GetDefaultQueue()
    {
        EnsureDefaultQueueFromSettings();
        return _queues.Values.First(q => q.Name == "Default");
    }

    /// <summary>
    /// Add a download to a queue.
    /// </summary>
    public void AddToQueue(string queueId, string downloadId)
    {
        if (_queues.TryGetValue(queueId, out var queue))
        {
            if (!queue.DownloadIds.Contains(downloadId))
            {
                queue.DownloadIds.Add(downloadId);
            }
        }
    }

    /// <summary>
    /// Get all queues.
    /// </summary>
    public List<QueueConfig> GetQueues() => _queues.Values.ToList();

    private void SchedulerTick(object? state)
    {
        if (!_isRunning) return;

        try
        {
            EnsureDefaultQueueFromSettings();

            foreach (var queue in _queues.Values.Where(q => q.IsActive))
            {
                if (!IsWithinSchedule(queue)) continue;

                var downloads = _repository.GetAll();
                var activeCount = downloads.Count(d => d.Status == DownloadStatus.Downloading);

                if (activeCount >= queue.MaxConcurrent) continue;

                var queued = downloads
                    .Where(d => d.Status == DownloadStatus.Queued)
                    .OrderBy(d => d.CreatedAt)
                    .Take(queue.MaxConcurrent - activeCount);

                foreach (var item in queued)
                {
                    _ = _engine.StartDownloadAsync(item.Id);
                }
            }
        }
        catch
        {
            // Ignore scheduler tick failures to keep queue loop alive.
        }
    }

    private void EnsureDefaultQueueFromSettings()
    {
        var defaultQueue = _queues.Values.FirstOrDefault(q => q.Name == "Default")
            ?? CreateQueue("Default", 3);

        defaultQueue.MaxConcurrent = ParseIntSetting("MaxConcurrentDownloads", defaultQueue.MaxConcurrent);
        defaultQueue.IsActive = true;

        var scheduleEnabled = ParseBoolSetting("QueueScheduleEnabled", false);
        if (scheduleEnabled)
        {
            defaultQueue.ScheduleStart = ParseTimeSetting("QueueScheduleStart");
            defaultQueue.ScheduleStop = ParseTimeSetting("QueueScheduleStop");
            defaultQueue.DaysOfWeek = ParseDaysOfWeek(_repository.GetSetting("QueueScheduleDays"));
        }
        else
        {
            defaultQueue.ScheduleStart = null;
            defaultQueue.ScheduleStop = null;
            defaultQueue.DaysOfWeek = null;
        }
    }

    private int ParseIntSetting(string key, int fallback)
    {
        var raw = _repository.GetSetting(key);
        return int.TryParse(raw, out var value) && value > 0 ? value : fallback;
    }

    private bool ParseBoolSetting(string key, bool fallback)
    {
        var raw = _repository.GetSetting(key);
        if (string.IsNullOrWhiteSpace(raw))
        {
            return fallback;
        }

        return !string.Equals(raw, "0", StringComparison.OrdinalIgnoreCase)
            && !string.Equals(raw, "false", StringComparison.OrdinalIgnoreCase);
    }

    private TimeOnly? ParseTimeSetting(string key)
    {
        var raw = _repository.GetSetting(key);
        return TimeOnly.TryParse(raw, out var time) ? time : null;
    }

    private static DayOfWeek[]? ParseDaysOfWeek(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
        {
            return null;
        }

        var values = new List<DayOfWeek>();
        foreach (var token in raw.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var day = token.ToLowerInvariant() switch
            {
                "mon" or "monday" => DayOfWeek.Monday,
                "tue" or "tues" or "tuesday" => DayOfWeek.Tuesday,
                "wed" or "wednesday" => DayOfWeek.Wednesday,
                "thu" or "thur" or "thurs" or "thursday" => DayOfWeek.Thursday,
                "fri" or "friday" => DayOfWeek.Friday,
                "sat" or "saturday" => DayOfWeek.Saturday,
                "sun" or "sunday" => DayOfWeek.Sunday,
                _ => (DayOfWeek?)null
            };

            if (day.HasValue)
            {
                values.Add(day.Value);
            }
        }

        return values.Count > 0 ? values.Distinct().ToArray() : null;
    }

    private static bool IsWithinSchedule(QueueConfig queue)
    {
        if (queue.ScheduleStart == null || queue.ScheduleStop == null)
        {
            return true;
        }

        var now = TimeOnly.FromDateTime(DateTime.Now);

        if (queue.DaysOfWeek != null && queue.DaysOfWeek.Length > 0)
        {
            if (!queue.DaysOfWeek.Contains(DateTime.Now.DayOfWeek))
            {
                return false;
            }
        }

        if (queue.ScheduleStart <= queue.ScheduleStop)
        {
            return now >= queue.ScheduleStart && now <= queue.ScheduleStop;
        }

        return now >= queue.ScheduleStart || now <= queue.ScheduleStop;
    }

    public void Dispose()
    {
        _schedulerTimer.Dispose();
    }
}
