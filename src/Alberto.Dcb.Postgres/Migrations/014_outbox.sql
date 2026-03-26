-- Alberto DCB Messaging - Transactional outbox for reliable external message delivery

CREATE TABLE IF NOT EXISTS $schema_prefix$outbox_entries (
    id UUID PRIMARY KEY DEFAULT gen_random_uuid(),
    source_event_id UUID NOT NULL,
    message_type TEXT NOT NULL,
    version TEXT NOT NULL DEFAULT '1',
    payload JSONB NOT NULL,
    metadata JSONB NOT NULL DEFAULT '{}',
    status TEXT NOT NULL DEFAULT 'pending' CHECK (status IN ('pending', 'delivered', 'failed')),
    retry_count INT NOT NULL DEFAULT 0,
    last_error TEXT,
    created_at TIMESTAMPTZ NOT NULL DEFAULT now(),
    delivered_at TIMESTAMPTZ,
    UNIQUE (source_event_id)
);

CREATE INDEX IF NOT EXISTS idx_$schema_prefix$outbox_pending
    ON $schema_prefix$outbox_entries (created_at) WHERE status = 'pending';
