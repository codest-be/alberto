-- Alberto DCB Event Store - Migration 010 (Multi-Tenant)
-- SQL-3: Replace the FOR EACH ROW notify trigger on alberto_events with a
-- FOR EACH STATEMENT trigger so exactly ONE pg_notify fires per batch insert,
-- regardless of how many events are appended in a single call.
--
-- The old alberto_trg_notify_events fired once per inserted row, producing N
-- notifications for an N-event batch.  Subscribers wake up N times, each
-- competing to catch up on the same head position — wasted round-trips.
--
-- The new trigger uses REFERENCING NEW TABLE AS new_table to access the full
-- set of rows inserted by the statement, picks the maximum global_position, and
-- emits a single pg_notify with that value.  The notification payload meaning
-- is unchanged: subscribers use the position as a "head hint" and fetch from
-- their own checkpoint, so receiving the maximum position is semantically
-- identical to receiving N incrementing positions.
--
-- The old per-row function (alberto_notify_events) is retained in place; it is
-- no longer attached to any trigger.  A future cleanup migration may drop it.

-- New batch-mode trigger function (statement-level, uses transition table).
CREATE OR REPLACE FUNCTION $schema_prefix$alberto_notify_events_batch()
RETURNS TRIGGER AS $$
DECLARE
    v_max_position BIGINT;
BEGIN
    SELECT MAX(global_position) INTO v_max_position FROM new_table;
    IF v_max_position IS NOT NULL THEN
        PERFORM pg_notify('$schema$_events', v_max_position::TEXT);
    END IF;
    RETURN NULL;
END;
$$ LANGUAGE plpgsql;

-- Replace the row-level trigger with a statement-level trigger.
-- The trigger name is kept so that monitoring/tooling referencing it by name
-- continues to find it.
DROP TRIGGER IF EXISTS alberto_trg_notify_events ON $schema_prefix$alberto_events;
CREATE TRIGGER alberto_trg_notify_events
    AFTER INSERT ON $schema_prefix$alberto_events
    REFERENCING NEW TABLE AS new_table
    FOR EACH STATEMENT
    EXECUTE FUNCTION $schema_prefix$alberto_notify_events_batch();
