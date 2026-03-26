-- Alberto DCB Event Store - Projection States
-- Stores projection state as JSONB, keyed by tenant + projection type + document ID

CREATE TABLE IF NOT EXISTS $schema_prefix$projection_states (
    tenant_id         TEXT NOT NULL,
    projection_type   TEXT NOT NULL,
    document_id       TEXT NOT NULL,
    state             JSONB NOT NULL,
    updated_at        TIMESTAMPTZ NOT NULL DEFAULT now(),
    PRIMARY KEY (tenant_id, projection_type, document_id)
);

CREATE INDEX IF NOT EXISTS idx_projection_states_tenant_type
    ON $schema_prefix$projection_states(tenant_id, projection_type);
