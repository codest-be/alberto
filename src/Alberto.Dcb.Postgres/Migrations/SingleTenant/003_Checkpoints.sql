-- Alberto DCB Event Store - Processor Checkpoints
-- Stores the last processed position for each processor

CREATE TABLE IF NOT EXISTS $schema_prefix$processor_checkpoints (
    processor_id      VARCHAR(200) PRIMARY KEY,
    last_position     BIGINT NOT NULL,
    updated_at        TIMESTAMPTZ NOT NULL DEFAULT now()
);
