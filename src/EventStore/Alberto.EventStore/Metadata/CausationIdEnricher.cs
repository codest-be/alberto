using Alberto.EventStore.Events;

namespace Alberto.EventStore.Metadata;

/// <summary>
/// Enriches events with causation ID to track the direct cause-and-effect relationship.
/// Causation ID points to the event that directly triggered this event.
/// </summary>
public class CausationIdEnricher : IEventMetadataEnricher
{
    private static readonly AsyncLocal<string?> _causationId = new();

    public void Enrich(IDictionary<string, string> metadata, IEventToPersist @event)
    {
        var causationId = _causationId.Value;

        if (!string.IsNullOrEmpty(causationId))
        {
            metadata["causation_id"] = causationId;
        }
    }

    /// <summary>
    /// Sets the causation ID for the current async context.
    /// Typically set to the ID of the event being processed in a subscription handler.
    /// </summary>
    public static void SetCausationId(string causationId)
    {
        _causationId.Value = causationId;
    }

    /// <summary>
    /// Sets the causation ID from an event ID (Guid).
    /// </summary>
    public static void SetCausationId(Guid eventId)
    {
        _causationId.Value = eventId.ToString();
    }

    /// <summary>
    /// Gets the current causation ID, or null if not set.
    /// </summary>
    public static string? GetCausationId() => _causationId.Value;

    /// <summary>
    /// Clears the causation ID for the current async context.
    /// </summary>
    public static void ClearCausationId()
    {
        _causationId.Value = null;
    }
}