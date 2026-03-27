# Plan 03: Outbox / Messaging Package

## Goal
Add a new `Alberto.Dcb.Messaging` NuGet package implementing the transactional outbox pattern for reliable external messaging. Events are mapped to external messages and stored in an outbox table atomically with event processing, then relayed to an external transport.

## Reference Implementation (TS)

`packages/messaging/`:
- `OutboxStore` — CRUD for `outbox_entries` table (pending/delivered/failed statuses)
- `OutboxHandler` — event processor that maps events to messages and inserts into outbox (idempotent via `ON CONFLICT (source_event_id) DO NOTHING`)
- `OutboxRelay` — polls pending entries, publishes via transport, marks delivered/failed
- `MappingRegistry` — maps event types to external message envelopes
- `Transport` interface — `publish(message)`, `start()`, `stop()`
- `InMemoryTransport` — for testing, with per-type subscribers
- `MessageSubscription` — subscribes handlers to inbound message types

## Implementation Plan

### Step 1: Create `Alberto.Dcb.Messaging` project

New project under `src/Alberto.Dcb.Messaging/`. Add to solution file.

### Step 2: Core abstractions

```csharp
// External message envelope
public record ExternalMessage(
    string MessageType,
    string Version,
    string Payload,        // JSON
    Dictionary<string, string> Metadata);

// Transport interface
public interface IMessageTransport
{
    Task PublishAsync(ExternalMessage message, CancellationToken ct = default);
    Task StartAsync(CancellationToken ct = default);
    Task StopAsync(CancellationToken ct = default);
}

// Mapping function: event → external message (null = skip)
public delegate ExternalMessage? EventToMessageMapper(IEventEnvelope envelope);

// Mapping registry
public interface IMessageMappingRegistry
{
    void Map(string eventType, EventToMessageMapper mapper);
    void Map<TEvent>(EventToMessageMapper mapper) where TEvent : IEvent;
    ExternalMessage? TryMap(IEventEnvelope envelope);
    IReadOnlySet<string> MappedEventTypes { get; }
}
```

### Step 3: Outbox store

```csharp
public enum OutboxEntryStatus { Pending, Delivered, Failed }

public record OutboxEntry(
    Guid Id,
    Guid SourceEventId,
    string MessageType,
    string Version,
    string Payload,
    Dictionary<string, string> Metadata,
    OutboxEntryStatus Status,
    int RetryCount,
    string? LastError,
    DateTimeOffset CreatedAt,
    DateTimeOffset? DeliveredAt);

public interface IOutboxStore
{
    Task InsertAsync(OutboxEntry entry, CancellationToken ct = default);
    Task<IReadOnlyList<OutboxEntry>> GetPendingAsync(int limit = 100, CancellationToken ct = default);
    Task MarkDeliveredAsync(Guid id, CancellationToken ct = default);
    Task MarkFailedAsync(Guid id, string error, CancellationToken ct = default);
    Task RetryFailedAsync(string? messageType = null, CancellationToken ct = default);
    Task PurgeDeliveredAsync(DateTimeOffset before, CancellationToken ct = default);
}
```

### Step 4: PostgreSQL migration for outbox table

New migration (add to `Alberto.Dcb.Postgres`):

```sql
CREATE TABLE IF NOT EXISTS {schema}.outbox_entries (
    id UUID PRIMARY KEY DEFAULT gen_random_uuid(),
    source_event_id UUID NOT NULL,
    message_type TEXT NOT NULL,
    version TEXT NOT NULL DEFAULT '1',
    payload JSONB NOT NULL,
    metadata JSONB NOT NULL DEFAULT '{}',
    status TEXT NOT NULL DEFAULT 'pending' CHECK (status IN ('pending', 'delivered', 'failed')),
    retry_count INT NOT NULL DEFAULT 0,
    last_error TEXT,
    created_at TIMESTAMPTZ NOT NULL DEFAULT now(),
    delivered_at TIMESTAMPTZ
);

CREATE UNIQUE INDEX IF NOT EXISTS idx_{schema}_outbox_source_event
    ON {schema}.outbox_entries (source_event_id);
CREATE INDEX IF NOT EXISTS idx_{schema}_outbox_pending
    ON {schema}.outbox_entries (created_at) WHERE status = 'pending';
```

### Step 5: Outbox handler (event processor)

An `IEventProcessor` implementation that maps events to outbox entries:

```csharp
internal sealed class OutboxHandler : IEventProcessor
{
    private readonly IMessageMappingRegistry _registry;
    private readonly IOutboxStore _store;

    public string ProcessorId => "outbox";
    public IReadOnlySet<string> HandledEventTypes => _registry.MappedEventTypes;

    public async Task ProcessEventAsync(IEventEnvelope @event, CancellationToken ct)
    {
        var message = _registry.TryMap(@event);
        if (message is null) return;

        var entry = new OutboxEntry(
            Id: Guid.CreateVersion7(),
            SourceEventId: @event.Id,
            MessageType: message.MessageType,
            Version: message.Version,
            Payload: message.Payload,
            Metadata: message.Metadata,
            Status: OutboxEntryStatus.Pending,
            RetryCount: 0, LastError: null,
            CreatedAt: DateTimeOffset.UtcNow,
            DeliveredAt: null);

        await _store.InsertAsync(entry, ct);
        // INSERT ... ON CONFLICT (source_event_id) DO NOTHING for idempotency
    }
}
```

### Step 6: Outbox relay (background service)

Polls pending outbox entries and publishes via transport:

```csharp
public sealed class OutboxRelay : BackgroundService
{
    private readonly IOutboxStore _store;
    private readonly IMessageTransport _transport;
    private readonly TimeSpan _pollingInterval;
    private readonly int _batchSize;
    private readonly int _maxRetries;

    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        await _transport.StartAsync(ct);

        while (!ct.IsCancellationRequested)
        {
            var pending = await _store.GetPendingAsync(_batchSize, ct);

            foreach (var entry in pending)
            {
                try
                {
                    await _transport.PublishAsync(
                        new ExternalMessage(entry.MessageType, entry.Version,
                            entry.Payload, entry.Metadata), ct);
                    await _store.MarkDeliveredAsync(entry.Id, ct);
                }
                catch (Exception ex)
                {
                    if (entry.RetryCount >= _maxRetries)
                        await _store.MarkFailedAsync(entry.Id, ex.Message, ct);
                    else
                        // Increment retry count, will be picked up next cycle
                        await _store.MarkFailedAsync(entry.Id, ex.Message, ct);
                }
            }

            if (pending.Count == 0)
                await Task.Delay(_pollingInterval, ct);
        }
    }
}
```

### Step 7: InMemoryTransport for testing

```csharp
public sealed class InMemoryTransport : IMessageTransport
{
    private readonly ConcurrentBag<ExternalMessage> _published = new();
    public IReadOnlyCollection<ExternalMessage> Published => _published;

    public Task PublishAsync(ExternalMessage message, CancellationToken ct)
    {
        _published.Add(message);
        return Task.CompletedTask;
    }
    // ...
}
```

### Step 8: Builder extensions

```csharp
public static ConsumerBuilder WithOutbox(
    this ConsumerBuilder builder,
    Action<IMessageMappingRegistry> configureMappings)
{
    // Registers OutboxHandler as a processor
    // Registers OutboxRelay as a hosted service
}
```

## Files to Create

- `src/Alberto.Dcb.Messaging/Alberto.Dcb.Messaging.csproj`
- `src/Alberto.Dcb.Messaging/ExternalMessage.cs`
- `src/Alberto.Dcb.Messaging/IMessageTransport.cs`
- `src/Alberto.Dcb.Messaging/IMessageMappingRegistry.cs` + `MessageMappingRegistry.cs`
- `src/Alberto.Dcb.Messaging/IOutboxStore.cs` + `OutboxEntry.cs`
- `src/Alberto.Dcb.Messaging/OutboxHandler.cs`
- `src/Alberto.Dcb.Messaging/OutboxRelay.cs`
- `src/Alberto.Dcb.Messaging/InMemoryTransport.cs`
- `src/Alberto.Dcb.Messaging/MessagingBuilderExtensions.cs`
- `src/Alberto.Dcb.Postgres/Migrations/013_outbox.sql`
- `src/Alberto.Dcb.Postgres/PostgresOutboxStore.cs`
- `tests/Alberto.Dcb.Tests/Messaging/OutboxHandlerTests.cs`
- `tests/Alberto.Dcb.Tests/Messaging/OutboxRelayTests.cs`

## Files to Modify

- `AlbertoV3.slnx` — add new project
- `Directory.Packages.props` — if new dependencies needed

## Acceptance Criteria

- [ ] Events mapped to outbox entries atomically (idempotent via source_event_id)
- [ ] Relay publishes pending entries via transport
- [ ] Failed entries tracked with retry count and error message
- [ ] `RetryFailed` resets failed entries to pending
- [ ] `PurgeDelivered` cleans old delivered entries
- [ ] InMemoryTransport works for testing
- [ ] Integration test: append event → outbox entry created → relay publishes → marked delivered
