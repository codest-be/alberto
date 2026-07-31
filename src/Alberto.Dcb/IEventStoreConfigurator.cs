using Alberto.Dcb.Subscriptions;

namespace Alberto.Dcb;

/// <summary>
/// Setup-time surface for configuring an <see cref="IEventStore"/> before it is used at runtime.
/// Implemented by <see cref="EventStore"/> and consumed exclusively by builder/registration
/// code — never by runtime consumers.
/// </summary>
/// <remarks>
/// Keeping setup methods here rather than on <see cref="IEventStore"/> prevents runtime code that
/// holds only an <see cref="IEventStore"/> reference from accidentally registering projections or
/// handlers after the store has already started serving requests.
/// </remarks>
internal interface IEventStoreConfigurator
{
    /// <summary>
    /// Registers an inline projection that runs immediately after events are appended.
    /// Accepts any <see cref="IInlineProjection"/> implementation —
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
