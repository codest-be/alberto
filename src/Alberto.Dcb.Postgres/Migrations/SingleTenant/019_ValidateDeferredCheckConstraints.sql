-- Alberto DCB Event Store - Migration 019 (Single-Tenant)
-- Validate the CHECK constraints that migrations 013-015 added NOT VALID.
--
-- See multi-tenant 019_ValidateDeferredCheckConstraints.sql for full rationale.
-- The constrained tables are identical in both variants (none of them carry a tenant
-- column), so this script is identical to its multi-tenant counterpart.

ALTER TABLE $schema_prefix$alberto_outbox_entries
    VALIDATE CONSTRAINT alberto_outbox_entries_status_check;

ALTER TABLE $schema_prefix$alberto_projection_rebuild_meta
    VALIDATE CONSTRAINT alberto_projection_rebuild_meta_status_check;

ALTER TABLE $schema_prefix$alberto_projection_rebuild_meta
    VALIDATE CONSTRAINT alberto_projection_rebuild_meta_version_check;

ALTER TABLE $schema_prefix$alberto_projection_rebuild_meta
    VALIDATE CONSTRAINT alberto_projection_rebuild_meta_high_water_check;
