namespace Alberto.EventStore.Subscriptions.Checkpoints;

/// <summary>
/// Represents a subscription checkpoint
/// </summary>
public sealed record Checkpoint(
    string SubscriptionId,
    long? Position,
    DateTimeOffset UpdatedAt
);