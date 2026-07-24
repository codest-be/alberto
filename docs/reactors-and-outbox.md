# Reactors and the outbox

Projections turn the log into state. **Reactors** turn it into *actions* — send an email, call a
payment provider, schedule a job. The **outbox** is the special case of "publish a message to
another system", handled properly.

Both are processors on the same control loop, with their own checkpoints, retries and dead-letter
behaviour.

## Reactors

The simplest form is a lambda per event type:

```csharp
.ReactTo<OrderPlaced>(
    sp => async (e, ct) =>
    {
        var email = sp.GetRequiredService<IEmailSender>();
        await email.SendOrderConfirmationAsync(e.CustomerId, e.OrderId, ct);
    },
    processorId: "order-confirmation-email")
```

The factory runs once at startup; the inner delegate runs per event. Resolve scoped services
*inside* the inner delegate, not in the factory.

For anything with real logic, point at a method on a class instead — the class is registered as
scoped for you and resolved per event:

```csharp
.ReactTo<OrderPlaced, OrderConfirmationHandler>(
    h => h.HandleAsync,
    processorId: "order-confirmation-email")
```

Both forms have an overload whose handler also takes a `ReactorContext`, carrying `EventId`,
`Position`, `Timestamp`, `TenantId` and `Metadata`:

```csharp
.ReactTo<OrderPlaced>(
    sp => async (e, ctx, ct) => await Notify(e, ctx.TenantId, ct),
    processorId: "order-notifications")
```

### The processor id matters

It is the checkpoint key. Two things follow:

- **Reuse it and the two reactors share a position** — one will skip events the other consumed.
  One reactor, one id.
- **Change it and you get a brand-new processor starting at position 0**, which replays the entire
  log through your side effect. Renaming a reactor's id in a deployment is how people accidentally
  send ten thousand emails. If you must rename one, set its checkpoint before it starts:
  `alberto ops checkpoint set <new-id> <current-head>`.

### Async vs sync

```csharp
.ReactTo<OrderPlaced>(handler, processorId: "…", mode: ReactorMode.Sync)
```

`Async` (the default) runs on the control loop with a checkpoint, retries and dead-lettering.
`Sync` runs during `AppendAsync`, after the events have committed and after inline projections.

Sync reactors have no checkpoint and no retry: if the process dies between the commit and the side
effect, the side effect never happens and nothing remembers that it should have. Use `Sync` only
for effects that are genuinely optional, and `Async` for everything that matters.

### At-least-once, so be idempotent

A reactor can see the same event twice — a crash between doing the work and saving the checkpoint
is enough, and an operator rewind will do it deliberately. Make handlers idempotent: key outgoing
requests on `ctx.EventId`, or check before you act. There is no exactly-once mode to switch on;
there is no such thing.

**Reactors are not replayed by a projection rebuild.** The rebuild coordinator drives projections
only, precisely because replaying side effects is not something it can make safe.

### Tuning one processor

```csharp
.ReactTo<OrderPlaced>(handler, "order-emails", configure: c => c.WithConcurrency(8))
```

The configurator also has `BatchIfSupported()`, `RequireBatching()` and `DisableBatching()` for
handlers that implement `IBatchableProcessor`. It is ignored for `Sync` reactors.

## The outbox

Calling a broker directly from a reactor has the classic failure: the broker call succeeds, the
checkpoint save fails, and the message is sent twice — or the reverse, and it is never sent at all.
The outbox breaks that into two durable steps.

```
event committed to the log
    ↓  OutboxHandler (a processor, with a checkpoint)
row in alberto_outbox_entries, status = pending
    ↓  OutboxRelay (a hosted service, polls every 1s)
claimed FOR UPDATE SKIP LOCKED, status = processing
    ↓  IMessageTransport.PublishAsync
status = delivered   (or failed, with the error recorded)
```

Because the handler is a processor, an entry only ever exists for an event that is already durable.
Because the relay claims with `FOR UPDATE SKIP LOCKED`, several relay replicas can run without
double-delivering.

### Wiring it

Declare the external contract — the shape other systems depend on, which is *not* your event:

```csharp
[Message("order-placed", 1)]
public sealed record OrderPlacedMessage(Guid OrderId, Guid CustomerId, decimal Amount);
```

Then map events to it:

```csharp
.WithOutbox(
    map =>
    {
        map.Map<OrderPlaced, OrderPlacedMessage>(
            e => new OrderPlacedMessage(e.OrderId, e.CustomerId, e.Amount));

        map.Map<OrderCancelled, IClock, OrderCancelledMessage>(
            (clock, e) => new OrderCancelledMessage(e.OrderId, clock.UtcNow));
    },
    outboxStore: new PostgresOutboxStore(dataSource, schema: "orders"),
    transport: new InMemoryTransport(),
    relayBatchSize: 50)
```

- `Map<TEvent, TMessage>` takes the message type and version from the `[Message]` attribute. The
  three-generic overload resolves one dependency from DI at map time.
- For full control, `map.Map<TEvent>((envelope, sp, ct) => …)` returns an `ExternalMessage`
  directly — or `null` to publish nothing for that event, which is the supported way to filter.
- **`transport` is optional.** Omit it and entries accumulate in the table with no relay; drain
  them from your own process. Provide one and an `OutboxRelay` is registered as a hosted service.

Keep the message record separate from the event even when they look identical today. The event is
your internal history and you will want to change it; the message is a published contract, which is
what `[Message("…", version)]` is for.

### Implementing a transport

```csharp
public interface IMessageTransport
{
    Task PublishAsync(ExternalMessage message, CancellationToken ct = default);
    Task StartAsync(CancellationToken ct = default);
    Task StopAsync(CancellationToken ct = default);
}
```

`ExternalMessage` is `(MessageType, Version, Payload, Metadata)` — a JSON string and headers. Throw
from `PublishAsync` and the entry is marked `failed` with the exception message recorded;
`RetryFailedAsync` (optionally filtered by message type) puts them back to `pending`.

`InMemoryTransport` ships in `Alberto.Dcb.Messaging` for tests.

### Known gap: orphaned `processing` entries

A relay that dies **between** claiming an entry and marking it `delivered`/`failed` strands that
row in `processing` forever:

- `alberto_outbox_entries` has no claim-lease columns, so nothing can tell a live claim from a dead
  one.
- `RetryFailedAsync` only matches `failed`, so it will not pick these up.

The fix would be a `ResetProcessingAsync(olderThan)` sweep plus a claim timestamp; it is not
implemented. The gap is pinned by the skipped test
`DiscoveredIssuesTests.OutboxStore_ProcessingEntriesOrphaned_CannotBeRecoveredByRetryFailed`.

Until then, a stuck message needs a manual `UPDATE` back to `pending`. If your relay restarts often
enough for this to bite, run a single relay replica so the window is a process restart rather than
a rolling deploy.

## Reactor, outbox, or inline projection?

| You want to | Use |
|---|---|
| Update something you will query later | An [async projection](projections.md) |
| Read your own write immediately after the mutation | An [inline projection](projections.md#inline-vs-async) |
| Tell another system something happened | The outbox |
| Do something to the outside world that is not a message | An async reactor |
| Fire-and-forget, and genuinely not care if it is lost | A sync reactor |
