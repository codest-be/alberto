-- Alberto DCB Event Store - Migration 022 (Single-Tenant)
-- alberto:no-transaction
--
-- Index tags by their concept so a wildcard tag boundary can be resolved by lookup instead of
-- by scanning every tag ever written. See multi-tenant 022_TagConceptIndex.sql for the full
-- rationale; the only difference here is that the table has no tenant_id column.

CREATE INDEX CONCURRENTLY IF NOT EXISTS ix_alberto_event_tag_positions_concept
    ON $schema_prefix$alberto_event_tag_positions
    ((left(tag::TEXT, position(':' IN tag::TEXT))), global_position);

COMMENT ON INDEX $schema_prefix$ix_alberto_event_tag_positions_concept IS
    'Resolves wildcard tag boundaries (concept:*) by concept lookup. See migration 022.';
