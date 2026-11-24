-- Alberto DCB Event Store – 012 Projection States Recent (Multi-Tenant)
--
-- ListRecentAsync reads one tenant's documents of one projection version, newest first:
--   WHERE tenant_id = $1 AND projection_type = $2 AND rebuild_version = $3
--   ORDER BY updated_at DESC LIMIT $4
-- No index ordered those rows by updated_at, so every call read and sorted the whole
-- projection for that tenant. This index hands the rows over in order and the LIMIT stops
-- the scan after n of them.
--
-- idx_alberto_projection_states_version (projection_type, rebuild_version) stays: discarding a
-- rebuild version deletes it across every tenant, which a tenant-led index cannot serve.
--
-- Cost: one more index entry per projection state row, rewritten on every upsert.
--
-- codest-be/alberto#270.

CREATE INDEX IF NOT EXISTS idx_alberto_projection_states_recent
    ON $schema_prefix$alberto_projection_states (tenant_id, projection_type, rebuild_version, updated_at DESC);
