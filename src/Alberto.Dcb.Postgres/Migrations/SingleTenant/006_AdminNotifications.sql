-- Alberto DCB Event Store - Admin Notifications
-- Uses PostgreSQL LISTEN/NOTIFY for push-based admin updates

-- Trigger function to notify on event append
CREATE OR REPLACE FUNCTION $schema_prefix$notify_events()
RETURNS TRIGGER AS $$
BEGIN
    -- Notify with the new global position
    PERFORM pg_notify('$schema$_events', NEW.global_position::TEXT);
    RETURN NEW;
END;
$$ LANGUAGE plpgsql;

-- Trigger function to notify on checkpoint update
CREATE OR REPLACE FUNCTION $schema_prefix$notify_checkpoint()
RETURNS TRIGGER AS $$
BEGIN
    -- Notify with processor_id:position
    PERFORM pg_notify('$schema$_checkpoints', NEW.processor_id || ':' || NEW.last_position::TEXT);
    RETURN NEW;
END;
$$ LANGUAGE plpgsql;

-- Trigger function to notify on dead letter insert
CREATE OR REPLACE FUNCTION $schema_prefix$notify_dead_letter_insert()
RETURNS TRIGGER AS $$
BEGIN
    -- Notify with processor_id
    PERFORM pg_notify('$schema$_dead_letters', 'added:' || NEW.processor_id);
    RETURN NEW;
END;
$$ LANGUAGE plpgsql;

-- Trigger function to notify on dead letter delete
CREATE OR REPLACE FUNCTION $schema_prefix$notify_dead_letter_delete()
RETURNS TRIGGER AS $$
BEGIN
    -- Notify with processor_id
    PERFORM pg_notify('$schema$_dead_letters', 'removed:' || OLD.processor_id);
    RETURN OLD;
END;
$$ LANGUAGE plpgsql;

-- Create triggers (drop first to make idempotent)
DROP TRIGGER IF EXISTS tr_events_notify ON $schema_prefix$events;
CREATE TRIGGER tr_events_notify
    AFTER INSERT ON $schema_prefix$events
    FOR EACH ROW
    EXECUTE FUNCTION $schema_prefix$notify_events();

DROP TRIGGER IF EXISTS tr_checkpoints_notify ON $schema_prefix$processor_checkpoints;
CREATE TRIGGER tr_checkpoints_notify
    AFTER INSERT OR UPDATE ON $schema_prefix$processor_checkpoints
    FOR EACH ROW
    EXECUTE FUNCTION $schema_prefix$notify_checkpoint();

DROP TRIGGER IF EXISTS tr_dead_letter_insert_notify ON $schema_prefix$dead_letter_events;
CREATE TRIGGER tr_dead_letter_insert_notify
    AFTER INSERT ON $schema_prefix$dead_letter_events
    FOR EACH ROW
    EXECUTE FUNCTION $schema_prefix$notify_dead_letter_insert();

DROP TRIGGER IF EXISTS tr_dead_letter_delete_notify ON $schema_prefix$dead_letter_events;
CREATE TRIGGER tr_dead_letter_delete_notify
    AFTER DELETE ON $schema_prefix$dead_letter_events
    FOR EACH ROW
    EXECUTE FUNCTION $schema_prefix$notify_dead_letter_delete();
