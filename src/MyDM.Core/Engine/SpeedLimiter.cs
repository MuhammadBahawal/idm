namespace MyDM.Core.Engine;

/// <summary>
/// Token-bucket speed limiter supporting both global and per-download limits.
/// </summary>
public class SpeedLimiter
{
    private readonly SemaphoreSlim _lock = new(1, 1);
    private long _globalLimit; // bytes/sec, 0 = unlimited
    private DateTime _nextAvailableUtc;

    public SpeedLimiter(long globalBytesPerSecond = 0)
    {
        _globalLimit = globalBytesPerSecond;
        _nextAvailableUtc = DateTime.UtcNow;
    }

    public long GlobalLimit
    {
        get => _globalLimit;
        set
        {
            _globalLimit = value;
            if (value == 0)
            {
                _nextAvailableUtc = DateTime.UtcNow;
            }
        }
    }

    /// <summary>
    /// Request permission to transfer a given number of bytes.
    /// Returns the number of bytes actually allowed.
    /// May delay the caller to enforce the rate limit.
    /// </summary>
    public async Task<int> RequestBytesAsync(int requested, long perDownloadLimit, CancellationToken ct)
    {
        if (_globalLimit == 0 && perDownloadLimit == 0)
            return requested;

        var effectiveLimit = GetEffectiveLimit(perDownloadLimit);
        if (effectiveLimit == 0) return requested;

        TimeSpan delay = TimeSpan.Zero;
        await _lock.WaitAsync(ct);
        try
        {
            var now = DateTime.UtcNow;
            if (_nextAvailableUtc < now)
            {
                _nextAvailableUtc = now;
            }

            delay = _nextAvailableUtc - now;

            // Leaky-bucket scheduling: each chunk reserves its transfer time budget.
            var transferMs = (double)requested / effectiveLimit * 1000.0;
            _nextAvailableUtc = _nextAvailableUtc.AddMilliseconds(transferMs);
        }
        finally
        {
            _lock.Release();
        }

        if (delay > TimeSpan.Zero)
        {
            await Task.Delay(delay, ct);
        }

        return requested;
    }

    private long GetEffectiveLimit(long perDownloadLimit)
    {
        if (_globalLimit > 0 && perDownloadLimit > 0)
            return Math.Min(_globalLimit, perDownloadLimit);
        if (_globalLimit > 0) return _globalLimit;
        if (perDownloadLimit > 0) return perDownloadLimit;
        return 0;
    }
}
