namespace Alberto.Dcb.Subscriptions;

/// <summary>
/// Represents an event that failed processing and was moved to dead letter storage.
/// </summary>
/// <param name="Id">Unique identifier for this dead letter entry.</param>
/// <param name="ProcessorId">The processor that failed to handle the event.</param>
/// <param name="EventId">The original event's identifier.</param>
/// <param name="EventType">The type of the failed event.</param>
/// <param name="EventData">The serialized event data.</param>
/// <param name="ErrorMessage">The error message from the last failure.</param>
/// <param name="StackTrace">The stack trace from the last failure.</param>
/// <param name="AttemptCount">Total number of processing attempts.</param>
/// <param name="FailedAt">When the event was moved to dead letter.</param>
/// <param name="GlobalPosition">Position of the original event in the global log (0 if unknown).</param>
public sealed record DeadLetterEntry(
    Guid Id,
    string ProcessorId,
    Guid EventId,
    string EventType,
    string EventData,
    string ErrorMessage,
    string? StackTrace,
    int AttemptCount,
    DateTimeOffset FailedAt,
    long GlobalPosition = 0);
