-- Alberto DCB Event Store - Migrate from Legacy (Multi-Tenant)
-- Renames old un-prefixed tables to alberto_-prefixed names on existing databases.
-- Also recreates all functions with the new names and drops old function names.
-- Safe to run on fresh installs (all guards use IF EXISTS).

-- ============================================================
-- TABLE RENAMES
-- ============================================================

DO $$ BEGIN
    IF EXISTS (SELECT FROM pg_tables WHERE schemaname = current_schema() AND tablename = 'events') THEN
        ALTER TABLE $schema_prefix$events RENAME TO $schema_prefix$alberto_events;
    END IF;
END $$;

DO $$ BEGIN
    IF EXISTS (SELECT FROM pg_tables WHERE schemaname = current_schema() AND tablename = 'event_type_positions') THEN
        ALTER TABLE $schema_prefix$event_type_positions RENAME TO $schema_prefix$alberto_event_type_positions;
    END IF;
END $$;

DO $$ BEGIN
    IF EXISTS (SELECT FROM pg_tables WHERE schemaname = current_schema() AND tablename = 'event_tag_positions') THEN
        ALTER TABLE $schema_prefix$event_tag_positions RENAME TO $schema_prefix$alberto_event_tag_positions;
    END IF;
END $$;

DO $$ BEGIN
    IF EXISTS (SELECT FROM pg_tables WHERE schemaname = current_schema() AND tablename = 'processor_checkpoints') THEN
        ALTER TABLE $schema_prefix$processor_checkpoints RENAME TO $schema_prefix$alberto_processor_checkpoints;
    END IF;
END $$;

DO $$ BEGIN
    IF EXISTS (SELECT FROM pg_tables WHERE schemaname = current_schema() AND tablename = 'projection_states') THEN
        ALTER TABLE $schema_prefix$projection_states RENAME TO $schema_prefix$alberto_projection_states;
    END IF;
END $$;

DO $$ BEGIN
    IF EXISTS (SELECT FROM pg_tables WHERE schemaname = current_schema() AND tablename = 'projection_rebuild_meta') THEN
        ALTER TABLE $schema_prefix$projection_rebuild_meta RENAME TO $schema_prefix$alberto_projection_rebuild_meta;
    END IF;
END $$;

DO $$ BEGIN
    IF EXISTS (SELECT FROM pg_tables WHERE schemaname = current_schema() AND tablename = 'dead_letter_events') THEN
        ALTER TABLE $schema_prefix$dead_letter_events RENAME TO $schema_prefix$alberto_dead_letter_events;
    END IF;
END $$;

DO $$ BEGIN
    IF EXISTS (SELECT FROM pg_tables WHERE schemaname = current_schema() AND tablename = 'outbox_entries') THEN
        ALTER TABLE $schema_prefix$outbox_entries RENAME TO $schema_prefix$alberto_outbox_entries;
    END IF;
END $$;

DO $$ BEGIN
    IF EXISTS (SELECT FROM pg_tables WHERE schemaname = current_schema() AND tablename = 'tenant_leases') THEN
        ALTER TABLE $schema_prefix$tenant_leases RENAME TO $schema_prefix$alberto_tenant_leases;
    END IF;
END $$;

DO $$ BEGIN
    IF EXISTS (SELECT FROM pg_tables WHERE schemaname = current_schema() AND tablename = 'tenant_assignments') THEN
        ALTER TABLE $schema_prefix$tenant_assignments RENAME TO $schema_prefix$alberto_tenant_assignments;
    END IF;
END $$;

-- ============================================================
-- DROP OLD TRIGGER NAMES
-- ============================================================

DROP TRIGGER IF EXISTS tr_events_notify ON $schema_prefix$alberto_events;
DROP TRIGGER IF EXISTS tr_checkpoints_notify ON $schema_prefix$alberto_processor_checkpoints;
DROP TRIGGER IF EXISTS tr_dead_letter_insert_notify ON $schema_prefix$alberto_dead_letter_events;
DROP TRIGGER IF EXISTS tr_dead_letter_delete_notify ON $schema_prefix$alberto_dead_letter_events;

-- ============================================================
-- RECREATE ALL FUNCTIONS WITH alberto_ PREFIX
-- ============================================================

CREATE OR REPLACE FUNCTION $schema_prefix$alberto_append_events(
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

        IF p_dcb_tags IS NOT NULL AND array_length(p_dcb_tags, 1) > 0 THEN
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

CREATE OR REPLACE FUNCTION $schema_prefix$alberto_append_events_v3(
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

        IF p_dcb_all_tags IS NOT NULL AND array_length(p_dcb_all_tags, 1) > 0 THEN
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

CREATE OR REPLACE FUNCTION $schema_prefix$alberto_read_all(
    p_tenant_id VARCHAR(100),
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
    FROM $schema_prefix$alberto_events e
    WHERE e.tenant_id = p_tenant_id
      AND e.global_position > p_after_position
    ORDER BY e.global_position
    LIMIT p_limit;
END;
$$ LANGUAGE plpgsql;

CREATE OR REPLACE FUNCTION $schema_prefix$alberto_read_all_global(
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
    FROM $schema_prefix$alberto_events e
    WHERE e.global_position > p_after_position
    ORDER BY e.global_position
    LIMIT p_limit;
END;
$$ LANGUAGE plpgsql;

CREATE OR REPLACE FUNCTION $schema_prefix$alberto_read_by_types(
    p_tenant_id VARCHAR(100),
    p_types VARCHAR(500)[],
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
    FROM $schema_prefix$alberto_events e
    INNER JOIN $schema_prefix$alberto_event_type_positions etp ON e.global_position = etp.global_position
    WHERE etp.tenant_id = p_tenant_id
      AND etp.event_type = ANY(p_types)
      AND e.global_position > p_after_position
    ORDER BY e.global_position
    LIMIT p_limit;
END;
$$ LANGUAGE plpgsql;

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
    RETURN QUERY
    SELECT e.global_position, e.tenant_id, e.event_id, e.event_type, e.event_tags, e.event_data, e.event_metadata, e.created_at
    FROM $schema_prefix$alberto_events e
    INNER JOIN $schema_prefix$alberto_event_tag_positions etagp ON e.global_position = etagp.global_position
    WHERE etagp.tenant_id = p_tenant_id
      AND etagp.tag = ANY(p_tags)
      AND e.global_position > p_after_position
    ORDER BY e.global_position
    LIMIT p_limit;
END;
$$ LANGUAGE plpgsql;

CREATE OR REPLACE FUNCTION $schema_prefix$alberto_read_by_types_or_tags(
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
    FROM $schema_prefix$alberto_events e
    LEFT JOIN $schema_prefix$alberto_event_type_positions etp ON e.global_position = etp.global_position AND etp.tenant_id = p_tenant_id
    LEFT JOIN $schema_prefix$alberto_event_tag_positions etagp ON e.global_position = etagp.global_position AND etagp.tenant_id = p_tenant_id
    WHERE e.tenant_id = p_tenant_id
      AND e.global_position > p_after_position
      AND (
          (p_types IS NOT NULL AND array_length(p_types, 1) > 0 AND etp.event_type = ANY(p_types))
          OR (p_tags IS NOT NULL AND array_length(p_tags, 1) > 0 AND etagp.tag = ANY(p_tags))
      )
    ORDER BY e.global_position
    LIMIT p_limit;
END;
$$ LANGUAGE plpgsql;

CREATE OR REPLACE FUNCTION $schema_prefix$alberto_read_by_tag_patterns(
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
    v_has_exact BOOLEAN := p_exact_tags IS NOT NULL AND array_length(p_exact_tags, 1) > 0;
    v_has_prefix BOOLEAN := p_tag_prefixes IS NOT NULL AND array_length(p_tag_prefixes, 1) > 0;
BEGIN
    IF NOT v_has_exact AND NOT v_has_prefix THEN
        RETURN;
    END IF;

    RETURN QUERY
    SELECT DISTINCT e.global_position, e.tenant_id, e.event_id, e.event_type, e.event_tags, e.event_data, e.event_metadata, e.created_at
    FROM $schema_prefix$alberto_events e
    INNER JOIN $schema_prefix$alberto_event_tag_positions etagp ON e.global_position = etagp.global_position
    WHERE etagp.tenant_id = p_tenant_id
      AND e.global_position > p_after_position
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

CREATE OR REPLACE FUNCTION $schema_prefix$alberto_read_by_types_or_tag_patterns(
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
    FROM $schema_prefix$alberto_events e
    LEFT JOIN $schema_prefix$alberto_event_type_positions etp ON e.global_position = etp.global_position AND etp.tenant_id = p_tenant_id
    LEFT JOIN $schema_prefix$alberto_event_tag_positions etagp ON e.global_position = etagp.global_position AND etagp.tenant_id = p_tenant_id
    WHERE e.tenant_id = p_tenant_id
      AND e.global_position > p_after_position
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

CREATE OR REPLACE FUNCTION $schema_prefix$alberto_read_by_all_tags(
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
    ORDER BY e.global_position
    LIMIT p_limit;
END;
$$ LANGUAGE plpgsql;

CREATE OR REPLACE FUNCTION $schema_prefix$alberto_read_by_types_or_all_tags(
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
    FROM $schema_prefix$alberto_events e
    LEFT JOIN $schema_prefix$alberto_event_type_positions etp
        ON e.global_position = etp.global_position
       AND etp.tenant_id = p_tenant_id
    LEFT JOIN (
        SELECT etagp.global_position
        FROM $schema_prefix$alberto_event_tag_positions etagp
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

CREATE OR REPLACE FUNCTION $schema_prefix$alberto_get_last_position(p_tenant_id VARCHAR(100))
RETURNS BIGINT AS $$
DECLARE
    v_position BIGINT;
BEGIN
    SELECT COALESCE(MAX(e.global_position), 0) INTO v_position
    FROM $schema_prefix$alberto_events e
    WHERE e.tenant_id = p_tenant_id;

    RETURN v_position;
END;
$$ LANGUAGE plpgsql;

CREATE OR REPLACE FUNCTION $schema_prefix$alberto_get_last_position_global()
RETURNS BIGINT AS $$
DECLARE
    v_position BIGINT;
BEGIN
    SELECT COALESCE(MAX(e.global_position), 0) INTO v_position
    FROM $schema_prefix$alberto_events e;

    RETURN v_position;
END;
$$ LANGUAGE plpgsql;

CREATE OR REPLACE FUNCTION $schema_prefix$alberto_notify_events()
RETURNS TRIGGER AS $$
BEGIN
    PERFORM pg_notify('$schema$_events', NEW.global_position::TEXT);
    RETURN NEW;
END;
$$ LANGUAGE plpgsql;

CREATE OR REPLACE FUNCTION $schema_prefix$alberto_notify_checkpoint()
RETURNS TRIGGER AS $$
BEGIN
    PERFORM pg_notify('$schema$_checkpoints', NEW.processor_id || ':' || NEW.last_position::TEXT);
    RETURN NEW;
END;
$$ LANGUAGE plpgsql;

CREATE OR REPLACE FUNCTION $schema_prefix$alberto_notify_dead_letter_insert()
RETURNS TRIGGER AS $$
BEGIN
    PERFORM pg_notify('$schema$_dead_letters', 'added:' || NEW.processor_id);
    RETURN NEW;
END;
$$ LANGUAGE plpgsql;

CREATE OR REPLACE FUNCTION $schema_prefix$alberto_notify_dead_letter_delete()
RETURNS TRIGGER AS $$
BEGIN
    PERFORM pg_notify('$schema$_dead_letters', 'removed:' || OLD.processor_id);
    RETURN OLD;
END;
$$ LANGUAGE plpgsql;

CREATE OR REPLACE FUNCTION $schema_prefix$alberto_save_checkpoint_if_lease_held(
    p_processor_id TEXT,
    p_consumer_id TEXT,
    p_replica_id TEXT,
    p_position BIGINT
) RETURNS BOOLEAN AS $$
DECLARE
    v_rows INTEGER;
BEGIN
    UPDATE $schema_prefix$alberto_processor_checkpoints
    SET last_position = p_position, updated_at = now()
    WHERE processor_id = p_processor_id
    AND EXISTS (
        SELECT 1 FROM $schema_prefix$alberto_tenant_leases
        WHERE consumer_id = p_consumer_id
        AND replica_id = p_replica_id
        AND expires_at > now()
    );

    GET DIAGNOSTICS v_rows = ROW_COUNT;
    RETURN v_rows > 0;
END;
$$ LANGUAGE plpgsql;

-- ============================================================
-- RECREATE TRIGGERS WITH NEW NAMES ON NEW TABLE NAMES
-- ============================================================

DROP TRIGGER IF EXISTS alberto_trg_notify_events ON $schema_prefix$alberto_events;
CREATE TRIGGER alberto_trg_notify_events
    AFTER INSERT ON $schema_prefix$alberto_events
    FOR EACH ROW
    EXECUTE FUNCTION $schema_prefix$alberto_notify_events();

DROP TRIGGER IF EXISTS alberto_trg_notify_checkpoint ON $schema_prefix$alberto_processor_checkpoints;
CREATE TRIGGER alberto_trg_notify_checkpoint
    AFTER INSERT OR UPDATE ON $schema_prefix$alberto_processor_checkpoints
    FOR EACH ROW
    EXECUTE FUNCTION $schema_prefix$alberto_notify_checkpoint();

DROP TRIGGER IF EXISTS alberto_trg_dead_letter_insert_notify ON $schema_prefix$alberto_dead_letter_events;
CREATE TRIGGER alberto_trg_dead_letter_insert_notify
    AFTER INSERT ON $schema_prefix$alberto_dead_letter_events
    FOR EACH ROW
    EXECUTE FUNCTION $schema_prefix$alberto_notify_dead_letter_insert();

DROP TRIGGER IF EXISTS alberto_trg_dead_letter_delete_notify ON $schema_prefix$alberto_dead_letter_events;
CREATE TRIGGER alberto_trg_dead_letter_delete_notify
    AFTER DELETE ON $schema_prefix$alberto_dead_letter_events
    FOR EACH ROW
    EXECUTE FUNCTION $schema_prefix$alberto_notify_dead_letter_delete();

-- ============================================================
-- DROP OLD FUNCTION NAMES
-- ============================================================

DROP FUNCTION IF EXISTS $schema_prefix$append_events(VARCHAR(100), JSONB, VARCHAR(500)[], VARCHAR(500)[], BIGINT);
DROP FUNCTION IF EXISTS $schema_prefix$append_events_v2(VARCHAR(100), JSONB, VARCHAR(500)[], VARCHAR(500)[], VARCHAR(500)[], BIGINT);
DROP FUNCTION IF EXISTS $schema_prefix$append_events_v3(VARCHAR(100), JSONB, VARCHAR(500)[], VARCHAR(500)[], BIGINT);
DROP FUNCTION IF EXISTS $schema_prefix$read_all(VARCHAR(100), BIGINT, INT);
DROP FUNCTION IF EXISTS $schema_prefix$read_all_global(BIGINT, INT);
DROP FUNCTION IF EXISTS $schema_prefix$read_by_types(VARCHAR(100), VARCHAR(500)[], BIGINT, INT);
DROP FUNCTION IF EXISTS $schema_prefix$read_by_tags(VARCHAR(100), VARCHAR(500)[], BIGINT, INT);
DROP FUNCTION IF EXISTS $schema_prefix$read_by_types_or_tags(VARCHAR(100), VARCHAR(500)[], VARCHAR(500)[], BIGINT, INT);
DROP FUNCTION IF EXISTS $schema_prefix$read_by_tag_patterns(VARCHAR(100), VARCHAR(500)[], VARCHAR(500)[], BIGINT, INT);
DROP FUNCTION IF EXISTS $schema_prefix$read_by_types_or_tag_patterns(VARCHAR(100), VARCHAR(500)[], VARCHAR(500)[], VARCHAR(500)[], BIGINT, INT);
DROP FUNCTION IF EXISTS $schema_prefix$read_by_all_tags(VARCHAR(100), VARCHAR(500)[], BIGINT, INT);
DROP FUNCTION IF EXISTS $schema_prefix$read_by_types_or_all_tags(VARCHAR(100), VARCHAR(500)[], VARCHAR(500)[], BIGINT, INT);
DROP FUNCTION IF EXISTS $schema_prefix$get_last_position(VARCHAR(100));
DROP FUNCTION IF EXISTS $schema_prefix$get_last_position_global();
DROP FUNCTION IF EXISTS $schema_prefix$notify_events();
DROP FUNCTION IF EXISTS $schema_prefix$notify_checkpoint();
DROP FUNCTION IF EXISTS $schema_prefix$notify_dead_letter_insert();
DROP FUNCTION IF EXISTS $schema_prefix$notify_dead_letter_delete();
DROP FUNCTION IF EXISTS $schema_prefix$save_checkpoint_if_lease_held(TEXT, TEXT, TEXT, BIGINT);
