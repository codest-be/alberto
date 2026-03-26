-- Alberto DCB Event Store - PostgreSQL Functions (Single-Tenant)
-- No tenant_id parameters - single tenant per schema/database

-- Append events with optional DCB conflict check
CREATE OR REPLACE FUNCTION $schema_prefix$append_events(
    p_events JSONB,
    p_dcb_types VARCHAR(500)[] DEFAULT NULL,
    p_dcb_tags VARCHAR(500)[] DEFAULT NULL,
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
            WHERE etp.event_type = ANY(p_dcb_types)
              AND etp.global_position > p_expected_position
            LIMIT 1;

            IF v_conflict_position IS NOT NULL THEN
                RAISE EXCEPTION 'DCB conflict: event type found at position %', v_conflict_position
                    USING ERRCODE = 'P0001';
            END IF;
        END IF;

        -- Check for conflicts by tags
        IF p_dcb_tags IS NOT NULL AND array_length(p_dcb_tags, 1) > 0 THEN
            SELECT etagp.global_position INTO v_conflict_position
            FROM $schema_prefix$event_tag_positions etagp
            WHERE etagp.tag = ANY(p_dcb_tags)
              AND etagp.global_position > p_expected_position
            LIMIT 1;

            IF v_conflict_position IS NOT NULL THEN
                RAISE EXCEPTION 'DCB conflict: event tag found at position %', v_conflict_position
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
        INSERT INTO $schema_prefix$events (event_id, event_type, event_tags, event_data, event_metadata, created_at)
        VALUES (v_event_id, v_event_type, v_event_tags, v_event_data, v_event_metadata, v_created_at)
        RETURNING $schema_prefix$events.global_position INTO v_new_position;

        -- Update type inverted index
        INSERT INTO $schema_prefix$event_type_positions (event_type, global_position)
        VALUES (v_event_type, v_new_position);

        -- Update tag inverted index
        FOREACH v_tag IN ARRAY v_event_tags
        LOOP
            INSERT INTO $schema_prefix$event_tag_positions (tag, global_position)
            VALUES (v_tag, v_new_position);
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

-- Append events with DCB conflict check supporting wildcard patterns (v2)
CREATE OR REPLACE FUNCTION $schema_prefix$append_events_v2(
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
    IF p_expected_position IS NOT NULL THEN
        IF p_dcb_types IS NOT NULL AND array_length(p_dcb_types, 1) > 0 THEN
            SELECT etp.global_position INTO v_conflict_position
            FROM $schema_prefix$event_type_positions etp
            WHERE etp.event_type = ANY(p_dcb_types)
              AND etp.global_position > p_expected_position
            LIMIT 1;

            IF v_conflict_position IS NOT NULL THEN
                RAISE EXCEPTION 'DCB conflict: event type found at position %', v_conflict_position
                    USING ERRCODE = 'P0001';
            END IF;
        END IF;

        IF p_dcb_exact_tags IS NOT NULL AND array_length(p_dcb_exact_tags, 1) > 0 THEN
            SELECT etagp.global_position INTO v_conflict_position
            FROM $schema_prefix$event_tag_positions etagp
            WHERE etagp.tag = ANY(p_dcb_exact_tags)
              AND etagp.global_position > p_expected_position
            LIMIT 1;

            IF v_conflict_position IS NOT NULL THEN
                RAISE EXCEPTION 'DCB conflict: event tag found at position %', v_conflict_position
                    USING ERRCODE = 'P0001';
            END IF;
        END IF;

        IF p_dcb_tag_prefixes IS NOT NULL AND array_length(p_dcb_tag_prefixes, 1) > 0 THEN
            SELECT etagp.global_position INTO v_conflict_position
            FROM $schema_prefix$event_tag_positions etagp
            WHERE etagp.global_position > p_expected_position
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

    FOR v_event IN SELECT * FROM jsonb_array_elements(p_events)
    LOOP
        v_event_id := COALESCE((v_event->>'event_id')::UUID, gen_random_uuid());
        v_event_type := v_event->>'event_type';
        v_event_tags := ARRAY(SELECT jsonb_array_elements_text(COALESCE(v_event->'event_tags', '[]'::JSONB)));
        v_event_data := COALESCE(v_event->'event_data', '{}'::JSONB);
        v_event_metadata := COALESCE(v_event->'event_metadata', '{}'::JSONB);
        v_created_at := now();

        INSERT INTO $schema_prefix$events (event_id, event_type, event_tags, event_data, event_metadata, created_at)
        VALUES (v_event_id, v_event_type, v_event_tags, v_event_data, v_event_metadata, v_created_at)
        RETURNING $schema_prefix$events.global_position INTO v_new_position;

        INSERT INTO $schema_prefix$event_type_positions (event_type, global_position)
        VALUES (v_event_type, v_new_position);

        FOREACH v_tag IN ARRAY v_event_tags
        LOOP
            INSERT INTO $schema_prefix$event_tag_positions (tag, global_position)
            VALUES (v_tag, v_new_position);
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

-- Append events with DCB conflict check for all-tags queries (v3)
CREATE OR REPLACE FUNCTION $schema_prefix$append_events_v3(
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
            WHERE etp.event_type = ANY(p_dcb_types)
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
                WHERE etagp.tag = ANY(p_dcb_all_tags)
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

        INSERT INTO $schema_prefix$events (event_id, event_type, event_tags, event_data, event_metadata, created_at)
        VALUES (v_event_id, v_event_type, v_event_tags, v_event_data, v_event_metadata, v_created_at)
        RETURNING $schema_prefix$events.global_position INTO v_new_position;

        INSERT INTO $schema_prefix$event_type_positions (event_type, global_position)
        VALUES (v_event_type, v_new_position);

        FOREACH v_tag IN ARRAY v_event_tags
        LOOP
            INSERT INTO $schema_prefix$event_tag_positions (tag, global_position)
            VALUES (v_tag, v_new_position);
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

-- Read events by types (OR logic)
CREATE OR REPLACE FUNCTION $schema_prefix$read_by_types(
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
    FROM $schema_prefix$events e
    INNER JOIN $schema_prefix$event_type_positions etp ON e.global_position = etp.global_position
    WHERE etp.event_type = ANY(p_types)
      AND e.global_position > p_after_position
    ORDER BY e.global_position
    LIMIT p_limit;
END;
$$ LANGUAGE plpgsql;

-- Read events by exact tags (OR logic)
CREATE OR REPLACE FUNCTION $schema_prefix$read_by_tags(
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
    RETURN QUERY
    SELECT DISTINCT e.global_position, e.event_id, e.event_type, e.event_tags, e.event_data, e.event_metadata, e.created_at
    FROM $schema_prefix$events e
    INNER JOIN $schema_prefix$event_tag_positions etagp ON e.global_position = etagp.global_position
    WHERE etagp.tag = ANY(p_tags)
      AND e.global_position > p_after_position
    ORDER BY e.global_position
    LIMIT p_limit;
END;
$$ LANGUAGE plpgsql;

-- Read events by types OR tags (DCB query)
CREATE OR REPLACE FUNCTION $schema_prefix$read_by_types_or_tags(
    p_types VARCHAR(500)[] DEFAULT NULL,
    p_tags VARCHAR(500)[] DEFAULT NULL,
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
    SELECT DISTINCT e.global_position, e.event_id, e.event_type, e.event_tags, e.event_data, e.event_metadata, e.created_at
    FROM $schema_prefix$events e
    LEFT JOIN $schema_prefix$event_type_positions etp ON e.global_position = etp.global_position
    LEFT JOIN $schema_prefix$event_tag_positions etagp ON e.global_position = etagp.global_position
    WHERE e.global_position > p_after_position
      AND (
          (p_types IS NOT NULL AND array_length(p_types, 1) > 0 AND etp.event_type = ANY(p_types))
          OR (p_tags IS NOT NULL AND array_length(p_tags, 1) > 0 AND etagp.tag = ANY(p_tags))
      )
    ORDER BY e.global_position
    LIMIT p_limit;
END;
$$ LANGUAGE plpgsql;

-- Read all events
CREATE OR REPLACE FUNCTION $schema_prefix$read_all(
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
    FROM $schema_prefix$events e
    WHERE e.global_position > p_after_position
    ORDER BY e.global_position
    LIMIT p_limit;
END;
$$ LANGUAGE plpgsql;

-- Read events by tag patterns (exact and/or prefix wildcards)
CREATE OR REPLACE FUNCTION $schema_prefix$read_by_tag_patterns(
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
    FROM $schema_prefix$events e
    INNER JOIN $schema_prefix$event_tag_positions etagp ON e.global_position = etagp.global_position
    WHERE e.global_position > p_after_position
      AND (
          (v_has_exact AND etagp.tag = ANY(p_exact_tags))
          OR (v_has_prefix AND EXISTS (
              SELECT 1 FROM unnest(p_tag_prefixes) AS prefix
              WHERE etagp.tag LIKE prefix || '%'
          ))
      )
    ORDER BY e.global_position
    LIMIT p_limit;
END;
$$ LANGUAGE plpgsql;

-- Read events by types OR tag patterns (wildcards)
CREATE OR REPLACE FUNCTION $schema_prefix$read_by_types_or_tag_patterns(
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
    FROM $schema_prefix$events e
    LEFT JOIN $schema_prefix$event_type_positions etp ON e.global_position = etp.global_position
    LEFT JOIN $schema_prefix$event_tag_positions etagp ON e.global_position = etagp.global_position
    WHERE e.global_position > p_after_position
      AND (
          (v_has_types AND etp.event_type = ANY(p_types))
          OR (v_has_exact AND etagp.tag = ANY(p_exact_tags))
          OR (v_has_prefix AND EXISTS (
              SELECT 1 FROM unnest(p_tag_prefixes) AS prefix
              WHERE etagp.tag LIKE prefix || '%'
          ))
      )
    ORDER BY e.global_position
    LIMIT p_limit;
END;
$$ LANGUAGE plpgsql;

-- Read events matching all specified tags (AND logic)
CREATE OR REPLACE FUNCTION $schema_prefix$read_by_all_tags(
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
    RETURN QUERY
    SELECT e.global_position, e.event_id, e.event_type, e.event_tags, e.event_data, e.event_metadata, e.created_at
    FROM $schema_prefix$events e
    INNER JOIN (
        SELECT etagp.global_position
        FROM $schema_prefix$event_tag_positions etagp
        WHERE etagp.tag = ANY(p_tags)
          AND etagp.global_position > p_after_position
        GROUP BY etagp.global_position
        HAVING COUNT(DISTINCT etagp.tag) = array_length(p_tags, 1)
    ) matching_positions ON e.global_position = matching_positions.global_position
    ORDER BY e.global_position
    LIMIT p_limit;
END;
$$ LANGUAGE plpgsql;

-- Read events by types OR all-tags match
CREATE OR REPLACE FUNCTION $schema_prefix$read_by_types_or_all_tags(
    p_types VARCHAR(500)[] DEFAULT NULL,
    p_tags VARCHAR(500)[] DEFAULT NULL,
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
    SELECT DISTINCT e.global_position, e.event_id, e.event_type, e.event_tags, e.event_data, e.event_metadata, e.created_at
    FROM $schema_prefix$events e
    LEFT JOIN $schema_prefix$event_type_positions etp ON e.global_position = etp.global_position
    LEFT JOIN (
        SELECT etagp.global_position
        FROM $schema_prefix$event_tag_positions etagp
        WHERE p_tags IS NOT NULL
          AND array_length(p_tags, 1) > 0
          AND etagp.tag = ANY(p_tags)
          AND etagp.global_position > p_after_position
        GROUP BY etagp.global_position
        HAVING COUNT(DISTINCT etagp.tag) = array_length(p_tags, 1)
    ) matching_positions ON e.global_position = matching_positions.global_position
    WHERE e.global_position > p_after_position
      AND (
          (p_types IS NOT NULL AND array_length(p_types, 1) > 0 AND etp.event_type = ANY(p_types))
          OR matching_positions.global_position IS NOT NULL
      )
    ORDER BY e.global_position
    LIMIT p_limit;
END;
$$ LANGUAGE plpgsql;

-- Get last position
CREATE OR REPLACE FUNCTION $schema_prefix$get_last_position()
RETURNS BIGINT AS $$
DECLARE
    v_position BIGINT;
BEGIN
    SELECT COALESCE(MAX(e.global_position), 0) INTO v_position
    FROM $schema_prefix$events e;

    RETURN v_position;
END;
$$ LANGUAGE plpgsql;
