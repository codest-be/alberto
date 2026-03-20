-- Alberto DCB Event Store - Exact all-tag query support
-- Adds functions that require an event to match all specified exact tags.

CREATE OR REPLACE FUNCTION $schema_prefix$read_by_all_tags(
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
    RETURN QUERY
    SELECT e.global_position, e.tenant_id, e.event_id, e.event_type, e.event_tags, e.event_data, e.event_metadata, e.created_at
    FROM $schema_prefix$events e
    INNER JOIN (
        SELECT etagp.global_position
        FROM $schema_prefix$event_tag_positions etagp
        WHERE etagp.tenant_id = p_tenant_id
          AND etagp.tag = ANY(p_tags)
          AND etagp.global_position > p_after_position
        GROUP BY etagp.global_position
        HAVING COUNT(DISTINCT etagp.tag) = array_length(p_tags, 1)
    ) matching_positions ON e.global_position = matching_positions.global_position
    ORDER BY e.global_position
    LIMIT p_limit;
END;
$$ LANGUAGE plpgsql;

CREATE OR REPLACE FUNCTION $schema_prefix$read_by_types_or_all_tags(
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
BEGIN
    RETURN QUERY
    SELECT DISTINCT e.global_position, e.tenant_id, e.event_id, e.event_type, e.event_tags, e.event_data, e.event_metadata, e.created_at
    FROM $schema_prefix$events e
    LEFT JOIN $schema_prefix$event_type_positions etp
        ON e.global_position = etp.global_position
       AND etp.tenant_id = p_tenant_id
    LEFT JOIN (
        SELECT etagp.global_position
        FROM $schema_prefix$event_tag_positions etagp
        WHERE etagp.tenant_id = p_tenant_id
          AND p_tags IS NOT NULL
          AND array_length(p_tags, 1) > 0
          AND etagp.tag = ANY(p_tags)
          AND etagp.global_position > p_after_position
        GROUP BY etagp.global_position
        HAVING COUNT(DISTINCT etagp.tag) = array_length(p_tags, 1)
    ) matching_positions ON e.global_position = matching_positions.global_position
    WHERE e.tenant_id = p_tenant_id
      AND e.global_position > p_after_position
      AND (
          (p_types IS NOT NULL AND array_length(p_types, 1) > 0 AND etp.event_type = ANY(p_types))
          OR matching_positions.global_position IS NOT NULL
      )
    ORDER BY e.global_position
    LIMIT p_limit;
END;
$$ LANGUAGE plpgsql;

CREATE OR REPLACE FUNCTION $schema_prefix$append_events_v3(
    p_tenant_id VARCHAR(100),
    p_events JSONB,
    p_dcb_types VARCHAR(500)[] DEFAULT NULL,
    p_dcb_all_tags VARCHAR(500)[] DEFAULT NULL,
    p_expected_position BIGINT DEFAULT NULL
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
    v_event JSONB;
    v_new_position BIGINT;
    v_event_id UUID;
    v_event_type VARCHAR(500);
    v_event_tags VARCHAR(500)[];
    v_event_data JSONB;
    v_event_metadata JSONB;
    v_created_at TIMESTAMPTZ;
    v_tag VARCHAR(500);
    v_conflict_position BIGINT;
BEGIN
    IF p_expected_position IS NOT NULL THEN
        IF p_dcb_types IS NOT NULL AND array_length(p_dcb_types, 1) > 0 THEN
            SELECT etp.global_position INTO v_conflict_position
            FROM $schema_prefix$event_type_positions etp
            WHERE etp.tenant_id = p_tenant_id
              AND etp.event_type = ANY(p_dcb_types)
              AND etp.global_position > p_expected_position
            LIMIT 1;

            IF v_conflict_position IS NOT NULL THEN
                RAISE EXCEPTION 'DCB conflict: event type found at position %', v_conflict_position
                    USING ERRCODE = 'P0001';
            END IF;
        END IF;

        IF p_dcb_all_tags IS NOT NULL AND array_length(p_dcb_all_tags, 1) > 0 THEN
            SELECT matching_positions.global_position INTO v_conflict_position
            FROM (
                SELECT etagp.global_position
                FROM $schema_prefix$event_tag_positions etagp
                WHERE etagp.tenant_id = p_tenant_id
                  AND etagp.tag = ANY(p_dcb_all_tags)
                  AND etagp.global_position > p_expected_position
                GROUP BY etagp.global_position
                HAVING COUNT(DISTINCT etagp.tag) = array_length(p_dcb_all_tags, 1)
            ) matching_positions
            ORDER BY matching_positions.global_position
            LIMIT 1;

            IF v_conflict_position IS NOT NULL THEN
                RAISE EXCEPTION 'DCB conflict: all event tags found at position %', v_conflict_position
                    USING ERRCODE = 'P0001';
            END IF;
        END IF;
    END IF;

    FOR v_event IN SELECT * FROM jsonb_array_elements(p_events)
    LOOP
        v_event_id := COALESCE((v_event->>'event_id')::UUID, gen_random_uuid());
        v_event_type := v_event->>'event_type';
        v_event_tags := ARRAY(SELECT jsonb_array_elements_text(COALESCE(v_event->'event_tags', '[]'::JSONB)));
        v_event_data := COALESCE(v_event->'event_data', '{}'::JSONB);
        v_event_metadata := COALESCE(v_event->'event_metadata', '{}'::JSONB);
        v_created_at := now();

        INSERT INTO $schema_prefix$events (tenant_id, event_id, event_type, event_tags, event_data, event_metadata, created_at)
        VALUES (p_tenant_id, v_event_id, v_event_type, v_event_tags, v_event_data, v_event_metadata, v_created_at)
        RETURNING $schema_prefix$events.global_position INTO v_new_position;

        INSERT INTO $schema_prefix$event_type_positions (tenant_id, event_type, global_position)
        VALUES (p_tenant_id, v_event_type, v_new_position);

        FOREACH v_tag IN ARRAY v_event_tags
        LOOP
            INSERT INTO $schema_prefix$event_tag_positions (tenant_id, tag, global_position)
            VALUES (p_tenant_id, v_tag, v_new_position);
        END LOOP;

        global_position := v_new_position;
        event_id := v_event_id;
        event_type := v_event_type;
        event_tags := v_event_tags;
        event_data := v_event_data;
        event_metadata := v_event_metadata;
        created_at := v_created_at;
        RETURN NEXT;
    END LOOP;
END;
$$ LANGUAGE plpgsql;
