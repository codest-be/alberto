-- Alberto DCB Event Store - Migration 015 (Multi-Tenant)
-- Stop reusing rebuild version numbers.
--
-- Migration 014 allocated a rebuild's version as active_version + 1.  That is
-- correct after a promotion, which advances active_version, but wrong after an
-- abort, which deliberately leaves active_version alone -- so the next rebuild
-- was handed the number the aborted one had just been using.
--
-- Reuse is not merely untidy.  Abort deletes the abandoned version's rows and
-- flips the status in one transaction, but the shadow control loop that is
-- replaying into that version lives in the application process and only learns
-- about the abort on its next poll.  Anything it writes in that window survives
-- the delete.  Reallocating the same number then replays the whole log on top of
-- those orphans, and every event lands twice: a projection that should read 6
-- reads 12, and promotion publishes that to readers.
--
-- The high-water mark makes the reuse impossible rather than merely unlikely.  It
-- is a separate column because neither active_version nor rebuilding_version can
-- carry it: promotion moves one and abort clears the other, and the number that
-- must never be handed out again outlives both.
--
-- Existing rows seed from the highest version they can prove was allocated.

ALTER TABLE $schema_prefix$alberto_projection_rebuild_meta
    ADD COLUMN IF NOT EXISTS last_allocated_version INTEGER;

UPDATE $schema_prefix$alberto_projection_rebuild_meta
    SET last_allocated_version = GREATEST(active_version, COALESCE(rebuilding_version, 1))
    WHERE last_allocated_version IS NULL;

ALTER TABLE $schema_prefix$alberto_projection_rebuild_meta
    ALTER COLUMN last_allocated_version SET DEFAULT 1;

ALTER TABLE $schema_prefix$alberto_projection_rebuild_meta
    ALTER COLUMN last_allocated_version SET NOT NULL;

-- A version that has been handed out cannot be un-handed-out, so the mark can
-- only ever be at or ahead of both live version numbers.
ALTER TABLE $schema_prefix$alberto_projection_rebuild_meta
    DROP CONSTRAINT IF EXISTS alberto_projection_rebuild_meta_high_water_check;

ALTER TABLE $schema_prefix$alberto_projection_rebuild_meta
    ADD CONSTRAINT alberto_projection_rebuild_meta_high_water_check
    CHECK (
        last_allocated_version >= active_version
        AND last_allocated_version >= COALESCE(rebuilding_version, 1)
    )
    NOT VALID;

COMMENT ON COLUMN $schema_prefix$alberto_projection_rebuild_meta.last_allocated_version IS
    'High-water mark of every version ever allocated to this processor. The next rebuild takes this + 1, so an aborted rebuild never donates its number to the rebuild that follows it.';
