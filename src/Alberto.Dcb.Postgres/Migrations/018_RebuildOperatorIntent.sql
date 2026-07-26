-- Operators request rebuild completion; the running coordinator owns the
-- multi-step protocol that stops loops, hands off checkpoints, refreshes
-- version selectors, and clears state outside this database transaction.

ALTER TABLE $schema_prefix$alberto_projection_rebuild_meta
    ADD COLUMN IF NOT EXISTS requested_action TEXT NULL;

ALTER TABLE $schema_prefix$alberto_projection_rebuild_meta
    DROP CONSTRAINT IF EXISTS alberto_projection_rebuild_meta_requested_action_check;

-- NOT VALID so the ALTER does not scan the table under the ACCESS EXCLUSIVE lock it just
-- took; migration 019 validates it under SHARE UPDATE EXCLUSIVE, which concurrent readers
-- and writers pass straight through.  Every existing row has requested_action NULL -- the
-- column is added by this same script -- so the validation cannot fail.
ALTER TABLE $schema_prefix$alberto_projection_rebuild_meta
    ADD CONSTRAINT alberto_projection_rebuild_meta_requested_action_check
    CHECK (requested_action IS NULL OR requested_action IN ('promote', 'force-promote', 'abort'))
    NOT VALID;

COMMENT ON COLUMN $schema_prefix$alberto_projection_rebuild_meta.requested_action IS
    'Operator intent waiting for the rebuild coordinator. Operators never perform completion transitions directly.';
