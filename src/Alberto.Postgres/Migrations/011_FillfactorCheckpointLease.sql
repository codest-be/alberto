-- Alberto DCB Event Store - Migration 011 (Multi-Tenant)
-- SQL-5: Set FILLFACTOR=70 on the checkpoint and lease tables.
--
-- alberto_processor_checkpoints and alberto_tenant_leases are updated very
-- frequently (every event processed, every lease renewal) but rarely deleted or
-- inserted outside of initial setup.  This makes them ideal candidates for HOT
-- (Heap-Only Tuple) updates, which avoid writing new index entries on each UPDATE.
--
-- HOT updates require free space on the same heap page as the old row version.
-- With the default FILLFACTOR=100 pages are packed full at INSERT time, leaving
-- no room for HOT chain extensions.  FILLFACTOR=70 reserves ~30% of each page
-- so that subsequent UPDATEs on the same row can stay on the same page.
--
-- Effect: reduced index bloat, fewer dead tuples per autovacuum cycle, and lower
-- WAL volume per checkpoint/lease update.
--
-- Note: ALTER TABLE SET only updates the storage parameter for *future* page
-- allocations; existing full pages are not immediately reclaimed.  The benefit
-- accumulates gradually as autovacuum cycles through and pages are rewritten, or
-- can be forced immediately with VACUUM (FULL) / CLUSTER if downtime allows.

ALTER TABLE $schema_prefix$alberto_processor_checkpoints SET (fillfactor = 70);
ALTER TABLE $schema_prefix$alberto_tenant_leases SET (fillfactor = 70);
