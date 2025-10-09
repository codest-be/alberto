namespace Alberto.EventSourcing.Projections;

/// <summary>
/// Extracts a projection key from an event.
/// Used by ProjectionRepositorySubscription to determine which projection to update.
/// </summary>
/// <typeparam name="TKey">The type of the projection key</typeparam>
public interface IProjectionKeyExtractor<out TKey> where TKey : notnull
{
    /// <summary>
    /// Extracts the projection key from an event.
    /// Returns null if the event is not relevant to this projection.
    /// </summary>
    /// <param name="event">The event to extract the key from</param>
    /// <returns>The projection key, or null if the event should be ignored</returns>
    TKey? ExtractKey(object @event);
}