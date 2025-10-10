-- =============================================================================
-- ALBERTO EVENT STORE SCHEMA FOR POSTGRESQL
-- =============================================================================
-- Schema: {schema}
-- =============================================================================

CREATE SCHEMA IF NOT EXISTS {schema};

-- Main events table with tenant support
CREATE TABLE IF NOT EXISTS {schema}.events
(
    position
    BIGSERIAL
    PRIMARY
    KEY,
    id
    UUID
    NOT
    NULL
    UNIQUE,
    tenant_id
    VARCHAR
(
    20
) NOT NULL,
    event_type TEXT NOT NULL,
    data JSONB NOT NULL,
    tags TEXT [] NOT NULL DEFAULT '{}',
    created_at TIMESTAMPTZ NOT NULL DEFAULT NOW
(
),
    metadata JSONB NOT NULL DEFAULT '{}'
    );

-- Essential indexes
CREATE INDEX IF NOT EXISTS idx_{schema} _events_tenant_position ON {schema}.events (tenant_id, position DESC);
CREATE INDEX IF NOT EXISTS idx_{schema} _events_consistency ON {schema}.events (tenant_id, position) WHERE position > 0;
CREATE INDEX IF NOT EXISTS idx_{schema} _events_global_position ON {schema}.events (position) INCLUDE (tenant_id, event_type, tags, data, metadata, created_at);

-- Optimized tenant-first indexes
CREATE INDEX IF NOT EXISTS idx_{schema} _events_tenant_tags_gin ON {schema}.events (tenant_id, tags) WHERE array_length(tags, 1) > 0;
CREATE INDEX IF NOT EXISTS idx_{schema} _events_tenant_type_tags ON {schema}.events (tenant_id, event_type, tags) WHERE array_length(tags, 1) > 0;
CREATE INDEX IF NOT EXISTS idx_{schema} _events_tenant_tags_covering ON {schema}.events (tenant_id) INCLUDE (event_type, tags, data, metadata, created_at, position) WHERE array_length(tags, 1) > 0;

-- Subscription checkpoints
CREATE TABLE IF NOT EXISTS {schema}.subscription_checkpoints
(
    subscription_id
    VARCHAR
    PRIMARY
    KEY,
    position
    BIGINT
    NULL,
    updated_at
    TIMESTAMPTZ
    NOT
    NULL
    DEFAULT
    NOW
(
)
    );

CREATE INDEX IF NOT EXISTS idx_{schema} _subscription_checkpoints_updated ON {schema}.subscription_checkpoints (updated_at DESC);

-- Poison pills
CREATE TABLE IF NOT EXISTS {schema}.subscription_poison_pills
(
    id
    UUID
    PRIMARY
    KEY,
    subscription_id
    VARCHAR
    NOT
    NULL,
    global_position
    BIGINT
    NOT
    NULL,
    event_id
    UUID
    NOT
    NULL,
    event_type
    VARCHAR
    NOT
    NULL,
    event_data
    JSONB
    NOT
    NULL,
    metadata
    JSONB
    NOT
    NULL,
    error_message
    TEXT
    NOT
    NULL,
    stack_trace
    TEXT,
    retry_count
    INT
    NOT
    NULL,
    first_failed_at
    TIMESTAMPTZ
    NOT
    NULL,
    last_failed_at
    TIMESTAMPTZ
    NOT
    NULL,
    resolved_at
    TIMESTAMPTZ,
    resolved_by
    VARCHAR,
    resolution_action
    VARCHAR,
    resolution_notes
    TEXT
);

CREATE INDEX IF NOT EXISTS idx_{schema} _poison_pills_subscription ON {schema}.subscription_poison_pills (subscription_id);
CREATE INDEX IF NOT EXISTS idx_{schema} _poison_pills_event ON {schema}.subscription_poison_pills (event_id);
CREATE INDEX IF NOT EXISTS idx_{schema} _poison_pills_unresolved ON {schema}.subscription_poison_pills (subscription_id, last_failed_at) WHERE resolved_at IS NULL;
