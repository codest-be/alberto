-- Alberto DCB Event Store – 003 Append Set-Based (Single-Tenant)
--
-- Rewrites the four alberto_append_events functions from a row-by-row PL/pgSQL
-- loop (one INSERT per event) to a single data-modifying CTE that inserts all
-- events in one batch.
--
-- Single-tenant variant: no tenant identifier on any table or parameter.
-- The insert body is otherwise identical to the multi-tenant script of the
-- same number, and this script must keep the same base filename as that one
-- (required by MigrationUpgradeAndParityTests parity assertion).
--
-- Delivery: DbUp journals scripts by name and runs each once.  002_QueryFunctions.sql
-- is already recorded in existing databases, so this separate file is required
-- to update those databases.  On a fresh install DbUp runs both scripts in order.
--
-- CTE execution guarantee: PostgreSQL always executes data-modifying CTEs to
-- completion regardless of whether they are referenced by the outer query.
-- type_ins and tag_ins therefore need no RETURNING clause.
--
-- Position ordering: INSERT … SELECT … ORDER BY ord assigns sequence values in
-- JSON array order.  Returned rows come directly from the INSERT RETURNING output
-- materialised into v_rows, not from a re-read of alberto_events.  For symmetry
-- with the multi-tenant variant (where the per-tenant advisory lock allows
-- concurrent appends to interleave on the shared sequence), both variants use
-- the same RETURNING-output pattern rather than a positional range re-read.
--
-- Empty-input contract: when p_events is '[]', no rows are inserted,
-- v_last_position remains NULL, and pg_notify is not called.

-- ============================================================
-- alberto_append_events -- any-tag union boundary.
-- ============================================================

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
    v_conflict_position BIGINT;
    v_last_position     BIGINT;
    v_rows              $schema_prefix$alberto_events[];
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

    WITH parsed AS (
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
    SELECT array_agg(i ORDER BY i.global_position), MAX(i.global_position)
    INTO v_rows, v_last_position
    FROM inserted AS i;

    -- One notification per append call with the last position written.
    -- Guarded so nothing is emitted when p_events was empty (v_last_position is NULL).
    IF v_last_position IS NOT NULL THEN
        PERFORM pg_notify('$schema$_events', v_last_position::TEXT);
        RETURN QUERY
        SELECT r.global_position, r.event_id, r.event_type, r.event_tags,
               r.event_data, r.event_metadata, r.created_at
        FROM unnest(v_rows) AS r
        ORDER BY r.global_position;
    END IF;
END;
$$ LANGUAGE plpgsql;

-- ============================================================
-- alberto_append_events_v3 -- all-tags intersection boundary.
-- ============================================================

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
    v_conflict_position BIGINT;
    v_last_position     BIGINT;
    v_rows              $schema_prefix$alberto_events[];
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

    WITH parsed AS (
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
    SELECT array_agg(i ORDER BY i.global_position), MAX(i.global_position)
    INTO v_rows, v_last_position
    FROM inserted AS i;

    IF v_last_position IS NOT NULL THEN
        PERFORM pg_notify('$schema$_events', v_last_position::TEXT);
        RETURN QUERY
        SELECT r.global_position, r.event_id, r.event_type, r.event_tags,
               r.event_data, r.event_metadata, r.created_at
        FROM unnest(v_rows) AS r
        ORDER BY r.global_position;
    END IF;
END;
$$ LANGUAGE plpgsql;

-- ============================================================
-- alberto_append_events_v4 -- types AND any-tag boundary.
-- ============================================================

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
    v_conflict_position BIGINT;
    v_last_position     BIGINT;
    v_rows              $schema_prefix$alberto_events[];
    v_has_types BOOLEAN := p_dcb_types IS NOT NULL AND array_length(p_dcb_types, 1) > 0;
    v_has_tags  BOOLEAN := p_dcb_tags  IS NOT NULL AND array_length(p_dcb_tags,  1) > 0;
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

    WITH parsed AS (
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
    SELECT array_agg(i ORDER BY i.global_position), MAX(i.global_position)
    INTO v_rows, v_last_position
    FROM inserted AS i;

    IF v_last_position IS NOT NULL THEN
        PERFORM pg_notify('$schema$_events', v_last_position::TEXT);
        RETURN QUERY
        SELECT r.global_position, r.event_id, r.event_type, r.event_tags,
               r.event_data, r.event_metadata, r.created_at
        FROM unnest(v_rows) AS r
        ORDER BY r.global_position;
    END IF;
END;
$$ LANGUAGE plpgsql;

-- ============================================================
-- alberto_append_events_v6 -- types AND all-tags boundary.
-- ============================================================

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
    v_conflict_position BIGINT;
    v_last_position     BIGINT;
    v_rows              $schema_prefix$alberto_events[];
    v_has_types BOOLEAN := p_dcb_types    IS NOT NULL AND array_length(p_dcb_types,    1) > 0;
    v_has_tags  BOOLEAN := p_dcb_all_tags IS NOT NULL AND array_length(p_dcb_all_tags, 1) > 0;
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

    WITH parsed AS (
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
    SELECT array_agg(i ORDER BY i.global_position), MAX(i.global_position)
    INTO v_rows, v_last_position
    FROM inserted AS i;

    IF v_last_position IS NOT NULL THEN
        PERFORM pg_notify('$schema$_events', v_last_position::TEXT);
        RETURN QUERY
        SELECT r.global_position, r.event_id, r.event_type, r.event_tags,
               r.event_data, r.event_metadata, r.created_at
        FROM unnest(v_rows) AS r
        ORDER BY r.global_position;
    END IF;
END;
$$ LANGUAGE plpgsql;
