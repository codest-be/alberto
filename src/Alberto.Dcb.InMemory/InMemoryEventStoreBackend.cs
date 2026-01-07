namespace Alberto.Dcb.InMemory;

/// <summary>
/// In-memory implementation of <see cref="IEventStoreBackend"/>.
/// Mimics the PostgreSQL structure with inverted indexes for efficient querying.
/// Thread-safe for concurrent access.
/// </summary>
public sealed class InMemoryEventStoreBackend : IEventStoreBackend
{
    private readonly object _lock = new();
    private readonly TimeProvider _timeProvider;

    // Main event storage (like 'events' table)
    private readonly List<EventEnvelope> _events = [];

    // Inverted index: (tenantId, eventType) → positions (like 'event_type_positions' table)
    private readonly Dictionary<(string TenantId, string EventType), SortedSet<long>> _typeIndex = new();

    // Inverted index: (tenantId, tag) → positions (like 'event_tag_positions' table)
    private readonly Dictionary<(string TenantId, string Tag), SortedSet<long>> _tagIndex = new();

    private long _nextPosition = 1;

    public InMemoryEventStoreBackend() : this(TimeProvider.System)
    {
    }

    public InMemoryEventStoreBackend(TimeProvider timeProvider)
    {
        _timeProvider = timeProvider;
    }

    public Task<IReadOnlyCollection<IEventEnvelope>> Stream(
        string tenantId,
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
                // Return all events for tenant
                result = _events
                    .Where(e => e.TenantId == tenantId && e.GlobalPosition > afterPosition);
            }
            else
            {
                // Get positions matching types OR tags
                var matchingPositions = new HashSet<long>();

                foreach (var type in query.Types)
                {
                    if (_typeIndex.TryGetValue((tenantId, type.Id), out var positions))
                    {
                        foreach (var pos in positions.Where(p => p > afterPosition))
                            matchingPositions.Add(pos);
                    }
                }

                foreach (var tag in query.Tags)
                {
                    if (_tagIndex.TryGetValue((tenantId, tag.Value), out var positions))
                    {
                        foreach (var pos in positions.Where(p => p > afterPosition))
                            matchingPositions.Add(pos);
                    }
                }

                result = _events
                    .Where(e => matchingPositions.Contains(e.GlobalPosition));
            }

            var ordered = result.OrderBy(e => e.GlobalPosition);
            var limited = limit.HasValue ? ordered.Take(limit.Value) : ordered;

            return Task.FromResult<IReadOnlyCollection<IEventEnvelope>>(limited.ToList());
        }
    }

    public Task<IReadOnlyCollection<IEventEnvelope>> StreamGlobal(
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

    public Task<IReadOnlyCollection<IEventEnvelope>> Append(
        string tenantId,
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
                var conflictPosition = FindConflictPosition(tenantId, dcbQuery, expectedPosition.Value);
                if (conflictPosition.HasValue)
                {
                    throw new DcbConflictException(conflictPosition.Value, expectedPosition.Value, dcbQuery);
                }
            }

            // Append events
            var appended = new List<EventEnvelope>();
            var now = _timeProvider.GetUtcNow();

            foreach (var evt in events)
            {
                var position = _nextPosition++;

                var envelope = new EventEnvelope
                {
                    Id = evt.Id,
                    TenantId = tenantId,
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
                var typeKey = (tenantId, evt.EventType.Id);
                if (!_typeIndex.TryGetValue(typeKey, out var typePositions))
                {
                    typePositions = [];
                    _typeIndex[typeKey] = typePositions;
                }
                typePositions.Add(position);

                // Update tag index
                foreach (var tag in evt.Tags)
                {
                    var tagKey = (tenantId, tag.Value);
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

    public Task<long> GetLastPosition(
        string tenantId,
        CancellationToken cancellationToken = default)
    {
        lock (_lock)
        {
            var lastPosition = _events
                .Where(e => e.TenantId == tenantId)
                .Select(e => e.GlobalPosition)
                .DefaultIfEmpty(0)
                .Max();

            return Task.FromResult(lastPosition);
        }
    }

    public Task<long> GetLastPositionGlobal(
        CancellationToken cancellationToken = default)
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

    /// <summary>
    /// Finds the first position after expectedPosition that matches the DCB query.
    /// Returns null if no conflict exists.
    /// </summary>
    private long? FindConflictPosition(string tenantId, DcbQuery query, long expectedPosition)
    {
        long? conflictPos = null;

        // Check types (OR: any type match is a conflict)
        foreach (var type in query.Types)
        {
            if (_typeIndex.TryGetValue((tenantId, type.Id), out var positions))
            {
                var conflict = positions.FirstOrDefault(p => p > expectedPosition);
                if (conflict > 0 && (conflictPos is null || conflict < conflictPos))
                {
                    conflictPos = conflict;
                }
            }
        }

        // Check tags (OR: any tag match is a conflict)
        foreach (var tag in query.Tags)
        {
            if (_tagIndex.TryGetValue((tenantId, tag.Value), out var positions))
            {
                var conflict = positions.FirstOrDefault(p => p > expectedPosition);
                if (conflict > 0 && (conflictPos is null || conflict < conflictPos))
                {
                    conflictPos = conflict;
                }
            }
        }

        return conflictPos;
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
