-- Alberto DCB Admin - Audit log for mutating admin operations

CREATE TABLE IF NOT EXISTS $schema_prefix$processor_audit_log (
    id UUID PRIMARY KEY DEFAULT gen_random_uuid(),
    operator TEXT NOT NULL DEFAULT 'system',
    action TEXT NOT NULL,
    processor_id TEXT,
    details JSONB NOT NULL DEFAULT '{}',
    created_at TIMESTAMPTZ NOT NULL DEFAULT now()
);

CREATE INDEX IF NOT EXISTS idx_$schema_prefix$audit_log_created
    ON $schema_prefix$processor_audit_log (created_at DESC);

CREATE INDEX IF NOT EXISTS idx_$schema_prefix$audit_log_processor
    ON $schema_prefix$processor_audit_log (processor_id, created_at DESC);
