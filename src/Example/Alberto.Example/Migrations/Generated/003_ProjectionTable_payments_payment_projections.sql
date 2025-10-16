-- =============================================================================
-- PROJECTION TABLE: payments.payment_projections
-- =============================================================================

CREATE TABLE IF NOT EXISTS payments.payment_projections
(
    tenant_id      TEXT        NOT NULL,
    key            TEXT        NOT NULL,
    state          JSONB       NOT NULL,
    global_version BIGINT      NOT NULL DEFAULT 0,
    updated_at     TIMESTAMPTZ NOT NULL DEFAULT NOW(),
    PRIMARY KEY (tenant_id, key)
);

CREATE INDEX IF NOT EXISTS idx_payments_payment_projections_tenant_updated ON payments.payment_projections (tenant_id, updated_at);
CREATE INDEX IF NOT EXISTS idx_payments_payment_projections_global_version ON payments.payment_projections (tenant_id, key, global_version);

COMMENT ON TABLE payments.payment_projections IS 'Projection state for payment_projections';
