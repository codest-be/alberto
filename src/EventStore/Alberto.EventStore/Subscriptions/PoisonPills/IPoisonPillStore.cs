namespace Alberto.EventStore.Subscriptions.PoisonPills;

/// <summary>
/// Store for managing poison pill events
/// </summary>
public interface IPoisonPillStore
{
    /// <summary>
    /// Stores a poison pill event
    /// </summary>
    ValueTask StorePoisonPill(PoisonPill poisonPill, CancellationToken ct);

    /// <summary>
    /// Checks if a poison pill exists for a subscription at a position
    /// </summary>
    ValueTask<PoisonPill?> GetPoisonPill(string subscriptionId, long position, CancellationToken ct);

    /// <summary>
    /// Marks a poison pill as resolved
    /// </summary>
    ValueTask ResolvePoisonPill(Guid poisonPillId, string resolvedBy, string action, CancellationToken ct);

    /// <summary>
    /// Gets all poison pills (for cache initialization)
    /// </summary>
    ValueTask<IReadOnlyList<PoisonPill>> GetAllPoisonPills(CancellationToken ct);
}