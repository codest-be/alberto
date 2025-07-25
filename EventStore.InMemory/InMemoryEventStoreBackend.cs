using System.Collections.Concurrent;
using EventStore.Events;
using EventStore.Exceptions;
using EventStore.MultiTenant;
using Microsoft.Extensions.Logging;

namespace EventStore.InMemory;

/// <summary>
/// In-memory implementation of IEventStore for testing purposes
/// </summary>
public class InMemoryEventStoreBackend(ILogger<InMemoryEventStoreBackend> logger) : IEventStoreBackend
{
    private readonly ConcurrentDictionary<string, ConcurrentDictionary<Guid, StoredEvent>> _events =
        new ConcurrentDictionary<string, ConcurrentDictionary<Guid, StoredEvent>>();

    private readonly SemaphoreSlim _mutex = new SemaphoreSlim(1, 1);
    private long _globalPosition = 0;

    public IEnumerable<IEventEnvelope> Events => _events.SelectMany(x => x.Value).Select(x => x.Value)
        .OrderBy(e => e.Position)
        .Select(IEventEnvelope (e) => new EventEnvelope
        {
            Id = e.Id,
            EventType = e.EventType,
            EventJson = e.EventJson,
            Metadata = new Dictionary<string, string>(e.Metadata)
            {
                ["_position"] = e.Position.ToString()
            },
            Created = e.Created
        });

    /// <inheritdoc />
    public Task<IReadOnlyCollection<IEventEnvelope>> Stream(
        Tenant tenant,
        StreamQuery query,
        int? maxCount = null,
        CancellationToken cancellationToken = default)
    {
        try
        {
            // Get events for this tenant
            var tenantEvents = _events.GetOrAdd(tenant.Id, _ => new ConcurrentDictionary<Guid, StoredEvent>());

            // Filter events by query criteria
            var filteredEvents = tenantEvents.Values
                .AsParallel()
                .Where(e => MatchesQuery(e, query))
                .OrderBy(e => e.Position) // Use position for consistent ordering like PostgreSQL
                .ToList();

            // Apply maxCount if specified
            if (maxCount.HasValue && maxCount.Value > 0)
            {
                filteredEvents = filteredEvents.Take(maxCount.Value).ToList();
            }

            // Map to IEventEnvelope
            var result = filteredEvents
                .Select(IEventEnvelope (e) => new EventEnvelope
                {
                    Id = e.Id,
                    EventType = e.EventType,
                    EventJson = e.EventJson,
                    Metadata = new Dictionary<string, string>(e.Metadata),
                    Created = e.Created,
                })
                .ToList();

            return Task.FromResult<IReadOnlyCollection<IEventEnvelope>>(result);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Error streaming events");
            throw;
        }
    }

    /// <inheritdoc />
    public async Task<IEnumerable<IEventEnvelope>> Append(
        Tenant tenant,
        IEnumerable<IEventToPersist> events,
        StreamQuery? consistencyBoundary,
        Guid? expectedLastEventId,
        CancellationToken cancellationToken = default)
    {
        var eventsList = events.ToList();
        if (eventsList.Count == 0)
            return [];

        // Use mutex to simulate a transaction and ensure thread safety
        await _mutex.WaitAsync(cancellationToken);

        try
        {
            var tenantId = tenant.Id;
            var tenantEvents = _events.GetOrAdd(tenantId, _ => new ConcurrentDictionary<Guid, StoredEvent>());

            // DCB consistency check
            if (consistencyBoundary != null)
            {
                CheckConsistencyBoundary(consistencyBoundary, expectedLastEventId, tenantId);
            }

            // Insert events with global positions
            var insertedEvents = new List<IEventEnvelope>();

            foreach (var @event in eventsList)
            {
                var position = Interlocked.Increment(ref _globalPosition);

                var storedEvent = new StoredEvent
                {
                    Id = @event.Id,
                    TenantId = tenantId,
                    Position = position,
                    EventType = @event.EventType,
                    EventJson = @event.EventJson,
                    Tags = @event.Tags.ToList(),
                    Metadata = new Dictionary<string, string>(@event.Metadata),
                    Created = @event.Created
                };

                if (!tenantEvents.TryAdd(@event.Id, storedEvent))
                {
                    throw new ConcurrencyConflictException($"Event with ID {@event.Id} already exists");
                }

                // Create the returned event with position in metadata
                var metadata = new Dictionary<string, string>(@event.Metadata)
                {
                    ["_position"] = position.ToString()
                };

                insertedEvents.Add(
                    new EventEnvelope
                    {
                        Id = @event.Id,
                        EventType = @event.EventType,
                        EventJson = @event.EventJson,
                        Metadata = metadata,
                        Created = @event.Created
                    });
            }

            return insertedEvents;
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Error appending events with consistency check");
            throw new ConcurrencyConflictException(
                "Failed to append events due to a concurrency conflict or other error.");
        }
        finally
        {
            _mutex.Release();
        }
    }

    private void CheckConsistencyBoundary(StreamQuery query, Guid? expectedLastEventId, string tenantId)
    {
        var tenantEvents = _events.GetOrAdd(tenantId, _ => new ConcurrentDictionary<Guid, StoredEvent>());

        // Get the latest event ID based on the consistency boundary
        var filteredEvents = tenantEvents.Values
            .Where(e => MatchesQuery(e, query))
            .OrderBy(e => e.Position)
            .ToList();

        var latestEventId = filteredEvents.Count > 0
            ? filteredEvents.Last().Id
            : (Guid?)null;

        logger.LogDebug(
            "Latest event ID for consistency boundary: {LatestEventId}, Expected: {ExpectedLastEventId}",
            latestEventId,
            expectedLastEventId);

        if (expectedLastEventId == null && latestEventId != null)
        {
            throw new ConcurrencyConflictException("Expected no events but found some"); 
        }

        if (expectedLastEventId != null && latestEventId != expectedLastEventId)
        {
            throw new ConcurrencyConflictException("Expected specific event but got different one"); 
        }
    }

    /// <summary>
    /// Check if an event matches the query criteria
    /// </summary>
    private static bool MatchesQuery(StoredEvent @event, StreamQuery? query)
    {
        if (query == null)
            return false;

        // Filter by domain identifiers if any are specified
        if (query.Tags.Count > 0)
        {
            var eventIdentifiers = @event.Tags;

            if (query.RequireAllTags)
            {
                // All specified domain identifiers must be present (DCB @> operator)
                if (!query.Tags.All(queryId => eventIdentifiers.Any(eventId => eventId.Equals(queryId))))
                {
                    return false;
                }
            }
            else
            {
                // Any of the specified domain identifiers can be present (DCB && operator)
                if (!query.Tags.Any(queryId => eventIdentifiers.Any(eventId => eventId.Equals(queryId))))
                {
                    return false;
                }
            }
        }

        // Filter by event types if any are specified
        if (query.EventTypes.Count > 0)
        {
            if (query.RequireAllEventTypes)
            {
                // For single events, this only makes sense if there's one type in query
                if (query.EventTypes.Count == 1)
                {
                    var queryType = query.EventTypes.First();
                    if (queryType.Id != "*" && !@event.EventType.Id.Equals(queryType.Id))
                    {
                        return false;
                    }
                }
                else
                {
                    // Multiple types required for single event is impossible
                    return false;
                }
            }
            else
            {
                // Any of the specified event types can be present
                if (!query.EventTypes.Any(et => et.Id == "*" || @event.EventType.Id.Equals(et.Id)))
                {
                    return false;
                }
            }
        }

        return true;
    }

    /// <summary>
    /// Clear all events for testing purposes
    /// </summary>
    public void Clear()
    {
        _events.Clear();
        _globalPosition = 0;
    }

    /// <summary>
    /// Clear events for a specific tenant for testing purposes
    /// </summary>
    public void Clear(string tenantId)
    {
        _events.TryRemove(tenantId, out _);
    }

    /// <summary>
    /// Get all events for a specific tenant for testing purposes
    /// </summary>
    public IReadOnlyCollection<IEventEnvelope> GetAllEvents(string tenantId)
    {
        if (_events.TryGetValue(tenantId, out var tenantEvents))
        {
            return tenantEvents.Values
                .OrderBy(e => e.Position)
                .Select(e => (IEventEnvelope)new EventEnvelope
                {
                    Id = e.Id,
                    EventType = e.EventType,
                    EventJson = e.EventJson,
                    Metadata = new Dictionary<string, string>(e.Metadata)
                    {
                        ["_position"] = e.Position.ToString()
                    },
                    Created = e.Created
                })
                .ToList();
        }

        return new List<IEventEnvelope>();
    }

    /// <summary>
    /// Get events by type for a specific tenant for testing purposes
    /// </summary>
    public IReadOnlyCollection<IEventEnvelope> GetEventsByType(string tenantId, string eventType)
    {
        if (_events.TryGetValue(tenantId, out var tenantEvents))
        {
            return tenantEvents.Values
                .Where(e => e.EventType.Id == eventType)
                .OrderBy(e => e.Position)
                .Select(e => (IEventEnvelope)new EventEnvelope
                {
                    Id = e.Id,
                    EventType = e.EventType,
                    EventJson = e.EventJson,
                    Metadata = new Dictionary<string, string>(e.Metadata),
                    Created = e.Created
                })
                .ToList();
        }

        return new List<IEventEnvelope>();
    }

    /// <summary>
    /// Get events by domain identifier for a specific tenant for testing purposes
    /// </summary>
    public IReadOnlyCollection<IEventEnvelope> GetEventsByTag(
        string tenantId,
        EventTag tag)
    {
        if (_events.TryGetValue(tenantId, out var tenantEvents))
        {
            return tenantEvents.Values
                .Where(e => e.Tags.Any(id => id.Equals(tag)))
                .OrderBy(e => e.Position)
                .Select(e => (IEventEnvelope)new EventEnvelope
                {
                    Id = e.Id,
                    EventType = e.EventType,
                    EventJson = e.EventJson,
                    Metadata = new Dictionary<string, string>(e.Metadata)
                    {
                        ["_position"] = e.Position.ToString()
                    },
                    Created = e.Created
                })
                .ToList();
        }

        return new List<IEventEnvelope>();
    }

    /// <summary>
    /// Get count of events for a specific tenant for testing purposes
    /// </summary>
    public int GetEventCount(string tenantId)
    {
        return _events.TryGetValue(tenantId, out var tenantEvents) ? tenantEvents.Count : 0;
    }

    /// <summary>
    /// Get count of events for testing purposes
    /// </summary>
    public int GetEventCount()
    {
        return _events.Values.Sum(e => e.Count);
    }

    /// <summary>
    /// Check if an event with the given ID exists for a specific tenant
    /// </summary>
    public bool ContainsEvent(string tenantId, Guid eventId)
    {
        return _events.TryGetValue(tenantId, out var tenantEvents) && tenantEvents.ContainsKey(eventId);
    }

    /// <summary>
    /// Class to store events in memory
    /// </summary>
    private class StoredEvent
    {
        public Guid Id { get; init; }
        public string TenantId { get; init; } = null!;
        public long Position { get; init; } // Added position like PostgreSQL version
        public EventType EventType { get; init; } = null!;
        public string EventJson { get; init; } = null!;
        public IReadOnlyCollection<EventTag> Tags { get; init; } = [];
        public Dictionary<string, string> Metadata { get; init; } = new Dictionary<string, string>();
        public DateTimeOffset Created { get; init; }
    }

    public bool Contains(EventTag[] tags)
    {
        return _events.SelectMany(x => x.Value).Any(e => e.Value.Tags.Any(tags.Contains));
    }

    public int EventCount(EventTag[] tags)
    {
        return _events.SelectMany(x => x.Value).Count(e => e.Value.Tags.Any(tags.Contains));
    }
}