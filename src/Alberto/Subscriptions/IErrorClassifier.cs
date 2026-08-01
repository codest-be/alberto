namespace Alberto.Subscriptions;

/// <summary>
/// Classification of errors for retry behavior.
/// </summary>
public enum ErrorClassification
{
    /// <summary>
    /// Error classification is unknown. Default retry behavior applies.
    /// </summary>
    Unknown,

    /// <summary>
    /// Transient error that may succeed on retry (timeout, network issues, etc.).
    /// Should be retried with exponential backoff.
    /// </summary>
    Transient,

    /// <summary>
    /// Permanent error that will not succeed on retry (validation, not found, etc.).
    /// Should be dead-lettered immediately without retries.
    /// </summary>
    Permanent
}

/// <summary>
/// Classifies exceptions to determine appropriate retry behavior.
/// </summary>
public interface IErrorClassifier
{
    /// <summary>
    /// Classifies an exception to determine if it's transient or permanent.
    /// </summary>
    /// <param name="exception">The exception to classify.</param>
    /// <returns>The classification of the error.</returns>
    ErrorClassification Classify(Exception exception);
}
