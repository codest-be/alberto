-- Adds claim/lease columns to dead_letter_events so the retry loop can hold a
-- row for a bounded time while a worker dispatches it. If the worker dies
-- mid-dispatch, the lease expires and another worker re-claims, instead of
-- the row being deleted up-front and the event lost.

ALTER TABLE $schema_prefix$alberto_dead_letter_events
    ADD COLUMN IF NOT EXISTS claimed_at        TIMESTAMPTZ NULL,
    ADD COLUMN IF NOT EXISTS claim_expires_at  TIMESTAMPTZ NULL,
    ADD COLUMN IF NOT EXISTS claimed_by        VARCHAR(200) NULL;

-- Speeds up the claim scan: predicate-filtered index on rows still flagged
-- for retry, ordered by failed_at (FIFO).
CREATE INDEX IF NOT EXISTS idx_alberto_dead_letter_retry_pending
    ON $schema_prefix$alberto_dead_letter_events (processor_id, failed_at)
    WHERE retry_requested = TRUE;
