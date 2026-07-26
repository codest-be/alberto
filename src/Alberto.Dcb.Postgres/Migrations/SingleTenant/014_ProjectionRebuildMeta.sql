-- Alberto DCB Event Store - Migration 014 (Single-Tenant)
-- Make alberto_projection_rebuild_meta usable by the rebuild coordinator.
--
-- Migration 001 created this table as scaffolding for zero-downtime projection
-- rebuilds and nothing ever read or wrote it.  Two things have to change before
-- it can back a real state machine:
--
--   1. It was keyed by projection_type.  A rebuild is driven by a *processor* --
--      that is the identity the control loop checkpoints under, the identity
--      IProjectionStateClearer carries, and the identity an operator types into
--      `alberto ops rebuild`.  projection_type is retained as a separate column
--      because promotion cleanup deletes from alberto_projection_states, which is
--      keyed by projection_type, not by processor.
--
--   2. rebuild_status was a free-text nullable column.  The coordinator relies on
--      the status to reject a second concurrent rebuild and to decide what to do
--      on restart, so the permitted values are now constrained.
--
-- Any pre-existing rows (a legacy v2 schema renamed into place by migration 002)
-- are carried over rather than dropped: processor_id is seeded from the old
-- projection_type value, which is the correct default for those installations.

ALTER TABLE $schema_prefix$alberto_projection_rebuild_meta
    ADD COLUMN IF NOT EXISTS processor_id TEXT;

UPDATE $schema_prefix$alberto_projection_rebuild_meta
    SET processor_id = projection_type
    WHERE processor_id IS NULL;

ALTER TABLE $schema_prefix$alberto_projection_rebuild_meta
    ALTER COLUMN processor_id SET NOT NULL;

-- Re-key on processor_id.  The old primary key was on projection_type; its
-- constraint name is the PostgreSQL default for a table created by 001.
ALTER TABLE $schema_prefix$alberto_projection_rebuild_meta
    DROP CONSTRAINT IF EXISTS alberto_projection_rebuild_meta_pkey;

ALTER TABLE $schema_prefix$alberto_projection_rebuild_meta
    ADD CONSTRAINT alberto_projection_rebuild_meta_pkey PRIMARY KEY (processor_id);

-- projection_type stays, but it is now an attribute rather than the key.
ALTER TABLE $schema_prefix$alberto_projection_rebuild_meta
    ALTER COLUMN projection_type SET NOT NULL;

-- Constrain the state machine.  'idle' is the resting state: no rebuild has ever
-- run, or the last one finished.  A row in 'rebuilding' or 'ready' is what blocks
-- a second concurrent rebuild for the same processor.
UPDATE $schema_prefix$alberto_projection_rebuild_meta
    SET rebuild_status = 'idle'
    WHERE rebuild_status IS NULL
       OR rebuild_status NOT IN ('idle', 'rebuilding', 'ready', 'completed', 'aborted');

ALTER TABLE $schema_prefix$alberto_projection_rebuild_meta
    ALTER COLUMN rebuild_status SET DEFAULT 'idle';

ALTER TABLE $schema_prefix$alberto_projection_rebuild_meta
    ALTER COLUMN rebuild_status SET NOT NULL;

ALTER TABLE $schema_prefix$alberto_projection_rebuild_meta
    DROP CONSTRAINT IF EXISTS alberto_projection_rebuild_meta_status_check;

ALTER TABLE $schema_prefix$alberto_projection_rebuild_meta
    ADD CONSTRAINT alberto_projection_rebuild_meta_status_check
    CHECK (rebuild_status IN ('idle', 'rebuilding', 'ready', 'completed', 'aborted'))
    NOT VALID;

-- A rebuild in flight must name the version it is writing into; a resting row
-- must not.  This is the invariant the coordinator depends on when it resumes
-- after a restart and has to decide whether a shadow loop should be running.
ALTER TABLE $schema_prefix$alberto_projection_rebuild_meta
    DROP CONSTRAINT IF EXISTS alberto_projection_rebuild_meta_version_check;

ALTER TABLE $schema_prefix$alberto_projection_rebuild_meta
    ADD CONSTRAINT alberto_projection_rebuild_meta_version_check
    CHECK (
        (rebuild_status IN ('rebuilding', 'ready') AND rebuilding_version IS NOT NULL)
        OR
        (rebuild_status IN ('idle', 'completed', 'aborted') AND rebuilding_version IS NULL)
    )
    NOT VALID;

ALTER TABLE $schema_prefix$alberto_projection_rebuild_meta
    ADD COLUMN IF NOT EXISTS updated_at TIMESTAMPTZ NOT NULL DEFAULT now();

COMMENT ON TABLE $schema_prefix$alberto_projection_rebuild_meta IS
    'Drives zero-downtime projection rebuilds. Keyed by processor. active_version is what readers see; rebuilding_version is what a shadow control loop is replaying into.';

COMMENT ON COLUMN $schema_prefix$alberto_projection_rebuild_meta.projection_type IS
    'Key into alberto_projection_states. Needed to drop superseded rows after promotion.';

COMMENT ON COLUMN $schema_prefix$alberto_projection_rebuild_meta.rebuild_target_position IS
    'Global position the shadow loop must reach before the rebuild can be promoted.';
