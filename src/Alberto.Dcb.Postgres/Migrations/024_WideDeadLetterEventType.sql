-- Alberto DCB Event Store - Migration 024 (Multi-Tenant)
--
-- Widen alberto_dead_letter_events.event_type from VARCHAR(200) to VARCHAR(500) so that it
-- matches alberto_events.event_type.
--
-- The mismatch means that any event whose type name exceeds 200 characters appends fine but
-- cannot be dead-lettered: the INSERT fails with a string-truncation error and the processor
-- is stuck in a retry loop with no escape into quarantine. That is precisely the worst case —
-- the events most likely to keep failing (because their type triggers a bug in the handler)
-- are exactly the ones that cannot be quarantined. Leaving the column narrow until users have
-- data converts a metadata-only fix into a table rewrite, so we address it now.
--
-- Full audit of alberto_dead_letter_events columns against alberto_events:
--   event_id        UUID        → UUID        ✓  (no change needed)
--   event_type      VARCHAR(200)→ VARCHAR(500) ✗  (this migration)
--   event_data      JSONB       → JSONB        ✓  (no change needed)
--   global_position BIGINT      → BIGINT       ✓  (no change needed)
-- Other columns (processor_id, error_message, stack_trace, etc.) have no direct counterpart
-- in alberto_events and are sized appropriately for their own purpose.
--
-- Widening a VARCHAR in PostgreSQL is a metadata-only operation: no rows are rewritten, no
-- indexes are rebuilt, and the AccessExclusive lock is released as soon as the catalog update
-- commits — measured in microseconds on any live table. A plain transactional ALTER TABLE is
-- therefore correct here, and the alberto:no-transaction marker is not needed.

ALTER TABLE $schema_prefix$alberto_dead_letter_events
    ALTER COLUMN event_type TYPE VARCHAR(500);
