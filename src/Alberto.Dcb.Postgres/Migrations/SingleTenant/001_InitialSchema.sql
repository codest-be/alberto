-- Alberto DCB Event Store - Initial Schema (Single-Tenant)
-- Uses inverted index tables for efficient DCB queries
-- No tenant_id columns - single tenant per schema

-- Create schema if not using public
CREATE SCHEMA IF NOT EXISTS $schema$;

-- Events table (no tenant_id)
CREATE TABLE IF NOT EXISTS $schema_prefix$events (
    global_position   BIGSERIAL PRIMARY KEY,
    event_id          UUID NOT NULL DEFAULT gen_random_uuid(),
    event_type        VARCHAR(500) NOT NULL,
    event_tags        VARCHAR(500)[] NOT NULL DEFAULT '{}',
    event_data        JSONB NOT NULL DEFAULT '{}',
    event_metadata    JSONB NOT NULL DEFAULT '{}',
    created_at        TIMESTAMPTZ NOT NULL DEFAULT now(),
    UNIQUE (event_id)
);

-- Inverted index for event types
CREATE TABLE IF NOT EXISTS $schema_prefix$event_type_positions (
    event_type        VARCHAR(500) NOT NULL,
    global_position   BIGINT NOT NULL REFERENCES $schema_prefix$events(global_position) ON DELETE CASCADE,
    PRIMARY KEY (event_type, global_position)
);

-- Inverted index for event tags
CREATE TABLE IF NOT EXISTS $schema_prefix$event_tag_positions (
    tag               VARCHAR(500) NOT NULL,
    global_position   BIGINT NOT NULL REFERENCES $schema_prefix$events(global_position) ON DELETE CASCADE,
    PRIMARY KEY (tag, global_position)
);
