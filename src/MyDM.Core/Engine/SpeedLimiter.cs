namespace MyDM.Core.Engine;

/// <summary>
/// Token-bucket speed limiter supporting both global and per-download limits.
/// </summary>
public class SpeedLimiter
{
    private readonly SemaphoreSlim _lock = new(1, 1);
    private long _globalLimit; // bytes/sec, 0 = unlimited
    private long _tokensAvailable;
    private DateTime _lastRefill;

    public SpeedLimiter(long globalBytesPerSecond = 0)
    {
        _globalLimit = globalBytesPerSecond;
        _tokensAvailable = globalBytesPerSecond > 0 ? globalBytesPerSecond : long.MaxValue;
        _lastRefill = DateTime.UtcNow;
    }

    public long GlobalLimit
    {
        get => _globalLimit;
        set
        {
            _globalLimit = value;
            if (value == 0) _tokensAvailable = long.MaxValue;
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

        await _lock.WaitAsync(ct);
        try
        {
            RefillTokens();

            var allowed = (int)Math.Min(requested, Math.Min(_tokensAvailable, effectiveLimit / 10));
            if (allowed <= 0)
            {
                // Wait for tokens to refill
                var waitMs = Math.Max(50, 1000 / (effectiveLimit / Math.Max(requested, 1)));
                await Task.Delay(Math.Min((int)waitMs, 200), ct);
                RefillTokens();
                allowed = (int)Math.Min(requested, Math.Min(_tokensAvailable, effectiveLimit / 10));
                allowed = Math.Max(allowed, 1); // Always allow at least 1 byte
            }

            _tokensAvailable -= allowed;
            return allowed;
        }
        finally
        {
            _lock.Release();
        }
    }

    private void RefillTokens()
    {
        var now = DateTime.UtcNow;
        var elapsed = (now - _lastRefill).TotalSeconds;
        if (elapsed <= 0) return;

        if (_globalLimit > 0)
        {
            _tokensAvailable = Math.Min(_globalLimit, _tokensAvailable + (long)(_globalLimit * elapsed));
        }
        else
        {
            _tokensAvailable = long.MaxValue;
        }

        _lastRefill = now;
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
