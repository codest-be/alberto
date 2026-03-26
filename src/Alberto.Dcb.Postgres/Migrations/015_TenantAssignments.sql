-- Alberto DCB Event Store - Tenant Assignments for Consistent Hash Ring
-- Stores deterministic tenant-to-node assignments based on consistent hashing

CREATE TABLE IF NOT EXISTS $schema_prefix$tenant_assignments (
    tenant_id TEXT PRIMARY KEY,
    node_id TEXT NOT NULL,
    assigned_at TIMESTAMPTZ NOT NULL DEFAULT now(),
    ring_version BIGINT NOT NULL DEFAULT 1
);

CREATE INDEX IF NOT EXISTS ix_tenant_assignments_node_id
    ON $schema_prefix$tenant_assignments (node_id);
