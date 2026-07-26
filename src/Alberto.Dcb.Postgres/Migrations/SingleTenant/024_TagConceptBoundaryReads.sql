-- Alberto DCB Event Store - Migration 024 (Single-Tenant)
--
-- Extend migration 023's concept matching from the append path to the three wildcard read
-- functions, so a read bounded by a wildcard tag looks its concept up in the index created by
-- migration 022 instead of scanning every tag ever written.
--
-- Only the boundary predicate changes. Each function is reproduced in full because PostgreSQL
-- replaces a function body whole; every other line is migration 002's and 007's, unchanged. In
-- each the predicate
--
--     AND EXISTS (SELECT 1 FROM unnest(p_tag_prefixes) AS prefix
--                 WHERE etagp.tag LIKE prefix || '%')
--
-- becomes an equality against the indexed expression
--
--     AND left(etagp.tag, position(':' IN etagp.tag)) = ANY(p_tag_prefixes)
--
-- which is the tag's concept including its separator -- exactly what a prefix carries. See
-- migration 022 for why LIKE could not use an index here and for the argument that the two
-- predicates agree on every prefix Alberto produces.
--
-- The cost being removed is not the same as on the append path. A read is not inside the append
-- transaction, so it adds no latency to writers; what the scan does is make a query's cost a
-- function of total history rather than of how much it matches.
--
-- Two of the three functions gain an access path from this; the third does not.
-- alberto_read_by_types_or_tag_patterns composes its axes with OR across two LEFT JOINs, so it
-- must visit every event in the position range whatever the tag predicate says, and measures the
-- same before and after -- one sequential scan of the tag table either way. Making that shape
-- indexable means restructuring it as a union of per-axis lookups, which is a change of its own.
-- The rewrite is applied to it regardless: the three functions should not disagree about what a
-- wildcard boundary means, and a per-row equality is cheaper than a per-row correlated subquery
-- even when the plan is unchanged.
--
-- Requires migration 022. Without the index the rewritten predicate is still correct, just no
-- faster than what it replaces.

-- ---------------------------------------------------------------------------------
-- alberto_read_by_tag_patterns
-- ---------------------------------------------------------------------------------

CREATE OR REPLACE FUNCTION $schema_prefix$alberto_read_by_tag_patterns(
    p_exact_tags VARCHAR(500)[] DEFAULT NULL,
    p_tag_prefixes VARCHAR(500)[] DEFAULT NULL,
    p_after_position BIGINT DEFAULT 0,
    p_limit INT DEFAULT NULL
)
RETURNS TABLE (
    global_position BIGINT,
    event_id UUID,
    event_type VARCHAR(500),
    event_tags VARCHAR(500)[],
    event_data JSONB,
    event_metadata JSONB,
    created_at TIMESTAMPTZ
) AS $$
DECLARE
    v_has_exact BOOLEAN := p_exact_tags IS NOT NULL AND array_length(p_exact_tags, 1) > 0;
    v_has_prefix BOOLEAN := p_tag_prefixes IS NOT NULL AND array_length(p_tag_prefixes, 1) > 0;
BEGIN
    IF NOT v_has_exact AND NOT v_has_prefix THEN
        RETURN;
    END IF;

    RETURN QUERY
    SELECT DISTINCT e.global_position, e.event_id, e.event_type, e.event_tags, e.event_data, e.event_metadata, e.created_at
    FROM $schema_prefix$alberto_events e
    INNER JOIN $schema_prefix$alberto_event_tag_positions etagp ON e.global_position = etagp.global_position
    WHERE e.global_position > p_after_position
      AND (
          (v_has_exact AND etagp.tag = ANY(p_exact_tags))
          OR (v_has_prefix AND left(etagp.tag::TEXT, position(':' IN etagp.tag::TEXT)) = ANY(p_tag_prefixes::TEXT[]))
      )
    ORDER BY e.global_position
    LIMIT p_limit;
END;
$$ LANGUAGE plpgsql;

-- ---------------------------------------------------------------------------------
-- alberto_read_by_types_and_tag_patterns
-- ---------------------------------------------------------------------------------

CREATE OR REPLACE FUNCTION $schema_prefix$alberto_read_by_types_and_tag_patterns(
    p_types VARCHAR(500)[],
    p_exact_tags VARCHAR(500)[] DEFAULT NULL,
    p_tag_prefixes VARCHAR(500)[] DEFAULT NULL,
    p_after_position BIGINT DEFAULT 0,
    p_limit INT DEFAULT NULL
)
RETURNS TABLE (
    global_position BIGINT,
    event_id UUID,
    event_type VARCHAR(500),
    event_tags VARCHAR(500)[],
    event_data JSONB,
    event_metadata JSONB,
    created_at TIMESTAMPTZ
) AS $$
DECLARE
    v_has_exact BOOLEAN := p_exact_tags IS NOT NULL AND array_length(p_exact_tags, 1) > 0;
    v_has_prefix BOOLEAN := p_tag_prefixes IS NOT NULL AND array_length(p_tag_prefixes, 1) > 0;
BEGIN
    IF p_types IS NULL OR array_length(p_types, 1) IS NULL THEN
        RETURN;
    END IF;

    IF NOT v_has_exact AND NOT v_has_prefix THEN
        RETURN;
    END IF;

    RETURN QUERY
    SELECT DISTINCT e.global_position, e.event_id, e.event_type, e.event_tags, e.event_data, e.event_metadata, e.created_at
    FROM $schema_prefix$alberto_events e
    INNER JOIN $schema_prefix$alberto_event_tag_positions etagp ON e.global_position = etagp.global_position
    WHERE e.global_position > p_after_position
      AND e.event_type = ANY(p_types)
      AND (
          (v_has_exact AND etagp.tag = ANY(p_exact_tags))
          OR (v_has_prefix AND left(etagp.tag::TEXT, position(':' IN etagp.tag::TEXT)) = ANY(p_tag_prefixes::TEXT[]))
      )
    ORDER BY e.global_position
    LIMIT p_limit;
END;
$$ LANGUAGE plpgsql;

-- ---------------------------------------------------------------------------------
-- alberto_read_by_types_or_tag_patterns
-- ---------------------------------------------------------------------------------

CREATE OR REPLACE FUNCTION $schema_prefix$alberto_read_by_types_or_tag_patterns(
    p_types VARCHAR(500)[] DEFAULT NULL,
    p_exact_tags VARCHAR(500)[] DEFAULT NULL,
    p_tag_prefixes VARCHAR(500)[] DEFAULT NULL,
    p_after_position BIGINT DEFAULT 0,
    p_limit INT DEFAULT NULL
)
RETURNS TABLE (
    global_position BIGINT,
    event_id UUID,
    event_type VARCHAR(500),
    event_tags VARCHAR(500)[],
    event_data JSONB,
    event_metadata JSONB,
    created_at TIMESTAMPTZ
) AS $$
DECLARE
    v_has_types BOOLEAN := p_types IS NOT NULL AND array_length(p_types, 1) > 0;
    v_has_exact BOOLEAN := p_exact_tags IS NOT NULL AND array_length(p_exact_tags, 1) > 0;
    v_has_prefix BOOLEAN := p_tag_prefixes IS NOT NULL AND array_length(p_tag_prefixes, 1) > 0;
BEGIN
    RETURN QUERY
    SELECT DISTINCT e.global_position, e.event_id, e.event_type, e.event_tags, e.event_data, e.event_metadata, e.created_at
    FROM $schema_prefix$alberto_events e
    LEFT JOIN $schema_prefix$alberto_event_type_positions etp ON e.global_position = etp.global_position
    LEFT JOIN $schema_prefix$alberto_event_tag_positions etagp ON e.global_position = etagp.global_position
    WHERE e.global_position > p_after_position
      AND (
          (v_has_types AND etp.event_type = ANY(p_types))
          OR (v_has_exact AND etagp.tag = ANY(p_exact_tags))
          OR (v_has_prefix AND left(etagp.tag::TEXT, position(':' IN etagp.tag::TEXT)) = ANY(p_tag_prefixes::TEXT[]))
      )
    ORDER BY e.global_position
    LIMIT p_limit;
END;
$$ LANGUAGE plpgsql;
