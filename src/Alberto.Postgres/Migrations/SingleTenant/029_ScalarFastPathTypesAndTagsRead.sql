-- Alberto DCB Event Store - Migration 029 (Single-Tenant)
--
-- Single-element fast path for alberto_read_by_types_and_tags.  See multi-tenant
-- 029_ScalarFastPathTypesAndTagsRead.sql for the full rationale and the measured
-- evidence: in short, `tag = ANY($N)` is opaque to the planner, which puts a blocking
-- Sort above the tag scan so the LIMIT cannot stop it early, while a scalar `tag = $v`
-- matches the PK prefix and is ordered by construction.  The only difference here is
-- that the function carries no tenant argument and the scans have no tenant predicate.

CREATE OR REPLACE FUNCTION $schema_prefix$alberto_read_by_types_and_tags(
    p_types VARCHAR(500)[],
    p_tags VARCHAR(500)[],
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
    v_tag  VARCHAR(500);
    v_type VARCHAR(500);
BEGIN
    IF p_types IS NULL OR array_length(p_types, 1) IS NULL
       OR p_tags IS NULL OR array_length(p_tags, 1) IS NULL THEN
        RETURN;
    END IF;

    IF array_length(p_tags, 1) = 1 AND array_length(p_types, 1) = 1 THEN
        v_tag  := p_tags[1];
        v_type := p_types[1];

        -- No DISTINCT here, and it is not an oversight.  The general path needs one
        -- because an event carrying two of p_tags is emitted twice by a tag-driven scan
        -- and would consume two slots of p_limit.  With a single tag that cannot happen:
        -- (tag, global_position) and (event_type, global_position) are the two PKs, so
        -- each side yields at most one row per position and their join yields at most one.
        RETURN QUERY
        SELECT e.global_position, e.event_id, e.event_type, e.event_tags, e.event_data, e.event_metadata, e.created_at
        FROM (
            SELECT tp.global_position
            FROM $schema_prefix$alberto_event_tag_positions tp
            INNER JOIN $schema_prefix$alberto_event_type_positions etp
                ON etp.global_position = tp.global_position
            WHERE tp.tag = v_tag
              AND tp.global_position > p_after_position
              AND etp.event_type = v_type
            ORDER BY 1
            LIMIT p_limit
        ) mp
        INNER JOIN $schema_prefix$alberto_events e ON e.global_position = mp.global_position
        ORDER BY mp.global_position;
        RETURN;
    END IF;

    -- General path, unchanged from 028.  See that migration's header for why the tag
    -- axis is deduplicated in its own subquery and why an outer DISTINCT over a
    -- correlated EXISTS must not be folded back in.
    RETURN QUERY
    SELECT e.global_position, e.event_id, e.event_type, e.event_tags, e.event_data, e.event_metadata, e.created_at
    FROM (
        SELECT tagged.global_position
        FROM (
            SELECT DISTINCT etagp.global_position
            FROM $schema_prefix$alberto_event_tag_positions etagp
            WHERE etagp.tag = ANY(p_tags)
              AND etagp.global_position > p_after_position
        ) tagged
        WHERE tagged.global_position IN (
            SELECT etp.global_position
            FROM $schema_prefix$alberto_event_type_positions etp
            WHERE etp.event_type = ANY(p_types)
        )
        ORDER BY 1
        LIMIT p_limit
    ) mp
    INNER JOIN $schema_prefix$alberto_events e ON e.global_position = mp.global_position
    ORDER BY mp.global_position;
END;
$$ LANGUAGE plpgsql;
