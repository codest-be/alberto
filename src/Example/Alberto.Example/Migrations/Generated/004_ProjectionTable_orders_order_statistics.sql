-- =============================================================================
-- PROJECTION TABLE: orders.order_statistics
-- =============================================================================

CREATE TABLE IF NOT EXISTS orders.order_statistics
(
    tenant_id      TEXT        NOT NULL,
    key            TEXT        NOT NULL,
    state          JSONB       NOT NULL,
    global_version BIGINT      NOT NULL DEFAULT 0,
    updated_at     TIMESTAMPTZ NOT NULL DEFAULT NOW(),
    PRIMARY KEY (tenant_id, key)
);

CREATE INDEX IF NOT EXISTS idx_orders_order_statistics_tenant_updated ON orders.order_statistics (tenant_id, updated_at);
CREATE INDEX IF NOT EXISTS idx_orders_order_statistics_global_version ON orders.order_statistics (tenant_id, key, global_version);

COMMENT ON TABLE orders.order_statistics IS 'Projection state for order_statistics';
