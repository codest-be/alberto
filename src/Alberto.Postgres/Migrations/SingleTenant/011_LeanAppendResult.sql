-- Alberto DCB Event Store – 011 Lean Append Result (Single-Tenant)
--
-- The four append functions (alberto_append_events, _v3, _v4, _v6) return every inserted row
-- in full: array_agg over whole alberto_events rows, then unnest back to the caller, which
-- reads back the payload, tags and metadata it has just sent. This script adds a lean twin of
-- each, alberto_append_events_lean, _v3_lean, _v4_lean and _v6_lean, returning only what the
-- database decides: global_position and created_at, one row per input event in input order.
-- The client builds the envelopes from the events it sent and calls the _lean family.
--
-- The lean bodies differ from their originals only in the return shape and the result
-- assembly. The conflict checks (009's bounded types-AND-tags check included), the P0001
-- raises with HINT, the insert, the position-table inserts and the single pg_notify are
-- copied unchanged: alberto_append_events and _v3 from 004, _v4 and _v6 from 009.
--
-- Order is tied to the input explicitly: each inserted row is joined back to its input
-- element on event_id (unique), and both result arrays are aggregated ORDER BY the element's
-- ordinal, then returned WITH ORDINALITY. It does not depend on the order positions were drawn.
--
-- The original four are left exactly as they are, for replicas still running an earlier
-- Alberto build during a rolling deploy: they read full rows by column name, and changing the
-- shape under them would fail an append after its write had committed. Both families have the
-- same conflict semantics. The originals can be dropped in a later minor release, once no
-- replica on a pre-011 build can exist; until then any change to the conflict checks must be
-- made in both families.
--
-- codest-be/alberto#269.

-- ============================================================
-- alberto_append_events_lean
-- ============================================================

CREATE FUNCTION $schema_prefix$alberto_append_events_lean(
    p_events JSONB,
    p_dcb_types VARCHAR(500)[] DEFAULT NULL,
    p_dcb_tags VARCHAR(500)[] DEFAULT NULL,
    p_expected_position BIGINT DEFAULT NULL
)
RETURNS TABLE (
    global_position BIGINT,
    created_at TIMESTAMPTZ
) AS $$
DECLARE
    v_conflict_position BIGINT;
    v_last_position     BIGINT;
    v_positions         BIGINT[];
    v_created_at        TIMESTAMPTZ[];
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
                    USING ERRCODE = 'P0001', HINT = v_conflict_position::TEXT;
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
                    USING ERRCODE = 'P0001', HINT = v_conflict_position::TEXT;
            END IF;
        END IF;
    END IF;

    WITH parsed AS MATERIALIZED (
        SELECT
            COALESCE((elem->>'event_id')::UUID, gen_random_uuid())              AS ev_id,
            (elem->>'event_type')::VARCHAR(500)                                 AS ev_type,
            ARRAY(SELECT jsonb_array_elements_text(
                COALESCE(elem->'event_tags', '[]'::JSONB)))::VARCHAR(500)[]     AS ev_tags,
            COALESCE(elem->'event_data',     '{}'::JSONB)                       AS ev_data,
            COALESCE(elem->'event_metadata', '{}'::JSONB)                       AS ev_meta,
            now()                                                                AS ev_created_at,
            ord
        FROM jsonb_array_elements(p_events) WITH ORDINALITY AS t(elem, ord)
    ),
    inserted AS (
        INSERT INTO $schema_prefix$alberto_events
            (event_id, event_type, event_tags, event_data, event_metadata, created_at)
        SELECT ev_id, ev_type, ev_tags, ev_data, ev_meta, ev_created_at
        FROM parsed
        ORDER BY ord
        RETURNING $schema_prefix$alberto_events.*
    ),
    type_ins AS (
        INSERT INTO $schema_prefix$alberto_event_type_positions (event_type, global_position)
        SELECT i.event_type, i.global_position FROM inserted AS i
    ),
    tag_ins AS (
        INSERT INTO $schema_prefix$alberto_event_tag_positions (tag, global_position)
        SELECT UNNEST(i.event_tags) AS tag, i.global_position FROM inserted AS i
    )
    -- Each inserted row is paired with its input element through event_id (unique), and
    -- both arrays are built in input order (p.ord), so row n of the result is element n of
    -- p_events by construction. parsed is MATERIALIZED so a generated event_id is evaluated
    -- once and the join sees the same value the insert stored.
    SELECT array_agg(i.global_position ORDER BY p.ord),
           array_agg(i.created_at      ORDER BY p.ord),
           MAX(i.global_position)
    INTO v_positions, v_created_at, v_last_position
    FROM inserted AS i
    JOIN parsed AS p ON p.ev_id = i.event_id;

    -- One notification per append call with the last position written.
    -- Guarded so nothing is emitted when p_events was empty (v_last_position is NULL).
    IF v_last_position IS NOT NULL THEN
        PERFORM pg_notify('$schema$_events', v_last_position::TEXT);
        RETURN QUERY
        SELECT r.global_position, r.created_at
        FROM unnest(v_positions, v_created_at) WITH ORDINALITY AS r(global_position, created_at, n)
        ORDER BY r.n;
    END IF;
END;
$$ LANGUAGE plpgsql;

-- ============================================================
-- alberto_append_events_v3_lean
-- ============================================================

CREATE FUNCTION $schema_prefix$alberto_append_events_v3_lean(
    p_events JSONB,
    p_dcb_types VARCHAR(500)[] DEFAULT NULL,
    p_dcb_all_tags VARCHAR(500)[] DEFAULT NULL,
    p_expected_position BIGINT DEFAULT NULL
)
RETURNS TABLE (
    global_position BIGINT,
    created_at TIMESTAMPTZ
) AS $$
DECLARE
    v_conflict_position BIGINT;
    v_last_position     BIGINT;
    v_positions         BIGINT[];
    v_created_at        TIMESTAMPTZ[];
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
                    USING ERRCODE = 'P0001', HINT = v_conflict_position::TEXT;
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
                    USING ERRCODE = 'P0001', HINT = v_conflict_position::TEXT;
            END IF;
        END IF;
    END IF;

    WITH parsed AS MATERIALIZED (
        SELECT
            COALESCE((elem->>'event_id')::UUID, gen_random_uuid())              AS ev_id,
            (elem->>'event_type')::VARCHAR(500)                                 AS ev_type,
            ARRAY(SELECT jsonb_array_elements_text(
                COALESCE(elem->'event_tags', '[]'::JSONB)))::VARCHAR(500)[]     AS ev_tags,
            COALESCE(elem->'event_data',     '{}'::JSONB)                       AS ev_data,
            COALESCE(elem->'event_metadata', '{}'::JSONB)                       AS ev_meta,
            now()                                                                AS ev_created_at,
            ord
        FROM jsonb_array_elements(p_events) WITH ORDINALITY AS t(elem, ord)
    ),
    inserted AS (
        INSERT INTO $schema_prefix$alberto_events
            (event_id, event_type, event_tags, event_data, event_metadata, created_at)
        SELECT ev_id, ev_type, ev_tags, ev_data, ev_meta, ev_created_at
        FROM parsed
        ORDER BY ord
        RETURNING $schema_prefix$alberto_events.*
    ),
    type_ins AS (
        INSERT INTO $schema_prefix$alberto_event_type_positions (event_type, global_position)
        SELECT i.event_type, i.global_position FROM inserted AS i
    ),
    tag_ins AS (
        INSERT INTO $schema_prefix$alberto_event_tag_positions (tag, global_position)
        SELECT UNNEST(i.event_tags) AS tag, i.global_position FROM inserted AS i
    )
    -- Each inserted row is paired with its input element through event_id (unique), and
    -- both arrays are built in input order (p.ord), so row n of the result is element n of
    -- p_events by construction. parsed is MATERIALIZED so a generated event_id is evaluated
    -- once and the join sees the same value the insert stored.
    SELECT array_agg(i.global_position ORDER BY p.ord),
           array_agg(i.created_at      ORDER BY p.ord),
           MAX(i.global_position)
    INTO v_positions, v_created_at, v_last_position
    FROM inserted AS i
    JOIN parsed AS p ON p.ev_id = i.event_id;

    IF v_last_position IS NOT NULL THEN
        PERFORM pg_notify('$schema$_events', v_last_position::TEXT);
        RETURN QUERY
        SELECT r.global_position, r.created_at
        FROM unnest(v_positions, v_created_at) WITH ORDINALITY AS r(global_position, created_at, n)
        ORDER BY r.n;
    END IF;
END;
$$ LANGUAGE plpgsql;

-- ============================================================
-- alberto_append_events_v4_lean
-- ============================================================

CREATE FUNCTION $schema_prefix$alberto_append_events_v4_lean(
    p_events JSONB,
    p_dcb_types VARCHAR(500)[] DEFAULT NULL,
    p_dcb_tags VARCHAR(500)[] DEFAULT NULL,
    p_expected_position BIGINT DEFAULT NULL
)
RETURNS TABLE (
    global_position BIGINT,
    created_at TIMESTAMPTZ
) AS $$
DECLARE
    v_conflict_position BIGINT;
    v_last_position     BIGINT;
    v_positions         BIGINT[];
    v_created_at        TIMESTAMPTZ[];
    v_has_types BOOLEAN := p_dcb_types IS NOT NULL AND array_length(p_dcb_types, 1) > 0;
    v_has_tags  BOOLEAN := p_dcb_tags  IS NOT NULL AND array_length(p_dcb_tags,  1) > 0;
BEGIN
    IF p_expected_position IS NOT NULL THEN
        IF v_has_types AND v_has_tags THEN
            -- Intersect: conflict only when an event matches BOTH a listed type AND a listed tag.
            -- Driven from the tag index and bounded by the expected position, then probing the
            -- type index per candidate. See the header for why not from alberto_events.
            SELECT tp.global_position INTO v_conflict_position
            FROM $schema_prefix$alberto_event_tag_positions tp
            WHERE tp.tag = ANY(p_dcb_tags)
              AND tp.global_position > p_expected_position
              AND EXISTS (
                  SELECT 1 FROM $schema_prefix$alberto_event_type_positions etp
                  WHERE etp.event_type = ANY(p_dcb_types)
                    AND etp.global_position = tp.global_position
              )
            ORDER BY tp.global_position
            LIMIT 1;

            IF v_conflict_position IS NOT NULL THEN
                RAISE EXCEPTION 'DCB conflict: event matching types AND tags found at position %', v_conflict_position
                    USING ERRCODE = 'P0001', HINT = v_conflict_position::TEXT;
            END IF;
        ELSIF v_has_types THEN
            SELECT etp.global_position INTO v_conflict_position
            FROM $schema_prefix$alberto_event_type_positions etp
            WHERE etp.event_type = ANY(p_dcb_types)
              AND etp.global_position > p_expected_position
            LIMIT 1;

            IF v_conflict_position IS NOT NULL THEN
                RAISE EXCEPTION 'DCB conflict: event type found at position %', v_conflict_position
                    USING ERRCODE = 'P0001', HINT = v_conflict_position::TEXT;
            END IF;
        ELSIF v_has_tags THEN
            SELECT etagp.global_position INTO v_conflict_position
            FROM $schema_prefix$alberto_event_tag_positions etagp
            WHERE etagp.tag = ANY(p_dcb_tags)
              AND etagp.global_position > p_expected_position
            LIMIT 1;

            IF v_conflict_position IS NOT NULL THEN
                RAISE EXCEPTION 'DCB conflict: event tag found at position %', v_conflict_position
                    USING ERRCODE = 'P0001', HINT = v_conflict_position::TEXT;
            END IF;
        END IF;
    END IF;

    WITH parsed AS MATERIALIZED (
        SELECT
            COALESCE((elem->>'event_id')::UUID, gen_random_uuid())              AS ev_id,
            (elem->>'event_type')::VARCHAR(500)                                 AS ev_type,
            ARRAY(SELECT jsonb_array_elements_text(
                COALESCE(elem->'event_tags', '[]'::JSONB)))::VARCHAR(500)[]     AS ev_tags,
            COALESCE(elem->'event_data',     '{}'::JSONB)                       AS ev_data,
            COALESCE(elem->'event_metadata', '{}'::JSONB)                       AS ev_meta,
            now()                                                                AS ev_created_at,
            ord
        FROM jsonb_array_elements(p_events) WITH ORDINALITY AS t(elem, ord)
    ),
    inserted AS (
        INSERT INTO $schema_prefix$alberto_events
            (event_id, event_type, event_tags, event_data, event_metadata, created_at)
        SELECT ev_id, ev_type, ev_tags, ev_data, ev_meta, ev_created_at
        FROM parsed
        ORDER BY ord
        RETURNING $schema_prefix$alberto_events.*
    ),
    type_ins AS (
        INSERT INTO $schema_prefix$alberto_event_type_positions (event_type, global_position)
        SELECT i.event_type, i.global_position FROM inserted AS i
    ),
    tag_ins AS (
        INSERT INTO $schema_prefix$alberto_event_tag_positions (tag, global_position)
        SELECT UNNEST(i.event_tags) AS tag, i.global_position FROM inserted AS i
    )
    -- Each inserted row is paired with its input element through event_id (unique), and
    -- both arrays are built in input order (p.ord), so row n of the result is element n of
    -- p_events by construction. parsed is MATERIALIZED so a generated event_id is evaluated
    -- once and the join sees the same value the insert stored.
    SELECT array_agg(i.global_position ORDER BY p.ord),
           array_agg(i.created_at      ORDER BY p.ord),
           MAX(i.global_position)
    INTO v_positions, v_created_at, v_last_position
    FROM inserted AS i
    JOIN parsed AS p ON p.ev_id = i.event_id;

    IF v_last_position IS NOT NULL THEN
        PERFORM pg_notify('$schema$_events', v_last_position::TEXT);
        RETURN QUERY
        SELECT r.global_position, r.created_at
        FROM unnest(v_positions, v_created_at) WITH ORDINALITY AS r(global_position, created_at, n)
        ORDER BY r.n;
    END IF;
END;
$$ LANGUAGE plpgsql;

-- ============================================================
-- alberto_append_events_v6_lean
-- ============================================================

CREATE FUNCTION $schema_prefix$alberto_append_events_v6_lean(
    p_events JSONB,
    p_dcb_types VARCHAR(500)[] DEFAULT NULL,
    p_dcb_all_tags VARCHAR(500)[] DEFAULT NULL,
    p_expected_position BIGINT DEFAULT NULL
)
RETURNS TABLE (
    global_position BIGINT,
    created_at TIMESTAMPTZ
) AS $$
DECLARE
    v_conflict_position BIGINT;
    v_last_position     BIGINT;
    v_positions         BIGINT[];
    v_created_at        TIMESTAMPTZ[];
    v_has_types BOOLEAN := p_dcb_types    IS NOT NULL AND array_length(p_dcb_types,    1) > 0;
    v_has_tags  BOOLEAN := p_dcb_all_tags IS NOT NULL AND array_length(p_dcb_all_tags, 1) > 0;
BEGIN
    IF p_expected_position IS NOT NULL THEN
        IF v_has_types AND v_has_tags THEN
            SELECT matching.global_position INTO v_conflict_position
            FROM (
                SELECT etagp.global_position
                FROM $schema_prefix$alberto_event_tag_positions etagp
                WHERE etagp.tag = ANY(p_dcb_all_tags)
                  AND etagp.global_position > p_expected_position
                GROUP BY etagp.global_position
                HAVING COUNT(DISTINCT etagp.tag) = array_length(p_dcb_all_tags, 1)
            ) matching
            WHERE EXISTS (
                SELECT 1 FROM $schema_prefix$alberto_event_type_positions etp
                WHERE etp.event_type = ANY(p_dcb_types)
                  AND etp.global_position = matching.global_position
            )
            ORDER BY matching.global_position
            LIMIT 1;

            IF v_conflict_position IS NOT NULL THEN
                RAISE EXCEPTION 'DCB conflict: event matching types AND all tags found at position %', v_conflict_position
                    USING ERRCODE = 'P0001', HINT = v_conflict_position::TEXT;
            END IF;
        ELSIF v_has_types THEN
            SELECT etp.global_position INTO v_conflict_position
            FROM $schema_prefix$alberto_event_type_positions etp
            WHERE etp.event_type = ANY(p_dcb_types)
              AND etp.global_position > p_expected_position
            LIMIT 1;

            IF v_conflict_position IS NOT NULL THEN
                RAISE EXCEPTION 'DCB conflict: event type found at position %', v_conflict_position
                    USING ERRCODE = 'P0001', HINT = v_conflict_position::TEXT;
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
                    USING ERRCODE = 'P0001', HINT = v_conflict_position::TEXT;
            END IF;
        END IF;
    END IF;

    WITH parsed AS MATERIALIZED (
        SELECT
            COALESCE((elem->>'event_id')::UUID, gen_random_uuid())              AS ev_id,
            (elem->>'event_type')::VARCHAR(500)                                 AS ev_type,
            ARRAY(SELECT jsonb_array_elements_text(
                COALESCE(elem->'event_tags', '[]'::JSONB)))::VARCHAR(500)[]     AS ev_tags,
            COALESCE(elem->'event_data',     '{}'::JSONB)                       AS ev_data,
            COALESCE(elem->'event_metadata', '{}'::JSONB)                       AS ev_meta,
            now()                                                                AS ev_created_at,
            ord
        FROM jsonb_array_elements(p_events) WITH ORDINALITY AS t(elem, ord)
    ),
    inserted AS (
        INSERT INTO $schema_prefix$alberto_events
            (event_id, event_type, event_tags, event_data, event_metadata, created_at)
        SELECT ev_id, ev_type, ev_tags, ev_data, ev_meta, ev_created_at
        FROM parsed
        ORDER BY ord
        RETURNING $schema_prefix$alberto_events.*
    ),
    type_ins AS (
        INSERT INTO $schema_prefix$alberto_event_type_positions (event_type, global_position)
        SELECT i.event_type, i.global_position FROM inserted AS i
    ),
    tag_ins AS (
        INSERT INTO $schema_prefix$alberto_event_tag_positions (tag, global_position)
        SELECT UNNEST(i.event_tags) AS tag, i.global_position FROM inserted AS i
    )
    -- Each inserted row is paired with its input element through event_id (unique), and
    -- both arrays are built in input order (p.ord), so row n of the result is element n of
    -- p_events by construction. parsed is MATERIALIZED so a generated event_id is evaluated
    -- once and the join sees the same value the insert stored.
    SELECT array_agg(i.global_position ORDER BY p.ord),
           array_agg(i.created_at      ORDER BY p.ord),
           MAX(i.global_position)
    INTO v_positions, v_created_at, v_last_position
    FROM inserted AS i
    JOIN parsed AS p ON p.ev_id = i.event_id;

    IF v_last_position IS NOT NULL THEN
        PERFORM pg_notify('$schema$_events', v_last_position::TEXT);
        RETURN QUERY
        SELECT r.global_position, r.created_at
        FROM unnest(v_positions, v_created_at) WITH ORDINALITY AS r(global_position, created_at, n)
        ORDER BY r.n;
    END IF;
END;
$$ LANGUAGE plpgsql;
