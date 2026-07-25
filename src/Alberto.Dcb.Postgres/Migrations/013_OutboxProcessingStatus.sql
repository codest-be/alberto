-- Alberto DCB Event Store - Migration 013 (Multi-Tenant)
-- Extend the alberto_outbox_entries.status CHECK constraint to permit 'processing'.
--
-- ClaimPendingAsync atomically claims pending outbox rows by updating their status to
-- 'processing' (via UPDATE … FROM candidates WHERE … FOR UPDATE SKIP LOCKED).
-- Migration 001 created the constraint as CHECK (status IN ('pending', 'delivered',
-- 'failed')), which means the claim UPDATE was rejected by PostgreSQL on any database
-- built by the shipped migrations.  This migration adds 'processing' to the allowed
-- set so that the relay can operate correctly against existing databases.
--
-- For fresh installations migration 001 has also been corrected in-place, so new
-- databases never enter an inconsistent state.
--
-- The DROP … IF EXISTS / ADD CONSTRAINT pattern is the standard PostgreSQL approach
-- for modifying a CHECK constraint (they cannot be altered in-place).  If an
-- installation was somehow already running with the extended constraint the DROP
-- removes it cleanly and the ADD re-creates it in its canonical form.

ALTER TABLE $schema_prefix$alberto_outbox_entries
    DROP CONSTRAINT IF EXISTS alberto_outbox_entries_status_check;

ALTER TABLE $schema_prefix$alberto_outbox_entries
    ADD CONSTRAINT alberto_outbox_entries_status_check
    CHECK (status IN ('pending', 'processing', 'delivered', 'failed'));
