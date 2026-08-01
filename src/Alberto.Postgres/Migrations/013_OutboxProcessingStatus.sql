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
--
-- The ADD is NOT VALID, which is what makes this script safe to run against a live
-- outbox.  A plain ADD CONSTRAINT scans every existing row to prove it conforms, and
-- it does so while holding the ACCESS EXCLUSIVE lock the ALTER already took — so on a
-- busy outbox the relay and every producer block for the length of a full table scan.
-- NOT VALID skips that scan: the constraint takes effect immediately for inserts and
-- updates, and only the pre-existing rows are left unverified.  Migration 017 does the
-- verification separately under SHARE UPDATE EXCLUSIVE, which readers and writers pass
-- straight through.  It has to be a separate *script* rather than a statement below:
-- DbUp runs each script in one transaction, so validating here would hold this
-- statement's ACCESS EXCLUSIVE lock across the scan and defeat the whole exercise.
--
-- The rows cannot in fact fail validation — 'processing' only widens the permitted set
-- — so 017 never has real work to reject.  Splitting it is about the lock, not the risk.

ALTER TABLE $schema_prefix$alberto_outbox_entries
    DROP CONSTRAINT IF EXISTS alberto_outbox_entries_status_check;

ALTER TABLE $schema_prefix$alberto_outbox_entries
    ADD CONSTRAINT alberto_outbox_entries_status_check
    CHECK (status IN ('pending', 'processing', 'delivered', 'failed'))
    NOT VALID;
