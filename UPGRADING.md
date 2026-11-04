# Upgrading

Notes for moving between Alberto releases. Breaking changes of a release are also listed in its
section of [CHANGELOG.md](CHANGELOG.md).

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
  or dismissed with `alberto ops dead-letters`.
- The outbox handler halts too, so messages are never published out of order.

### Custom dead-letter stores

`IClaimableDeadLetterStore.ClaimDueAsync` has a default body, so nothing breaks. A store that
keeps the default never redelivers automatically. To support it, store `NextRetryAt` and
`RedeliveryCount` from `DeadLetterEntry` and claim rows that are retry-requested **or** due at
the `now` passed in (the application clock, not the database's).
