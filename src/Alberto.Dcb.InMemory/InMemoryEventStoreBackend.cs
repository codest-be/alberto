namespace Alberto.Dcb.InMemory;

/// <summary>
/// In-memory implementation of <see cref="IEventStoreBackend"/>.
/// Single-tenant mode: no tenant scoping applied.
/// Mimics the PostgreSQL structure with inverted indexes for efficient querying.
/// Thread-safe for concurrent access.
/// </summary>
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

            var ordered = result.OrderBy(e => e.GlobalPosition);
            var limited = limit.HasValue ? ordered.Take(limit.Value) : ordered;

            return Task.FromResult<IReadOnlyCollection<IEventEnvelope>>(limited.ToList());
        }
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
        {
            // DCB conflict check
            if (dcbQuery is not null && expectedPosition.HasValue)
            {
                var conflictPosition = FindConflictPosition(dcbQuery, expectedPosition.Value);
                if (conflictPosition.HasValue)
                {
                    throw new DcbConflictException(conflictPosition.Value, expectedPosition.Value, dcbQuery);
                }
            }

            // Append events
            var appended = new List<EventEnvelope>();
            var now = timeProvider.GetUtcNow().UtcDateTime;

            foreach (var evt in events)
            {
                var position = _nextPosition++;

                var envelope = new EventEnvelope
                {
                    Id = evt.Id,
                    TenantId = null,
                    GlobalPosition = position,
                    EventType = evt.EventType,
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

            return Task.FromResult<IReadOnlyCollection<IEventEnvelope>>(appended);
        }
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
    /// </summary>
    private long? FindConflictPosition(DcbQuery query, long expectedPosition)
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
