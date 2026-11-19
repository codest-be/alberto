-- Alberto DCB Event Store – 009 Bounded Intersect Conflict Check (Single-Tenant)
--
-- Redefines alberto_append_events_v4 (types AND any tag) and alberto_append_events_v6
-- (types AND all tags). Only the types-AND-tags conflict check changes; signatures, the
-- types-only and tags-only branches, the insert, the raise text, ERRCODE and HINT are
-- copied unchanged from 004.
--
-- _v4: the check drove from alberto_events (global_position > expected, event_type = ANY)
-- with an EXISTS into the tag positions that was not bounded by the expected position.
-- After five calls on a connection plpgsql switches to a generic plan, which became a merge
-- semi join walking the events primary key from the expected position up to the tag's newest
-- event, all under the append advisory lock. Measured on 2M events, expected position 0, a
-- tag with 400 events of other types: 1.6-3.3 s per append. The rewrite drives from the
-- (tag, global_position) primary key, bounded by the expected position, and probes
-- (event_type, global_position) per candidate: 1.07 ms for the same input, generic plan.
-- ORDER BY global_position LIMIT 1 still yields the lowest conflicting position; a position
-- that carries several of the listed tags appears more than once in the scan, which the
-- LIMIT makes harmless.
--
-- _v6: the tag side was already bounded, but the type filter joined alberto_events. Every
-- append writes exactly one alberto_event_type_positions row per event, with the same
-- type, so an EXISTS against that index is equivalent and never touches the
-- wide events table.
--
-- codest-be/alberto#266.
--
-- Single-tenant variant: no tenant identifier anywhere. Otherwise identical to the
-- multi-tenant script of the same name (MigrationUpgradeAndParityTests requires the match).

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
