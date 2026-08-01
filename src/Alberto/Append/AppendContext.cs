namespace Alberto.Append;

/// <summary>
/// Context for append operations, allowing filters to inspect and enrich events before persistence.
/// </summary>
public sealed record AppendContext
{
    /// <summary>
    /// The events to be persisted.
    /// </summary>
    public required IReadOnlyList<IEventToPersist> Events { get; init; }

    /// <summary>
    /// Optional DCB query for consistency boundary checking.
    /// </summary>
    public DcbQuery? DcbQuery { get; init; }

    /// <summary>
    /// Optional expected position for optimistic concurrency.
    /// </summary>
    public long? ExpectedPosition { get; init; }

    /// <summary>
    /// Creates a new context with different events (for enrichment).
    /// </summary>
    public AppendContext WithEvents(IReadOnlyList<IEventToPersist> events)
        => this with { Events = events };
}
