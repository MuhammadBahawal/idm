using MyDM.Core.Engine;
using Xunit;

namespace MyDM.Core.Tests;

public class RetryPolicyTests
{
    [Fact]
    public void GetDelay_ShouldIncreaseExponentially()
    {
        var policy = new RetryPolicy();
        var delay1 = policy.GetDelay(1);
        var delay2 = policy.GetDelay(2);
        var delay3 = policy.GetDelay(3);

        Assert.True(delay2 > delay1);
        Assert.True(delay3 > delay2);
    }

    [Fact]
    public void GetDelay_ShouldNotExceedMaxDelay()
    {
        var policy = new RetryPolicy();
        var delay = policy.GetDelay(100); // Very high retry count
        Assert.True(delay.TotalMinutes <= 5); // Should cap at default MaxDelay
    }

    [Fact]
    public void IsRetryableStatusCode_ShouldClassifyCorrectly()
    {
        // Server errors → should retry
        Assert.True(RetryPolicy.IsRetryableStatusCode(500));
        Assert.True(RetryPolicy.IsRetryableStatusCode(502));
        Assert.True(RetryPolicy.IsRetryableStatusCode(503));
        Assert.True(RetryPolicy.IsRetryableStatusCode(429)); // Rate limited
        Assert.True(RetryPolicy.IsRetryableStatusCode(408)); // Timeout

        // Client errors → should not retry
        Assert.False(RetryPolicy.IsRetryableStatusCode(404));
        Assert.False(RetryPolicy.IsRetryableStatusCode(403));
        Assert.False(RetryPolicy.IsRetryableStatusCode(401));
        Assert.False(RetryPolicy.IsRetryableStatusCode(200)); // Success
    }

    [Fact]
    public void IsRetryable_IOException_ShouldRetry()
    {
        Assert.True(RetryPolicy.IsRetryable(new IOException("connection reset")));
    }

    [Fact]
    public void IsRetryable_TaskCancelled_ShouldNotRetry()
    {
        Assert.False(RetryPolicy.IsRetryable(new TaskCanceledException()));
    }
}
