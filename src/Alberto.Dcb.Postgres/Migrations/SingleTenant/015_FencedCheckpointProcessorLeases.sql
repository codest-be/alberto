-- Alberto DCB Event Store - Fenced Checkpoint for Processor Leases
-- Prevents zombie processors from writing stale checkpoints after their lease expires.

CREATE OR REPLACE FUNCTION $schema_prefix$save_checkpoint_if_processor_lease_held(
    p_processor_id TEXT,
    p_consumer_id TEXT,
    p_replica_id TEXT,
    p_position BIGINT
) RETURNS BOOLEAN AS $$
DECLARE
    v_rows INTEGER;
BEGIN
    UPDATE $schema_prefix$processor_checkpoints
    SET last_position = p_position, updated_at = now()
    WHERE processor_id = p_processor_id
    AND EXISTS (
        SELECT 1 FROM $schema_prefix$processor_leases
        WHERE consumer_id = p_consumer_id
        AND processor_id = p_processor_id
        AND replica_id = p_replica_id
        AND expires_at > now()
    );

    GET DIAGNOSTICS v_rows = ROW_COUNT;
    RETURN v_rows > 0;
END;
$$ LANGUAGE plpgsql;
