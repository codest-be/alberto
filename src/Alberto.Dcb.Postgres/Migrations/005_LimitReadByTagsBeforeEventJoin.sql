-- Refine tag-only event reads so matching positions are limited from the
-- inverted tag index before joining back to alberto_events.

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

    RETURN QUERY
    SELECT e.global_position, e.tenant_id, e.event_id, e.event_type, e.event_tags, e.event_data, e.event_metadata, e.created_at
    FROM (
        SELECT DISTINCT etagp.global_position
        FROM $schema_prefix$alberto_event_tag_positions etagp
        WHERE etagp.tenant_id = p_tenant_id
          AND etagp.tag = ANY(p_tags)
          AND etagp.global_position > p_after_position
        ORDER BY etagp.global_position
        LIMIT p_limit
    ) matching_positions
    INNER JOIN $schema_prefix$alberto_events e ON e.global_position = matching_positions.global_position
    ORDER BY matching_positions.global_position;
END;
$$ LANGUAGE plpgsql;
