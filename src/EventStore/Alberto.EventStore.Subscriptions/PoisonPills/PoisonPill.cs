namespace Alberto.EventStore.Subscriptions.PoisonPills;

/// <summary>
/// Represents an event that failed processing after retries
/// </summary>
public sealed record PoisonPill(
    Guid Id,
    string SubscriptionId,
    long GlobalPosition,
    Guid EventId,
    string EventType,
    string EventData,
    string Metadata,
    string ErrorMessage,
    string? StackTrace,
    int RetryCount,
    DateTimeOffset FirstFailedAt,
    DateTimeOffset LastFailedAt,
    DateTimeOffset? ResolvedAt,
    string? ResolvedBy,
    string? ResolutionAction
);