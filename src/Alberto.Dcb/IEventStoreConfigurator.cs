#pragma warning disable CS0618 // Obsolete projection types referenced intentionally for backward-compatibility
using Alberto.Dcb.Subscriptions;

namespace Alberto.Dcb;

/// <summary>
/// Setup-time surface for configuring an <see cref="IEventStore"/> before it is used at runtime.
/// Implemented by the concrete event-store classes (<c>PostgresEventStore</c>, <c>InMemoryEventStore</c>)
/// and consumed exclusively by builder/registration code — never by runtime consumers.
/// </summary>
/// <remarks>
/// Keeping setup methods here rather than on <see cref="IEventStore"/> prevents runtime code that
/// holds only an <see cref="IEventStore"/> reference from accidentally registering projections or
/// handlers after the store has already started serving requests.
/// </remarks>
public interface IEventStoreConfigurator
{
    /// <summary>
    /// Registers an inline projection that runs immediately after events are appended.
    /// Uses the same <see cref="Projection{TState}"/> definition as async projections,
    /// but updates state synchronously for lower latency.
    /// </summary>
    /// <typeparam name="TState">The projection state type.</typeparam>
    /// <typeparam name="TProjection">The projection implementation type.</typeparam>
    /// <param name="stateStore">The state store for persisting projection state.</param>
    void RegisterInlineProjection<TState, TProjection>(IStateStore<TState> stateStore)
        where TProjection : Projection<TState>, new()
        where TState : new();

    /// <summary>
    /// Registers an inline projection that runs immediately after events are appended.
    /// Lower-level overload that accepts any <see cref="IInlineProjection"/> implementation —
    /// used by declaration-based projection wiring.
    /// </summary>
    /// <param name="projection">The inline projection to register.</param>
    void RegisterInlineProjection(IInlineProjection projection);

    /// <summary>
    /// Registers a post-append handler that runs immediately after events are appended
    /// and inline projections have completed. Used by <see cref="ReactorMode.Sync"/> reactors.
    /// </summary>
    /// <param name="handler">The handler to register.</param>
    void RegisterPostAppendHandler(IPostAppendHandler handler);
}
