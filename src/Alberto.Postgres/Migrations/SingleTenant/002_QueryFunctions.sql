-- Alberto DCB Event Store - Query Functions (Single-Tenant)
--
-- All CREATE OR REPLACE FUNCTION definitions for the final versions of every
-- PostgreSQL function. Generated from the authoritative pg_dump baseline to
-- preserve exact source text (PostgreSQL stores function bodies verbatim).
--
-- Using CREATE OR REPLACE makes this file idempotent: existing databases (which
-- journaled 001-034 under the old filenames) will execute this file and receive
-- the latest function bodies without any other scripts needed.
--
-- Fresh installs running the consolidated 001_InitialSchema.sql already have the
-- correct schema; this file brings function bodies to their final state on top.

CREATE OR REPLACE FUNCTION $schema_prefix$alberto_append_events(p_events jsonb, p_dcb_types character varying[] DEFAULT NULL::character varying[], p_dcb_tags character varying[] DEFAULT NULL::character varying[], p_expected_position bigint DEFAULT NULL::bigint) RETURNS TABLE(global_position bigint, event_id uuid, event_type character varying, event_tags character varying[], event_data jsonb, event_metadata jsonb, created_at timestamp with time zone)
    LANGUAGE plpgsql
    AS $$
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

        IF p_dcb_tags IS NOT NULL AND array_length(p_dcb_tags, 1) > 0 THEN
            SELECT etagp.global_position INTO v_conflict_position
            FROM $schema_prefix$alberto_event_tag_positions etagp
            WHERE etagp.tag = ANY(p_dcb_tags)
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

    -- One notification per append call, emitted here rather than by a trigger on
    -- $schema_prefix$alberto_events: the loop above runs one INSERT statement per event, so any
    -- trigger on that table -- row-level or statement-level -- fires once per event.
    -- v_new_position holds the position of the last event inserted, i.e. the head this
    -- call produced; it is NULL when p_events was empty, and then nothing is signalled.
    IF v_new_position IS NOT NULL THEN
        PERFORM pg_notify('$schema$_events', v_new_position::TEXT);
    END IF;
END;
$$;

CREATE OR REPLACE FUNCTION $schema_prefix$alberto_append_events_v3(p_events jsonb, p_dcb_types character varying[] DEFAULT NULL::character varying[], p_dcb_all_tags character varying[] DEFAULT NULL::character varying[], p_expected_position bigint DEFAULT NULL::bigint) RETURNS TABLE(global_position bigint, event_id uuid, event_type character varying, event_tags character varying[], event_data jsonb, event_metadata jsonb, created_at timestamp with time zone)
    LANGUAGE plpgsql
    AS $$
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

        IF p_dcb_all_tags IS NOT NULL AND array_length(p_dcb_all_tags, 1) > 0 THEN
            SELECT matching_positions.global_position INTO v_conflict_position
            FROM (
                SELECT etagp.global_position
                FROM $schema_prefix$alberto_event_tag_positions etagp
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

    -- One notification per append call, emitted here rather than by a trigger on
    -- $schema_prefix$alberto_events: the loop above runs one INSERT statement per event, so any
    -- trigger on that table -- row-level or statement-level -- fires once per event.
    -- v_new_position holds the position of the last event inserted, i.e. the head this
    -- call produced; it is NULL when p_events was empty, and then nothing is signalled.
    IF v_new_position IS NOT NULL THEN
        PERFORM pg_notify('$schema$_events', v_new_position::TEXT);
    END IF;
END;
$$;

CREATE OR REPLACE FUNCTION $schema_prefix$alberto_append_events_v4(p_events jsonb, p_dcb_types character varying[] DEFAULT NULL::character varying[], p_dcb_tags character varying[] DEFAULT NULL::character varying[], p_expected_position bigint DEFAULT NULL::bigint) RETURNS TABLE(global_position bigint, event_id uuid, event_type character varying, event_tags character varying[], event_data jsonb, event_metadata jsonb, created_at timestamp with time zone)
    LANGUAGE plpgsql
    AS $$
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
            -- Intersect: conflict only when an event matches BOTH a listed type AND a listed tag.
            SELECT e.global_position INTO v_conflict_position
            FROM $schema_prefix$alberto_events e
            WHERE e.global_position > p_expected_position
              AND e.event_type = ANY(p_dcb_types)
              AND EXISTS (
                  SELECT 1 FROM $schema_prefix$alberto_event_tag_positions etagp
                  WHERE etagp.global_position = e.global_position
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
            WHERE etp.event_type = ANY(p_dcb_types)
              AND etp.global_position > p_expected_position
            LIMIT 1;

            IF v_conflict_position IS NOT NULL THEN
                RAISE EXCEPTION 'DCB conflict: event type found at position %', v_conflict_position
                    USING ERRCODE = 'P0001';
            END IF;
        ELSIF v_has_tags THEN
            SELECT etagp.global_position INTO v_conflict_position
            FROM $schema_prefix$alberto_event_tag_positions etagp
            WHERE etagp.tag = ANY(p_dcb_tags)
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

    -- One notification per append call, emitted here rather than by a trigger on
    -- $schema_prefix$alberto_events: the loop above runs one INSERT statement per event, so any
    -- trigger on that table -- row-level or statement-level -- fires once per event.
    -- v_new_position holds the position of the last event inserted, i.e. the head this
    -- call produced; it is NULL when p_events was empty, and then nothing is signalled.
    IF v_new_position IS NOT NULL THEN
        PERFORM pg_notify('$schema$_events', v_new_position::TEXT);
    END IF;
END;
$$;

CREATE OR REPLACE FUNCTION $schema_prefix$alberto_append_events_v6(p_events jsonb, p_dcb_types character varying[] DEFAULT NULL::character varying[], p_dcb_all_tags character varying[] DEFAULT NULL::character varying[], p_expected_position bigint DEFAULT NULL::bigint) RETURNS TABLE(global_position bigint, event_id uuid, event_type character varying, event_tags character varying[], event_data jsonb, event_metadata jsonb, created_at timestamp with time zone)
    LANGUAGE plpgsql
    AS $$
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
                WHERE etagp.tag = ANY(p_dcb_all_tags)
                  AND etagp.global_position > p_expected_position
                GROUP BY etagp.global_position
                HAVING COUNT(DISTINCT etagp.tag) = array_length(p_dcb_all_tags, 1)
            ) matching ON e.global_position = matching.global_position
            WHERE e.event_type = ANY(p_dcb_types)
            ORDER BY e.global_position
            LIMIT 1;

            IF v_conflict_position IS NOT NULL THEN
                RAISE EXCEPTION 'DCB conflict: event matching types AND all tags found at position %', v_conflict_position
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
        ELSIF v_has_tags THEN
            SELECT matching_positions.global_position INTO v_conflict_position
            FROM (
                SELECT etagp.global_position
                FROM $schema_prefix$alberto_event_tag_positions etagp
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

    -- One notification per append call, emitted here rather than by a trigger on
    -- $schema_prefix$alberto_events: the loop above runs one INSERT statement per event, so any
    -- trigger on that table -- row-level or statement-level -- fires once per event.
    -- v_new_position holds the position of the last event inserted, i.e. the head this
    -- call produced; it is NULL when p_events was empty, and then nothing is signalled.
    IF v_new_position IS NOT NULL THEN
        PERFORM pg_notify('$schema$_events', v_new_position::TEXT);
    END IF;
END;
$$;

CREATE OR REPLACE FUNCTION $schema_prefix$alberto_get_last_position() RETURNS bigint
    LANGUAGE plpgsql
    AS $$
DECLARE
    v_position BIGINT;
BEGIN
    SELECT COALESCE(MAX(e.global_position), 0) INTO v_position
    FROM $schema_prefix$alberto_events e;

    RETURN v_position;
END;
$$;

CREATE OR REPLACE FUNCTION $schema_prefix$alberto_pick_all_tags_driver(p_tags character varying[], p_after_position bigint, p_limit integer) RETURNS character varying
    LANGUAGE plpgsql
    AS $$
DECLARE
    v_driver VARCHAR(500);
    v_tag    VARCHAR(500);
    v_probe  INT;
    v_count  INT;
    v_last   BIGINT;
    v_best   BIGINT := -1;
BEGIN
    -- One tag is its own driver, and a missing axis has none. Neither is worth a probe.
    IF p_tags IS NULL OR array_length(p_tags, 1) IS NULL OR array_length(p_tags, 1) < 2 THEN
        RETURN p_tags[1];
    END IF;

    -- An unlimited read still needs a bounded probe; 1000 rows is enough to rank the tags.
    v_probe := COALESCE(p_limit, 1000);
    v_driver := p_tags[1];

    FOREACH v_tag IN ARRAY p_tags LOOP
        SELECT count(*), COALESCE(max(probe.global_position), -1)
        INTO v_count, v_last
        FROM (
            SELECT tp.global_position
            FROM $schema_prefix$alberto_event_tag_positions tp
            WHERE tp.tag = v_tag
              AND tp.global_position > p_after_position
            ORDER BY 1
            LIMIT v_probe
        ) probe;

        -- Fewer rows in range than the caller asked for: this tag caps the whole conjunction
        -- and no other tag can beat it.
        IF v_count < v_probe THEN
            RETURN v_tag;
        END IF;

        IF v_last > v_best THEN
            v_best := v_last;
            v_driver := v_tag;
        END IF;
    END LOOP;

    RETURN v_driver;
END;
$$;

CREATE OR REPLACE FUNCTION $schema_prefix$alberto_read_all(p_after_position bigint DEFAULT 0, p_limit integer DEFAULT NULL::integer) RETURNS TABLE(global_position bigint, event_id uuid, event_type character varying, event_tags character varying[], event_data jsonb, event_metadata jsonb, created_at timestamp with time zone)
    LANGUAGE plpgsql
    AS $$
BEGIN
    RETURN QUERY
    SELECT e.global_position, e.event_id, e.event_type, e.event_tags, e.event_data, e.event_metadata, e.created_at
    FROM $schema_prefix$alberto_events e
    WHERE e.global_position > p_after_position
    ORDER BY e.global_position
    LIMIT p_limit;
END;
$$;

CREATE OR REPLACE FUNCTION $schema_prefix$alberto_read_by_all_tags(p_tags character varying[], p_after_position bigint DEFAULT 0, p_limit integer DEFAULT NULL::integer) RETURNS TABLE(global_position bigint, event_id uuid, event_type character varying, event_tags character varying[], event_data jsonb, event_metadata jsonb, created_at timestamp with time zone)
    LANGUAGE plpgsql
    AS $$
DECLARE
    v_driver VARCHAR(500);
    v_rest   VARCHAR(500)[];
BEGIN
    IF p_tags IS NULL OR array_length(p_tags, 1) IS NULL THEN
        RETURN;
    END IF;

    v_driver := $schema_prefix$alberto_pick_all_tags_driver(p_tags, p_after_position, p_limit);
    v_rest := array_remove(p_tags, v_driver);

    RETURN QUERY
    SELECT e.global_position, e.event_id, e.event_type, e.event_tags, e.event_data, e.event_metadata, e.created_at
    FROM (
        SELECT tp.global_position
        FROM $schema_prefix$alberto_event_tag_positions tp
        WHERE tp.tag = v_driver
          AND tp.global_position > p_after_position
          AND NOT EXISTS (
              SELECT 1
              FROM (SELECT DISTINCT u.tag FROM unnest(v_rest) AS u(tag)) rest
              WHERE NOT EXISTS (
                  SELECT 1
                  FROM $schema_prefix$alberto_event_tag_positions x
                  WHERE x.tag = rest.tag
                    AND x.global_position = tp.global_position
              )
          )
        ORDER BY 1
        LIMIT p_limit
    ) mp
    INNER JOIN $schema_prefix$alberto_events e ON e.global_position = mp.global_position
    ORDER BY mp.global_position
    LIMIT p_limit;
END;
$$;

CREATE OR REPLACE FUNCTION $schema_prefix$alberto_read_by_tags(p_tags character varying[], p_after_position bigint DEFAULT 0, p_limit integer DEFAULT NULL::integer) RETURNS TABLE(global_position bigint, event_id uuid, event_type character varying, event_tags character varying[], event_data jsonb, event_metadata jsonb, created_at timestamp with time zone)
    LANGUAGE plpgsql
    AS $$
BEGIN
    IF p_tags IS NULL OR array_length(p_tags, 1) IS NULL THEN
        RETURN;
    END IF;

    -- Single-tag fast path: one index seek, no deduplication needed.
    IF array_length(p_tags, 1) = 1 THEN
        RETURN QUERY
        SELECT e.global_position, e.event_id, e.event_type, e.event_tags, e.event_data, e.event_metadata, e.created_at
        FROM (
            SELECT etagp.global_position
            FROM $schema_prefix$alberto_event_tag_positions etagp
            WHERE etagp.tag = p_tags[1]
              AND etagp.global_position > p_after_position
            ORDER BY etagp.global_position
            LIMIT p_limit
        ) matching_positions
        INNER JOIN $schema_prefix$alberto_events e ON e.global_position = matching_positions.global_position
        ORDER BY matching_positions.global_position;
        RETURN;
    END IF;

    -- Multi-tag path: one index seek per tag (LATERAL), then merge-deduplicate.
    -- Each per-tag lateral scan is bounded at p_limit rows, so total index I/O
    -- is at most array_length(p_tags) * p_limit.
    RETURN QUERY
    SELECT e.global_position, e.event_id, e.event_type, e.event_tags, e.event_data, e.event_metadata, e.created_at
    FROM (
        SELECT DISTINCT sq.global_position AS gp
        FROM unnest(p_tags) AS t(tag)
        CROSS JOIN LATERAL (
            SELECT etagp.global_position
            FROM $schema_prefix$alberto_event_tag_positions etagp
            WHERE etagp.tag = t.tag
              AND etagp.global_position > p_after_position
            ORDER BY etagp.global_position
            LIMIT p_limit
        ) sq
        ORDER BY gp
        LIMIT p_limit
    ) matching_positions
    INNER JOIN $schema_prefix$alberto_events e ON e.global_position = matching_positions.gp
    ORDER BY matching_positions.gp;
END;
$$;

CREATE OR REPLACE FUNCTION $schema_prefix$alberto_read_by_types(p_types character varying[], p_after_position bigint DEFAULT 0, p_limit integer DEFAULT NULL::integer) RETURNS TABLE(global_position bigint, event_id uuid, event_type character varying, event_tags character varying[], event_data jsonb, event_metadata jsonb, created_at timestamp with time zone)
    LANGUAGE plpgsql
    AS $$
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
$$;

CREATE OR REPLACE FUNCTION $schema_prefix$alberto_read_by_types_and_all_tags(p_types character varying[], p_tags character varying[], p_after_position bigint DEFAULT 0, p_limit integer DEFAULT NULL::integer) RETURNS TABLE(global_position bigint, event_id uuid, event_type character varying, event_tags character varying[], event_data jsonb, event_metadata jsonb, created_at timestamp with time zone)
    LANGUAGE plpgsql
    AS $$
DECLARE
    v_driver VARCHAR(500);
    v_rest   VARCHAR(500)[];
    v_type   VARCHAR(500);
BEGIN
    IF p_types IS NULL OR array_length(p_types, 1) IS NULL
       OR p_tags IS NULL OR array_length(p_tags, 1) IS NULL THEN
        RETURN;
    END IF;

    v_driver := $schema_prefix$alberto_pick_all_tags_driver(p_tags, p_after_position, p_limit);
    v_rest := array_remove(p_tags, v_driver);

    IF array_length(p_types, 1) = 1 THEN
        v_type := p_types[1];

        -- One type: probe the type-position PK with a scalar, as 029 and 030 do.
        RETURN QUERY
        SELECT e.global_position, e.event_id, e.event_type, e.event_tags, e.event_data, e.event_metadata, e.created_at
        FROM (
            SELECT tp.global_position
            FROM $schema_prefix$alberto_event_tag_positions tp
            INNER JOIN $schema_prefix$alberto_event_type_positions etp
                ON etp.global_position = tp.global_position
               AND etp.event_type = v_type
            WHERE tp.tag = v_driver
              AND tp.global_position > p_after_position
              AND NOT EXISTS (
                  SELECT 1
                  FROM (SELECT DISTINCT u.tag FROM unnest(v_rest) AS u(tag)) rest
                  WHERE NOT EXISTS (
                      SELECT 1
                      FROM $schema_prefix$alberto_event_tag_positions x
                      WHERE x.tag = rest.tag
                        AND x.global_position = tp.global_position
                  )
              )
            ORDER BY 1
            LIMIT p_limit
        ) mp
        INNER JOIN $schema_prefix$alberto_events e ON e.global_position = mp.global_position
        ORDER BY mp.global_position
        LIMIT p_limit;
        RETURN;
    END IF;

    -- Several types: test event_type on the events row this query has to fetch anyway. Safe
    -- because the driving tag scan bounds how many rows are ever considered.
    RETURN QUERY
    SELECT e.global_position, e.event_id, e.event_type, e.event_tags, e.event_data, e.event_metadata, e.created_at
    FROM $schema_prefix$alberto_event_tag_positions tp
    INNER JOIN $schema_prefix$alberto_events e ON e.global_position = tp.global_position
    WHERE tp.tag = v_driver
      AND tp.global_position > p_after_position
      AND e.event_type = ANY(p_types)
      AND NOT EXISTS (
          SELECT 1
          FROM (SELECT DISTINCT u.tag FROM unnest(v_rest) AS u(tag)) rest
          WHERE NOT EXISTS (
              SELECT 1
              FROM $schema_prefix$alberto_event_tag_positions x
              WHERE x.tag = rest.tag
                AND x.global_position = tp.global_position
          )
      )
    ORDER BY tp.global_position
    LIMIT p_limit;
END;
$$;

CREATE OR REPLACE FUNCTION $schema_prefix$alberto_read_by_types_and_tags(p_types character varying[], p_tags character varying[], p_after_position bigint DEFAULT 0, p_limit integer DEFAULT NULL::integer) RETURNS TABLE(global_position bigint, event_id uuid, event_type character varying, event_tags character varying[], event_data jsonb, event_metadata jsonb, created_at timestamp with time zone)
    LANGUAGE plpgsql
    AS $$
DECLARE
    v_tag  VARCHAR(500);
    v_type VARCHAR(500);
BEGIN
    IF p_types IS NULL OR array_length(p_types, 1) IS NULL
       OR p_tags IS NULL OR array_length(p_tags, 1) IS NULL THEN
        RETURN;
    END IF;

    IF array_length(p_tags, 1) = 1 AND array_length(p_types, 1) = 1 THEN
        v_tag  := p_tags[1];
        v_type := p_types[1];

        -- One type: probe the type-position PK with a scalar. Unchanged from 029.
        RETURN QUERY
        SELECT e.global_position, e.event_id, e.event_type, e.event_tags, e.event_data, e.event_metadata, e.created_at
        FROM (
            SELECT tp.global_position
            FROM $schema_prefix$alberto_event_tag_positions tp
            INNER JOIN $schema_prefix$alberto_event_type_positions etp
                ON etp.global_position = tp.global_position
            WHERE tp.tag = v_tag
              AND tp.global_position > p_after_position
              AND etp.event_type = v_type
            ORDER BY 1
            LIMIT p_limit
        ) mp
        INNER JOIN $schema_prefix$alberto_events e ON e.global_position = mp.global_position
        ORDER BY mp.global_position;
        RETURN;
    END IF;

    IF array_length(p_tags, 1) = 1 THEN
        v_tag := p_tags[1];

        -- Several types: skip the type-position index and test event_type on the events
        -- row this query already has to fetch. The tag scan stays an ordered PK-prefix
        -- range scan, so the LIMIT still stops it early.
        RETURN QUERY
        SELECT e.global_position, e.event_id, e.event_type, e.event_tags, e.event_data, e.event_metadata, e.created_at
        FROM $schema_prefix$alberto_event_tag_positions tp
        INNER JOIN $schema_prefix$alberto_events e ON e.global_position = tp.global_position
        WHERE tp.tag = v_tag
          AND tp.global_position > p_after_position
          AND e.event_type = ANY(p_types)
        ORDER BY tp.global_position
        LIMIT p_limit;
        RETURN;
    END IF;

    -- General path, unchanged from 028.  See that migration's header for why the tag
    -- axis is deduplicated in its own subquery and why an outer DISTINCT over a
    -- correlated EXISTS must not be folded back in.
    RETURN QUERY
    SELECT e.global_position, e.event_id, e.event_type, e.event_tags, e.event_data, e.event_metadata, e.created_at
    FROM (
        SELECT tagged.global_position
        FROM (
            SELECT DISTINCT etagp.global_position
            FROM $schema_prefix$alberto_event_tag_positions etagp
            WHERE etagp.tag = ANY(p_tags)
              AND etagp.global_position > p_after_position
        ) tagged
        WHERE tagged.global_position IN (
            SELECT etp.global_position
            FROM $schema_prefix$alberto_event_type_positions etp
            WHERE etp.event_type = ANY(p_types)
        )
        ORDER BY 1
        LIMIT p_limit
    ) mp
    INNER JOIN $schema_prefix$alberto_events e ON e.global_position = mp.global_position
    ORDER BY mp.global_position;
END;
$$;

CREATE OR REPLACE FUNCTION $schema_prefix$alberto_read_by_types_or_all_tags(p_types character varying[] DEFAULT NULL::character varying[], p_tags character varying[] DEFAULT NULL::character varying[], p_after_position bigint DEFAULT 0, p_limit integer DEFAULT NULL::integer) RETURNS TABLE(global_position bigint, event_id uuid, event_type character varying, event_tags character varying[], event_data jsonb, event_metadata jsonb, created_at timestamp with time zone)
    LANGUAGE plpgsql
    AS $$
DECLARE
    v_driver VARCHAR(500);
    v_rest   VARCHAR(500)[];
BEGIN
    v_driver := $schema_prefix$alberto_pick_all_tags_driver(p_tags, p_after_position, p_limit);
    v_rest := array_remove(p_tags, v_driver);

    RETURN QUERY
    SELECT e.global_position, e.event_id, e.event_type, e.event_tags, e.event_data, e.event_metadata, e.created_at
    FROM (
        SELECT arms.gp
        FROM (
            (
                SELECT probe.global_position AS gp
                FROM (SELECT DISTINCT u.event_type FROM unnest(p_types) AS u(event_type)) t
                CROSS JOIN LATERAL (
                    SELECT etp.global_position
                    FROM $schema_prefix$alberto_event_type_positions etp
                    WHERE etp.event_type = t.event_type
                      AND etp.global_position > p_after_position
                    ORDER BY 1
                    LIMIT p_limit
                ) probe
            )
            UNION
            (
                SELECT tp.global_position
                FROM $schema_prefix$alberto_event_tag_positions tp
                WHERE tp.tag = v_driver
                  AND tp.global_position > p_after_position
                  AND NOT EXISTS (
                      SELECT 1
                      FROM (SELECT DISTINCT u.tag FROM unnest(v_rest) AS u(tag)) rest
                      WHERE NOT EXISTS (
                          SELECT 1
                          FROM $schema_prefix$alberto_event_tag_positions x
                          WHERE x.tag = rest.tag
                            AND x.global_position = tp.global_position
                      )
                  )
                ORDER BY 1
                LIMIT p_limit
            )
        ) arms
        ORDER BY 1
        LIMIT p_limit
    ) mp
    INNER JOIN $schema_prefix$alberto_events e ON e.global_position = mp.gp
    ORDER BY mp.gp
    LIMIT p_limit;
END;
$$;

CREATE OR REPLACE FUNCTION $schema_prefix$alberto_read_by_types_or_tags(p_types character varying[] DEFAULT NULL::character varying[], p_tags character varying[] DEFAULT NULL::character varying[], p_after_position bigint DEFAULT 0, p_limit integer DEFAULT NULL::integer) RETURNS TABLE(global_position bigint, event_id uuid, event_type character varying, event_tags character varying[], event_data jsonb, event_metadata jsonb, created_at timestamp with time zone)
    LANGUAGE plpgsql
    AS $$
DECLARE
    v_has_types BOOLEAN := p_types IS NOT NULL AND array_length(p_types, 1) > 0;
    v_has_tags  BOOLEAN := p_tags  IS NOT NULL AND array_length(p_tags,  1) > 0;
BEGIN
    IF NOT v_has_types AND NOT v_has_tags THEN
        RETURN;
    END IF;

    -- One axis: the single-axis function already has the right plan for it.
    IF v_has_types AND NOT v_has_tags THEN
        RETURN QUERY
        SELECT * FROM $schema_prefix$alberto_read_by_types(p_types, p_after_position, p_limit);
        RETURN;
    END IF;

    IF NOT v_has_types AND v_has_tags THEN
        RETURN QUERY
        SELECT * FROM $schema_prefix$alberto_read_by_tags(p_tags, p_after_position, p_limit);
        RETURN;
    END IF;

    RETURN QUERY
    SELECT e.global_position, e.event_id, e.event_type, e.event_tags, e.event_data, e.event_metadata, e.created_at
    FROM (
        SELECT arms.gp
        FROM (
            (
                SELECT probe.global_position AS gp
                FROM (SELECT DISTINCT u.event_type FROM unnest(p_types) AS u(event_type)) t
                CROSS JOIN LATERAL (
                    SELECT etp.global_position
                    FROM $schema_prefix$alberto_event_type_positions etp
                    WHERE etp.event_type = t.event_type
                      AND etp.global_position > p_after_position
                    ORDER BY 1
                    LIMIT p_limit
                ) probe
            )
            UNION
            (
                SELECT probe.global_position
                FROM (SELECT DISTINCT u.tag FROM unnest(p_tags) AS u(tag)) g
                CROSS JOIN LATERAL (
                    SELECT tp.global_position
                    FROM $schema_prefix$alberto_event_tag_positions tp
                    WHERE tp.tag = g.tag
                      AND tp.global_position > p_after_position
                    ORDER BY 1
                    LIMIT p_limit
                ) probe
            )
        ) arms
        ORDER BY 1
        LIMIT p_limit
    ) mp
    INNER JOIN $schema_prefix$alberto_events e ON e.global_position = mp.gp
    ORDER BY mp.gp
    LIMIT p_limit;
END;
$$;

CREATE OR REPLACE FUNCTION $schema_prefix$alberto_save_checkpoint_if_processor_lease_held(p_processor_id text, p_consumer_id text, p_replica_id text, p_position bigint, p_fence_token bigint) RETURNS boolean
    LANGUAGE plpgsql
    AS $$
DECLARE
    v_written INTEGER;
BEGIN
    -- One statement. The lease is the INSERT's source, so it is read under the same snapshot
    -- that writes, and the ON CONFLICT arm re-reads the checkpoint row it is about to overwrite.
    INSERT INTO $schema_prefix$alberto_processor_checkpoints
        (processor_id, last_position, fence_token, updated_at)
    SELECT p_processor_id, p_position, p_fence_token, now()
    FROM $schema_prefix$alberto_processor_leases l
    WHERE l.consumer_id  = p_consumer_id
      AND l.processor_id = p_processor_id
      AND l.replica_id   = p_replica_id
      AND l.expires_at   > now()
      AND l.fence_token  = p_fence_token
    ON CONFLICT (processor_id) DO UPDATE
    SET last_position = GREATEST(
            $schema_prefix$alberto_processor_checkpoints.last_position,
            EXCLUDED.last_position),
        fence_token   = EXCLUDED.fence_token,
        updated_at    = now()
    WHERE $schema_prefix$alberto_processor_checkpoints.fence_token <= EXCLUDED.fence_token;

    GET DIAGNOSTICS v_written = ROW_COUNT;
    RETURN v_written > 0;
END;
$$;

COMMENT ON FUNCTION $schema_prefix$alberto_save_checkpoint_if_processor_lease_held(text, text, text, bigint, bigint) IS
    'Advances a checkpoint only for the replica that currently owns the processor lease, in the generation it presents. Returns false when the caller has been fenced out.';
