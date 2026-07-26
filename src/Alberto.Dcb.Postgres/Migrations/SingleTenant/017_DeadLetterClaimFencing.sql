-- Adds an opaque fencing token to retry claims. Terminal retry transitions
-- compare this token so a worker cannot complete or abandon a claim that a
-- newer worker acquired after lease expiry.

ALTER TABLE $schema_prefix$alberto_dead_letter_events
    ADD COLUMN IF NOT EXISTS claim_id UUID NULL;
