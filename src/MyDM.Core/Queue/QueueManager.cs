using MyDM.Core.Data;
using MyDM.Core.Engine;
using MyDM.Core.Models;
using System.Collections.Concurrent;

namespace MyDM.Core.Queue;

/// <summary>
/// Manages download queues with concurrency limits and scheduling.
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
            MaxConcurrent = maxConcurrent,
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
        var defaultQueue = _queues.Values.FirstOrDefault(q => q.Name == "Default");
        if (defaultQueue == null)
        {
            defaultQueue = CreateQueue("Default", 3);
        }
        return defaultQueue;
    }

    /// <summary>
    /// Add a download to a queue.
    /// </summary>
    public void AddToQueue(string queueId, string downloadId)
    {
        if (_queues.TryGetValue(queueId, out var queue))
        {
            if (!queue.DownloadIds.Contains(downloadId))
                queue.DownloadIds.Add(downloadId);
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
            foreach (var queue in _queues.Values.Where(q => q.IsActive))
            {
                // Check schedule
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
        catch { /* Ignore errors in scheduler tick */ }
    }

    private static bool IsWithinSchedule(QueueConfig queue)
    {
        if (queue.ScheduleStart == null || queue.ScheduleStop == null)
            return true;

        var now = TimeOnly.FromDateTime(DateTime.Now);

        if (queue.DaysOfWeek != null && queue.DaysOfWeek.Length > 0)
        {
            if (!queue.DaysOfWeek.Contains(DateTime.Now.DayOfWeek))
                return false;
        }

        if (queue.ScheduleStart <= queue.ScheduleStop)
            return now >= queue.ScheduleStart && now <= queue.ScheduleStop;
        else
            return now >= queue.ScheduleStart || now <= queue.ScheduleStop;
    }

    public void Dispose()
    {
        _schedulerTimer.Dispose();
    }
}
