namespace Alberto.Dcb.Upcasting;

/// <summary>
/// Entry point for declaring an upcast chain.
/// </summary>
/// <example>
/// <code>
/// // Simple one-step upcast: v1 → v2
/// var decl = DeclareUpcaster
///     .For&lt;OrderPlaced&gt;("order-placed")
///     .From&lt;OrderPlacedV1&gt;(1, v1 => new OrderPlaced(v1.OrderId, v1.Amount, "default-region"))
///     .Build();
///
/// // Two-step chain: v1 → v2 → v3
/// var decl = DeclareUpcaster
///     .For&lt;OrderPlacedV3&gt;("order-placed")
///     .From&lt;OrderPlacedV1, OrderPlacedV2&gt;(1, v1 => new OrderPlacedV2(v1.OrderId, v1.Amount))
///     .From&lt;OrderPlacedV2&gt;(2, v2 => new OrderPlacedV3(v2.OrderId, v2.Amount, "default-region"))
///     .Build();
/// </code>
/// </example>
public static class DeclareUpcaster
{
    /// <summary>
    /// Starts building an upcast declaration where <typeparamref name="TEvent"/> is the
    /// <em>current</em> (latest) CLR type for this event type ID.
    /// </summary>
    /// <param name="eventTypeId">
    /// The event type slug (e.g., <c>"order-placed"</c>). Must match the value in the
    /// <see cref="EventTypeAttribute"/> on <typeparamref name="TEvent"/>.
    /// </param>
    public static UpcasterDeclarationBuilder<TEvent> For<TEvent>(string eventTypeId)
        where TEvent : IEvent
        => new(eventTypeId);
}

/// <summary>
/// Fluent builder for an upcast chain targeting <typeparamref name="TEvent"/> as the
/// final output type.
/// </summary>
/// <typeparam name="TEvent">The current (latest) event CLR type.</typeparam>
public sealed class UpcasterDeclarationBuilder<TEvent> where TEvent : IEvent
{
    private readonly string _eventTypeId;
    private readonly List<UpcasterStep> _steps = [];

    internal UpcasterDeclarationBuilder(string eventTypeId)
    {
        if (string.IsNullOrWhiteSpace(eventTypeId))
            throw new ArgumentException("Event type ID cannot be null or whitespace.", nameof(eventTypeId));
        _eventTypeId = eventTypeId;
    }

    /// <summary>
    /// Adds an <b>intermediate</b> upcast step that converts an event at
    /// <paramref name="fromVersion"/> from <typeparamref name="TOld"/> to
    /// <typeparamref name="TNew"/>. Use when the chain has more than one step and
    /// <typeparamref name="TNew"/> is not the final event type <typeparamref name="TEvent"/>.
    /// </summary>
    /// <typeparam name="TOld">The CLR type that represents the event at <paramref name="fromVersion"/>.</typeparam>
    /// <typeparam name="TNew">The CLR type produced by this step (input to the next step).</typeparam>
    /// <param name="fromVersion">The source schema version (must be &gt;= 1).</param>
    /// <param name="transform">Converts an old-version event to the next-version type.</param>
    public UpcasterDeclarationBuilder<TEvent> From<TOld, TNew>(
        int fromVersion,
        Func<TOld, TNew> transform)
        where TOld : class
        where TNew : class
    {
        ValidateVersion(fromVersion);
        _steps.Add(new UpcasterStep(fromVersion, typeof(TOld), obj => transform((TOld)obj)));
        return this;
    }

    /// <summary>
    /// Adds the <b>final</b> upcast step that converts an event at
    /// <paramref name="fromVersion"/> from <typeparamref name="TOld"/> to
    /// <typeparamref name="TEvent"/> (the current version type).
    /// </summary>
    /// <typeparam name="TOld">The CLR type that represents the event at <paramref name="fromVersion"/>.</typeparam>
    /// <param name="fromVersion">The source schema version (must be &gt;= 1).</param>
    /// <param name="transform">Converts an old-version event to the current event type.</param>
    public UpcasterDeclarationBuilder<TEvent> From<TOld>(
        int fromVersion,
        Func<TOld, TEvent> transform)
        where TOld : class
    {
        ValidateVersion(fromVersion);
        _steps.Add(new UpcasterStep(fromVersion, typeof(TOld), obj => transform((TOld)obj)));
        return this;
    }

    /// <summary>
    /// Validates the chain and returns an immutable <see cref="UpcasterDeclaration"/>.
    /// </summary>
    /// <exception cref="InvalidOperationException">
    /// Thrown when the chain has no steps, has gaps between versions, or has duplicate versions
    /// (which would imply a cycle).
    /// </exception>
    public UpcasterDeclaration Build()
    {
        if (_steps.Count == 0)
            throw new InvalidOperationException(
                $"Upcaster for '{_eventTypeId}' has no steps. " +
                "Call From<TOld>(...) at least once before Build().");

        var sorted = _steps.OrderBy(s => s.FromVersion).ToList();

        // Check for duplicate versions (which a cyclic chain would produce).
        var versions = sorted.Select(s => s.FromVersion).ToList();
        if (versions.Distinct().Count() != versions.Count)
        {
            var duplicates = versions.GroupBy(v => v).Where(g => g.Count() > 1).Select(g => g.Key);
            throw new InvalidOperationException(
                $"Upcaster for '{_eventTypeId}' has duplicate steps for version(s) " +
                $"{string.Join(", ", duplicates)}. Each source version may appear at most once.");
        }

        // Check for gaps.
        for (var i = 1; i < sorted.Count; i++)
        {
            if (sorted[i].FromVersion != sorted[i - 1].FromVersion + 1)
                throw new InvalidOperationException(
                    $"Upcaster for '{_eventTypeId}' has a gap between versions " +
                    $"{sorted[i - 1].FromVersion} and {sorted[i].FromVersion}. " +
                    $"Add a step for source version {sorted[i - 1].FromVersion + 1}.");
        }

        return new UpcasterDeclaration(_eventTypeId, sorted);
    }

    private static void ValidateVersion(int fromVersion)
    {
        if (fromVersion < 1)
            throw new ArgumentOutOfRangeException(nameof(fromVersion),
                $"Source version must be >= 1, but was {fromVersion}.");
    }
}
