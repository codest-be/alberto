-- Alberto DCB Event Store - Migration 026 (Single-Tenant)
--
-- Widen event_type on alberto_dead_letter_events from VARCHAR(200) to VARCHAR(500) to match
-- alberto_events.event_type. See multi-tenant 026_WideDeadLetterEventType.sql for the full
-- rationale and the column audit; the only difference here is that the table has no tenant_id
-- column.

ALTER TABLE $schema_prefix$alberto_dead_letter_events
    ALTER COLUMN event_type TYPE VARCHAR(500);
