-- Fix: fenced checkpoint functions previously only did UPDATE, so processors
-- that started fresh (no prior checkpoint row) would never get their checkpoint
-- persisted. Changed to INSERT ... ON CONFLICT DO UPDATE (UPSERT) guarded by
-- the same lease check.

CREATE OR REPLACE FUNCTION $schema_prefix$alberto_save_checkpoint_if_processor_lease_held(
    p_processor_id TEXT,
    p_consumer_id TEXT,
    p_replica_id TEXT,
    p_position BIGINT
) RETURNS BOOLEAN AS $$
DECLARE
    v_lease_held BOOLEAN;
BEGIN
    -- Check if the processor lease is still held
    SELECT EXISTS (
        SELECT 1 FROM $schema_prefix$alberto_processor_leases
        WHERE consumer_id = p_consumer_id
        AND processor_id = p_processor_id
        AND replica_id = p_replica_id
        AND expires_at > now()
    ) INTO v_lease_held;

    IF NOT v_lease_held THEN
        RETURN FALSE;
    END IF;

    INSERT INTO $schema_prefix$alberto_processor_checkpoints (processor_id, last_position, updated_at)
    VALUES (p_processor_id, p_position, now())
    ON CONFLICT (processor_id) DO UPDATE
    SET last_position = GREATEST($schema_prefix$alberto_processor_checkpoints.last_position, p_position),
        updated_at = now();

    RETURN TRUE;
END;
$$ LANGUAGE plpgsql;
