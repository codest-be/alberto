-- Alberto DCB Event Store - Migration 018 (Multi-Tenant)
-- alberto:no-transaction
--
-- Index the rows the stable-head query can actually match.
--
-- GetStableHeadAsync finds the first position above the current head whose inserting
-- transaction is not yet older than every in-flight transaction, and clamps the head
-- just below it:
--
--   SELECT global_position FROM alberto_events
--    WHERE global_position > $1
--      AND pg_xact_id IS NOT NULL
--      AND pg_xact_id >= pg_snapshot_xmin(pg_current_snapshot())::TEXT::BIGINT
--    ORDER BY global_position ASC LIMIT 1
--
-- Migration 008 added pg_xact_id to existing tables without backfilling it -- deliberately,
-- so the ALTER stayed metadata-only -- which means every row written before that upgrade
-- has it NULL.  On a database with any history the IS NOT NULL predicate therefore matches
-- only the recent tail, the planner sees a filter that eliminates nearly the whole table,
-- and it chooses a parallel sequential scan.  The primary key on global_position does not
-- rescue it: an ascending walk from a cold head has to step over every historical row
-- before reaching one that can match.
--
-- A partial index contains only the rows that can satisfy the predicate, so the same query
-- becomes an index scan over the tail regardless of how far back the head starts.  It also
-- stays small: rows enter it only from migration 008 onward, and it indexes one bigint.
--
-- Built CONCURRENTLY because alberto_events is the append path -- a plain CREATE INDEX
-- holds a SHARE lock for the length of the build, which blocks every writer.  That is why
-- this script carries the alberto:no-transaction marker above: PostgreSQL rejects
-- CREATE INDEX CONCURRENTLY inside a transaction block, and DbUp otherwise wraps each
-- script in one.
--
-- The DROP is not redundant.  An interrupted concurrent build leaves an index that exists
-- in the catalog but is marked invalid and is never used by the planner; because the failed
-- script was not journaled, the migration is retried, and a bare CREATE ... IF NOT EXISTS
-- would find that carcass, skip the build and report success.  Dropping first means a retry
-- rebuilds rather than silently inheriting a dead index.

DROP INDEX CONCURRENTLY IF EXISTS $schema_prefix$ix_alberto_events_inflight;

CREATE INDEX CONCURRENTLY IF NOT EXISTS ix_alberto_events_inflight
    ON $schema_prefix$alberto_events (global_position)
    WHERE pg_xact_id IS NOT NULL;
