namespace Alberto.Subscriptions;

/// <summary>
/// A live handle on which rebuild version a projection's state store should read and write.
/// </summary>
/// <remarks>
/// <para>
/// The same factory builds both the live projection and the shadow copy a rebuild replays into.
/// The only thing that differs between them is this version handle. It is a handle rather than a
/// value precisely so a promotion takes effect underneath a running store: every call to
/// <see cref="Current"/> reads through to the live version at that instant.
/// </para>
/// <para>
/// Never cache the result of <see cref="Current"/>. The whole point of the type is that each
/// read is a live read: a single cached integer would not follow a promotion and would leave the
/// store writing into a version that no reader is looking at.
/// </para>
/// <para>
/// <see langword="default"/><c>(ProjectionVersion)</c> behaves as never-rebuilt — that is, its
/// <see cref="Current"/> returns <see cref="ProjectionVersions.Initial"/> — so an optional
/// parameter typed as <c>ProjectionVersion rebuildVersion = default</c> is always safe to omit.
/// </para>
/// </remarks>
public readonly record struct ProjectionVersion
{
    private readonly Func<int>? _selector;

    private ProjectionVersion(Func<int> selector) => _selector = selector;

    /// <summary>
    /// The version this store should read and write right now.
    /// Call this per operation; never cache the result.
    /// </summary>
    public int Current => _selector?.Invoke() ?? ProjectionVersions.Initial;

    /// <summary>
    /// The handle for a projection that is not participating in rebuilds.
    /// Its <see cref="Current"/> always returns <see cref="ProjectionVersions.Initial"/>.
    /// </summary>
    public static ProjectionVersion NeverRebuilt { get; } = default;

    /// <summary>
    /// Wraps <paramref name="selector"/> in a <see cref="ProjectionVersion"/> handle.
    /// The selector is called on every <see cref="Current"/> read, so a promotion
    /// takes effect underneath a store that is already running.
    /// </summary>
    /// <param name="selector">The live version selector. Must not be <see langword="null"/>.</param>
    /// <exception cref="ArgumentNullException"><paramref name="selector"/> is <see langword="null"/>.</exception>
    public static ProjectionVersion From(Func<int> selector)
    {
        ArgumentNullException.ThrowIfNull(selector);
        return new ProjectionVersion(selector);
    }
}
