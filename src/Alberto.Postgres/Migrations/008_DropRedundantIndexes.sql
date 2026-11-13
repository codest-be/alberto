-- Alberto DCB Event Store – 008 Drop Redundant Indexes (Multi-Tenant)
--
-- Drops indexes that duplicate another index on the same table. Each one was paid for on
-- every insert and bought no plan the surviving index does not already serve.
--
-- ix_alberto_events_inflight (global_position) WHERE pg_xact_id IS NOT NULL
--   Intended to hold only the in-flight tail, on the assumption that pg_xact_id becomes NULL
--   once the writer commits. Nothing ever clears it, so the predicate is true for every row
--   written since the column existed and the index is a second copy of the primary key
--   (measured: identical size at two million events). GetStableHeadAsync is now bounded to
--   the window the head just read, which is a primary-key range scan.
--
-- idx_alberto_projection_states_tenant_type (tenant_id, projection_type)
--   A leading prefix of the primary key (tenant_id, projection_type, document_id, rebuild_version).
--
-- idx_alberto_projection_states_type_version_cleanup (projection_type, rebuild_version) WHERE rebuild_version > 0
--   The same columns as idx_alberto_projection_states_version, which is not partial and so
--   serves every query this one could.

DROP INDEX IF EXISTS $schema_prefix$ix_alberto_events_inflight;
DROP INDEX IF EXISTS $schema_prefix$idx_alberto_projection_states_tenant_type;
DROP INDEX IF EXISTS $schema_prefix$idx_alberto_projection_states_type_version_cleanup;
