-- Alberto DCB Event Store - Migration 016
-- Give every processing outbox row a time-bounded, token-fenced claim.
--
-- Existing processing rows have no claim metadata. ClaimPendingAsync deliberately treats
-- a NULL expiry as expired, so rows stranded by an older relay become recoverable as soon
-- as this migration lands.

ALTER TABLE $schema_prefix$alberto_outbox_entries
    ADD COLUMN IF NOT EXISTS claim_id UUID,
    ADD COLUMN IF NOT EXISTS claimed_by TEXT,
    ADD COLUMN IF NOT EXISTS claim_expires_at TIMESTAMPTZ;

CREATE INDEX IF NOT EXISTS idx_alberto_outbox_expired_claims
    ON $schema_prefix$alberto_outbox_entries (claim_expires_at, created_at)
    WHERE status = 'processing';
