using System.Diagnostics;
using MyDM.Core.Engine;
using Xunit;

namespace MyDM.Core.Tests;

public class SpeedLimiterTests
{
    [Fact]
    public async Task RequestBytesAsync_Unlimited_ShouldNotThrottle()
    {
        var limiter = new SpeedLimiter();
        var sw = Stopwatch.StartNew();

        await limiter.RequestBytesAsync(100_000, 0, CancellationToken.None);
        await limiter.RequestBytesAsync(100_000, 0, CancellationToken.None);

        sw.Stop();
        Assert.True(sw.ElapsedMilliseconds < 200);
    }

    [Fact]
    public async Task RequestBytesAsync_PerDownloadLimit_ShouldThrottleSequentialCalls()
    {
        var limiter = new SpeedLimiter();
        var sw = Stopwatch.StartNew();

        await limiter.RequestBytesAsync(1000, 1000, CancellationToken.None);
        await limiter.RequestBytesAsync(1000, 1000, CancellationToken.None);

        sw.Stop();
        Assert.True(sw.ElapsedMilliseconds >= 900);
    }
}
