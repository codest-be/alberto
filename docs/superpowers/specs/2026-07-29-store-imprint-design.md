# Store imprint: refusing declarations the store cannot honour

Date: 2026-07-29

## Problem

Every check Alberto performs at startup validates the declaration against itself.
`AlbertoModuleValidator` reports ALB0001–ALB0020 by reading `AlbertoModuleDefinition`
and nothing else. One check compares the declaration against what the store already
is — `PostgresMigrator.ValidateTenancyMode` — and it is sequenced behind the thing it
exists to guard.

`AlbertoMigrationHostedService.TryMigrate` runs `PostgresMigrator.Migrate` first and
calls `ValidateTenancyMode` afterwards. So a module that adds `.WithTenancy()` to an
existing single-tenant database gets the multi-tenant script set run against it before
anything checks whether that is possible. Both script sets journal to the same
`schemaversions` table under their full resource names, so none of the 30 multi-tenant
scripts look applied and DbUp attempts all of them from `001_InitialSchema`. Its
`CREATE TABLE IF NOT EXISTS` statements no-op against the existing tables, `tenant_id`
is never added, and the first statement referencing it fails with a raw PostgreSQL
error. The purpose-built diagnostic never fires.

(With `AutoMigrate = false` the check does run first and its message is good. The bug
is the ordering, not the check.)

Single-tenant and multi-tenant are not a setting. They are two disjoint DDL sets —
`Migrations/` and `Migrations/SingleTenant/` — with no bridging script and no backfill
for `tenant_id` on existing events. There is no in-place transition in either
direction, and there is not meant to be.

## Goal

A general mechanism for validating a declaration against the store it is pointed at,
seeded with the one fact that motivated it. Every mismatch is fatal: an ALB-coded
diagnostic naming the recorded value, the declared value, and the remedy. No override
flag. An operator who genuinely means it edits the store.

## Design

### Where it sits

`PostgresMigrator.Migrate` gains a gate ahead of the script runs. Putting it there
rather than in `AlbertoMigrationHostedService` covers every caller: the standalone
`apps/Alberto.Orders.Migrations/Program.cs`, which today hardcodes `singleTenant: false`
and never validates, plus the test and benchmark harnesses.

The imprint table is created by the migrator itself, not by a migration script — the
same way DbUp manages its own `schemaversions` journal out-of-band. It has to be, or
the check that must precede every script would depend on a script having run.

Shards need no special handling. `ShardExpansion.ForShard` keeps `TenancyEnabled`,
because a shard is still row-level multi-tenant inside itself, so every shard database
is migrated with the multi-tenant set and gets its own imprint in its own schema.

### The table

```sql
CREATE TABLE IF NOT EXISTS <schema>.alberto_store_imprint (
    fact        VARCHAR(100) PRIMARY KEY,
    value       VARCHAR(200) NOT NULL,
    recorded_at TIMESTAMPTZ NOT NULL DEFAULT now()
);
```

Key-value rather than one row of many columns, so adding `module_key` or `shard_id`
later is an insert rather than a migration of the imprint table itself. One fact
today: `('tenancy', 'single' | 'multi')`.

The table is per-schema, matching the journal, so two modules in two schemas of one
database each keep their own.

### Control flow

`Migrate(connectionString, schema, singleTenant)` becomes:

1. validate the schema name, ensure the database, ensure the schema — unchanged
2. ensure the imprint table exists
3. resolve the store's recorded tenancy
4. if a resolved value contradicts `singleTenant`, throw before running any script
5. run the transaction runs — unchanged
6. on success, record the imprint with `ON CONFLICT (fact) DO NOTHING`

Resolution in step 3 has three outcomes:

- a `fact = 'tenancy'` row exists — that is the recorded mode
- no row, but `alberto_events` exists — infer from whether it has a `tenant_id`
  column, the query `ValidateTenancyMode` already uses
- neither — the store is fresh and nothing is recorded, so either mode is allowed

The sniff is not only a legacy adoption path. It is the authoritative fallback
whenever the imprint is absent, which reduces the rule to one sentence: **the schema
shape is the source of truth, and the imprint is a durable record of it.**

### Why the imprint is recorded after migration, not before

If migration fails halfway, no imprint is written — but step 3's sniff then reads a
store where `alberto_events` exists *with* `tenant_id`, so a retry with the flag
flipped is still refused. Conversely a store that failed at script `001` has no
`alberto_events` at all, reads as fresh, and can legitimately be re-attempted in
either mode. The sniff covers the partially-migrated case correctly, so writing the
imprint before the scripts would only add false blocks.

### Honest accounting of what the imprint buys

For tenancy specifically, moving the existing check ahead of the migration is the
actual fix, and the sniff carries most of the weight. The imprint's value is that
`module_key`, `shard_id` and catalog role cannot be sniffed at all. It is the frame
those need, seeded with the one fact in scope.

### The diagnostic

A dedicated public `AlbertoStoreMismatchException`, so
`AlbertoMigrationHostedService.TryMigrate` can let it through unwrapped instead of
prefixing it with "schema migration failed" — which would be actively misleading,
since no migration was attempted.

```
ALB0021: Alberto store tenancy mismatch in schema 'orders'.

  The store was created single-tenant; the module declares .WithTenancy().
  (Inferred from alberto_events, which has no tenant_id column.)

  Single-tenant and multi-tenant are separate schemas, not a setting. There is no
  in-place migration between them, and no backfill for tenant_id on existing events.

  To run multi-tenant, point this module at a new database and replay into it.
  To keep this store, remove .WithTenancy() from the module declaration.

  No migration scripts were run.
```

The parenthetical swaps to `(Recorded in alberto_store_imprint.)` when the imprint
supplied the value, so an operator can tell which source spoke. The closing line is
load-bearing: it tells them the store is untouched.

`ALB0021` continues the `ALB00xx` configuration-diagnostic sequence even though it
originates in `PostgresMigrator` rather than `AlbertoModuleValidator`. Greppability is
worth more than provenance purity.

`ValidateTenancyMode` stays as a public method and becomes a thin wrapper over the
same resolution, so the post-migration call in `AlbertoMigrationHostedService` remains
a harmless second opinion rather than the load-bearing check.

`GetPendingMigrations` applies the same resolution and throws the same exception.
Otherwise `alberto status` would report 30 pending migrations against a store that can
never accept them.

### Concurrency

Replicas starting together all hit `CREATE TABLE IF NOT EXISTS` on the imprint as the
first thing they do, which can still race on `pg_type` and raise `42P07` or `23505`.
Those two SQLSTATEs are tolerated on the create and the read is retried. This is
hotter than the equivalent exposure in `EnsureSchemaExists`, which is why it gets
handling rather than matching existing behaviour.

## Out of scope

**Pointing a module at an empty database.** Schema renamed, connection string wrong,
volume lost — there is no imprint and no `alberto_events`, so it reads as a first run,
migrates cleanly, and serves an empty store with reset checkpoints. That is arguably a
worse failure than the tenancy flip, and this design does nothing about it. Recording
`module_key` would catch the wrong-store variant but not the empty-store one, which
needs a different mechanism. Recorded here so the imprint is not mistaken for covering
it.

Also untouched: `PostgresCatalogMigrator`, which has its own script set, and the
in-memory backend, which persists nothing.

## Testing

Testcontainers, alongside the existing cases in `MigrationUpgradeAndParityTests`:

- single-tenant store, then `Migrate(singleTenant: false)` throws **and**
  `schemaversions` gained no rows
- the reverse direction
- legacy store — imprint dropped after a normal migrate — mismatch still caught by the
  sniff; matching mode proceeds and backfills the imprint
- fresh database — either mode succeeds, imprint written afterwards
- migrate twice — exactly one imprint row

The partially-migrated case is the legacy/sniff test by another name: same code path,
and inducing a real mid-run failure would need fragile fault injection.
