-- Alberto DCB Event Store - Migration 007 (Multi-Tenant)
-- Adds Intersect-mode read and append functions: events that match BOTH the
-- type axis AND the tag axis. Existing _or_ variants are retained for the
-- explicit Union mode (DcbQuery.AsUnion()).

-- ============================================================
-- INTERSECT READS
-- ============================================================

-- Read events of any of the given types AND tagged with any of the given exact tags.
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

    RETURN QUERY
    SELECT e.global_position, e.tenant_id, e.event_id, e.event_type, e.event_tags, e.event_data, e.event_metadata, e.created_at
    FROM $schema_prefix$alberto_events e
    WHERE e.tenant_id = p_tenant_id
      AND e.global_position > p_after_position
      AND e.event_type = ANY(p_types)
      AND EXISTS (
          SELECT 1 FROM $schema_prefix$alberto_event_tag_positions etagp
          WHERE etagp.tenant_id = p_tenant_id
            AND etagp.global_position = e.global_position
            AND etagp.tag = ANY(p_tags)
      )
    ORDER BY e.global_position
    LIMIT p_limit;
END;
$$ LANGUAGE plpgsql;

-- Read events of any of the given types AND tagged with ALL of the given tags.
CREATE OR REPLACE FUNCTION $schema_prefix$alberto_read_by_types_and_all_tags(
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

    RETURN QUERY
    SELECT e.global_position, e.tenant_id, e.event_id, e.event_type, e.event_tags, e.event_data, e.event_metadata, e.created_at
    FROM $schema_prefix$alberto_events e
    INNER JOIN (
        SELECT etagp.global_position
        FROM $schema_prefix$alberto_event_tag_positions etagp
        WHERE etagp.tenant_id = p_tenant_id
          AND etagp.tag = ANY(p_tags)
          AND etagp.global_position > p_after_position
        GROUP BY etagp.global_position
        HAVING COUNT(DISTINCT etagp.tag) = array_length(p_tags, 1)
    ) matching_positions ON e.global_position = matching_positions.global_position
    WHERE e.tenant_id = p_tenant_id
      AND e.event_type = ANY(p_types)
    ORDER BY e.global_position
    LIMIT p_limit;
END;
$$ LANGUAGE plpgsql;

-- Read events of any of the given types AND matching any of the given tag patterns
-- (exact tags and/or prefix wildcards).
CREATE OR REPLACE FUNCTION $schema_prefix$alberto_read_by_types_and_tag_patterns(
    p_tenant_id VARCHAR(100),
    p_types VARCHAR(500)[],
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
    SELECT DISTINCT e.global_position, e.tenant_id, e.event_id, e.event_type, e.event_tags, e.event_data, e.event_metadata, e.created_at
    FROM $schema_prefix$alberto_events e
    INNER JOIN $schema_prefix$alberto_event_tag_positions etagp
        ON e.global_position = etagp.global_position
       AND etagp.tenant_id = p_tenant_id
    WHERE e.tenant_id = p_tenant_id
      AND e.global_position > p_after_position
      AND e.event_type = ANY(p_types)
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

-- ============================================================
-- INTERSECT APPENDS (DCB conflict check on types AND tags)
-- ============================================================

-- Append with DCB conflict on (any type) AND (any exact tag).
CREATE OR REPLACE FUNCTION $schema_prefix$alberto_append_events_v4(
    p_tenant_id VARCHAR(100),
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
    v_has_types BOOLEAN := p_dcb_types IS NOT NULL AND array_length(p_dcb_types, 1) > 0;
    v_has_tags BOOLEAN := p_dcb_tags IS NOT NULL AND array_length(p_dcb_tags, 1) > 0;
BEGIN
    IF p_expected_position IS NOT NULL THEN
        IF v_has_types AND v_has_tags THEN
            SELECT e.global_position INTO v_conflict_position
            FROM $schema_prefix$alberto_events e
            WHERE e.tenant_id = p_tenant_id
              AND e.global_position > p_expected_position
              AND e.event_type = ANY(p_dcb_types)
              AND EXISTS (
                  SELECT 1 FROM $schema_prefix$alberto_event_tag_positions etagp
                  WHERE etagp.tenant_id = p_tenant_id
                    AND etagp.global_position = e.global_position
                    AND etagp.tag = ANY(p_dcb_tags)
              )
            ORDER BY e.global_position
            LIMIT 1;

            IF v_conflict_position IS NOT NULL THEN
                RAISE EXCEPTION 'DCB conflict: event matching types AND tags found at position %', v_conflict_position
                    USING ERRCODE = 'P0001';
            END IF;
        ELSIF v_has_types THEN
            SELECT etp.global_position INTO v_conflict_position
            FROM $schema_prefix$alberto_event_type_positions etp
            WHERE etp.tenant_id = p_tenant_id
              AND etp.event_type = ANY(p_dcb_types)
              AND etp.global_position > p_expected_position
            LIMIT 1;

            IF v_conflict_position IS NOT NULL THEN
                RAISE EXCEPTION 'DCB conflict: event type found at position %', v_conflict_position
                    USING ERRCODE = 'P0001';
            END IF;
        ELSIF v_has_tags THEN
            SELECT etagp.global_position INTO v_conflict_position
            FROM $schema_prefix$alberto_event_tag_positions etagp
            WHERE etagp.tenant_id = p_tenant_id
              AND etagp.tag = ANY(p_dcb_tags)
              AND etagp.global_position > p_expected_position
            LIMIT 1;

            IF v_conflict_position IS NOT NULL THEN
                RAISE EXCEPTION 'DCB conflict: event tag found at position %', v_conflict_position
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

        INSERT INTO $schema_prefix$alberto_events (tenant_id, event_id, event_type, event_tags, event_data, event_metadata, created_at)
        VALUES (p_tenant_id, v_event_id, v_event_type, v_event_tags, v_event_data, v_event_metadata, v_created_at)
        RETURNING $schema_prefix$alberto_events.global_position INTO v_new_position;

        INSERT INTO $schema_prefix$alberto_event_type_positions (tenant_id, event_type, global_position)
        VALUES (p_tenant_id, v_event_type, v_new_position);

        FOREACH v_tag IN ARRAY v_event_tags
        LOOP
            INSERT INTO $schema_prefix$alberto_event_tag_positions (tenant_id, tag, global_position)
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

-- Append with DCB conflict on (any type) AND (any tag pattern: exact or wildcard).
CREATE OR REPLACE FUNCTION $schema_prefix$alberto_append_events_v5(
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
    v_has_types BOOLEAN := p_dcb_types IS NOT NULL AND array_length(p_dcb_types, 1) > 0;
    v_has_exact BOOLEAN := p_dcb_exact_tags IS NOT NULL AND array_length(p_dcb_exact_tags, 1) > 0;
    v_has_prefix BOOLEAN := p_dcb_tag_prefixes IS NOT NULL AND array_length(p_dcb_tag_prefixes, 1) > 0;
    v_has_tags BOOLEAN := v_has_exact OR v_has_prefix;
BEGIN
    IF p_expected_position IS NOT NULL THEN
        IF v_has_types AND v_has_tags THEN
            SELECT e.global_position INTO v_conflict_position
            FROM $schema_prefix$alberto_events e
            WHERE e.tenant_id = p_tenant_id
              AND e.global_position > p_expected_position
              AND e.event_type = ANY(p_dcb_types)
              AND EXISTS (
                  SELECT 1 FROM $schema_prefix$alberto_event_tag_positions etagp
                  WHERE etagp.tenant_id = p_tenant_id
                    AND etagp.global_position = e.global_position
                    AND (
                        (v_has_exact AND etagp.tag = ANY(p_dcb_exact_tags))
                        OR (v_has_prefix AND EXISTS (
                            SELECT 1 FROM unnest(p_dcb_tag_prefixes) AS prefix
                            WHERE etagp.tag LIKE prefix || '%'
                        ))
                    )
              )
            ORDER BY e.global_position
            LIMIT 1;

            IF v_conflict_position IS NOT NULL THEN
                RAISE EXCEPTION 'DCB conflict: event matching types AND tag patterns found at position %', v_conflict_position
                    USING ERRCODE = 'P0001';
            END IF;
        ELSIF v_has_types THEN
            SELECT etp.global_position INTO v_conflict_position
            FROM $schema_prefix$alberto_event_type_positions etp
            WHERE etp.tenant_id = p_tenant_id
              AND etp.event_type = ANY(p_dcb_types)
              AND etp.global_position > p_expected_position
            LIMIT 1;

            IF v_conflict_position IS NOT NULL THEN
                RAISE EXCEPTION 'DCB conflict: event type found at position %', v_conflict_position
                    USING ERRCODE = 'P0001';
            END IF;
        ELSE
            IF v_has_exact THEN
                SELECT etagp.global_position INTO v_conflict_position
                FROM $schema_prefix$alberto_event_tag_positions etagp
                WHERE etagp.tenant_id = p_tenant_id
                  AND etagp.tag = ANY(p_dcb_exact_tags)
                  AND etagp.global_position > p_expected_position
                LIMIT 1;

                IF v_conflict_position IS NOT NULL THEN
                    RAISE EXCEPTION 'DCB conflict: event tag found at position %', v_conflict_position
                        USING ERRCODE = 'P0001';
                END IF;
            END IF;

            IF v_has_prefix THEN
                SELECT etagp.global_position INTO v_conflict_position
                FROM $schema_prefix$alberto_event_tag_positions etagp
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
    END IF;

    FOR v_event IN SELECT * FROM jsonb_array_elements(p_events)
    LOOP
        v_event_id := COALESCE((v_event->>'event_id')::UUID, gen_random_uuid());
        v_event_type := v_event->>'event_type';
        v_event_tags := ARRAY(SELECT jsonb_array_elements_text(COALESCE(v_event->'event_tags', '[]'::JSONB)));
        v_event_data := COALESCE(v_event->'event_data', '{}'::JSONB);
        v_event_metadata := COALESCE(v_event->'event_metadata', '{}'::JSONB);
        v_created_at := now();

        INSERT INTO $schema_prefix$alberto_events (tenant_id, event_id, event_type, event_tags, event_data, event_metadata, created_at)
        VALUES (p_tenant_id, v_event_id, v_event_type, v_event_tags, v_event_data, v_event_metadata, v_created_at)
        RETURNING $schema_prefix$alberto_events.global_position INTO v_new_position;

        INSERT INTO $schema_prefix$alberto_event_type_positions (tenant_id, event_type, global_position)
        VALUES (p_tenant_id, v_event_type, v_new_position);

        FOREACH v_tag IN ARRAY v_event_tags
        LOOP
            INSERT INTO $schema_prefix$alberto_event_tag_positions (tenant_id, tag, global_position)
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

-- Append with DCB conflict on (any type) AND (ALL of the given tags).
CREATE OR REPLACE FUNCTION $schema_prefix$alberto_append_events_v6(
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
    v_has_types BOOLEAN := p_dcb_types IS NOT NULL AND array_length(p_dcb_types, 1) > 0;
    v_has_tags BOOLEAN := p_dcb_all_tags IS NOT NULL AND array_length(p_dcb_all_tags, 1) > 0;
BEGIN
    IF p_expected_position IS NOT NULL THEN
        IF v_has_types AND v_has_tags THEN
            SELECT e.global_position INTO v_conflict_position
            FROM $schema_prefix$alberto_events e
            INNER JOIN (
                SELECT etagp.global_position
                FROM $schema_prefix$alberto_event_tag_positions etagp
                WHERE etagp.tenant_id = p_tenant_id
                  AND etagp.tag = ANY(p_dcb_all_tags)
                  AND etagp.global_position > p_expected_position
                GROUP BY etagp.global_position
                HAVING COUNT(DISTINCT etagp.tag) = array_length(p_dcb_all_tags, 1)
            ) matching ON e.global_position = matching.global_position
            WHERE e.tenant_id = p_tenant_id
              AND e.event_type = ANY(p_dcb_types)
            ORDER BY e.global_position
            LIMIT 1;

            IF v_conflict_position IS NOT NULL THEN
                RAISE EXCEPTION 'DCB conflict: event matching types AND all tags found at position %', v_conflict_position
                    USING ERRCODE = 'P0001';
            END IF;
        ELSIF v_has_types THEN
            SELECT etp.global_position INTO v_conflict_position
            FROM $schema_prefix$alberto_event_type_positions etp
            WHERE etp.tenant_id = p_tenant_id
              AND etp.event_type = ANY(p_dcb_types)
              AND etp.global_position > p_expected_position
            LIMIT 1;

            IF v_conflict_position IS NOT NULL THEN
                RAISE EXCEPTION 'DCB conflict: event type found at position %', v_conflict_position
                    USING ERRCODE = 'P0001';
            END IF;
        ELSIF v_has_tags THEN
            SELECT matching_positions.global_position INTO v_conflict_position
            FROM (
                SELECT etagp.global_position
                FROM $schema_prefix$alberto_event_tag_positions etagp
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

        INSERT INTO $schema_prefix$alberto_events (tenant_id, event_id, event_type, event_tags, event_data, event_metadata, created_at)
        VALUES (p_tenant_id, v_event_id, v_event_type, v_event_tags, v_event_data, v_event_metadata, v_created_at)
        RETURNING $schema_prefix$alberto_events.global_position INTO v_new_position;

        INSERT INTO $schema_prefix$alberto_event_type_positions (tenant_id, event_type, global_position)
        VALUES (p_tenant_id, v_event_type, v_new_position);

        FOREACH v_tag IN ARRAY v_event_tags
        LOOP
            INSERT INTO $schema_prefix$alberto_event_tag_positions (tenant_id, tag, global_position)
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
