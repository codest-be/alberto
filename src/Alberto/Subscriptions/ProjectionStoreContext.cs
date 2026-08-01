namespace Alberto.Subscriptions;

/// <summary>
/// What a projection needs to build its state store: the service provider, and which rebuild
/// version this particular instance of the projection is reading and writing.
/// </summary>
/// <remarks>
/// <para>
/// The same factory builds both the live projection and the shadow copy a rebuild replays into.
/// The only thing that differs between them is <see cref="RebuildVersion"/>, which is why it
/// arrives as a selector the store calls per operation rather than as a fixed value: a
/// promotion has to take effect underneath a store that is already running.
/// </para>
/// <para>
/// The tenant is deliberately not here. It varies per store rather than per projection — the
/// consumer resolves one store per tenant — so it arrives as the argument of the
/// <c>Func&lt;string?, IStateStore&lt;TState&gt;&gt;</c> this context is used to build. Anything
/// captured while building that delegate is therefore shared across every tenant, which is what
/// a cross-tenant projection holding a single in-memory dictionary depends on.
/// </para>
/// </remarks>
public readonly record struct ProjectionStoreContext
{
    /// <summary>The application's service provider.</summary>
    public required IServiceProvider Services { get; init; }

    /// <summary>
    /// The version this store writes to and reads from. Pass it straight through to the state
    /// store; do not call it at construction time and cache the result.
    /// </summary>
    public required Func<int> RebuildVersion { get; init; }
}
