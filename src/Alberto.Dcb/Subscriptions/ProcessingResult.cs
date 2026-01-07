namespace Alberto.Dcb.Subscriptions;

/// <summary>
/// Result of processing a batch of events.
/// </summary>
public enum ProcessingResult
{
    /// <summary>
    /// Continue processing. The consumer should fetch more events.
    /// </summary>
    Continue,

    /// <summary>
    /// Stop processing. The processor wants to pause.
    /// </summary>
    Stop
}
