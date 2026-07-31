-- Alberto DCB Event Store - Migration 011 (Single-Tenant)
-- SQL-5: Set FILLFACTOR=70 on the checkpoint and lease tables.
--
-- See multi-tenant 011_FillfactorCheckpointLease.sql for full rationale.
-- In single-tenant mode the lease table is alberto_processor_leases (not
-- alberto_tenant_leases).

ALTER TABLE $schema_prefix$alberto_processor_checkpoints SET (fillfactor = 70);
ALTER TABLE $schema_prefix$alberto_processor_leases SET (fillfactor = 70);
