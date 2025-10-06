namespace Alberto.EventStore.Subscriptions.Subscriptions;

/// <summary>
/// Context passed to event handlers containing event metadata
/// </summary>
public sealed record EventContext(
    long GlobalPosition,
    Guid EventId,
    string EventType,
    string TenantId,
    string SubscriptionName,
    IReadOnlyDictionary<string, string> Metadata,
    DateTimeOffset Created
);