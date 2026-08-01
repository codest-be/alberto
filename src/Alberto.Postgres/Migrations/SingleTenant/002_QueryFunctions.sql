-- Alberto DCB Event Store – 002 Query Functions (Single-Tenant)
--
-- All PostgreSQL functions for the single-tenant event store, in their final form.
-- DDL (tables, sequences, indexes) lives in 001_InitialSchema.sql.
-- Every statement is CREATE OR REPLACE FUNCTION — this script is idempotent and
-- runs on upgrade from any earlier journal state.
--
-- Rationale comments are carried forward from the migration that last changed
-- each function.

-- Alberto DCB Event Store - Migrate from Legacy (Single-Tenant)
-- Renames old un-prefixed tables to alberto_-prefixed names on existing databases.
-- Also recreates all functions with the new names and drops old function names.
-- Safe to run on fresh installs (all guards use IF EXISTS).

CREATE OR REPLACE FUNCTION $schema_prefix$alberto_read_all(
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
    FROM $schema_prefix$alberto_events e
    WHERE e.global_position > p_after_position
    ORDER BY e.global_position
    LIMIT p_limit;
END;
$$ LANGUAGE plpgsql;

CREATE OR REPLACE FUNCTION $schema_prefix$alberto_get_last_position()
RETURNS BIGINT AS $$
DECLARE
    v_position BIGINT;
BEGIN
    SELECT COALESCE(MAX(e.global_position), 0) INTO v_position
    FROM $schema_prefix$alberto_events e;

    RETURN v_position;
END;
$$ LANGUAGE plpgsql;

-- Alberto DCB Event Store - Migration 009 (Single-Tenant)
-- Query performance: eliminate DISTINCT-before-LIMIT over-scans.
--
-- SQL-1: alberto_read_by_tags multi-tag branch
--   Old: SELECT DISTINCT ... tag = ANY(p_tags) ... LIMIT p_limit
--        Forces PostgreSQL to materialise ALL matching rows across all tags before
--        deduplication, then discard all but p_limit rows.
--   New: CROSS JOIN LATERAL with LIMIT p_limit per individual tag index scan.
--        Each per-tag scan reads at most p_limit rows from the B-tree (an
--        index-only seek), then the outer DISTINCT + LIMIT p_limit deduplicates
--        the merged stream.  Correctness proof: the k-th distinct position in the
--        merged union must appear in at least one tag's sorted run at rank <= k,
--        so bounding each inner scan at p_limit rows is sufficient.
--
-- SQL-6: alberto_read_by_types_or_tags
--   Old: LEFT JOIN events + type_positions + tag_positions => DISTINCT
--        Drives from the events heap with two optional index lookups.
--   New: UNION of per-axis index-driven subqueries (each limited), then join
--        events.  Tags-only branch re-uses the already-optimised read_by_tags.
--
-- SQL-16: alberto_read_by_types_and_tags
--   Old: drives from alberto_events with a correlated EXISTS subquery into
--        alberto_event_tag_positions — one subquery execution per outer row.
--   New: INTERSECT of two index-driven subqueries, then join events.

-- ============================================================
-- SQL-1: alberto_read_by_tags  (multi-tag LATERAL rewrite)
-- ============================================================

CREATE OR REPLACE FUNCTION $schema_prefix$alberto_read_by_tags(
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
$$ LANGUAGE plpgsql;

-- Alberto DCB Event Store - Migration 021 (Single-Tenant)
--
-- Make the fenced checkpoint write atomic, and fence it on lease generation rather than on
-- replica identity. See multi-tenant 021_CheckpointFenceTokens.sql for the full rationale.
--
-- The single-tenant set already creates alberto_processor_leases, so this variant only adds the
-- generation and rewrites the function. There is no tenant-lease variant here: the two lease
-- tables never coexist in one schema.
--
-- BREAKING, in the same way as the multi-tenant variant -- the four-argument function is
-- dropped, so a replica on the previous release fails its flushes rather than writing unfenced.

CREATE OR REPLACE FUNCTION $schema_prefix$alberto_save_checkpoint_if_processor_lease_held(
    p_processor_id TEXT,
    p_consumer_id  TEXT,
    p_replica_id   TEXT,
    p_position     BIGINT,
    p_fence_token  BIGINT
) RETURNS BOOLEAN AS $$
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
$$ LANGUAGE plpgsql;

COMMENT ON FUNCTION $schema_prefix$alberto_save_checkpoint_if_processor_lease_held(TEXT, TEXT, TEXT, BIGINT, BIGINT) IS
    'Advances a checkpoint only for the replica that currently owns the processor lease, in the generation it presents. Returns false when the caller has been fenced out.';

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

-- Alberto DCB Event Store - Migration 030 (Single-Tenant)
--
-- Widens migration 029's fast path from one tag AND one type to one tag and any number
-- of types, by adding a second branch with a different plan.  See multi-tenant
-- 030_ScalarTagFastPathTypesAndTagsRead.sql for the rationale and the measured evidence:
-- in short, the blocking Sort the general path carries sits above the *tag* scan and is
-- caused by the opaque `tag = ANY($N)`, so a single tag is the right guard; but once
-- more than one type is named, testing event_type on the events row beats probing the
-- type-position index with `= ANY`, while one type still wants the scalar probe.  Two
-- branches, not one widened branch.  The only difference here is that the function
-- carries no tenant argument and the scans have no tenant predicate.

CREATE OR REPLACE FUNCTION $schema_prefix$alberto_read_by_types_and_tags(
    p_types VARCHAR(500)[],
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
$$ LANGUAGE plpgsql;

-- Alberto DCB Event Store - Migration 031 (Single-Tenant)
--
-- Removes the `= ANY` planner opacity from alberto_read_by_types.  See multi-tenant
-- 031_BoundedProbePerTypeReadByTypes.sql for the rationale and the measured evidence: in
-- short, `event_type = ANY($N)` hides the element count from the planner, so it cannot use
-- (event_type, global_position) as an ordered range scan and falls back to a seq scan, a
-- Sort and a merge join; migration 030's remedy of testing event_type on the events row does
-- NOT transfer, because this function has no tag axis to bound that scan and the shape
-- degrades to reading the whole log exactly when the named type is rare; and one bounded
-- scalar probe per named type is fast at every type count and has no such edge.
--
-- The DISTINCT over unnest is load-bearing rather than tidy: DcbQuery does not deduplicate
-- types, and a repeated type would otherwise run its probe twice and return each position
-- twice, which the `= ANY` form never did.
--
-- The only difference here is that the function carries no tenant argument and the probe has
-- no tenant predicate.

CREATE OR REPLACE FUNCTION $schema_prefix$alberto_read_by_types(
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
$$ LANGUAGE plpgsql;

-- Alberto DCB Event Store - Migration 033 (Single-Tenant)
--
-- Removes the unbounded tag-axis scans from alberto_read_by_all_tags,
-- alberto_read_by_types_and_all_tags, alberto_read_by_types_or_all_tags and
-- alberto_read_by_types_or_tags.  See multi-tenant 033_BoundedTagAxisReads.sql for the
-- rationale and the measured evidence.  In short: each of these four put a blocking node -- a
-- GROUP BY, a UNION or a DISTINCT -- above a `tag = ANY($N)` scan the planner cannot recognise
-- as ordered, so the LIMIT could not stop it early and cost was set by how many events carry
-- the tag rather than by how many the caller asked for.  The union shapes are fixed by bounding
-- each arm with its own scalar probe and LIMIT, as 031 does on the type axis; the all-tags
-- shapes are fixed by driving off one tag and testing the rest with EXISTS, since every match
-- must carry every named tag; and the driving tag is chosen at runtime by
-- alberto_pick_all_tags_driver, because a pooled connection gets a generic plan and the planner
-- therefore has no tag values to judge selectivity from.
--
-- Two behaviour changes, both deliberate and both described in full in the multi-tenant script:
-- a duplicated tag (ByAllTags("a").WithTags("a") arrives as {a,a}) now matches instead of
-- returning nothing, and the types-only branch of alberto_read_by_types_or_tags delegates to
-- alberto_read_by_types rather than carrying its own copy of the `= ANY` scan.
--
-- The only difference here is that the functions carry no tenant argument and the scans have no
-- tenant predicate.  The measurements in the multi-tenant script were taken on this schema.

-- ---------------------------------------------------------------------------------------------
-- Driver selection for the all-tags shapes.
-- ---------------------------------------------------------------------------------------------

CREATE OR REPLACE FUNCTION $schema_prefix$alberto_pick_all_tags_driver(
    p_tags VARCHAR(500)[],
    p_after_position BIGINT,
    p_limit INT
)
RETURNS VARCHAR(500) AS $$
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
$$ LANGUAGE plpgsql;

-- ---------------------------------------------------------------------------------------------
-- All tags, no type axis.
-- ---------------------------------------------------------------------------------------------

CREATE OR REPLACE FUNCTION $schema_prefix$alberto_read_by_all_tags(
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
$$ LANGUAGE plpgsql;

-- ---------------------------------------------------------------------------------------------
-- All tags AND any of several types.
-- ---------------------------------------------------------------------------------------------

CREATE OR REPLACE FUNCTION $schema_prefix$alberto_read_by_types_and_all_tags(
    p_types VARCHAR(500)[],
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
$$ LANGUAGE plpgsql;

-- ---------------------------------------------------------------------------------------------
-- Any of several types OR all tags.
-- ---------------------------------------------------------------------------------------------

CREATE OR REPLACE FUNCTION $schema_prefix$alberto_read_by_types_or_all_tags(
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
$$ LANGUAGE plpgsql;

-- ---------------------------------------------------------------------------------------------
-- Any of several types OR any of several tags.
-- ---------------------------------------------------------------------------------------------

CREATE OR REPLACE FUNCTION $schema_prefix$alberto_read_by_types_or_tags(
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
$$ LANGUAGE plpgsql;
