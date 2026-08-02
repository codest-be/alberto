# Reactors and the outbox

Projections turn the log into state. **Reactors** turn it into *actions*: send an email, call a
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

For anything with real logic, point at a method on a class instead. The class is registered as
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

- **Reuse it and the two reactors share a position**: one will skip events the other consumed.
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

A reactor can see the same event twice: a crash between doing the work and saving the checkpoint
is enough, and an operator rewind will do it deliberately. Make handlers idempotent: key outgoing
requests on `ctx.EventId`, or check before you act. There is no exactly-once mode to switch on;
there is no such thing.

**Reactors are not replayed by a projection rebuild.** The rebuild coordinator drives projections
only, precisely because replaying side effects is not something it can make safe.

### Tuning one processor

```csharp
.ReactTo<OrderPlaced>(handler, "order-emails", configure: o => o with { MaxConcurrency = 8 })
```

The `configure` parameter is `Func<ProcessorExecutionOptions, ProcessorExecutionOptions>`: return
a modified record to change batching or concurrency. Batching mode values are
`ProcessorBatchingMode.IfSupported`, `ProcessorBatchingMode.Required` (the default), and
`ProcessorBatchingMode.Disabled`. For example: `o => o with { BatchingMode = ProcessorBatchingMode.IfSupported }`.
The setting is ignored for `Sync` reactors.

## The outbox

Calling a broker directly from a reactor has the classic failure: the broker call succeeds, the
checkpoint save fails, and the message is sent twice, or the reverse, and it is never sent at all.
The outbox breaks that into two durable steps.

```
event committed to the log
    ↓  OutboxHandler (a processor, with a checkpoint)
row in alberto_outbox_entries, status = pending
    ↓  OutboxRelay (a hosted service, polls every 1s)
claimed FOR UPDATE SKIP LOCKED, status = processing
claim token + claim expiry stored on the row
    ↓  IMessageTransport.PublishAsync
status = delivered   (or failed, only if the claim token still matches)
```

Because the handler is a processor, an entry only ever exists for an event that is already durable.
Because the relay claims with `FOR UPDATE SKIP LOCKED`, several relay replicas can run without
holding the same live claim. Delivery remains at-least-once: if a publish succeeds but the relay
dies before recording success, the entry is published again after its claim expires. It is also
**unordered.** See [Ordering](#ordering-there-isnt-any) before you assume otherwise.

### Wiring it

Declare the external contract, the shape other systems depend on, which is *not* your event:

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
    relayBatchSize: 50,
    relayClaimLease: TimeSpan.FromMinutes(5))
```

- `Map<TEvent, TMessage>` takes the message type and version from the `[Message]` attribute. The
  three-generic overload resolves one dependency from DI at map time.
- For full control, `map.Map<TEvent>((envelope, sp, ct) => …)` returns an `ExternalMessage`
  directly, or `null` to publish nothing for that event, which is the supported way to filter.
- **`transport` is optional.** Omit it and entries accumulate in the table with no relay; drain
  them from your own process. Provide one and an `OutboxRelay` is registered as a hosted service.
- The outbox owns a provided transport's **start/stop lifecycle**, but not its disposal. Reusing
  the same transport instance across `WithOutbox` registrations in one service provider shares
  that lifecycle: the transport starts once before either relay claims a message and stops once
  after the last relay. The application that constructed the transport remains responsible for
  `IDisposable` or `IAsyncDisposable`. A shared transport must support concurrent `PublishAsync`
  calls; use a separate instance per registration if its broker client is single-threaded.
- A polling-store failure faults the relay and closes the transport. Configure the host's
  background-service failure/restart policy for recovery, and put transient database retry in the
  store or database client rather than around the relay lifecycle.

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

`ExternalMessage` is `(MessageType, Version, Payload, Metadata)`: a JSON string and headers. Throw
from `PublishAsync` and the entry is marked `failed` with the exception message recorded;
`RetryFailedAsync` (optionally filtered by message type) puts them back to `pending`.

`StartAsync` completes before the relay makes its first claim or publish call. Once Alberto invokes
it, Alberto makes exactly one `StopAsync` call after the last relay exits, including when startup
partially initializes the transport and then throws. A transport must therefore make `StopAsync`
safe after a failed `StartAsync`.

Transport cleanup gets an independent cancellation token and is bounded to 30 seconds, so a
transport that ignores cancellation cannot hold host shutdown open forever. A cleanup failure is
observable when the relay otherwise stopped normally. When startup or relay execution has already
failed, that causal exception keeps precedence; the cleanup exception is attached to its `Data`
dictionary under `Alberto.Messaging.TransportStopException`.

`InMemoryTransport` ships in `Alberto.Messaging` for tests.

### Claim leases and relay crashes

Every claim has a unique token and expiry. A relay completes an entry by presenting that token;
once another relay has recovered an expired claim, the old relay can no longer mark the row
`delivered` or `failed`.

`OutboxRelay.DefaultClaimLease` is five minutes. Set `relayClaimLease` longer than the transport's
worst-case publish time. If it is too short, another relay can recover an entry while the first
publish is still running, producing the duplicate delivery that at-least-once messaging permits.
If it is too long, recovery after a crash waits longer.

### Ordering: there isn't any

**The outbox is at-least-once and unordered.** Two messages that were appended in a known order can
be delivered in the opposite one, and neither Alberto nor the table gives you a total order to fall
back on. Three separate mechanisms cause it, and removing any one of them would not fix the others:

- **The claim query orders by `created_at`, which is not unique.** `created_at` defaults to `now()`,
  which in PostgreSQL is transaction-*start* time, so two entries written by concurrent
  transactions can share a timestamp, and an entry written by a long transaction can carry an
  earlier timestamp than one committed before it. There is no tiebreaker column; `id` is a v4 UUID
  and sorts randomly.
- **A failed delivery is re-delivered later.** `RetryFailedAsync` puts an entry back to `pending`
  long after its neighbours went out, and an expired claim is recovered the same way.
- **Relays claim disjoint batches.** `FOR UPDATE SKIP LOCKED` is what lets several replicas run at
  once; it also means replica B can publish entry 2 while replica A is still publishing entry 1.

This is normal for integration messaging and usually fine: the messages go to *other* systems, which
have their own state and their own clocks, and a consumer that requires order across systems has a
design problem the transport cannot solve for it. Order *within* the event log is unaffected: the
log is strictly ordered, and `OutboxHandler` is a checkpointed processor that reads it in order. It
is only the delivery leg that reorders.

If you do need per-entity order, that is the transport's job, and `ExternalMessage.RoutingHint` is
the hook: set it to the entity id and a transport that supports partition keys (Kafka), message
group ids (SQS FIFO) or a routing key (RabbitMQ) will keep one entity's messages on one ordered
path. Alberto passes the hint through untouched and never interprets it.

```csharp
map.Map<OrderPlaced>((envelope, sp, ct) => new ExternalMessage(
    "order-placed", "1", Serialize(envelope.Event), [],
    RoutingHint: envelope.Event.OrderId.ToString()));
```

Otherwise, make consumers order-insensitive the same way you make them idempotent: carry a version
or a timestamp in the message and let the consumer discard what it has already superseded.

### Retention

Delivered entries stay in the table until something removes them, and nothing did before `0.1.0`.
The outbox was append-only in practice, and the partial indexes the relay depends on grew with it.
`WithOutbox` now registers an `OutboxRetentionService` that deletes delivered entries older than
`deliveredRetention` (**default 7 days**) every `retentionSweepInterval` (default 1 hour):

```csharp
.WithOutbox(
    map => { … },
    outboxStore: new PostgresOutboxStore(dataSource, schema: "orders"),
    deliveredRetention: TimeSpan.FromDays(30),
    retentionSweepInterval: TimeSpan.FromHours(6))
```

- **Only `delivered` entries are eligible.** `pending`, `processing` and `failed` are work, not
  history, and are never removed by age: a `failed` entry sits there until you `RetryFailedAsync`
  it or delete it yourself.
- Pass `Timeout.InfiniteTimeSpan` as `deliveredRetention` to keep delivered entries forever. Do that
  if the table is your integration audit trail, and plan to archive it elsewhere.
- Retention is registered whether or not you supply a `transport`: entries reach `delivered` by
  being delivered, and a host that drains the table itself still accumulates them.
- The sweep runs on its own schedule, not on the relay's loop, so a slow purge delays only the next
  purge and never publishing. It waits out one full interval before its first sweep, because host
  startup is both the busiest moment and the one where the backlog is largest.
- Several replicas sweeping concurrently is safe. The deletes are idempotent and the losers delete
  nothing.

For a one-off cleanup, or to catch up a table that predates this, `alberto ops outbox purge --before
<timestamp>` does the same delete from the CLI and records an `admin-outbox-purged` audit event.

> **Upgrading:** if your outbox predates `0.1.0`, the first sweep faces every delivered entry you
> have ever written. If that table is large, purge it once from the CLI during a quiet window before
> enabling the service, or set `deliveredRetention` wide and walk it down.

## Reactor, outbox, or inline projection?

| You want to | Use |
|---|---|
| Update something you will query later | An [async projection](projections.md) |
| Read your own write immediately after the mutation | An [inline projection](projections.md#inline-vs-async) |
| Tell another system something happened | The outbox |
| Do something to the outside world that is not a message | An async reactor |
| Fire-and-forget, and genuinely not care if it is lost | A sync reactor |
