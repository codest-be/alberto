-- Alberto DCB Event Store - Initial Schema (Multi-Tenant)
-- Uses inverted index tables for efficient DCB queries

-- Events table
CREATE TABLE IF NOT EXISTS events (
    global_position   BIGSERIAL PRIMARY KEY,
    tenant_id         VARCHAR(100) NOT NULL,
    event_id          UUID NOT NULL DEFAULT gen_random_uuid(),
    event_type        VARCHAR(500) NOT NULL,
    event_tags        VARCHAR(500)[] NOT NULL DEFAULT '{}',
    event_data        JSONB NOT NULL DEFAULT '{}',
    event_metadata    JSONB NOT NULL DEFAULT '{}',
    created_at        TIMESTAMPTZ NOT NULL DEFAULT now(),
    CONSTRAINT uq_events_event_id UNIQUE (event_id)
);

CREATE INDEX IF NOT EXISTS ix_events_tenant ON events (tenant_id, global_position);

-- Inverted index for event types (tenant-scoped)
CREATE TABLE IF NOT EXISTS event_type_positions (
    tenant_id         VARCHAR(100) NOT NULL,
    event_type        VARCHAR(500) NOT NULL,
    global_position   BIGINT NOT NULL REFERENCES events(global_position) ON DELETE CASCADE,
    PRIMARY KEY (tenant_id, event_type, global_position)
);

-- Inverted index for event tags (tenant-scoped)
CREATE TABLE IF NOT EXISTS event_tag_positions (
    tenant_id         VARCHAR(100) NOT NULL,
    tag               VARCHAR(500) NOT NULL,
    global_position   BIGINT NOT NULL REFERENCES events(global_position) ON DELETE CASCADE,
    PRIMARY KEY (tenant_id, tag, global_position)
);
