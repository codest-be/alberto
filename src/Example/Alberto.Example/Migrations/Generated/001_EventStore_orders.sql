-- =============================================================================
-- ALBERTO EVENT STORE SCHEMA FOR POSTGRESQL
-- =============================================================================
-- Schema: orders
-- =============================================================================

CREATE SCHEMA IF NOT EXISTS orders;

-- Main events table with tenant support
CREATE TABLE IF NOT EXISTS orders.events
(
    position   BIGSERIAL PRIMARY KEY,
    id         UUID        NOT NULL UNIQUE,
    tenant_id  VARCHAR(20) NOT NULL,
    event_type TEXT        NOT NULL,
    data       JSONB       NOT NULL,
    tags       TEXT[]      NOT NULL DEFAULT '{}',
    created_at TIMESTAMPTZ NOT NULL DEFAULT NOW(),
    metadata   JSONB       NOT NULL DEFAULT '{}'
);

-- Index for consistency checks: WHERE tenant_id = ? AND id = ?
-- Used by: Consistency boundary checks in append operations
CREATE INDEX IF NOT EXISTS idx_orders_events_tenant_id
    ON orders.events (tenant_id, id);

-- Primary covering index for tenant-scoped queries
-- Used by: Stream(tenant), Stream(tenant, tags), Stream(tenant, eventType, tags)
-- Covers: Most tenant queries with index-only scans for minority tenants
CREATE INDEX IF NOT EXISTS idx_orders_events_tenant_all
    ON orders.events (tenant_id, position)
    INCLUDE (event_type, tags, id, data, metadata, created_at);

-- Global position index for subscription queries
-- Used by: StreamAll(fromPosition) - cross-tenant event streaming
-- Covers: Subscription queries with index-only scans
CREATE INDEX IF NOT EXISTS idx_orders_events_global_position
    ON orders.events (position)
    INCLUDE (id, tenant_id, event_type, tags, data, metadata, created_at);

-- Specialized index for event type filtering
-- Used by: Stream(tenant, eventType) when event_type is highly selective
-- Note: tenant_all can handle this too, but this is faster for event_type-first queries
CREATE INDEX IF NOT EXISTS idx_orders_events_tenant_type_position
    ON orders.events (tenant_id, event_type, position);

-- Subscription checkpoints
CREATE TABLE IF NOT EXISTS orders.subscription_checkpoints
(
    subscription_id VARCHAR PRIMARY KEY,
    position BIGINT NULL,
    updated_at      TIMESTAMPTZ NOT NULL DEFAULT NOW()
);

CREATE INDEX IF NOT EXISTS idx_orders_subscription_checkpoints_updated ON orders.subscription_checkpoints (updated_at DESC);

-- Poison pills
CREATE TABLE IF NOT EXISTS orders.subscription_poison_pills
(
    id                UUID PRIMARY KEY,
    subscription_id   VARCHAR     NOT NULL,
    global_position   BIGINT      NOT NULL,
    event_id          UUID        NOT NULL,
    event_type        VARCHAR     NOT NULL,
    event_data        JSONB       NOT NULL,
    metadata          JSONB       NOT NULL,
    error_message     TEXT        NOT NULL,
    stack_trace       TEXT,
    retry_count       INT         NOT NULL,
    first_failed_at   TIMESTAMPTZ NOT NULL,
    last_failed_at    TIMESTAMPTZ NOT NULL,
    resolved_at       TIMESTAMPTZ,
    resolved_by       VARCHAR,
    resolution_action VARCHAR,
    resolution_notes TEXT,
    UNIQUE (subscription_id, global_position) -- Ensure global_position is unique within subscription
);

CREATE INDEX IF NOT EXISTS idx_orders_poison_pills_subscription ON orders.subscription_poison_pills (subscription_id);
CREATE INDEX IF NOT EXISTS idx_orders_poison_pills_event ON orders.subscription_poison_pills (event_id);
CREATE INDEX IF NOT EXISTS idx_orders_poison_pills_unresolved ON orders.subscription_poison_pills (subscription_id, last_failed_at) WHERE resolved_at IS NULL;
