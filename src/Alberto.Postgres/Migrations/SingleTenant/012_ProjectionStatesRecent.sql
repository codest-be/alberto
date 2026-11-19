-- Alberto DCB Event Store – 012 Projection States Recent (Single-Tenant)
--
-- ListRecentAsync reads one projection version's documents, newest first:
--   WHERE projection_type = $1 AND rebuild_version = $2
--   ORDER BY updated_at DESC LIMIT $3
-- idx_alberto_projection_states_version (projection_type, rebuild_version) finds the rows but
-- not in order, so every call read and sorted the whole projection. This index hands the rows
-- over in order and the LIMIT stops the scan after n of them.
--
-- It also serves everything idx_alberto_projection_states_version does, its columns being a
-- leading prefix of this one. That index is left in place for now so a replica still on the
-- previous build loses no plan during a rolling deploy; a later release can drop it.
--
-- Cost: one more index entry per projection state row, rewritten on every upsert.
--
-- codest-be/alberto#270.

CREATE INDEX IF NOT EXISTS idx_alberto_projection_states_recent
    ON $schema_prefix$alberto_projection_states (projection_type, rebuild_version, updated_at DESC);
