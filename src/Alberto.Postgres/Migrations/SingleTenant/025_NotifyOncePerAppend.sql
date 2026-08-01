-- Alberto DCB Event Store - Migration 025 (Single-Tenant)
-- Emit exactly one pg_notify per append call, by moving the notification out of the
-- trigger on alberto_events and into the append functions themselves.
--
-- Migration 010 set out to collapse an N-event append into a single notification. It
-- replaced the FOR EACH ROW trigger with a FOR EACH STATEMENT one, on the premise that
-- an append inserts its batch in one statement. That premise never held: every version
-- of alberto_append_events inserts events one at a time inside a PL/pgSQL loop, and a
-- statement-level trigger fires once per INSERT *statement*, not once per transaction.
-- So an N-event append kept producing N notifications, and every subscriber kept waking
-- N times to chase the same head position -- the exact cost 010 was written to remove.
--
-- No trigger placement can fix that while the loop stands, so the notification moves to
-- the one place that knows an append is finished: the end of the append function. The
-- payload is unchanged -- the position of the last event written, which subscribers
-- treat as a head hint before fetching from their own checkpoint -- so a listener sees
-- the same thing it saw before, once instead of N times.
--
-- Dropping the trigger loses nothing: the append functions are the only writers to
-- alberto_events, so there is no insert path left that would go unannounced. Both
-- trigger functions go with it. alberto_notify_events (per-row, 001/002) has been
-- attached to nothing since 010; alberto_notify_events_batch (statement-level, 010) is
-- what this script detaches. The checkpoint and dead-letter notify triggers stay as they
-- are: still FOR EACH ROW, still on their own tables. A bulk dead-letter clear does fan
-- out one notification per row deleted, but that is an operator action, not the
-- per-append hot path this script is about.
--
-- The four functions below are the live append set. _v2 and _v5 are not restated: they
-- were dropped in 024 along with the wildcard tag boundary they served.

DROP TRIGGER IF EXISTS alberto_trg_notify_events ON $schema_prefix$alberto_events;
DROP FUNCTION IF EXISTS $schema_prefix$alberto_notify_events_batch();
DROP FUNCTION IF EXISTS $schema_prefix$alberto_notify_events();

-- alberto_append_events -- any-tag union boundary.
CREATE OR REPLACE FUNCTION $schema_prefix$alberto_append_events(
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
    -- alberto_events: the loop above runs one INSERT statement per event, so any
    -- trigger on that table -- row-level or statement-level -- fires once per event.
    -- v_new_position holds the position of the last event inserted, i.e. the head this
    -- call produced; it is NULL when p_events was empty, and then nothing is signalled.
    IF v_new_position IS NOT NULL THEN
        PERFORM pg_notify('$schema$_events', v_new_position::TEXT);
    END IF;
END;
$$ LANGUAGE plpgsql;

-- alberto_append_events_v3 -- all-tags intersection boundary.
CREATE OR REPLACE FUNCTION $schema_prefix$alberto_append_events_v3(
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
    -- alberto_events: the loop above runs one INSERT statement per event, so any
    -- trigger on that table -- row-level or statement-level -- fires once per event.
    -- v_new_position holds the position of the last event inserted, i.e. the head this
    -- call produced; it is NULL when p_events was empty, and then nothing is signalled.
    IF v_new_position IS NOT NULL THEN
        PERFORM pg_notify('$schema$_events', v_new_position::TEXT);
    END IF;
END;
$$ LANGUAGE plpgsql;

-- alberto_append_events_v4 -- types AND any-tag boundary.
CREATE OR REPLACE FUNCTION $schema_prefix$alberto_append_events_v4(
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
    -- alberto_events: the loop above runs one INSERT statement per event, so any
    -- trigger on that table -- row-level or statement-level -- fires once per event.
    -- v_new_position holds the position of the last event inserted, i.e. the head this
    -- call produced; it is NULL when p_events was empty, and then nothing is signalled.
    IF v_new_position IS NOT NULL THEN
        PERFORM pg_notify('$schema$_events', v_new_position::TEXT);
    END IF;
END;
$$ LANGUAGE plpgsql;

-- alberto_append_events_v6 -- types AND all-tags boundary.
CREATE OR REPLACE FUNCTION $schema_prefix$alberto_append_events_v6(
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
    -- alberto_events: the loop above runs one INSERT statement per event, so any
    -- trigger on that table -- row-level or statement-level -- fires once per event.
    -- v_new_position holds the position of the last event inserted, i.e. the head this
    -- call produced; it is NULL when p_events was empty, and then nothing is signalled.
    IF v_new_position IS NOT NULL THEN
        PERFORM pg_notify('$schema$_events', v_new_position::TEXT);
    END IF;
END;
$$ LANGUAGE plpgsql;
