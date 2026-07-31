-- Alberto DCB Event Store - Migration 034 (Multi-Tenant)
-- alberto:no-transaction
--
-- Index the rows the retention purge deletes.
--
-- PurgeDeliveredAsync runs:
--
--   DELETE FROM alberto_outbox_entries
--    WHERE status = 'delivered' AND delivered_at < $1
--
-- Until now nothing indexed that predicate.  The three existing outbox indexes are all
-- partial on status 'pending' or 'processing', so a purge had to sequentially scan the
-- whole table -- including every delivered row it was about to keep.  That is tolerable
-- when the method is called by hand and ruinous once a background service calls it on a
-- schedule: the sweep's cost grows with total history rather than with the number of rows
-- it removes, which is the opposite of what a retention sweep should do.
--
-- A partial index on delivered_at where status = 'delivered' turns the sweep into a range
-- scan over exactly the doomed rows.  Delivered entries are also the only ones that ever
-- enter it, and they leave it when they are deleted, so it stays proportional to the
-- retention window rather than to the table.
--
-- Built CONCURRENTLY because the outbox is a write path -- the handler inserts into it in
-- the same transaction as the append, so a plain CREATE INDEX would hold a SHARE lock that
-- blocks appends for the length of the build.  Hence the alberto:no-transaction marker:
-- PostgreSQL rejects CREATE INDEX CONCURRENTLY inside a transaction block and DbUp
-- otherwise wraps each script in one.
--
-- The DROP is not redundant, for the reason migration 020 spells out: an interrupted
-- concurrent build leaves an invalid index the planner never uses, and because the failed
-- script was not journaled the migration is retried -- where a bare CREATE ... IF NOT
-- EXISTS would find that carcass, skip the build and report success.

DROP INDEX CONCURRENTLY IF EXISTS $schema_prefix$idx_alberto_outbox_delivered;

CREATE INDEX CONCURRENTLY IF NOT EXISTS idx_alberto_outbox_delivered
    ON $schema_prefix$alberto_outbox_entries (delivered_at)
    WHERE status = 'delivered';
