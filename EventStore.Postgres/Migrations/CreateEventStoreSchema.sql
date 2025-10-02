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
    tags TEXT [] NOT NULL DEFAULT '{}',
    created_at TIMESTAMPTZ NOT NULL DEFAULT NOW
(
),
    metadata JSONB NOT NULL DEFAULT '{}'
    );

-- =============================================================================
-- OPTIMIZED TENANT-FIRST INDEXES FOR ACTUAL QUERY PATTERNS
-- =============================================================================

-- 1. PRIMARY PATTERN: tenant + event_type + tags
-- Optimized for: "get order_created events for order:123" (most common)
CREATE INDEX IF NOT EXISTS idx_events_tenant_type_tags ON events (tenant_id, event_type) INCLUDE (tags, position, data, metadata, created_at);

-- 2. SECONDARY PATTERN: tenant + tags (when no event_type filter)
-- For broad tag queries within tenant
CREATE INDEX IF NOT EXISTS idx_events_tenant_tags ON events (tenant_id) INCLUDE (tags, event_type, position, data, metadata, created_at);

-- 3. ORDERING: tenant-based result ordering and pagination
-- Supports ORDER BY position DESC within tenant
CREATE INDEX IF NOT EXISTS idx_events_tenant_position ON events (tenant_id, position DESC);

-- 4. CONSISTENCY: optimistic concurrency control
-- For consistency boundary checks (version-like behavior)
CREATE INDEX IF NOT EXISTS idx_events_consistency ON events (tenant_id, position)
    WHERE position > 0;

-- 5. CROSS-TENANT: multi-tenant queries (keep for cross-tenant access)
-- Critical for queries that span multiple tenants
CREATE INDEX IF NOT EXISTS idx_events_global_position ON events (position)
    INCLUDE (tenant_id, event_type, tags, data, metadata, created_at);