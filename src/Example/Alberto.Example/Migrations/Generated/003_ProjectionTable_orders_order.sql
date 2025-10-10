-- =============================================================================
-- PROJECTION TABLE: {schema}.order
-- =============================================================================

CREATE TABLE IF NOT EXISTS {schema}.order
(
    tenant_id
    TEXT
    NOT
    NULL,
    key
    TEXT
    NOT
    NULL,
    state
    JSONB
    NOT
    NULL,
    global_version
    BIGINT
    NOT
    NULL
    DEFAULT
    0,
    updated_at
    TIMESTAMPTZ
    NOT
    NULL
    DEFAULT
    NOW
(
),
    PRIMARY KEY
(
    tenant_id,
    key
)
    );

CREATE INDEX IF NOT EXISTS idx_{schema} _order_tenant_updated ON {schema}.order(tenant_id, updated_at);
CREATE INDEX IF NOT EXISTS idx_{schema} _order_global_version ON {schema}.order(tenant_id, key, global_version);

COMMENT ON TABLE {schema}.order IS 'Projection state for order';
