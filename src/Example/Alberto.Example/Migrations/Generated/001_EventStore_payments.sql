-- =============================================================================
-- ALBERTO EVENT STORE SCHEMA FOR POSTGRESQL
-- =============================================================================
-- Schema: payments
-- =============================================================================

CREATE SCHEMA IF NOT EXISTS payments;

-- Main events table with tenant support
CREATE TABLE IF NOT EXISTS payments.events
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

-- Consistency check queries - WHERE tenant_id = ? AND id = ?
CREATE INDEX IF NOT EXISTS idx_{schema} _events_tenant_id ON {schema}.events (tenant_id, id);

-- Primary covering index for most tenant queries
CREATE INDEX IF NOT EXISTS idx_{schema} _events_tenant_all
    ON {schema}.events (tenant_id, position)
    INCLUDE (event_type, tags, id, data, metadata, created_at);

-- Global position index for StreamAll() subscription queries
CREATE INDEX IF NOT EXISTS idx_{schema} _events_global_position ON {schema}.events (position)
    INCLUDE (id, tenant_id, event_type, tags, data, metadata, created_at);

-- For queries filtering by event type: WHERE tenant_id = ? AND event_type = ?
CREATE INDEX IF NOT EXISTS idx_{schema} _events_tenant_type_position ON {schema}.events (tenant_id, event_type, position);

-- GIN index for tag array queries ONLY (separate index)
CREATE INDEX IF NOT EXISTS idx_{schema} _events_tags_gin ON {schema}.events USING GIN (tags)
    WHERE array_length(tags, 1) > 0;

-- Subscription checkpoints
CREATE TABLE IF NOT EXISTS payments.subscription_checkpoints
(
    subscription_id VARCHAR PRIMARY KEY,
    position BIGINT NULL,
    updated_at      TIMESTAMPTZ NOT NULL DEFAULT NOW()
);

CREATE INDEX IF NOT EXISTS idx_payments_subscription_checkpoints_updated ON payments.subscription_checkpoints (updated_at DESC);

-- Poison pills
CREATE TABLE IF NOT EXISTS payments.subscription_poison_pills
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

CREATE INDEX IF NOT EXISTS idx_payments_poison_pills_subscription ON payments.subscription_poison_pills (subscription_id);
CREATE INDEX IF NOT EXISTS idx_payments_poison_pills_event ON payments.subscription_poison_pills (event_id);
CREATE INDEX IF NOT EXISTS idx_payments_poison_pills_unresolved ON payments.subscription_poison_pills (subscription_id, last_failed_at) WHERE resolved_at IS NULL;
