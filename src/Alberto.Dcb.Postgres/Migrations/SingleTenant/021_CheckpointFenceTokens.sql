-- Alberto DCB Event Store - Migration 021 (Single-Tenant)
--
-- Make the fenced checkpoint write atomic, and fence it on lease generation rather than on
-- replica identity. See multi-tenant 021_CheckpointFenceTokens.sql for the full rationale.
--
-- The single-tenant set already creates alberto_processor_leases, so this variant only adds the
-- generation and rewrites the function. There is no tenant-lease variant here: the two lease
-- tables never coexist in one schema.
--
-- BREAKING, in the same way as the multi-tenant variant -- the four-argument function is
-- dropped, so a replica on the previous release fails its flushes rather than writing unfenced.

CREATE SEQUENCE IF NOT EXISTS $schema_prefix$alberto_fence_tokens AS BIGINT START WITH 1;

COMMENT ON SEQUENCE $schema_prefix$alberto_fence_tokens IS
    'Issues one strictly increasing generation number per lease acquisition. Shared across processors: tokens are only ever compared within a single checkpoint row.';

ALTER TABLE $schema_prefix$alberto_processor_leases
    ADD COLUMN IF NOT EXISTS fence_token BIGINT NOT NULL DEFAULT 0;

ALTER TABLE $schema_prefix$alberto_processor_checkpoints
    ADD COLUMN IF NOT EXISTS fence_token BIGINT NOT NULL DEFAULT 0;

COMMENT ON COLUMN $schema_prefix$alberto_processor_checkpoints.fence_token IS
    'Highest lease generation that has written this checkpoint. Writes from an earlier generation are refused.';

DROP FUNCTION IF EXISTS $schema_prefix$alberto_save_checkpoint_if_processor_lease_held(TEXT, TEXT, TEXT, BIGINT);

CREATE OR REPLACE FUNCTION $schema_prefix$alberto_save_checkpoint_if_processor_lease_held(
    p_processor_id TEXT,
    p_consumer_id  TEXT,
    p_replica_id   TEXT,
    p_position     BIGINT,
    p_fence_token  BIGINT
) RETURNS BOOLEAN AS $$
DECLARE
    v_written INTEGER;
BEGIN
    -- One statement. The lease is the INSERT's source, so it is read under the same snapshot
    -- that writes, and the ON CONFLICT arm re-reads the checkpoint row it is about to overwrite.
    INSERT INTO $schema_prefix$alberto_processor_checkpoints
        (processor_id, last_position, fence_token, updated_at)
    SELECT p_processor_id, p_position, p_fence_token, now()
    FROM $schema_prefix$alberto_processor_leases l
    WHERE l.consumer_id  = p_consumer_id
      AND l.processor_id = p_processor_id
      AND l.replica_id   = p_replica_id
      AND l.expires_at   > now()
      AND l.fence_token  = p_fence_token
    ON CONFLICT (processor_id) DO UPDATE
    SET last_position = GREATEST(
            $schema_prefix$alberto_processor_checkpoints.last_position,
            EXCLUDED.last_position),
        fence_token   = EXCLUDED.fence_token,
        updated_at    = now()
    WHERE $schema_prefix$alberto_processor_checkpoints.fence_token <= EXCLUDED.fence_token;

    GET DIAGNOSTICS v_written = ROW_COUNT;
    RETURN v_written > 0;
END;
$$ LANGUAGE plpgsql;

COMMENT ON FUNCTION $schema_prefix$alberto_save_checkpoint_if_processor_lease_held(TEXT, TEXT, TEXT, BIGINT, BIGINT) IS
    'Advances a checkpoint only for the replica that currently owns the processor lease, in the generation it presents. Returns false when the caller has been fenced out.';
