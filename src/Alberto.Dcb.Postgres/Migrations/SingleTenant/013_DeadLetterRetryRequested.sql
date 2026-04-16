-- Alberto DCB Event Store - Add retry_requested flag for dead letter replay

ALTER TABLE $schema_prefix$dead_letter_events
    ADD COLUMN IF NOT EXISTS retry_requested BOOLEAN NOT NULL DEFAULT FALSE;
