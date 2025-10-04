-- =============================================================================
-- DCB EVENT STORE SCHEMA FOR POSTGRESQL
-- =============================================================================

-- Main events table with tenant support
CREATE TABLE IF NOT EXISTS events
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
    tags TEXT[] NOT NULL DEFAULT '{}',
    created_at TIMESTAMPTZ NOT NULL DEFAULT NOW
(
),
    metadata JSONB NOT NULL DEFAULT '{}'
    );

-- =============================================================================
-- BASELINE INDEXES + SIMPLE GIN INDEX (PHASE 2 TESTING)
-- =============================================================================

-- 1. ESSENTIAL: tenant-based ordering and pagination
-- Supports ORDER BY position DESC within tenant
CREATE INDEX IF NOT EXISTS idx_events_tenant_position ON events (tenant_id, position DESC);

-- 2. ESSENTIAL: optimistic concurrency control
-- For consistency boundary checks (version-like behavior)
CREATE INDEX IF NOT EXISTS idx_events_consistency ON events (tenant_id, position)
    WHERE position > 0;

-- 3. ESSENTIAL: cross-tenant queries
-- Critical for queries that span multiple tenants
CREATE INDEX IF NOT EXISTS idx_events_global_position ON events (position)
    INCLUDE (tenant_id, event_type, tags, data, metadata, created_at);

-- =============================================================================
-- PHASE 3: OPTIMIZED TENANT-FIRST INDEXES (RECOMMENDED APPROACH)
-- =============================================================================

-- 4. OPTIMIZED: Tenant-first composite index with GIN
-- This leverages that ALL tag queries start with tenant_id
-- PostgreSQL can use this for: WHERE tenant_id = ? AND tags @> ?
CREATE INDEX IF NOT EXISTS idx_events_tenant_tags_gin ON events (tenant_id, tags)
    WHERE array_length(tags, 1) > 0;

-- 5. OPTIMIZED: Tenant + event_type + tags (most common pattern)
-- For queries: WHERE tenant_id = ? AND event_type = ? AND tags @> ?
-- Uses partial index to avoid empty tag arrays
CREATE INDEX IF NOT EXISTS idx_events_tenant_type_tags ON events (tenant_id, event_type, tags)
    WHERE array_length(tags, 1) > 0;

-- 6. OPTIMIZED: Include index for covering queries
-- Provides all data needed for most queries without table lookups
CREATE INDEX IF NOT EXISTS idx_events_tenant_tags_covering ON events (tenant_id)
    INCLUDE (event_type, tags, data, metadata, created_at, position)
    WHERE array_length(tags, 1) > 0;

-- =============================================================================
-- SUBSCRIPTION CHECKPOINTS TABLE
-- =============================================================================

-- Table for tracking subscription checkpoints
CREATE TABLE IF NOT EXISTS subscription_checkpoints
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

-- Index for checkpoint queries and health checks
CREATE INDEX IF NOT EXISTS idx_subscription_checkpoints_updated
    ON subscription_checkpoints (updated_at DESC);

-- Index for monitoring lag
CREATE INDEX IF NOT EXISTS idx_subscription_checkpoints_position
    ON subscription_checkpoints (position DESC NULLS LAST);

COMMENT
ON TABLE subscription_checkpoints IS 'Tracks last processed position for each subscription';
COMMENT
ON COLUMN subscription_checkpoints.position IS 'Last successfully processed global position (NULL = from beginning)';

-- =============================================================================
-- POISON PILLS TABLE
-- =============================================================================

CREATE TABLE IF NOT EXISTS subscription_poison_pills
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

    UNIQUE
(
    subscription_id,
    global_position
)
    );

CREATE INDEX IF NOT EXISTS idx_poison_pills_subscription
    ON subscription_poison_pills (subscription_id, resolved_at NULLS FIRST);

CREATE INDEX IF NOT EXISTS idx_poison_pills_unresolved
    ON subscription_poison_pills (subscription_id, global_position)
    WHERE resolved_at IS NULL;

COMMENT
ON TABLE subscription_poison_pills IS 'Events that failed processing after retries';
COMMENT
ON COLUMN subscription_poison_pills.resolution_action IS 'Actions: skip, reprocess, manual';