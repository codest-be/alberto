-- Alberto DCB Event Store - Migration 012 (Multi-Tenant)
-- SQL-2: Add alberto_tenants catalog table maintained by a trigger on
--         alberto_events, so GetKnownTenantsAsync can query the catalog instead
--         of doing a full-table SELECT DISTINCT tenant_id.
--
-- P1.2 (TEN-8): Add tenant_id column to alberto_dead_letter_events and
--               alberto_outbox_entries so dead letters and outbox entries can be
--               filtered and isolated per tenant.

-- ============================================================
-- SQL-2: alberto_tenants catalog table
-- ============================================================

-- Catalog table: one row per known tenant, appended on first event for that
-- tenant and updated (last_seen_at) on every subsequent event batch.
CREATE TABLE IF NOT EXISTS $schema_prefix$alberto_tenants (
    tenant_id    VARCHAR(100) NOT NULL,
    first_seen_at TIMESTAMPTZ NOT NULL DEFAULT now(),
    last_seen_at  TIMESTAMPTZ NOT NULL DEFAULT now(),
    CONSTRAINT pk_alberto_tenants PRIMARY KEY (tenant_id)
);

COMMENT ON TABLE $schema_prefix$alberto_tenants IS
    'Catalog of known tenant IDs, maintained by trigger on alberto_events. '
    'Avoids full-scan SELECT DISTINCT tenant_id queries in GetKnownTenantsAsync.';

-- Trigger function: upsert each distinct tenant_id seen in the new batch.
-- Statement-level so it fires once per INSERT statement, not once per row.
CREATE OR REPLACE FUNCTION $schema_prefix$alberto_maintain_tenants_batch()
RETURNS TRIGGER AS $$
BEGIN
    INSERT INTO $schema_prefix$alberto_tenants (tenant_id, first_seen_at, last_seen_at)
    SELECT DISTINCT tenant_id, now(), now()
    FROM new_table
    ON CONFLICT (tenant_id) DO UPDATE
        SET last_seen_at = now();
    RETURN NULL;
END;
$$ LANGUAGE plpgsql;

DROP TRIGGER IF EXISTS alberto_trg_maintain_tenants ON $schema_prefix$alberto_events;
CREATE TRIGGER alberto_trg_maintain_tenants
    AFTER INSERT ON $schema_prefix$alberto_events
    REFERENCING NEW TABLE AS new_table
    FOR EACH STATEMENT
    EXECUTE FUNCTION $schema_prefix$alberto_maintain_tenants_batch();

-- Backfill: populate from existing events so the catalog is immediately
-- consistent when the migration runs.  The statement is idempotent (ON CONFLICT
-- DO UPDATE), so re-running is safe.
INSERT INTO $schema_prefix$alberto_tenants (tenant_id, first_seen_at, last_seen_at)
SELECT tenant_id,
       MIN(created_at) AS first_seen_at,
       MAX(created_at) AS last_seen_at
FROM $schema_prefix$alberto_events
GROUP BY tenant_id
ON CONFLICT (tenant_id) DO UPDATE
    SET first_seen_at = LEAST(  $schema_prefix$alberto_tenants.first_seen_at, EXCLUDED.first_seen_at),
        last_seen_at  = GREATEST($schema_prefix$alberto_tenants.last_seen_at,  EXCLUDED.last_seen_at);

-- ============================================================
-- P1.2 (TEN-8): tenant_id column on dead_letter_events
-- ============================================================

-- Add tenant_id to dead letter events.  Nullable: rows inserted before this
-- migration have no known tenant.  The C# insertion path (PostgresAdminDataAccess
-- or equivalent) must be updated to populate this column; see crossFileNeeds.
ALTER TABLE $schema_prefix$alberto_dead_letter_events
    ADD COLUMN IF NOT EXISTS tenant_id VARCHAR(100);

-- Index for tenant-scoped dead-letter admin queries
-- (e.g. "all failed events for tenant X in processor Y").
CREATE INDEX IF NOT EXISTS idx_alberto_dead_letter_tenant_processor
    ON $schema_prefix$alberto_dead_letter_events(tenant_id, processor_id)
    WHERE tenant_id IS NOT NULL;

-- ============================================================
-- P1.2 (TEN-8): tenant_id column on outbox_entries
-- ============================================================

-- Add tenant_id to outbox entries.  Nullable for the same reason as above.
ALTER TABLE $schema_prefix$alberto_outbox_entries
    ADD COLUMN IF NOT EXISTS tenant_id VARCHAR(100);

-- Partial index for tenant-scoped pending outbox queries.
CREATE INDEX IF NOT EXISTS idx_alberto_outbox_tenant_pending
    ON $schema_prefix$alberto_outbox_entries(tenant_id, created_at)
    WHERE tenant_id IS NOT NULL AND status = 'pending';
