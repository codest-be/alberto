-- Alberto DCB Event Store - Add global_position to dead letter entries

ALTER TABLE $schema_prefix$dead_letter_events
    ADD COLUMN IF NOT EXISTS global_position BIGINT NOT NULL DEFAULT 0;
