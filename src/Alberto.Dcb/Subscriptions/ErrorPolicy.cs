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
    /// Delay between retry attempts.
    /// Default is 1 second.
    /// </summary>
    public TimeSpan RetryDelay { get; init; } = TimeSpan.FromSeconds(1);

    /// <summary>
    /// Whether to dead-letter events that exceed max retries.
    /// If false, events are skipped after max retries.
    /// Default is true.
    /// </summary>
    public bool DeadLetterOnMaxRetries { get; init; } = true;

    /// <summary>
    /// Default policy with standard settings.
    /// </summary>
    public static ErrorPolicy Default { get; } = new();
}
