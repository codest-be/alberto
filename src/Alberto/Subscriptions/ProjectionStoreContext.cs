namespace Alberto.Subscriptions;

/// <summary>
/// What a projection needs to build its state store: the service provider, and which rebuild
/// version this particular instance of the projection is reading and writing.
/// </summary>
/// <remarks>
/// <para>
/// The same factory builds both the live projection and the shadow copy a rebuild replays into.
/// The only thing that differs between them is <see cref="RebuildVersion"/>, which is a live
/// handle rather than a fixed value: a promotion has to take effect underneath a store that is
/// already running, which is why stores call <see cref="ProjectionVersion.Current"/> per
/// operation rather than caching the version at construction time.
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
    /// The version handle this store writes to and reads from. Pass it straight through to the
    /// state store; read <see cref="ProjectionVersion.Current"/> per operation, never cache it.
    /// </summary>
    public required ProjectionVersion RebuildVersion { get; init; }
}
