namespace Alberto.Dcb.Subscriptions;

/// <summary>
/// Configuration for event processing error handling.
/// </summary>
public sealed class ErrorPolicy
{
    /// <summary>
    /// Maximum number of retry attempts before escalating.
    /// Default is 3.
    /// </summary>
    public int MaxRetries { get; init; } = 3;

    /// <summary>
    /// Base delay between retry attempts (used for first retry).
    /// Subsequent retries use exponential backoff: delay * backoffMultiplier^(attempt-1).
    /// Default is 1 second.
    /// </summary>
    public TimeSpan RetryDelay { get; init; } = TimeSpan.FromSeconds(1);

    /// <summary>
    /// Multiplier for exponential backoff between retries.
    /// Set to 1.0 for constant delay.
    /// Default is 2.0 (double the delay each retry).
    /// </summary>
    public double BackoffMultiplier { get; init; } = 2.0;

    /// <summary>
    /// Maximum delay between retries (cap for exponential backoff).
    /// Default is 30 seconds.
    /// </summary>
    public TimeSpan MaxRetryDelay { get; init; } = TimeSpan.FromSeconds(30);

    /// <summary>
    /// Whether to dead-letter events that exceed max retries.
    /// If false, events are skipped after max retries.
    /// Default is true.
    /// </summary>
    public bool DeadLetterOnMaxRetries { get; init; } = true;

    /// <summary>
    /// Error classifier used to determine if errors are transient or permanent.
    /// Permanent errors skip retries and go directly to dead-letter.
    /// Default is <see cref="DefaultErrorClassifier"/>.
    /// </summary>
    public IErrorClassifier ErrorClassifier { get; init; } = DefaultErrorClassifier.Instance;

    /// <summary>
    /// Default policy with standard settings.
    /// </summary>
    public static ErrorPolicy Default { get; } = new();

    /// <summary>
    /// Calculates the delay for a given retry attempt using exponential backoff.
    /// </summary>
    /// <param name="attemptNumber">The attempt number (1-based).</param>
    /// <returns>The delay to wait before the next retry.</returns>
    public TimeSpan CalculateDelay(int attemptNumber)
    {
        if (attemptNumber <= 1)
            return RetryDelay;

        var multiplier = Math.Pow(BackoffMultiplier, attemptNumber - 1);
        var delay = TimeSpan.FromMilliseconds(RetryDelay.TotalMilliseconds * multiplier);

        // Cap at MaxRetryDelay
        return delay > MaxRetryDelay ? MaxRetryDelay : delay;
    }
}
