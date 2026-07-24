namespace Alberto.Dcb.Subscriptions;

/// <summary>
/// What a projection needs to build its state store: the service provider, and which rebuild
/// version this particular instance of the projection is reading and writing.
/// </summary>
/// <remarks>
/// The same factory builds both the live projection and the shadow copy a rebuild replays into.
/// The only thing that differs between them is <see cref="RebuildVersion"/>, which is why it
/// arrives as a selector the store calls per operation rather than as a fixed value: a
/// promotion has to take effect underneath a store that is already running.
/// </remarks>
/// <param name="Services">The application's service provider.</param>
/// <param name="RebuildVersion">
/// The version this store writes to and reads from. Pass it straight through to the state
/// store; do not call it at construction time and cache the result.
/// </param>
public readonly record struct ProjectionStoreContext(
    IServiceProvider Services,
    Func<int> RebuildVersion);
