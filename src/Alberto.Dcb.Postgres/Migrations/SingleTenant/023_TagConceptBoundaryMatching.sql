-- Alberto DCB Event Store - Migration 023 (Single-Tenant)
--
-- Match wildcard tag boundaries against the concept index created in migration 022. See the
-- multi-tenant 023_TagConceptBoundaryMatching.sql for the full rationale; the only difference
-- here is that the table has no tenant_id column.

-- ---------------------------------------------------------------------------------
-- alberto_append_events_v2 (union composition)
-- ---------------------------------------------------------------------------------

CREATE OR REPLACE FUNCTION $schema_prefix$alberto_append_events_v2(
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
            FROM $schema_prefix$alberto_event_type_positions etp
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
            FROM $schema_prefix$alberto_event_tag_positions etagp
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
            FROM $schema_prefix$alberto_event_tag_positions etagp
            WHERE etagp.global_position > p_expected_position
              AND left(etagp.tag::TEXT, position(':' IN etagp.tag::TEXT)) = ANY(p_dcb_tag_prefixes::TEXT[])
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

        INSERT INTO $schema_prefix$alberto_events (event_id, event_type, event_tags, event_data, event_metadata, created_at)
        VALUES (v_event_id, v_event_type, v_event_tags, v_event_data, v_event_metadata, v_created_at)
        RETURNING $schema_prefix$alberto_events.global_position INTO v_new_position;

        INSERT INTO $schema_prefix$alberto_event_type_positions (event_type, global_position)
        VALUES (v_event_type, v_new_position);

        FOREACH v_tag IN ARRAY v_event_tags
        LOOP
            INSERT INTO $schema_prefix$alberto_event_tag_positions (tag, global_position)
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

-- ---------------------------------------------------------------------------------
-- alberto_append_events_v5 (intersect composition)
-- ---------------------------------------------------------------------------------

CREATE OR REPLACE FUNCTION $schema_prefix$alberto_append_events_v5(
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
            WHERE e.global_position > p_expected_position
              AND e.event_type = ANY(p_dcb_types)
              AND EXISTS (
                  SELECT 1 FROM $schema_prefix$alberto_event_tag_positions etagp
                  WHERE etagp.global_position = e.global_position
                    AND (
                        (v_has_exact AND etagp.tag = ANY(p_dcb_exact_tags))
                        OR (v_has_prefix AND left(etagp.tag::TEXT, position(':' IN etagp.tag::TEXT)) = ANY(p_dcb_tag_prefixes::TEXT[]))
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
            WHERE etp.event_type = ANY(p_dcb_types)
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
                WHERE etagp.tag = ANY(p_dcb_exact_tags)
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
                WHERE etagp.global_position > p_expected_position
                  AND left(etagp.tag::TEXT, position(':' IN etagp.tag::TEXT)) = ANY(p_dcb_tag_prefixes::TEXT[])
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

        INSERT INTO $schema_prefix$alberto_events (event_id, event_type, event_tags, event_data, event_metadata, created_at)
        VALUES (v_event_id, v_event_type, v_event_tags, v_event_data, v_event_metadata, v_created_at)
        RETURNING $schema_prefix$alberto_events.global_position INTO v_new_position;

        INSERT INTO $schema_prefix$alberto_event_type_positions (event_type, global_position)
        VALUES (v_event_type, v_new_position);

        FOREACH v_tag IN ARRAY v_event_tags
        LOOP
            INSERT INTO $schema_prefix$alberto_event_tag_positions (tag, global_position)
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
