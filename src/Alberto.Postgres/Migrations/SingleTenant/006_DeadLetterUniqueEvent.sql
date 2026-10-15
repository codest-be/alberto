-- Alberto DCB Event Store – 006 Dead-Letter Unique Event (Single-Tenant)
--
-- One dead-letter row per (processor_id, event_id). The same event could be
-- dead-lettered twice for one processor (redelivered before the checkpoint was
-- saved, or re-run by a batch bisect), and an operator retry then dispatched
-- both rows, running the handler twice. PostgresDeadLetterStore.StoreAsync now
-- upserts on this key, refreshing the failure columns and keeping id,
-- retry_requested and the claim columns.
--
-- The key has no tenant column on purpose: event_id is UNIQUE in alberto_events,
-- so the event already fixes the tenant, and a nullable column in a unique index
-- would let NULL-keyed duplicates through.
--
-- Existing duplicates are collapsed before the index is built. THIS DELETES ROWS.
-- Survivor: the row with the latest failed_at (ties broken by the highest id),
-- because it carries the most recent error. If any row in the group was marked
-- for retry, the survivor is marked too, so a pending operator retry is not
-- dropped. Its own attempt_count, error and claim columns are kept as they are.
-- A worker still holding a claim on a deleted duplicate finds its complete or
-- abandon a no-op (logged as a lost claim) and the survivor stands in for it.
--
-- One transaction, no CONCURRENTLY: the dedupe and the index build must not let
-- a writer slip a new duplicate in between. The lock blocks writes to the table
-- for the length of the script and leaves reads alone.

LOCK TABLE $schema_prefix$alberto_dead_letter_events IN SHARE ROW EXCLUSIVE MODE;

UPDATE $schema_prefix$alberto_dead_letter_events dl
SET retry_requested = TRUE
FROM (
    SELECT id,
           row_number() OVER w AS rn,
           bool_or(retry_requested) OVER (PARTITION BY processor_id, event_id) AS any_retry
    FROM $schema_prefix$alberto_dead_letter_events
    WINDOW w AS (PARTITION BY processor_id, event_id ORDER BY failed_at DESC, id DESC)
) ranked
WHERE dl.id = ranked.id
  AND ranked.rn = 1
  AND ranked.any_retry
  AND NOT dl.retry_requested;

DELETE FROM $schema_prefix$alberto_dead_letter_events dl
USING (
    SELECT id,
           row_number() OVER (PARTITION BY processor_id, event_id ORDER BY failed_at DESC, id DESC) AS rn
    FROM $schema_prefix$alberto_dead_letter_events
) ranked
WHERE dl.id = ranked.id
  AND ranked.rn > 1;

CREATE UNIQUE INDEX IF NOT EXISTS ux_alberto_dead_letter_processor_event
    ON $schema_prefix$alberto_dead_letter_events (processor_id, event_id);
