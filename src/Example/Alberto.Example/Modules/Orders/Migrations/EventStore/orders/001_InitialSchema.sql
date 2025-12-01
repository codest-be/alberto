-- ============================================================================
-- Migration: 001_InitialSchema
-- Description: Initial EventStore schema with events, checkpoints, and poison pills
-- Breaking: true
-- Estimated Impact: Fast on empty database (<1 second)
-- ============================================================================

-- Idempotent check
DO
$$
    BEGIN
        IF NOT EXISTS (SELECT 1
                       FROM information_schema.tables
                       WHERE table_schema = 'orders'
                         AND table_name = '__alberto_schema_version') THEN

            -- Create schema
            CREATE SCHEMA IF NOT EXISTS orders;

            -- Create version tracking table
            CREATE TABLE orders.__alberto_schema_version
            (
                migration_name
                            VARCHAR(255) PRIMARY KEY,
                applied_at  TIMESTAMPTZ NOT NULL DEFAULT NOW
                                                         (
                                                         ),
                is_breaking BOOLEAN     NOT NULL DEFAULT false,
                description TEXT
            );

        END IF;

        -- Check if migration already applied
        IF NOT EXISTS (SELECT 1
                       FROM orders.__alberto_schema_version
                       WHERE migration_name = '001_InitialSchema') THEN

            -- Main events table with tenant support
            CREATE TABLE IF NOT EXISTS orders.events
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
                           VARCHAR(20) NOT NULL,
                event_type TEXT        NOT NULL,
                data       JSONB       NOT NULL,
                tags       TEXT[]      NOT NULL DEFAULT '{}',
                created_at TIMESTAMPTZ NOT NULL DEFAULT NOW
                                                        (
                                                        ),
                metadata   JSONB       NOT NULL DEFAULT '{}'
            );

            -- Index for consistency checks: WHERE tenant_id = ? AND id = ?
            -- Used by: Consistency boundary checks in append operations
            CREATE INDEX IF NOT EXISTS idx_orders_events_tenant_id
                ON orders.events (tenant_id, id);

            -- Primary covering index for tenant-scoped queries
            -- Used by: Stream(tenant), Stream(tenant, tags), Stream(tenant, eventType, tags)
            -- Covers: Most tenant queries with index-only scans
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
            CREATE INDEX IF NOT EXISTS idx_orders_events_tenant_type_position
                ON orders.events (tenant_id, event_type, position);

            -- Index for tag-based filtering
            CREATE INDEX IF NOT EXISTS idx_orders_events_tags
                ON orders.events USING GIN (tags);

            -- Subscription checkpoints
            CREATE TABLE IF NOT EXISTS orders.subscription_checkpoints
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

            CREATE INDEX IF NOT EXISTS idx_orders_subscription_checkpoints_updated
                ON orders.subscription_checkpoints (updated_at DESC);

            -- Poison pills
            CREATE TABLE IF NOT EXISTS orders.subscription_poison_pills
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
                    TEXT,
                UNIQUE
                    (
                     subscription_id,
                     global_position
                        )
            );

            CREATE INDEX IF NOT EXISTS idx_orders_poison_pills_subscription
                ON orders.subscription_poison_pills (subscription_id);

            CREATE INDEX IF NOT EXISTS idx_orders_poison_pills_event
                ON orders.subscription_poison_pills (event_id);

            CREATE INDEX IF NOT EXISTS idx_orders_poison_pills_unresolved
                ON orders.subscription_poison_pills (subscription_id, last_failed_at)
                WHERE resolved_at IS NULL;

            -- Record migration
            INSERT INTO orders.__alberto_schema_version (migration_name, applied_at, is_breaking, description)
            VALUES ('001_InitialSchema', NOW(), true,
                    'Initial EventStore schema with events, checkpoints, and poison pills');

        END IF;
    END
$$;
