-- Alberto DCB Event Store - Migration 028 (Single-Tenant)
--
-- Rewrite alberto_read_by_types_and_tags from INTERSECT to a correlated semi-join.
-- See multi-tenant 028_SemiJoinTypesAndTagsRead.sql for the full rationale and the
-- measured plan evidence; the only difference here is that the function carries no
-- tenant argument and the position lookups have no tenant predicate.

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
BEGIN
    IF p_types IS NULL OR array_length(p_types, 1) IS NULL
       OR p_tags IS NULL OR array_length(p_tags, 1) IS NULL THEN
        RETURN;
    END IF;

    -- Correlated semi-join rather than INTERSECT.  A LIMIT cannot push through a
    -- set operation, so the INTERSECT form hashed both branches in full before
    -- discarding a single row — and the type branch matches a constant fraction of
    -- the store, making the query a function of total size rather than of its own
    -- selectivity.  EXISTS lets the LIMIT stop the scan early.
    --
    -- Written tag-side-outer, but that does not pin the drive side: PostgreSQL
    -- flattens the EXISTS into a semi-join and reorders, so a query whose type axis
    -- is narrower than its tag axis is driven from the type side instead.
    --
    -- DISTINCT is load-bearing.  INTERSECT deduplicates; a tag-driven scan emits one
    -- row per matching tag, so an event carrying two of p_tags would otherwise appear
    -- twice and consume two slots of p_limit.
    RETURN QUERY
    SELECT e.global_position, e.event_id, e.event_type, e.event_tags, e.event_data, e.event_metadata, e.created_at
    FROM (
        SELECT DISTINCT etagp.global_position
        FROM $schema_prefix$alberto_event_tag_positions etagp
        WHERE etagp.tag = ANY(p_tags)
          AND etagp.global_position > p_after_position
          AND EXISTS (
              SELECT 1
              FROM $schema_prefix$alberto_event_type_positions etp
              WHERE etp.global_position = etagp.global_position
                AND etp.event_type = ANY(p_types)
          )
        ORDER BY 1
        LIMIT p_limit
    ) mp
    INNER JOIN $schema_prefix$alberto_events e ON e.global_position = mp.global_position
    ORDER BY mp.global_position;
END;
$$ LANGUAGE plpgsql;
