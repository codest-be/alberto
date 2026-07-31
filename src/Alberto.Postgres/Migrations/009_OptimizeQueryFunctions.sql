-- Alberto DCB Event Store - Migration 009 (Multi-Tenant)
-- Query performance: eliminate DISTINCT-before-LIMIT over-scans.
--
-- SQL-1: alberto_read_by_tags multi-tag branch
--   Old: SELECT DISTINCT ... tag = ANY(p_tags) ... LIMIT p_limit
--        Forces PostgreSQL to materialise ALL matching rows across all tags before
--        deduplication, then discard all but p_limit rows.
--   New: CROSS JOIN LATERAL with LIMIT p_limit per individual tag index scan.
--        Each per-tag scan reads at most p_limit rows from the B-tree (an
--        index-only seek), then the outer DISTINCT + LIMIT p_limit deduplicates
--        the merged stream.  Correctness proof: the k-th distinct position in the
--        merged union must appear in at least one tag's sorted run at rank <= k,
--        so bounding each inner scan at p_limit rows is sufficient.
--
-- SQL-6: alberto_read_by_types_or_tags
--   Old: LEFT JOIN events + type_positions + tag_positions => DISTINCT
--        Drives from the events heap with two optional index lookups, requiring
--        PostgreSQL to scan/filter the events table before deduplication.
--   New: UNION of per-axis index-driven subqueries (each limited), then join
--        events.  Types-only and tags-only branches short-cut to a single index
--        scan; the tags-only branch re-uses the already-optimised read_by_tags.
--
-- SQL-16: alberto_read_by_types_and_tags
--   Old: drives from alberto_events with a correlated EXISTS subquery into
--        alberto_event_tag_positions — one subquery execution per outer row.
--   New: INTERSECT of two index-driven subqueries (types index ∩ tags index),
--        then join events for the full row.  Both inputs are sorted by position
--        so PostgreSQL can satisfy INTERSECT with a merge join, reading each
--        index once.

-- ============================================================
-- SQL-1: alberto_read_by_tags  (multi-tag LATERAL rewrite)
-- ============================================================

CREATE OR REPLACE FUNCTION $schema_prefix$alberto_read_by_tags(
    p_tenant_id VARCHAR(100),
    p_tags VARCHAR(500)[],
    p_after_position BIGINT DEFAULT 0,
    p_limit INT DEFAULT NULL
)
RETURNS TABLE (
    global_position BIGINT,
    tenant_id VARCHAR(100),
    event_id UUID,
    event_type VARCHAR(500),
    event_tags VARCHAR(500)[],
    event_data JSONB,
    event_metadata JSONB,
    created_at TIMESTAMPTZ
) AS $$
BEGIN
    IF p_tags IS NULL OR array_length(p_tags, 1) IS NULL THEN
        RETURN;
    END IF;

    -- Single-tag fast path: one index seek, no deduplication needed.
    IF array_length(p_tags, 1) = 1 THEN
        RETURN QUERY
        SELECT e.global_position, e.tenant_id, e.event_id, e.event_type, e.event_tags, e.event_data, e.event_metadata, e.created_at
        FROM (
            SELECT etagp.global_position
            FROM $schema_prefix$alberto_event_tag_positions etagp
            WHERE etagp.tenant_id = p_tenant_id
              AND etagp.tag = p_tags[1]
              AND etagp.global_position > p_after_position
            ORDER BY etagp.global_position
            LIMIT p_limit
        ) matching_positions
        INNER JOIN $schema_prefix$alberto_events e ON e.global_position = matching_positions.global_position
        ORDER BY matching_positions.global_position;
        RETURN;
    END IF;

    -- Multi-tag path: one index seek per tag (LATERAL), then merge-deduplicate.
    -- Each per-tag lateral scan is bounded at p_limit rows, so total index I/O
    -- is at most array_length(p_tags) * p_limit — versus an unbounded scan in the
    -- old DISTINCT ... tag = ANY(p_tags) form.
    RETURN QUERY
    SELECT e.global_position, e.tenant_id, e.event_id, e.event_type, e.event_tags, e.event_data, e.event_metadata, e.created_at
    FROM (
        SELECT DISTINCT sq.global_position AS gp
        FROM unnest(p_tags) AS t(tag)
        CROSS JOIN LATERAL (
            SELECT etagp.global_position
            FROM $schema_prefix$alberto_event_tag_positions etagp
            WHERE etagp.tenant_id = p_tenant_id
              AND etagp.tag = t.tag
              AND etagp.global_position > p_after_position
            ORDER BY etagp.global_position
            LIMIT p_limit
        ) sq
        ORDER BY gp
        LIMIT p_limit
    ) matching_positions
    INNER JOIN $schema_prefix$alberto_events e ON e.global_position = matching_positions.gp
    ORDER BY matching_positions.gp;
END;
$$ LANGUAGE plpgsql;

-- ============================================================
-- SQL-6: alberto_read_by_types_or_tags  (index-driven UNION)
-- ============================================================

CREATE OR REPLACE FUNCTION $schema_prefix$alberto_read_by_types_or_tags(
    p_tenant_id VARCHAR(100),
    p_types VARCHAR(500)[] DEFAULT NULL,
    p_tags VARCHAR(500)[] DEFAULT NULL,
    p_after_position BIGINT DEFAULT 0,
    p_limit INT DEFAULT NULL
)
RETURNS TABLE (
    global_position BIGINT,
    tenant_id VARCHAR(100),
    event_id UUID,
    event_type VARCHAR(500),
    event_tags VARCHAR(500)[],
    event_data JSONB,
    event_metadata JSONB,
    created_at TIMESTAMPTZ
) AS $$
DECLARE
    v_has_types BOOLEAN := p_types IS NOT NULL AND array_length(p_types, 1) > 0;
    v_has_tags  BOOLEAN := p_tags  IS NOT NULL AND array_length(p_tags,  1) > 0;
BEGIN
    IF NOT v_has_types AND NOT v_has_tags THEN
        RETURN;
    END IF;

    -- Types-only: single index scan on event_type_positions.
    IF v_has_types AND NOT v_has_tags THEN
        RETURN QUERY
        SELECT e.global_position, e.tenant_id, e.event_id, e.event_type, e.event_tags, e.event_data, e.event_metadata, e.created_at
        FROM (
            SELECT etp.global_position
            FROM $schema_prefix$alberto_event_type_positions etp
            WHERE etp.tenant_id = p_tenant_id
              AND etp.event_type = ANY(p_types)
              AND etp.global_position > p_after_position
            ORDER BY etp.global_position
            LIMIT p_limit
        ) mp
        INNER JOIN $schema_prefix$alberto_events e ON e.global_position = mp.global_position
        ORDER BY mp.global_position;
        RETURN;
    END IF;

    -- Tags-only: delegate to the already-optimised read_by_tags (which handles
    -- single-tag vs multi-tag branching internally).
    IF NOT v_has_types AND v_has_tags THEN
        RETURN QUERY
        SELECT * FROM $schema_prefix$alberto_read_by_tags(p_tenant_id, p_tags, p_after_position, p_limit);
        RETURN;
    END IF;

    -- Both types and tags (OR semantics): UNION of two index-driven subqueries.
    -- UNION deduplicates automatically; ORDER BY + LIMIT apply to the merged result.
    RETURN QUERY
    SELECT e.global_position, e.tenant_id, e.event_id, e.event_type, e.event_tags, e.event_data, e.event_metadata, e.created_at
    FROM (
        SELECT etp.global_position
        FROM $schema_prefix$alberto_event_type_positions etp
        WHERE etp.tenant_id = p_tenant_id
          AND etp.event_type = ANY(p_types)
          AND etp.global_position > p_after_position
        UNION
        SELECT etagp.global_position
        FROM $schema_prefix$alberto_event_tag_positions etagp
        WHERE etagp.tenant_id = p_tenant_id
          AND etagp.tag = ANY(p_tags)
          AND etagp.global_position > p_after_position
        ORDER BY 1
        LIMIT p_limit
    ) mp
    INNER JOIN $schema_prefix$alberto_events e ON e.global_position = mp.global_position
    ORDER BY mp.global_position;
END;
$$ LANGUAGE plpgsql;

-- ============================================================
-- SQL-16: alberto_read_by_types_and_tags  (INTERSECT rewrite)
-- ============================================================

CREATE OR REPLACE FUNCTION $schema_prefix$alberto_read_by_types_and_tags(
    p_tenant_id VARCHAR(100),
    p_types VARCHAR(500)[],
    p_tags VARCHAR(500)[],
    p_after_position BIGINT DEFAULT 0,
    p_limit INT DEFAULT NULL
)
RETURNS TABLE (
    global_position BIGINT,
    tenant_id VARCHAR(100),
    event_id UUID,
    event_type VARCHAR(500),
    event_tags VARCHAR(500)[],
    event_data JSONB,
    event_metadata JSONB,
    created_at TIMESTAMPTZ
) AS $$
BEGIN
    IF p_types IS NULL OR array_length(p_types, 1) IS NULL
       OR p_tags IS NULL OR array_length(p_tags, 1) IS NULL THEN
        RETURN;
    END IF;

    -- INTERSECT of two index-driven subqueries: positions that match both the
    -- type axis AND the tag axis.  PostgreSQL satisfies this with a sort-merge
    -- or hash intersect — no correlated subquery per outer row.
    RETURN QUERY
    SELECT e.global_position, e.tenant_id, e.event_id, e.event_type, e.event_tags, e.event_data, e.event_metadata, e.created_at
    FROM (
        SELECT etp.global_position
        FROM $schema_prefix$alberto_event_type_positions etp
        WHERE etp.tenant_id = p_tenant_id
          AND etp.event_type = ANY(p_types)
          AND etp.global_position > p_after_position
        INTERSECT
        SELECT etagp.global_position
        FROM $schema_prefix$alberto_event_tag_positions etagp
        WHERE etagp.tenant_id = p_tenant_id
          AND etagp.tag = ANY(p_tags)
          AND etagp.global_position > p_after_position
        ORDER BY 1
        LIMIT p_limit
    ) mp
    INNER JOIN $schema_prefix$alberto_events e ON e.global_position = mp.global_position
    ORDER BY mp.global_position;
END;
$$ LANGUAGE plpgsql;
