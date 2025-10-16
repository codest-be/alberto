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

-- Essential indexes
CREATE INDEX IF NOT EXISTS idx_orders_events_tenant_position ON orders.events (tenant_id, position DESC);
CREATE INDEX IF NOT EXISTS idx_orders_events_consistency ON orders.events (tenant_id, position) WHERE position > 0;
CREATE INDEX IF NOT EXISTS idx_orders_events_global_position ON orders.events (position) INCLUDE (tenant_id, event_type, tags, data, metadata, created_at);

-- Optimized tenant-first indexes
CREATE INDEX IF NOT EXISTS idx_orders_events_tenant_tags_gin ON orders.events (tenant_id, tags) WHERE array_length(tags, 1) > 0;
CREATE INDEX IF NOT EXISTS idx_orders_events_tenant_type_tags ON orders.events (tenant_id, event_type, tags) WHERE array_length(tags, 1) > 0;
CREATE INDEX IF NOT EXISTS idx_orders_events_tenant_tags_covering ON orders.events (tenant_id) INCLUDE (event_type, tags, data, metadata, created_at, position) WHERE array_length(tags, 1) > 0;

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
