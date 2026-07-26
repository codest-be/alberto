-- Alberto DCB Event Store - Migration 024 (Multi-Tenant)
-- alberto:no-transaction
--
-- Drop the wildcard tag boundary: the concept index from migration 022, the two append functions
-- that resolved a boundary of the form "order:*", and the three read functions that answered one.
--
-- A wildcard boundary asked a single question -- "every event tagged with any order" -- and that
-- is not a question this store wants to answer. It is a boundary as wide as the concept, so every
-- order serialises against every other, and nothing in the repository ever built one. The DSL no
-- longer exposes it: DcbQuery has exact EventTags and nothing else, so no caller can reach these
-- functions, and no plan the planner builds can reach the index.
--
-- Migration 022's index is the reason this is worth a migration rather than being left to rot.
-- It is an expression index on alberto_event_tag_positions -- left(tag, position of the first
-- ':') -- so its cost falls on writes, not reads: every tag row ever written evaluates the
-- expression and maintains an extra index tuple, measured at +28% on bulk tag-row insert and
-- about 12 MB per 300k rows. That is a standing charge on every append, paid by everyone, for a
-- query nobody was asking. Dropping it is the point of this script; the functions merely go with
-- what they served.
--
-- Ordering: this script must not land before the C# that stopped calling these functions,
-- which is why it follows that change rather than shipping alongside it. The reverse order
-- would leave a released client calling a function that had already been dropped.
--
-- The version numbering keeps its gaps. _v2 and _v5 are not renumbered into the survivors,
-- so a function name in an old log line or a saved plan still means what it meant.
--
-- CONCURRENTLY, and therefore outside a transaction: a plain DROP INDEX takes an ACCESS
-- EXCLUSIVE lock on alberto_event_tag_positions, which is the append path. Every statement here
-- is IF EXISTS, so a script interrupted part-way is safe to re-run.

DROP INDEX CONCURRENTLY IF EXISTS $schema_prefix$ix_alberto_event_tag_positions_concept;

-- Append functions: the wildcard variants of the DCB conflict check.
-- _v2 is union composition (001/002, rewritten by 023), _v5 is intersect (007, rewritten by 023).
DROP FUNCTION IF EXISTS $schema_prefix$alberto_append_events_v2(
    VARCHAR(100), JSONB, VARCHAR(500)[], VARCHAR(500)[], VARCHAR(500)[], BIGINT);
DROP FUNCTION IF EXISTS $schema_prefix$alberto_append_events_v5(
    VARCHAR(100), JSONB, VARCHAR(500)[], VARCHAR(500)[], VARCHAR(500)[], BIGINT);

-- Read functions: the query-path counterparts. Exact-tag reads go through
-- alberto_read_by_tags / _types_and_tags / _types_or_tags, which are untouched.
DROP FUNCTION IF EXISTS $schema_prefix$alberto_read_by_tag_patterns(
    VARCHAR(100), VARCHAR(500)[], VARCHAR(500)[], BIGINT, INT);
DROP FUNCTION IF EXISTS $schema_prefix$alberto_read_by_types_or_tag_patterns(
    VARCHAR(100), VARCHAR(500)[], VARCHAR(500)[], VARCHAR(500)[], BIGINT, INT);
DROP FUNCTION IF EXISTS $schema_prefix$alberto_read_by_types_and_tag_patterns(
    VARCHAR(100), VARCHAR(500)[], VARCHAR(500)[], VARCHAR(500)[], BIGINT, INT);
