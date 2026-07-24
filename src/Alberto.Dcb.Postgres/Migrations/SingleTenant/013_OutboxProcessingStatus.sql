-- Alberto DCB Event Store - Migration 013 (Single-Tenant)
-- Extend the alberto_outbox_entries.status CHECK constraint to permit 'processing'.
--
-- See multi-tenant 013_OutboxProcessingStatus.sql for full rationale.
-- The single-tenant and multi-tenant outbox tables are identical in structure
-- (no tenant_id column), so this script is identical to its multi-tenant counterpart.

ALTER TABLE $schema_prefix$alberto_outbox_entries
    DROP CONSTRAINT IF EXISTS alberto_outbox_entries_status_check;

ALTER TABLE $schema_prefix$alberto_outbox_entries
    ADD CONSTRAINT alberto_outbox_entries_status_check
    CHECK (status IN ('pending', 'processing', 'delivered', 'failed'));
