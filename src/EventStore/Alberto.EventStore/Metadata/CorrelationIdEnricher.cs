using System.Diagnostics;
using Alberto.EventStore.Events;

namespace Alberto.EventStore.Metadata;

/// <summary>
/// Enriches events with correlation ID for tracking related operations across aggregates.
/// Correlation ID groups all events that are part of the same business transaction.
/// </summary>
public class CorrelationIdEnricher : IEventMetadataEnricher
{
    private static readonly AsyncLocal<string?> CorrelationId = new();

    public void Enrich(IDictionary<string, string> metadata, IEventToPersist @event)
    {
        // Try to get correlation ID from async local context
        var correlationId = CorrelationId.Value;

        // If not set, try to extract from current Activity (OpenTelemetry)
        if (string.IsNullOrEmpty(correlationId))
        {
            var currentActivity = Activity.Current;
            if (currentActivity != null)
            {
                // Use TraceId as correlation ID if available
                correlationId = currentActivity.TraceId.ToString();
            }
        }

        // If still not set, generate a new one
        if (string.IsNullOrEmpty(correlationId))
        {
            correlationId = Guid.NewGuid().ToString();
            CorrelationId.Value = correlationId;
        }

        metadata["correlation_id"] = correlationId;
    }

    /// <summary>
    /// Sets the correlation ID for the current async context.
    /// All events appended within this context will have this correlation ID.
    /// </summary>
    public static void SetCorrelationId(string correlationId)
    {
        CorrelationId.Value = correlationId;
    }

    /// <summary>
    /// Gets the current correlation ID, or null if not set.
    /// </summary>
    public static string? GetCorrelationId() => CorrelationId.Value;

    /// <summary>
    /// Clears the correlation ID for the current async context.
    /// </summary>
    public static void ClearCorrelationId()
    {
        CorrelationId.Value = null;
    }
}