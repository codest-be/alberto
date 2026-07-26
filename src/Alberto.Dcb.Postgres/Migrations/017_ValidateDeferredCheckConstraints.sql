-- Alberto DCB Event Store - Migration 017 (Multi-Tenant)
-- Validate the CHECK constraints that migrations 013-015 added NOT VALID.
--
-- Those three scripts add their constraints without verifying the rows that already
-- existed, because a validating ADD CONSTRAINT scans the whole table while holding the
-- ACCESS EXCLUSIVE lock the ALTER took -- on a live outbox that is a write outage for
-- the length of a full scan.  VALIDATE CONSTRAINT performs the same scan under
-- SHARE UPDATE EXCLUSIVE instead, which concurrent readers and writers pass straight
-- through, so the verification happens here rather than there.
--
-- This has to be its own script and not extra statements at the bottom of 013-015:
-- DbUp runs each script inside a single transaction, so a VALIDATE placed after its own
-- ADD would still sit behind that statement's ACCESS EXCLUSIVE lock until commit.  A
-- separate script is a separate transaction, which is the whole point.
--
-- VALIDATE CONSTRAINT is a no-op on an already-validated constraint, so this script is
-- safe on databases that were built before 013-015 were split (their constraints were
-- created validated) as well as on fresh installations.
--
-- Every constraint below only ever widened or restated an invariant the writing code
-- already upheld, so no existing row is expected to fail.  If one does, the migration
-- fails loudly here rather than letting the constraint sit permanently unenforced --
-- which is exactly what should happen, and is why NOT VALID is not left dangling.

ALTER TABLE $schema_prefix$alberto_outbox_entries
    VALIDATE CONSTRAINT alberto_outbox_entries_status_check;

ALTER TABLE $schema_prefix$alberto_projection_rebuild_meta
    VALIDATE CONSTRAINT alberto_projection_rebuild_meta_status_check;

ALTER TABLE $schema_prefix$alberto_projection_rebuild_meta
    VALIDATE CONSTRAINT alberto_projection_rebuild_meta_version_check;

ALTER TABLE $schema_prefix$alberto_projection_rebuild_meta
    VALIDATE CONSTRAINT alberto_projection_rebuild_meta_high_water_check;
