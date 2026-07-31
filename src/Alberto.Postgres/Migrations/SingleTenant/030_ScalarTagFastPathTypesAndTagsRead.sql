-- Alberto DCB Event Store - Migration 030 (Single-Tenant)
--
-- Widens migration 029's fast path from one tag AND one type to one tag and any number
-- of types, by adding a second branch with a different plan.  See multi-tenant
-- 030_ScalarTagFastPathTypesAndTagsRead.sql for the rationale and the measured evidence:
-- in short, the blocking Sort the general path carries sits above the *tag* scan and is
-- caused by the opaque `tag = ANY($N)`, so a single tag is the right guard; but once
-- more than one type is named, testing event_type on the events row beats probing the
-- type-position index with `= ANY`, while one type still wants the scalar probe.  Two
-- branches, not one widened branch.  The only difference here is that the function
-- carries no tenant argument and the scans have no tenant predicate.

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

        -- One type: probe the type-position PK with a scalar. Unchanged from 029.
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

    IF array_length(p_tags, 1) = 1 THEN
        v_tag := p_tags[1];

        -- Several types: skip the type-position index and test event_type on the events
        -- row this query already has to fetch. The tag scan stays an ordered PK-prefix
        -- range scan, so the LIMIT still stops it early.
        RETURN QUERY
        SELECT e.global_position, e.event_id, e.event_type, e.event_tags, e.event_data, e.event_metadata, e.created_at
        FROM $schema_prefix$alberto_event_tag_positions tp
        INNER JOIN $schema_prefix$alberto_events e ON e.global_position = tp.global_position
        WHERE tp.tag = v_tag
          AND tp.global_position > p_after_position
          AND e.event_type = ANY(p_types)
        ORDER BY tp.global_position
        LIMIT p_limit;
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
