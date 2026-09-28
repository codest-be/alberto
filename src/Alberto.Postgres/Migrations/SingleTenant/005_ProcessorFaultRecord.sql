-- Alberto DCB Event Store – 005 Processor Fault Record (Single-Tenant)
--
-- Adds nullable fault columns to alberto_processor_checkpoints so the reason a
-- processor stopped survives the process that logged it (issue #73). All six are
-- NULL when the processor is healthy; the fault-context trio (position, event
-- type, tenant) is NULL when the failure did not occur dispatching one known
-- event. fault_stack_trace is untruncated TEXT, matching the dead-letter table.
--
-- fault_tenant_id exists here too, although single-tenant events carry no tenant:
-- PostgresCheckpointStore writes one SQL shape for both imprints, and the column
-- simply stays NULL in this mode.
--
-- ADD COLUMN IF NOT EXISTS keeps the script re-runnable on a store left
-- half-migrated.

ALTER TABLE $schema_prefix$alberto_processor_checkpoints
    ADD COLUMN IF NOT EXISTS faulted_at        TIMESTAMPTZ,
    ADD COLUMN IF NOT EXISTS fault_message     TEXT,
    ADD COLUMN IF NOT EXISTS fault_stack_trace TEXT,
    ADD COLUMN IF NOT EXISTS fault_position    BIGINT,
    ADD COLUMN IF NOT EXISTS fault_event_type  TEXT,
    ADD COLUMN IF NOT EXISTS fault_tenant_id   TEXT;

COMMENT ON COLUMN $schema_prefix$alberto_processor_checkpoints.faulted_at IS
    'When the processor last faulted. NULL while healthy; cleared by the next successful checkpoint save, reset, or rewind.';
