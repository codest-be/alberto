namespace Alberto.Dcb.Subscriptions;

/// <summary>
/// Determines how to handle a failed event.
/// </summary>
public enum ErrorHandlingDecision
{
    /// <summary>
    /// Retry processing the event.
    /// </summary>
    Retry,

    /// <summary>
    /// Skip the event and continue processing.
    /// </summary>
    Skip,

    /// <summary>
    /// Move the event to dead letter storage and continue processing.
    /// </summary>
    DeadLetter,

    /// <summary>
    /// Stop the processor entirely.
    /// </summary>
    Stop
}
