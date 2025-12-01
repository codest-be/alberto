using Alberto.EventSourcing.Projectors;
using Alberto.EventStore;

namespace Alberto.EventSourcing.Deciders;

/// <summary>
/// Represents a decider that can project events into state and construct queries for loading aggregates.
/// Extends IProjector with query construction capability to reduce boilerplate in command handlers.
/// The query specification is strongly typed and co-located with the decider.
/// </summary>
/// <typeparam name="TState">The type of state that this decider projects to</typeparam>
/// <typeparam name="TQuerySpec">The type of the query specification (typically a nested record in the decider)</typeparam>
[Obsolete(
    "IDecider is no longer used. Use IProjector<TState> directly and construct StreamQuery explicitly in command handlers. This interface will be removed in a future version.")]
public interface IDecider<TState, in TQuerySpec> : IProjector<TState> where TState : new()
{
    /// <summary>
    /// Constructs a StreamQuery for loading the aggregate using a strongly-typed query specification.
    /// </summary>
    /// <param name="spec">The query specification containing the parameters needed to construct the query</param>
    /// <returns>A StreamQuery configured for loading this aggregate's events</returns>
    StreamQuery GetQuery(TQuerySpec spec);
}