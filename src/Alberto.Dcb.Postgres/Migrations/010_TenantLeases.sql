-- Alberto DCB Event Store - Tenant Leases for Fair Distribution
-- Database-backed leases that expire and must be renewed

-- Table for tracking tenant ownership with expiring leases
CREATE TABLE IF NOT EXISTS $schema_prefix$tenant_leases (
    consumer_id TEXT NOT NULL,
    tenant_id TEXT NOT NULL,
    replica_id TEXT NOT NULL,
    acquired_at TIMESTAMPTZ NOT NULL DEFAULT NOW(),
    expires_at TIMESTAMPTZ NOT NULL,
    PRIMARY KEY (consumer_id, tenant_id)
);

-- Index for efficiently finding expired leases
CREATE INDEX IF NOT EXISTS ix_tenant_leases_expires
    ON $schema_prefix$tenant_leases (expires_at);

-- Index for efficiently finding leases by replica (for renewal)
CREATE INDEX IF NOT EXISTS ix_tenant_leases_replica
    ON $schema_prefix$tenant_leases (replica_id);

-- Add comments explaining the lease system
COMMENT ON TABLE $schema_prefix$tenant_leases IS 'Tracks tenant ownership with expiring leases for fair distribution across replicas.';
COMMENT ON COLUMN $schema_prefix$tenant_leases.consumer_id IS 'Unique identifier for the consumer group.';
COMMENT ON COLUMN $schema_prefix$tenant_leases.tenant_id IS 'The tenant ID being claimed.';
COMMENT ON COLUMN $schema_prefix$tenant_leases.replica_id IS 'Unique identifier for the replica holding the lease.';
COMMENT ON COLUMN $schema_prefix$tenant_leases.acquired_at IS 'When the lease was first acquired.';
COMMENT ON COLUMN $schema_prefix$tenant_leases.expires_at IS 'When the lease expires if not renewed.';
