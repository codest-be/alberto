-- Alberto DCB Event Store - Migration 023 (Multi-Tenant)
--
-- Match wildcard tag boundaries against the concept index created in migration 022, so the DCB
-- conflict check on the append path is a lookup instead of a scan of every tag ever written.
--
-- Only the boundary predicate changes. Both live wildcard append functions are reproduced here
-- in full because PostgreSQL replaces a function body whole; every other line is migration 002's
-- and 007's, unchanged. In each the predicate
--
--     AND EXISTS (SELECT 1 FROM unnest(p_dcb_tag_prefixes) AS prefix
--                 WHERE etagp.tag LIKE prefix || '%')
--
-- becomes an equality against the indexed expression
--
--     AND left(etagp.tag, position(':' IN etagp.tag)) = ANY(p_dcb_tag_prefixes)
--
-- which is the tag's concept including its separator -- exactly what a prefix carries. See
-- migration 022 for why LIKE could not use an index here, for the measurements, and for the
-- argument that the two predicates agree on every prefix Alberto produces.
--
-- Requires migration 022. Without the index the rewritten predicate is still correct, just no
-- faster than what it replaces.
--
-- The read functions (alberto_read_by_tag_patterns, alberto_read_by_types_or_tag_patterns,
-- alberto_read_by_types_and_tag_patterns) still match prefixes with LIKE. They are on the query
-- path rather than inside the append transaction, and are left for a separate change.

-- ---------------------------------------------------------------------------------
-- alberto_append_events_v2 (union composition)
-- ---------------------------------------------------------------------------------

CREATE OR REPLACE FUNCTION $schema_prefix$alberto_append_events_v2(
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
    IF p_expected_position IS NOT NULL THEN
        IF p_dcb_types IS NOT NULL AND array_length(p_dcb_types, 1) > 0 THEN
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
        END IF;

        IF p_dcb_exact_tags IS NOT NULL AND array_length(p_dcb_exact_tags, 1) > 0 THEN
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

        IF p_dcb_tag_prefixes IS NOT NULL AND array_length(p_dcb_tag_prefixes, 1) > 0 THEN
            SELECT etagp.global_position INTO v_conflict_position
            FROM $schema_prefix$alberto_event_tag_positions etagp
            WHERE etagp.tenant_id = p_tenant_id
              AND etagp.global_position > p_expected_position
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

-- ---------------------------------------------------------------------------------
-- alberto_append_events_v5 (intersect composition)
-- ---------------------------------------------------------------------------------

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
