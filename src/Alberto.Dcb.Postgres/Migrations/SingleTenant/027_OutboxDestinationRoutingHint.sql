-- Alberto DCB Event Store - Migration 027 (Single-Tenant)
--
-- Add optional routing metadata to the outbox. See multi-tenant
-- 027_OutboxDestinationRoutingHint.sql for the full rationale; the only difference here
-- is that the table has no tenant_id column, which does not affect these two additions.

ALTER TABLE $schema_prefix$alberto_outbox_entries
    ADD COLUMN IF NOT EXISTS destination TEXT,
    ADD COLUMN IF NOT EXISTS routing_hint TEXT;
