-- Alberto DCB Event Store – 007 Dead-Letter Redelivery (Single-Tenant)
--
-- Automatic redelivery of dead letters. next_retry_at is when the entry is next redelivered
-- without an operator; NULL means it is parked and waits for a manual retry (a permanent
-- error, redelivery switched off for the processor, or the last redelivery used up).
-- redelivery_count is how many automatic redeliveries have already failed.
--
-- Rows that exist before this migration get next_retry_at NULL, so nothing already parked
-- starts redelivering by itself on upgrade; MarkForRetry still works on them.
--
-- The partial index serves the claim query (processor_id, next_retry_at <= @now) and stays
-- small: it holds only the rows that are scheduled.

ALTER TABLE $schema_prefix$alberto_dead_letter_events
    ADD COLUMN IF NOT EXISTS next_retry_at timestamptz NULL;

ALTER TABLE $schema_prefix$alberto_dead_letter_events
    ADD COLUMN IF NOT EXISTS redelivery_count integer NOT NULL DEFAULT 0;

CREATE INDEX IF NOT EXISTS idx_alberto_dead_letter_next_retry
    ON $schema_prefix$alberto_dead_letter_events (processor_id, next_retry_at)
    WHERE next_retry_at IS NOT NULL;
