-- Alberto DCB Event Store - Migration 024 (Single-Tenant)
-- alberto:no-transaction
--
-- Drop the wildcard tag boundary: the concept index from migration 022, the two append functions
-- that resolved a boundary of the form "order:*", and the three read functions that answered one.
-- See multi-tenant 024_DropWildcardTagBoundaries.sql for the full rationale; the only difference
-- here is that these functions carry no tenant argument.

DROP INDEX CONCURRENTLY IF EXISTS $schema_prefix$ix_alberto_event_tag_positions_concept;

-- Append functions: the wildcard variants of the DCB conflict check.
-- _v2 is union composition (001/002, rewritten by 023), _v5 is intersect (007, rewritten by 023).
DROP FUNCTION IF EXISTS $schema_prefix$alberto_append_events_v2(
    JSONB, VARCHAR(500)[], VARCHAR(500)[], VARCHAR(500)[], BIGINT);
DROP FUNCTION IF EXISTS $schema_prefix$alberto_append_events_v5(
    JSONB, VARCHAR(500)[], VARCHAR(500)[], VARCHAR(500)[], BIGINT);

-- Read functions: the query-path counterparts. Exact-tag reads go through
-- alberto_read_by_tags / _types_and_tags / _types_or_tags, which are untouched.
DROP FUNCTION IF EXISTS $schema_prefix$alberto_read_by_tag_patterns(
    VARCHAR(500)[], VARCHAR(500)[], BIGINT, INT);
DROP FUNCTION IF EXISTS $schema_prefix$alberto_read_by_types_or_tag_patterns(
    VARCHAR(500)[], VARCHAR(500)[], VARCHAR(500)[], BIGINT, INT);
DROP FUNCTION IF EXISTS $schema_prefix$alberto_read_by_types_and_tag_patterns(
    VARCHAR(500)[], VARCHAR(500)[], VARCHAR(500)[], BIGINT, INT);
