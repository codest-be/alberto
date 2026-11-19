-- Alberto DCB Event Store – 013 Outbox Failed Index (Multi-Tenant)
--
-- idx_alberto_outbox_failed (message_type) WHERE status = 'failed'
--   Serves IOutboxStore.RetryFailedAsync, with and without a message type. The existing
--   partial indexes cover pending and delivered rows only, so the operator's retry scanned
--   the whole outbox, delivered history included, to find the few rows that gave up. Failed
--   rows are rare, so the index stays small and costs nothing on the hot insert path.
--
-- idx_alberto_dead_letter_processor (processor_id)
--   Dropped: a leading prefix of ux_alberto_dead_letter_processor_event (processor_id,
--   event_id) from 006, which serves every query this one did. No tenant column leads
--   either index.

CREATE INDEX IF NOT EXISTS idx_alberto_outbox_failed
    ON $schema_prefix$alberto_outbox_entries (message_type)
    WHERE status = 'failed';

DROP INDEX IF EXISTS $schema_prefix$idx_alberto_dead_letter_processor;
