namespace Alberto.EventSourcing;

/// <summary>
/// Represents an aggregate with its current state and metadata for optimistic concurrency.
/// </summary>
/// <typeparam name="TState">The type of the aggregate state</typeparam>
public sealed class Aggregate<TState> where TState : new()
{
    /// <summary>
    /// The current state of the aggregate
    /// </summary>
    public required TState State { get; init; }

    /// <summary>
    /// The ID of the last event applied to this aggregate (null if new aggregate)
    /// </summary>
    public Guid? LastEventId { get; init; }

    /// <summary>
    /// Indicates whether this is a new aggregate with no events
    /// </summary>
    public bool IsNew => LastEventId == null;

    /// <summary>
    /// Creates a new aggregate instance
    /// </summary>
    public static Aggregate<TState> Create(TState state, Guid? lastEventId) =>
        new() { State = state, LastEventId = lastEventId };

    /// <summary>
    /// Creates a new empty aggregate
    /// </summary>
    public static Aggregate<TState> Empty() =>
        new() { State = new TState(), LastEventId = null };
}