-- =============================================================================
-- DCB EVENT STORE SCHEMA FOR POSTGRESQL
-- =============================================================================

SET search_path = app, public;

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
    100
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
-- OPTIMIZED INDEXES 
-- =============================================================================

-- 1. Core tenant-based queries (covers most DCB operations)
-- This index supports ORDER BY position DESC and tenant filtering
CREATE INDEX IF NOT EXISTS idx_events_tenant_position_desc ON events (tenant_id, position DESC);

-- 2. Tenant + tag combination (most common DCB query pattern)
-- Uses INCLUDE for covering index benefits
CREATE INDEX IF NOT EXISTS idx_events_tenant_tags_gin ON events (tenant_id) INCLUDE (tags, event_type, position);

-- 3. Event type filtering within tenant
-- Optimized with INCLUDE for common projections
CREATE INDEX IF NOT EXISTS idx_events_tenant_type ON events (tenant_id, event_type) INCLUDE (position, tags);

-- 4. Consistency boundary checks (after specific position)
-- Supports efficient "position > X" queries within tenant
CREATE INDEX IF NOT EXISTS idx_events_consistency ON events (tenant_id, position)
    WHERE position > 0;

-- 5. Multi-tenant reading optimization
-- Critical for IMultitenantEventstoreBackend performance
-- Supports efficient "position > X ORDER BY position" across all tenants
CREATE INDEX IF NOT EXISTS idx_events_global_position ON events (position)
    INCLUDE (tenant_id, event_type, tags, data, metadata, created_at);