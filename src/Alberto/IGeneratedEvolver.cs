using System.ComponentModel;

namespace Alberto;

/// <summary>
/// The dispatch table Alberto's source generator writes for a <c>partial</c>
/// <see cref="Evolver{TState}"/>, so the evolver never has to be discovered by reflection or
/// compiled with <c>System.Linq.Expressions</c> (which Native AOT can only interpret).
/// Not meant to be implemented by hand.
/// </summary>
/// <typeparam name="TState">The state the evolver folds.</typeparam>
[EditorBrowsable(EditorBrowsableState.Never)]
public interface IGeneratedEvolver<TState>
{
    /// <summary>
    /// The evolver type the table was generated for. A subclass inherits the interface but not
    /// its own <see cref="IEvolve{TState,TEvent}"/>s, so the table is only used when this is the
    /// runtime type; anything else falls back to reflection rather than drop handlers.
    /// </summary>
    Type GeneratedFor { get; }

    /// <summary>One entry per <see cref="IEvolve{TState,TEvent}"/>: the event type and a call to its <c>Apply</c>.</summary>
    IReadOnlyList<(Type EventType, Func<TState, object, TState> Apply)> EvolveTable();
}
