namespace MyDM.Core.Engine;

/// <summary>
/// Retry policy with exponential backoff and error classification.
/// </summary>
public class RetryPolicy
{
    public int MaxRetries { get; set; } = 10;
    public TimeSpan InitialDelay { get; set; } = TimeSpan.FromSeconds(2);
    public TimeSpan MaxDelay { get; set; } = TimeSpan.FromMinutes(5);
    public double BackoffMultiplier { get; set; } = 2.0;

    /// <summary>
    /// Calculate the delay for a given retry attempt.
    /// </summary>
    public TimeSpan GetDelay(int retryCount)
    {
        var delay = InitialDelay.TotalMilliseconds * Math.Pow(BackoffMultiplier, retryCount);
        // Add jitter (±20%)
        var jitter = delay * 0.2 * (Random.Shared.NextDouble() * 2 - 1);
        delay += jitter;
        delay = Math.Min(delay, MaxDelay.TotalMilliseconds);
        return TimeSpan.FromMilliseconds(Math.Max(delay, 100));
    }

    /// <summary>
    /// Classify whether an error is retryable.
    /// </summary>
    public static bool IsRetryable(Exception ex)
    {
        return ex switch
        {
            HttpRequestException httpEx => httpEx.StatusCode switch
            {
                System.Net.HttpStatusCode.TooManyRequests => true,
                System.Net.HttpStatusCode.ServiceUnavailable => true,
                System.Net.HttpStatusCode.GatewayTimeout => true,
                System.Net.HttpStatusCode.BadGateway => true,
                System.Net.HttpStatusCode.InternalServerError => true,
                System.Net.HttpStatusCode.RequestTimeout => true,
                null => true, // Connection error
                _ => false
            },
            TaskCanceledException => false,
            OperationCanceledException => false,
            IOException => true,
            System.Net.Sockets.SocketException => true,
            _ => false
        };
    }

    /// <summary>
    /// Classify an HTTP status code.
    /// </summary>
    public static bool IsRetryableStatusCode(int statusCode)
    {
        return statusCode is 408 or 429 or 500 or 502 or 503 or 504;
    }
}
