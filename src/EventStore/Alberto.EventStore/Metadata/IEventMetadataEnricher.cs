using Alberto.EventStore.Events;

namespace Alberto.EventStore.Metadata;

/// <summary>
/// Enriches event metadata with additional context information.
/// </summary>
public interface IEventMetadataEnricher
{
    /// <summary>
    /// Enriches the event metadata before persistence.
    /// </summary>
    /// <param name="metadata">The current metadata dictionary</param>
    /// <param name="event">The event being persisted</param>
    void Enrich(IDictionary<string, string> metadata, IEventToPersist @event);
}