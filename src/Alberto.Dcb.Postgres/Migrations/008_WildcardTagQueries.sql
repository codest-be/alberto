-- Alberto DCB Event Store - Wildcard Tag Query Support
-- Adds functions that support prefix matching for tag queries (e.g., "order:*")

-- Read events by tag patterns (exact tags and/or prefix wildcards)
CREATE OR REPLACE FUNCTION $schema_prefix$read_by_tag_patterns(
    p_tenant_id VARCHAR(100),
    p_exact_tags VARCHAR(500)[] DEFAULT NULL,
    p_tag_prefixes VARCHAR(500)[] DEFAULT NULL,
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
    v_prefix VARCHAR(500);
    v_has_exact BOOLEAN := p_exact_tags IS NOT NULL AND array_length(p_exact_tags, 1) > 0;
    v_has_prefix BOOLEAN := p_tag_prefixes IS NOT NULL AND array_length(p_tag_prefixes, 1) > 0;
BEGIN
    -- Build dynamic query based on what's provided
    IF NOT v_has_exact AND NOT v_has_prefix THEN
        -- No filters, return empty
        RETURN;
    END IF;

    RETURN QUERY
    SELECT DISTINCT e.global_position, e.tenant_id, e.event_id, e.event_type, e.event_tags, e.event_data, e.event_metadata, e.created_at
    FROM $schema_prefix$events e
    INNER JOIN $schema_prefix$event_tag_positions etagp ON e.global_position = etagp.global_position
    WHERE etagp.tenant_id = p_tenant_id
      AND e.global_position > p_after_position
      AND (
          -- Match exact tags
          (v_has_exact AND etagp.tag = ANY(p_exact_tags))
          -- Match prefix patterns (tag starts with any prefix)
          OR (v_has_prefix AND EXISTS (
              SELECT 1 FROM unnest(p_tag_prefixes) AS prefix
              WHERE etagp.tag LIKE prefix || '%'
          ))
      )
    ORDER BY e.global_position
    LIMIT p_limit;
END;
$$ LANGUAGE plpgsql;

-- Read events by types OR tag patterns (DCB query with wildcard support)
CREATE OR REPLACE FUNCTION $schema_prefix$read_by_types_or_tag_patterns(
    p_tenant_id VARCHAR(100),
    p_types VARCHAR(500)[] DEFAULT NULL,
    p_exact_tags VARCHAR(500)[] DEFAULT NULL,
    p_tag_prefixes VARCHAR(500)[] DEFAULT NULL,
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
    v_has_exact BOOLEAN := p_exact_tags IS NOT NULL AND array_length(p_exact_tags, 1) > 0;
    v_has_prefix BOOLEAN := p_tag_prefixes IS NOT NULL AND array_length(p_tag_prefixes, 1) > 0;
BEGIN
    RETURN QUERY
    SELECT DISTINCT e.global_position, e.tenant_id, e.event_id, e.event_type, e.event_tags, e.event_data, e.event_metadata, e.created_at
    FROM $schema_prefix$events e
    LEFT JOIN $schema_prefix$event_type_positions etp ON e.global_position = etp.global_position AND etp.tenant_id = p_tenant_id
    LEFT JOIN $schema_prefix$event_tag_positions etagp ON e.global_position = etagp.global_position AND etagp.tenant_id = p_tenant_id
    WHERE e.tenant_id = p_tenant_id
      AND e.global_position > p_after_position
      AND (
          -- Match event types
          (v_has_types AND etp.event_type = ANY(p_types))
          -- Match exact tags
          OR (v_has_exact AND etagp.tag = ANY(p_exact_tags))
          -- Match prefix patterns
          OR (v_has_prefix AND EXISTS (
              SELECT 1 FROM unnest(p_tag_prefixes) AS prefix
              WHERE etagp.tag LIKE prefix || '%'
          ))
      )
    ORDER BY e.global_position
    LIMIT p_limit;
END;
$$ LANGUAGE plpgsql;

-- Append events with DCB conflict check supporting wildcard patterns
CREATE OR REPLACE FUNCTION $schema_prefix$append_events_v2(
    p_tenant_id VARCHAR(100),
    p_events JSONB,
    p_dcb_types VARCHAR(500)[] DEFAULT NULL,
    p_dcb_exact_tags VARCHAR(500)[] DEFAULT NULL,
    p_dcb_tag_prefixes VARCHAR(500)[] DEFAULT NULL,
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
    -- Perform DCB conflict check if expected position is provided
    IF p_expected_position IS NOT NULL THEN
        -- Check for conflicts by types
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

        -- Check for conflicts by exact tags
        IF p_dcb_exact_tags IS NOT NULL AND array_length(p_dcb_exact_tags, 1) > 0 THEN
            SELECT etagp.global_position INTO v_conflict_position
            FROM $schema_prefix$event_tag_positions etagp
            WHERE etagp.tenant_id = p_tenant_id
              AND etagp.tag = ANY(p_dcb_exact_tags)
              AND etagp.global_position > p_expected_position
            LIMIT 1;

            IF v_conflict_position IS NOT NULL THEN
                RAISE EXCEPTION 'DCB conflict: event tag found at position %', v_conflict_position
                    USING ERRCODE = 'P0001';
            END IF;
        END IF;

        -- Check for conflicts by tag prefixes (wildcards)
        IF p_dcb_tag_prefixes IS NOT NULL AND array_length(p_dcb_tag_prefixes, 1) > 0 THEN
            SELECT etagp.global_position INTO v_conflict_position
            FROM $schema_prefix$event_tag_positions etagp
            WHERE etagp.tenant_id = p_tenant_id
              AND etagp.global_position > p_expected_position
              AND EXISTS (
                  SELECT 1 FROM unnest(p_dcb_tag_prefixes) AS prefix
                  WHERE etagp.tag LIKE prefix || '%'
              )
            LIMIT 1;

            IF v_conflict_position IS NOT NULL THEN
                RAISE EXCEPTION 'DCB conflict: event tag matching prefix found at position %', v_conflict_position
                    USING ERRCODE = 'P0001';
            END IF;
        END IF;
    END IF;

    -- Insert each event
    FOR v_event IN SELECT * FROM jsonb_array_elements(p_events)
    LOOP
        v_event_id := COALESCE((v_event->>'event_id')::UUID, gen_random_uuid());
        v_event_type := v_event->>'event_type';
        v_event_tags := ARRAY(SELECT jsonb_array_elements_text(COALESCE(v_event->'event_tags', '[]'::JSONB)));
        v_event_data := COALESCE(v_event->'event_data', '{}'::JSONB);
        v_event_metadata := COALESCE(v_event->'event_metadata', '{}'::JSONB);
        v_created_at := now();

        -- Insert into events table
        INSERT INTO $schema_prefix$events (tenant_id, event_id, event_type, event_tags, event_data, event_metadata, created_at)
        VALUES (p_tenant_id, v_event_id, v_event_type, v_event_tags, v_event_data, v_event_metadata, v_created_at)
        RETURNING $schema_prefix$events.global_position INTO v_new_position;

        -- Update type inverted index
        INSERT INTO $schema_prefix$event_type_positions (tenant_id, event_type, global_position)
        VALUES (p_tenant_id, v_event_type, v_new_position);

        -- Update tag inverted index
        FOREACH v_tag IN ARRAY v_event_tags
        LOOP
            INSERT INTO $schema_prefix$event_tag_positions (tenant_id, tag, global_position)
            VALUES (p_tenant_id, v_tag, v_new_position);
        END LOOP;

        -- Return the inserted event
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
