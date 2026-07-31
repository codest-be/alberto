# Backup and recovery

Alberto has no backup tool of its own. It stores everything in PostgreSQL, so `pg_dump`,
`pg_basebackup` and point-in-time recovery are the whole mechanism, and your existing database
backup policy already covers an Alberto store.

What this page is for is the part your policy does not know about: **which tables are truth, which
are derived, and what a restore silently invalidates.** Restoring an event store the way you would
restore a CRUD database can leave a projection permanently missing events with nothing logged and
no error raised.

## What lives where

Everything below is in one PostgreSQL database and one schema, so a single `pg_dump` of that
database captures a consistent snapshot of all of it. The distinction matters for *restoring*, not
for taking the backup.

| Table | What it is | If you lose it |
|---|---|---|
| `alberto_events` | **The truth.** Append-only, `global_position` is a `BIGSERIAL` | Unrecoverable. Everything else is derived from this |
| `alberto_event_type_positions`, `alberto_event_tag_positions` | Inverted indexes over the log, written in the append transaction | Recoverable only by rebuilding them from `alberto_events`; treat them as part of the log |
| `alberto_processor_checkpoints` | How far each processor has consumed | Recoverable by replaying — costly, not fatal. **But a checkpoint that is too far *ahead* is fatal**, see below |
| `alberto_projection_states` | Projection documents, keyed by `rebuild_version` | Rebuildable from the log |
| `alberto_projection_rebuild_meta` | Which version readers see, and any rebuild in flight | Rebuildable, but a stale copy strands readers on a version that no longer exists |
| `alberto_outbox_entries` | Messages waiting to go out, and delivered ones kept for `deliveredRetention` | Undelivered entries are lost work — the events survive, the intent to publish them does not |
| `alberto_dead_letter_events` | Events a processor gave up on | Losing them loses the record of what failed; the events themselves are still in the log |
| `alberto_processor_leases`, `alberto_tenant_leases` | Who is allowed to run what, right now | Nothing. They expire and are re-acquired |
| `alberto_tenants`, `alberto_tenant_assignments` | Tenant registry (multi-tenant stores) | Configuration, not derived. Back it up like configuration |
| `alberto_store_imprint` | Records whether this store is single- or multi-tenant | The migrator re-derives it by sniffing `alberto_events` for a `tenant_id` column |
| `schemaversions` | DbUp's migration journal | Restore it *with* the schema it describes. A journal that disagrees with the tables is worse than no journal |

**EF-backed projections are not in this list.** They live in your own `DbContext` and your own
tables — often your own database. They are derived state like `alberto_projection_states`, and the
ordering rule below applies to them just as much.

## Taking a backup

Nothing Alberto-specific is required. Two things are worth knowing:

- **`pg_dump` is consistent enough.** It runs in a repeatable-read snapshot, so the dump cannot
  catch an append half-written or a checkpoint that refers to an event the dump missed. You do not
  need to stop the application.
- **PITR is what you actually want.** The log is the record of everything that happened, so being
  able to recover to a point in time — rather than to last night — is the difference between losing
  a day of history and losing an hour. Alberto does nothing that interferes with WAL archiving.

### Sharded modules are N+1 databases with no consistency between them

If a module uses `.WithTenancy(t => t.AcrossPostgresDatabases(...))`, each shard is a complete,
independent store in its own database, and the `alberto_tenant_shards` catalog lives in a separate
control database. That means:

- **Back up the control database too.** Without the catalog, `TenantShardResolver` cannot tell which
  database a tenant lives in, and every request fails to route. It is small and it changes rarely,
  which makes it easy to forget.
- **There is no cross-database snapshot.** `pg_dump` of five shards is five snapshots at five
  instants. Positions are per-database and were never comparable across shards, so this does not
  corrupt anything — but a restore is a per-shard operation, and a tenant moved between shards near
  the recovery point can land in neither or both. Reconcile the catalog against the shards after any
  restore that is not all-shards-to-the-same-time.

## Restoring

### The one rule

> **Derived state must never be newer than the log it was derived from.**

Restore the whole database to one point in time and the rule holds automatically — the log, the
checkpoints and the projections all move back together. It is only broken when the pieces are
restored from different points, which happens more easily than it sounds:

- restoring `alberto_events` from last night while leaving today's projection tables in place
- restoring the event store database but not the separate database holding your EF projections
- rolling back a shard without rolling back the control database

If derived state is ahead, checkpoint monotonicity turns it into silent data loss. `SaveAsync` uses
`GREATEST`, so a checkpoint sitting at position 9000 over a log that now ends at 8000 does not
rewind on its own and does not error. The processor waits for position 9001. Every event the restore
brought back between 8000 and 9000 is skipped, permanently, with nothing in the logs.

The cure is `alberto ops checkpoint set` — `RewindAsync` is the deliberate escape hatch and the only
way to move a checkpoint backwards:

```bash
alberto ops checkpoint set <processor-id> <restored-log-head> --dry-run
```

Positions are per-database, so on a sharded module set them one shard at a time. `ops checkpoint
set` is the one mutation with no `--all-shards`, for exactly that reason.

### After any restore, before starting the application

Bring the application up *after* these, not before — a control loop that starts first will act on
the state you are about to correct.

1. **Compare every checkpoint against the log head.** Anything ahead of it must be rewound.
   `alberto status` prints both — the store's global position and every processor's — which is the
   whole check in one command.

   ```bash
   alberto status
   ```

   ```bash
   alberto checkpoints
   ```

2. **Resolve rebuilds that were in flight.** A rebuild interrupted by the restore has a
   `rebuilding_version` and a target position that may no longer exist. Abort it and start again;
   a rebuild is cheap to redo and there is no way to verify a partial one.

   ```bash
   alberto ops rebuild status
   ```

   ```bash
   alberto ops rebuild abort <processor-id> --yes
   ```

   Check `active_version` too. If the restore rolled `alberto_projection_rebuild_meta` back past a
   promotion, readers resolve a version whose rows the reclaim sweep has since removed — the
   projection reads as empty rather than wrong. Starting a fresh rebuild is the fix.

3. **Decide what to do about the outbox.** Entries restored as `processing` carry a claim from a
   relay that no longer exists, and recover on their own: `ClaimPendingAsync` re-claims anything
   whose `claim_expires_at` has passed or was never set. Entries restored as `failed` stay failed —
   `IOutboxStore.RetryFailedAsync` (optionally filtered by message type) is what moves them back to
   `pending`, and there is no CLI verb for it; `alberto ops outbox purge` is the only outbox command
   and it only deletes delivered entries.

   Dead letters are the separate case, and they do have CLI verbs:

   ```bash
   alberto dead-letters
   ```

   ```bash
   alberto ops dead-letters retry <processor-id> --yes
   ```

4. **Leave the leases alone.** `alberto_processor_leases` and `alberto_tenant_leases` restore with
   expiry times in the past and are re-acquired on the first poll. `alberto ops tenants release`
   exists if something is genuinely stuck, but it is not part of a normal restore.

5. **On a sharded module, reconcile the catalog.** `alberto shards list` and `alberto shards where
   <tenant>` against each restored shard; fix assignments with `alberto shards assign`.

### What a restore cannot undo

**Messages already published.** Once the relay handed an `ExternalMessage` to a transport, it is
gone — in another system's queue, or already processed. Rolling the outbox table back to before that
delivery does not unsend it; it makes Alberto publish it *again* when the restored entry is
re-claimed. Consumers must be idempotent for this to be safe, which is the same property
[at-least-once delivery](reactors-and-outbox.md#the-outbox) already requires of them.

**Side effects from reactors.** A reactor that charged a card or sent an email has no record in the
outbox at all. Rewinding its checkpoint re-runs it. This is the reason
[reactors are asked to be idempotent](reactors-and-outbox.md#at-least-once-so-be-idempotent), and a
restore is where that stops being theoretical.

## Rebuilding derived state instead of restoring it

Because the log is the truth, there is a second option that a CRUD database does not have: restore
`alberto_events` and throw the derived state away.

```bash
alberto ops rebuild start <processor-id> --yes
```

This replays the log into a shadow version under its own checkpoint and swaps it in one transaction,
so the projection stays readable throughout — see
[Rebuilding a projection](projections.md#rebuilding-a-projection). It costs a full replay and it is
correct by construction, which is often the better trade when you are not certain a restored
projection is consistent with the restored log.

`alberto ops checkpoint reset` does the cruder version — delete the checkpoint, replay from zero
into the *live* state — and is appropriate for a reactor, which has no state to swap, but not for a
projection readers are querying.

## Verifying a backup

A backup you have not restored is a hypothesis. The event-store-specific checks, once you have
restored into a scratch database:

- **The migrator agrees with the schema.** Point a module at it and let `PostgresMigrator.Migrate`
  run. It reads `alberto_store_imprint` first and refuses the wrong migration set with
  `AlbertoStoreMismatchException` (`ALB0021`), which catches a single-tenant dump restored over a
  multi-tenant store before anything else does.
- **No checkpoint is ahead of the log head.** The one condition that fails silently in production is
  the one worth asserting in a test.
- **A projection rebuilt from the restored log matches the restored projection.** This is the real
  test of "is this backup consistent", and it is a script you can run unattended.
