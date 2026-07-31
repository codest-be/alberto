-- Alberto DCB Event Store - Migration 010 (Single-Tenant)
-- SQL-3: Replace the FOR EACH ROW notify trigger on alberto_events with a
-- FOR EACH STATEMENT trigger so exactly ONE pg_notify fires per batch insert,
-- regardless of how many events are appended in a single call.
--
-- See multi-tenant 010_BatchNotifyTrigger.sql for full rationale.

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
