-- Alberto DCB Event Store - Migration 008 (Single-Tenant)
-- In-flight visibility barrier (#3): record the inserting transaction's xid8 on
-- every new event. Subscribers use it to compute a "stable head" that never
-- advances past an append whose transaction has not committed yet — closing a
-- race where a slow-committing append can be skipped by the contiguous-gap scan
-- once later positions commit ahead of it.
--
-- Existing rows keep NULL and are always treated as stable (they committed long
-- before any live subscriber snapshot). The column is added WITHOUT a default
-- first so the ALTER is metadata-only (no table rewrite on large logs); the
-- default is attached afterwards and therefore only applies to future inserts.
-- In single-tenant mode every append serializes on one advisory lock, so
-- global_position order and xid order agree globally, making the
-- pg_snapshot_xmin() barrier exact.
ALTER TABLE $schema_prefix$alberto_events
    ADD COLUMN IF NOT EXISTS pg_xact_id BIGINT;

ALTER TABLE $schema_prefix$alberto_events
    ALTER COLUMN pg_xact_id SET DEFAULT pg_current_xact_id()::TEXT::BIGINT;
