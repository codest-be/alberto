namespace Alberto.Dcb.InMemory;

/// <summary>
/// In-memory implementation of <see cref="IEventStoreBackend"/>.
/// Mimics the PostgreSQL structure with inverted indexes for efficient querying.
/// Thread-safe for concurrent access.
/// </summary>
/// <remarks>
/// <para>
/// <strong>Single-tenant mode</strong> (default, via <c>.WithInMemory()</c> without <c>.WithTenancy()</c>):
/// <see cref="IEventStoreBackend.AppendAsync"/> records every event with <c>TenantId = null</c>,
/// matching the Postgres schema that has no <c>tenant_id</c> column.
/// </para>
/// <para>
/// <strong>Multi-tenant mode</strong> (via <c>.WithInMemory()</c> plus <c>.WithTenancy()</c>):
/// <see cref="InMemoryTenantEventStoreDecorator"/> wraps this backend and calls
/// <see cref="AppendForTenant"/> / <see cref="StreamForTenant"/> instead, stamping and
/// filtering by the tenant ID supplied from <c>ITenantAccessor</c> — the same way
/// <c>TenantEventStoreDecorator</c> wraps the Postgres tenant backend.
/// </para>
/// </remarks>
public sealed class InMemoryEventStoreBackend(TimeProvider timeProvider) : IEventStoreBackend, IEventStoreHeadBackend
{
    private readonly object _lock = new();

    // Main event storage (like 'events' table)
    private readonly List<EventEnvelope> _events = [];

    // Inverted index: eventType → positions (like 'event_type_positions' table)
    private readonly Dictionary<string, SortedSet<long>> _typeIndex = new();

    // Inverted index: tag → positions (like 'event_tag_positions' table)
    private readonly Dictionary<string, SortedSet<long>> _tagIndex = new();

    private long _nextPosition = 1;

    public InMemoryEventStoreBackend() : this(TimeProvider.System)
    {
    }

    public Task<IReadOnlyCollection<IEventEnvelope>> StreamAsync(
        DcbQuery query,
        long afterPosition = 0,
        int? limit = null,
        CancellationToken cancellationToken = default)
    {
        lock (_lock)
            return Task.FromResult(StreamCore(query, tenantId: null, afterPosition, limit));
    }

    /// <summary>
    /// Streams events for a specific tenant. Used by <see cref="InMemoryTenantEventStoreDecorator"/>
    /// in multi-tenant mode; mirrors <c>PostgresTenantEventStoreBackend.StreamForTenant</c>.
    /// </summary>
    internal Task<IReadOnlyCollection<IEventEnvelope>> StreamForTenant(
        string tenantId,
        DcbQuery query,
        long afterPosition = 0,
        int? limit = null,
        CancellationToken cancellationToken = default)
    {
        lock (_lock)
            return Task.FromResult(StreamCore(query, tenantId, afterPosition, limit));
    }

    /// <summary>
    /// Streams all events across all tenants. Used by <see cref="InMemoryTenantEventStoreDecorator"/>
    /// for the ControlLoop consumer path; mirrors <c>PostgresTenantEventStoreBackend.StreamAllTenants</c>.
    /// </summary>
    internal Task<IReadOnlyCollection<IEventEnvelope>> StreamAllTenants(
        long afterPosition = 0,
        int? limit = null,
        CancellationToken cancellationToken = default)
        => StreamAllAsync(afterPosition, limit, cancellationToken);

    /// <summary>
    /// Shared implementation for <see cref="StreamAsync"/> and <see cref="StreamForTenant"/>.
    /// Must be called under <see cref="_lock"/>.
    /// When <paramref name="tenantId"/> is non-null the result is filtered to that tenant only.
    /// </summary>
    private IReadOnlyCollection<IEventEnvelope> StreamCore(DcbQuery query, string? tenantId, long afterPosition, int? limit)
    {
        IEnumerable<EventEnvelope> result;

        if (query.IsEmpty)
        {
            result = _events.Where(e => e.GlobalPosition > afterPosition);
        }
        else
        {
            var typeMatches = query.Types.Count > 0
                ? CollectTypeMatches(query, afterPosition)
                : null;

            var tagMatches = query.Tags.Count > 0
                ? CollectTagMatches(query, afterPosition)
                : null;

            HashSet<long> matchingPositions;
            if (typeMatches is not null && tagMatches is not null)
            {
                matchingPositions = query.IntersectsTypesAndTags
                    ? new HashSet<long>(typeMatches.Where(tagMatches.Contains))
                    : Union(typeMatches, tagMatches);
            }
            else
            {
                matchingPositions = typeMatches ?? tagMatches!;
            }

            result = _events.Where(e => matchingPositions.Contains(e.GlobalPosition));
        }

        // In multi-tenant mode the caller supplies a tenant filter; in single-tenant mode
        // (tenantId == null) all events are returned regardless of their stored TenantId.
        if (tenantId is not null)
            result = result.Where(e => e.TenantId == tenantId);

        var ordered = result.OrderBy(e => e.GlobalPosition);
        var limited = limit.HasValue ? ordered.Take(limit.Value) : ordered;
        return limited.ToList();
    }

    private HashSet<long> CollectTypeMatches(DcbQuery query, long afterPosition)
    {
        var matches = new HashSet<long>();
        foreach (var type in query.Types)
        {
            if (_typeIndex.TryGetValue(type.Id, out var positions))
            {
                foreach (var pos in positions.Where(p => p > afterPosition))
                    matches.Add(pos);
            }
        }
        return matches;
    }

    private HashSet<long> CollectTagMatches(DcbQuery query, long afterPosition)
    {
        var matches = new HashSet<long>();

        if (query.RequiresAllTags)
        {
            IEnumerable<long>? intersected = null;
            foreach (var tag in query.Tags)
            {
                if (!_tagIndex.TryGetValue(tag.Value, out var positions))
                {
                    intersected = [];
                    break;
                }

                var filtered = positions.Where(p => p > afterPosition);
                intersected = intersected is null
                    ? filtered.ToArray()
                    : intersected.Intersect(filtered).ToArray();
            }

            if (intersected is not null)
            {
                foreach (var pos in intersected)
                    matches.Add(pos);
            }

            return matches;
        }

        foreach (var tag in query.Tags)
        {
            if (_tagIndex.TryGetValue(tag.Value, out var positions))
            {
                foreach (var pos in positions.Where(p => p > afterPosition))
                    matches.Add(pos);
            }
        }

        return matches;
    }

    private static HashSet<long> Union(HashSet<long> a, HashSet<long> b)
    {
        var result = new HashSet<long>(a);
        result.UnionWith(b);
        return result;
    }

    public Task<IReadOnlyCollection<IEventEnvelope>> StreamAllAsync(
        long afterPosition = 0,
        int? limit = null,
        CancellationToken cancellationToken = default)
    {
        lock (_lock)
        {
            var result = _events
                .Where(e => e.GlobalPosition > afterPosition)
                .OrderBy(e => e.GlobalPosition);

            var limited = limit.HasValue ? result.Take(limit.Value) : result;

            return Task.FromResult<IReadOnlyCollection<IEventEnvelope>>(limited.ToList());
        }
    }

    public Task<IReadOnlyCollection<IEventEnvelope>> AppendAsync(
        IEnumerable<IEventToPersist> events,
        DcbQuery? dcbQuery = null,
        long? expectedPosition = null,
        CancellationToken cancellationToken = default)
    {
        lock (_lock)
            return Task.FromResult(AppendCore(tenantId: null, events, dcbQuery, expectedPosition));
    }

    /// <summary>
    /// Appends events tagged with the given tenant. Used by <see cref="InMemoryTenantEventStoreDecorator"/>
    /// in multi-tenant mode; mirrors <c>PostgresTenantEventStoreBackend.AppendForTenant</c>.
    /// </summary>
    internal Task<IReadOnlyCollection<IEventEnvelope>> AppendForTenant(
        string tenantId,
        IEnumerable<IEventToPersist> events,
        DcbQuery? dcbQuery = null,
        long? expectedPosition = null,
        CancellationToken cancellationToken = default)
    {
        lock (_lock)
            return Task.FromResult(AppendCore(tenantId, events, dcbQuery, expectedPosition));
    }

    /// <summary>
    /// Shared append logic for single-tenant and multi-tenant paths.
    /// Must be called under <see cref="_lock"/>.
    /// When <paramref name="tenantId"/> is non-null, that ID is stamped on every appended event
    /// and conflict detection is scoped to that tenant only — matching the Postgres behaviour where
    /// DCB boundaries are per-tenant.
    /// </summary>
    private IReadOnlyCollection<IEventEnvelope> AppendCore(
        string? tenantId,
        IEnumerable<IEventToPersist> events,
        DcbQuery? dcbQuery,
        long? expectedPosition)
    {
        // DCB conflict check — scoped to the tenant when one is provided, so events from
        // other tenants never cause a false conflict (mirroring the Postgres tenant_id WHERE clause).
        if (dcbQuery is not null && expectedPosition.HasValue)
        {
            var conflictPosition = FindConflictPosition(dcbQuery, expectedPosition.Value, tenantId);
            if (conflictPosition.HasValue)
            {
                throw new DcbConflictException(conflictPosition.Value, expectedPosition.Value, dcbQuery);
            }
        }

        // Append events
        var appended = new List<EventEnvelope>();
        var now = timeProvider.GetUtcNow();

        foreach (var evt in events)
        {
            var position = _nextPosition++;

            // Derive the schema version from the stored _version:N tag rather than trusting
            // EventType.Version on the input object.  This mirrors what the Postgres
            // backend does when it reconstructs an EventType on read: storage is the
            // source of truth, so the envelope handed to the upcaster chain is driven
            // by the tag, not by whatever the caller's in-memory object said at write
            // time.  Absent tag → v1 (pre-versioning rows).
            var schemaVersion = EventVersionTag.ParseFromTags(evt.Tags);

            var envelope = new EventEnvelope
            {
                Id = evt.Id,
                TenantId = tenantId,   // null in single-tenant mode; caller-supplied in multi-tenant
                GlobalPosition = position,
                EventType = new EventType(evt.EventType.Id, schemaVersion),
                Tags = evt.Tags,
                EventData = evt.EventData,
                Metadata = evt.Metadata,
                CreatedAt = now
            };

            _events.Add(envelope);
            appended.Add(envelope);

            // Update type index
            var typeKey = evt.EventType.Id;
            if (!_typeIndex.TryGetValue(typeKey, out var typePositions))
            {
                typePositions = [];
                _typeIndex[typeKey] = typePositions;
            }
            typePositions.Add(position);

            // Update tag index
            foreach (var tag in evt.Tags)
            {
                var tagKey = tag.Value;
                if (!_tagIndex.TryGetValue(tagKey, out var tagPositions))
                {
                    tagPositions = [];
                    _tagIndex[tagKey] = tagPositions;
                }
                tagPositions.Add(position);
            }
        }

        return appended;
    }

    public Task<long> GetLastPositionAsync(CancellationToken cancellationToken = default)
    {
        lock (_lock)
        {
            var lastPosition = _events
                .Select(e => e.GlobalPosition)
                .DefaultIfEmpty(0)
                .Max();

            return Task.FromResult(lastPosition);
        }
    }

    public Task<IReadOnlyList<long>> GetPositionsAsync(
        long afterPosition, int windowSize, CancellationToken cancellationToken = default)
    {
        lock (_lock)
        {
            var ceiling = afterPosition + windowSize;
            var result = _events
                .Where(e => e.GlobalPosition > afterPosition && e.GlobalPosition <= ceiling)
                .Select(e => e.GlobalPosition)
                .OrderBy(p => p)
                .ToList();
            return Task.FromResult<IReadOnlyList<long>>(result);
        }
    }

    /// <summary>
    /// Finds the first position after expectedPosition that matches the DCB query.
    /// Returns null if no conflict exists.
    /// When <paramref name="tenantId"/> is non-null, only positions belonging to that tenant
    /// are considered — matching the per-tenant WHERE clause in the Postgres append path.
    /// </summary>
    private long? FindConflictPosition(DcbQuery query, long expectedPosition, string? tenantId)
    {
        if (query.IsEmpty)
            return null;

        var typeMatches = query.Types.Count > 0
            ? CollectTypeMatches(query, expectedPosition)
            : null;

        var tagMatches = query.Tags.Count > 0
            ? CollectTagMatches(query, expectedPosition)
            : null;

        IEnumerable<long> matches;
        if (typeMatches is not null && tagMatches is not null)
        {
            matches = query.IntersectsTypesAndTags
                ? typeMatches.Where(tagMatches.Contains)
                : typeMatches.Concat(tagMatches);
        }
        else
        {
            matches = (IEnumerable<long>?)typeMatches ?? tagMatches!;
        }

        // Scope conflicts to the given tenant so that appends from different tenants
        // never interfere with each other — the same isolation the Postgres schema
        // achieves by partitioning the events table on tenant_id.
        if (tenantId is not null)
        {
            var tenantPositions = _events
                .Where(e => e.TenantId == tenantId)
                .Select(e => e.GlobalPosition)
                .ToHashSet();
            matches = matches.Where(tenantPositions.Contains);
        }

        long? conflict = null;
        foreach (var pos in matches)
        {
            if (conflict is null || pos < conflict)
                conflict = pos;
        }

        return conflict;
    }

    /// <summary>
    /// Clears all events from the store. Useful for testing.
    /// </summary>
    public void Clear()
    {
        lock (_lock)
        {
            _events.Clear();
            _typeIndex.Clear();
            _tagIndex.Clear();
            _nextPosition = 1;
        }
    }
}
