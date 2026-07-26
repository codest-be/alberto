-- Alberto DCB Event Store - Migration 018 (Single-Tenant)
-- alberto:no-transaction
--
-- Index the rows the stable-head query can actually match.
--
-- See multi-tenant 018_InFlightVisibilityIndex.sql for full rationale.  The stable-head
-- query is the same in both variants -- it reads global_position and pg_xact_id and nothing
-- else -- so the index is identical.

DROP INDEX CONCURRENTLY IF EXISTS $schema_prefix$ix_alberto_events_inflight;

CREATE INDEX CONCURRENTLY IF NOT EXISTS ix_alberto_events_inflight
    ON $schema_prefix$alberto_events (global_position)
    WHERE pg_xact_id IS NOT NULL;
