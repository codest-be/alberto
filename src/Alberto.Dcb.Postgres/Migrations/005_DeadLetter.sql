-- Alberto DCB Event Store - Dead Letter Storage
-- Stores events that failed processing after max retries

CREATE TABLE IF NOT EXISTS dead_letter_events (
    id                UUID PRIMARY KEY,
    processor_id      VARCHAR(200) NOT NULL,
    event_id          UUID NOT NULL,
    event_type        VARCHAR(200) NOT NULL,
    event_data        JSONB NOT NULL,
    error_message     TEXT NOT NULL,
    stack_trace       TEXT,
    attempt_count     INT NOT NULL,
    failed_at         TIMESTAMPTZ NOT NULL
);

CREATE INDEX IF NOT EXISTS idx_dead_letter_processor ON dead_letter_events(processor_id);
CREATE INDEX IF NOT EXISTS idx_dead_letter_failed_at ON dead_letter_events(failed_at DESC);
