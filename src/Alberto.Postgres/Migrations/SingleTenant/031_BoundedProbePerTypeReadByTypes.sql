-- Alberto DCB Event Store - Migration 031 (Single-Tenant)
--
-- Removes the `= ANY` planner opacity from alberto_read_by_types.  See multi-tenant
-- 031_BoundedProbePerTypeReadByTypes.sql for the rationale and the measured evidence: in
-- short, `event_type = ANY($N)` hides the element count from the planner, so it cannot use
-- (event_type, global_position) as an ordered range scan and falls back to a seq scan, a
-- Sort and a merge join; migration 030's remedy of testing event_type on the events row does
-- NOT transfer, because this function has no tag axis to bound that scan and the shape
-- degrades to reading the whole log exactly when the named type is rare; and one bounded
-- scalar probe per named type is fast at every type count and has no such edge.
--
-- The DISTINCT over unnest is load-bearing rather than tidy: DcbQuery does not deduplicate
-- types, and a repeated type would otherwise run its probe twice and return each position
-- twice, which the `= ANY` form never did.
--
-- The only difference here is that the function carries no tenant argument and the probe has
-- no tenant predicate.

CREATE OR REPLACE FUNCTION $schema_prefix$alberto_read_by_types(
    p_types VARCHAR(500)[],
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
BEGIN
    RETURN QUERY
    SELECT e.global_position, e.event_id, e.event_type, e.event_tags, e.event_data, e.event_metadata, e.created_at
    FROM (
        SELECT probe.global_position
        FROM (SELECT DISTINCT u.event_type FROM unnest(p_types) AS u(event_type)) t
        CROSS JOIN LATERAL (
            SELECT etp.global_position
            FROM $schema_prefix$alberto_event_type_positions etp
            WHERE etp.event_type = t.event_type
              AND etp.global_position > p_after_position
            ORDER BY 1
            LIMIT p_limit
        ) probe
        ORDER BY 1
        LIMIT p_limit
    ) mp
    INNER JOIN $schema_prefix$alberto_events e ON e.global_position = mp.global_position
    ORDER BY mp.global_position
    LIMIT p_limit;
END;
$$ LANGUAGE plpgsql;
