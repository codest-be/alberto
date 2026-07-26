-- Operators request rebuild completion; the running coordinator owns the
-- multi-step protocol that stops loops, hands off checkpoints, refreshes
-- version selectors, and clears state outside this database transaction.

ALTER TABLE $schema_prefix$alberto_projection_rebuild_meta
    ADD COLUMN IF NOT EXISTS requested_action TEXT NULL;

ALTER TABLE $schema_prefix$alberto_projection_rebuild_meta
    DROP CONSTRAINT IF EXISTS alberto_projection_rebuild_meta_requested_action_check;

ALTER TABLE $schema_prefix$alberto_projection_rebuild_meta
    ADD CONSTRAINT alberto_projection_rebuild_meta_requested_action_check
    CHECK (requested_action IS NULL OR requested_action IN ('promote', 'force-promote', 'abort'));

COMMENT ON COLUMN $schema_prefix$alberto_projection_rebuild_meta.requested_action IS
    'Operator intent waiting for the rebuild coordinator. Operators never perform completion transitions directly.';
