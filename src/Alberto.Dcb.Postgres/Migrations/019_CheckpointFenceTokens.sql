-- Alberto DCB Event Store - Migration 019 (Multi-Tenant)
--
-- Make the fenced checkpoint write atomic, and fence it on lease generation rather than on
-- replica identity.
--
-- Two defects, both in alberto_save_checkpoint_if_lease_held and its processor-lease twin.
--
-- 1. The lease check and the checkpoint write were separate statements. plpgsql runs them in
--    the caller's transaction, and under READ COMMITTED each statement takes its own snapshot,
--    so a lease that changes hands between the two is not seen by the second. The write lands
--    on behalf of a replica that no longer owns the processor.
--
-- 2. Even without that window, verifying the replica identity does not establish ownership over
--    time. A replica can lose its lease, watch another replica take over and advance the
--    checkpoint, and then re-acquire the same lease. A write it had already prepared under its
--    earlier ownership matches on identity, is accepted, and — because the upsert is monotonic
--    (GREATEST) — drags the checkpoint past everything the intervening owner processed. Those
--    events are then never delivered, and nothing recovers automatically: the checkpoint only
--    moves backwards through the operator's rewind.
--
-- The fix gives every acquisition of a lease a generation number drawn from a sequence, records
-- it on the lease, and has the writer present the generation it acquired. The checkpoint row
-- remembers the highest generation that has written to it, so a superseded generation is locked
-- out permanently rather than only until it happens to re-acquire. Check and write become one
-- statement, which closes the window in (1) and makes the checkpoint row itself the arbiter:
-- ON CONFLICT DO UPDATE re-reads the live row, so of two writers racing, the second sees the
-- first's generation.
--
-- The generation comes from a sequence rather than a per-row counter because releasing a lease
-- deletes its row. A counter would restart, and a write held over from before the release would
-- match again.
--
-- Also creates alberto_processor_leases here. It existed only in the single-tenant migration
-- set, while the control loop enables processor-lease fencing in both modes and the lease
-- manager is registered for every module regardless of tenancy — so a multi-tenant deployment
-- with leases enabled was failing on "relation alberto_processor_leases does not exist".
--
-- BREAKING. The old four-argument functions are dropped rather than left alongside the new
-- five-argument ones, so a replica still running the previous release fails its checkpoint
-- flushes instead of silently writing unfenced. That is the safe direction — such a replica
-- stops rather than advancing a checkpoint it no longer owns — but it does mean the two
-- releases cannot both write during a rolling upgrade.

-- ============================================================
-- PROCESSOR LEASES (mirrors the single-tenant schema)
-- ============================================================

CREATE TABLE IF NOT EXISTS $schema_prefix$alberto_processor_leases (
    consumer_id   TEXT        NOT NULL,
    processor_id  TEXT        NOT NULL,
    replica_id    TEXT        NOT NULL,
    acquired_at   TIMESTAMPTZ NOT NULL DEFAULT now(),
    expires_at    TIMESTAMPTZ NOT NULL,
    PRIMARY KEY (consumer_id, processor_id)
);

CREATE INDEX IF NOT EXISTS ix_alberto_processor_leases_replica
    ON $schema_prefix$alberto_processor_leases (replica_id);

-- Renewed on a timer and never deleted outside handoff, so the same HOT-update reasoning as
-- migration 011 applies.
ALTER TABLE $schema_prefix$alberto_processor_leases SET (fillfactor = 70);

-- ============================================================
-- FENCE TOKENS
-- ============================================================

CREATE SEQUENCE IF NOT EXISTS $schema_prefix$alberto_fence_tokens AS BIGINT START WITH 1;

COMMENT ON SEQUENCE $schema_prefix$alberto_fence_tokens IS
    'Issues one strictly increasing generation number per lease acquisition. Shared across processors: tokens are only ever compared within a single checkpoint row.';

-- Existing rows take 0, which reads as "no generation has written here yet" and so is below
-- every token the sequence will issue. Both are metadata-only ALTERs on PostgreSQL 11+.
ALTER TABLE $schema_prefix$alberto_processor_leases
    ADD COLUMN IF NOT EXISTS fence_token BIGINT NOT NULL DEFAULT 0;

ALTER TABLE $schema_prefix$alberto_processor_checkpoints
    ADD COLUMN IF NOT EXISTS fence_token BIGINT NOT NULL DEFAULT 0;

COMMENT ON COLUMN $schema_prefix$alberto_processor_checkpoints.fence_token IS
    'Highest lease generation that has written this checkpoint. Writes from an earlier generation are refused.';

-- ============================================================
-- FENCED CHECKPOINT FUNCTIONS
-- ============================================================

-- Dropped by full signature: adding a parameter would otherwise create an overload and leave
-- the unfenced version callable.
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

-- The tenant-lease variant becomes a single statement too, but it takes no generation. Tenant
-- leases are keyed on (consumer_id, tenant_id), so a replica holds many of them and none of
-- them names an owner of this processor -- the check asks only whether the replica holds some
-- tenant under this consumer, which two replicas can satisfy at once. It fences against a
-- replica that has stopped renewing entirely; it does not decide between live replicas.
-- Processor-lease fencing is what the control loop uses and is the one to prefer.
CREATE OR REPLACE FUNCTION $schema_prefix$alberto_save_checkpoint_if_lease_held(
    p_processor_id TEXT,
    p_consumer_id  TEXT,
    p_replica_id   TEXT,
    p_position     BIGINT
) RETURNS BOOLEAN AS $$
DECLARE
    v_written INTEGER;
BEGIN
    INSERT INTO $schema_prefix$alberto_processor_checkpoints
        (processor_id, last_position, updated_at)
    SELECT p_processor_id, p_position, now()
    WHERE EXISTS (
        SELECT 1 FROM $schema_prefix$alberto_tenant_leases
        WHERE consumer_id = p_consumer_id
          AND replica_id  = p_replica_id
          AND expires_at  > now()
    )
    ON CONFLICT (processor_id) DO UPDATE
    SET last_position = GREATEST(
            $schema_prefix$alberto_processor_checkpoints.last_position,
            EXCLUDED.last_position),
        updated_at    = now();

    GET DIAGNOSTICS v_written = ROW_COUNT;
    RETURN v_written > 0;
END;
$$ LANGUAGE plpgsql;
