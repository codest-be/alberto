-- Alberto DCB Event Store - Fix Notify Channel Names
-- Fixes channel names to use schema_name (e.g., orders_events) instead of schema.name

-- Recreate trigger functions with correct channel names
CREATE OR REPLACE FUNCTION $schema_prefix$notify_events()
RETURNS TRIGGER AS $$
BEGIN
    PERFORM pg_notify('$schema$_events', NEW.global_position::TEXT);
    RETURN NEW;
END;
$$ LANGUAGE plpgsql;

CREATE OR REPLACE FUNCTION $schema_prefix$notify_checkpoint()
RETURNS TRIGGER AS $$
BEGIN
    PERFORM pg_notify('$schema$_checkpoints', NEW.processor_id || ':' || NEW.last_position::TEXT);
    RETURN NEW;
END;
$$ LANGUAGE plpgsql;

CREATE OR REPLACE FUNCTION $schema_prefix$notify_dead_letter_insert()
RETURNS TRIGGER AS $$
BEGIN
    PERFORM pg_notify('$schema$_dead_letters', 'added:' || NEW.processor_id);
    RETURN NEW;
END;
$$ LANGUAGE plpgsql;

CREATE OR REPLACE FUNCTION $schema_prefix$notify_dead_letter_delete()
RETURNS TRIGGER AS $$
BEGIN
    PERFORM pg_notify('$schema$_dead_letters', 'removed:' || OLD.processor_id);
    RETURN OLD;
END;
$$ LANGUAGE plpgsql;
