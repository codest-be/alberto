# Upgrading

Notes for moving between Alberto releases. Breaking changes of a release are also listed in its
section of [CHANGELOG.md](CHANGELOG.md).

This file covers 0.x release-to-release steps; [docs/migrating-to-1.0.md](docs/migrating-to-1.0.md) covers the older `0.1.0-beta` to 1.0 breaking changes.

## To the next release (pre-1.0, breaking)

### Event store reads: batch `Read*`, lazy `Stream*`

One interface answers "what do I depend on": `IEventStore`. `IEventStoreBackend` now extends it and
adds only `ReadAllOfTypesAsync`, so `EventStore` and the tenant and shard routers are written once.

| Was | Is |
|---|---|
| `StreamAsync(query, afterPosition, limit?, cancellationToken)` returning a collection | `ReadAsync(query, afterPosition, limit, cancellationToken)` |
| `StreamAllAsync(afterPosition, limit?, cancellationToken)` returning a collection | `ReadAllAsync(afterPosition, limit, cancellationToken)` |
| `IEventStoreBackend.StreamAllOfTypesAsync(...)`, defaulted | `ReadAllOfTypesAsync(eventTypes, afterPosition, limit, cancellationToken)`, abstract, filtering mandatory |
| (none) | `StreamAsync(query, afterPosition = 0, cancellationToken)` and `StreamAllAsync(afterPosition = 0, cancellationToken)` returning `IAsyncEnumerable<IEventEnvelope>` |

`limit` is required: no caller in `src` read unbounded. Pass `int.MaxValue` for the old `null`.
`await store.StreamAsync(q)` becomes `await store.StreamAsync(q).ToListAsync()`, or better an
`await foreach`.

A custom `IEventStoreBackend` must now implement `ReadAsync`, `ReadAllAsync`, `StreamAsync`,
`StreamAllAsync` and `ReadAllOfTypesAsync` (the old default silently returned unfiltered events).
`ShardRoutingEventStoreBackend` is gone; sharding routes at `IEventStore` only. A streamed read is
spanned (`alberto.read`) for as long as it is enumerated.

## To 0.8.0

### Run the migration

`008_DropRedundantIndexes` drops `ix_alberto_events_inflight` and two indexes on
`alberto_projection_states` that duplicated a surviving one. The in-flight index was meant to
hold only uncommitted rows, but `pg_xact_id` is never cleared, so it was a second copy of the
primary key written on every append. It runs on startup; `DROP INDEX` takes a brief
`ACCESS EXCLUSIVE` lock on the table and returns the disk space at once.

### Custom event store backends

`IEventStoreHeadBackend.GetStableHeadUpToAsync(afterPosition, upToPosition, cancellationToken)` is new and has a
default implementation that calls `GetStableHeadAsync`, so nothing breaks. A backend whose
barrier query scans forward from `afterPosition` should override it and stop at `upToPosition`:
the head only asks about the window it just read.

`IEventStoreBackend.StreamAllOfTypesAsync(eventTypes, afterPosition, limit, cancellationToken)` is new too, and
defaults to `StreamAllAsync`. The control loop now reads through it, passing the processor's
handled types, so a backend that overrides it sends only the events the processor will handle.
Two things must hold whether it filters or not: events come back in position order, and fewer
than `limit` means no further event of those types was visible. The loop relies on the second
to checkpoint at the head after a short batch, rather than at the last event it was given.

### The outbox relay claims 100 entries per poll

`WithOutbox(relayBatchSize:)` defaults to 100; it was 1. The relay publishes the batch in order
and marks it delivered in one round trip. A relay that dies mid-batch redelivers what it had
published but not yet marked, so the batch size is the most duplicates one crash can cause.
Consumers already had to be idempotent; pass `relayBatchSize: 1` to keep the old ceiling.

A custom `IOutboxStore` keeps working: `MarkDeliveredBatchAsync` has a default that loops
`MarkDeliveredAsync`. Override it to mark the batch in one statement.

## To 0.7.0

### Run the migration

`007_DeadLetterRedelivery` adds `next_retry_at` and `redelivery_count` to
`alberto_dead_letter_events`, plus a partial index. It runs on startup, for multi-tenant and
single-tenant schemas alike. Existing dead letters get `NULL`, so **nothing is redelivered
automatically on upgrade**: they stay parked for manual retry exactly as before.

### Reactors redeliver on their own

A reactor or message subscription whose failure is transient (or unknown) is now redelivered
automatically: 1 min, 4 min, 16 min, about 1 h, about 4 h, then 12 h, ten times in all, before it
is parked. **The handler runs again by itself, so it must be idempotent**, which delivery here
already assumed. To change or switch it off:

```csharp
.WithControlLoop(o => o with { Redelivery = o.Redelivery with { MaxRedeliveries = 5 } })
```

```json
"ControlLoop": { "Redelivery": { "Enabled": false } },
"Processors": { "send-email": { "Redelivery": { "MaxRedeliveries": 20 } } }
```

### Projections halt instead of dead-lettering

A projection that cannot apply an event used to write a dead letter and carry on, leaving a gap
in the read model. It now stays on that event, retries it in place (1 s doubling to 1 min), and
reports itself halted: an error log naming the processor and position, `alberto.processor.halts`,
`alberto.processor.halted` above zero, and an unhealthy processor, until the event applies.
Later events wait. Fix the cause and the projection resumes by itself.

- **Alert on** `alberto.processor.halted > 0` instead of (or as well as) `alberto.dead_letters`.
- Settings that only make sense for dead letters are rejected on a projection at startup
  (`ALB0028`): `Processors:<id>:Redelivery` and `Processors:<id>:Retry:DeadLetterOnMaxRetries`.
  Remove them. `ControlLoop:Retry` still sets the fast in-memory retries first, and `Halt` the waits after.
- Dead letters a projection wrote before the upgrade stay in the table and can still be retried
  or dismissed through `IDeadLetterStore`.
- The outbox handler halts too, so messages are never published out of order.

### Custom dead-letter stores

`IClaimableDeadLetterStore.ClaimDueAsync` has a default body, so nothing breaks. A store that
keeps the default never redelivers automatically. To support it, store `NextRetryAt` and
`RedeliveryCount` from `DeadLetterEntry` and claim rows that are retry-requested **or** due at
the `now` passed in (the application clock, not the database's).
