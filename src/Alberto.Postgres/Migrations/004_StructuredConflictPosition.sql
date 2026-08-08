-- Alberto DCB Event Store – 004 Structured Conflict Position (Multi-Tenant)
--
-- Replaces the four alberto_append_events functions from 003 to add a
-- machine-readable HINT field to every RAISE EXCEPTION that reports a DCB
-- conflict.  The USING ERRCODE clause gains HINT = v_conflict_position::TEXT
-- at every raise site so the position is recoverable from PostgresException.Hint
-- without parsing the prose message text.
--
-- Why a new migration rather than editing 003_AppendSetBased.sql:
-- DbUp records scripts by name and never re-runs a journaled script.  Editing a
-- filed script would silently leave the old function on every existing database.
--
-- Why HINT and not DETAIL:
-- Npgsql redacts PostgresException.Detail by default — it returns the literal
-- string "[Detail redacted as it may contain sensitive data...]" unless the
-- consumer sets "Include Error Detail=true" in the connection string.  Alberto
-- cannot require that flag: it is handed an NpgsqlDataSource the consumer built,
-- and the flag is global — it would expose constraint-violation detail for every
-- statement on that data source, not just DCB raises.  PostgresException.Hint
-- is not redacted by default, so it is the only Npgsql field that reliably
-- carries the position across standard connection-string configurations.
-- Alberto owns HINT on these raises: it carries the position and nothing else,
-- which makes reading it back unambiguous.
--
-- Why HINT and not a result column:
-- RAISE EXCEPTION aborts the statement, so there is no result set to put the
-- position in.  HINT survives the abort: Postgres populates it in the error
-- record and Npgsql surfaces it as PostgresException.Hint.
--
-- Why a bare integer, not key=value prose:
-- ERRCODE P0001 and the MessageText prefix "DCB conflict:" already identify the
-- error as a DCB conflict; adding prose to HINT would recreate the parsing
-- problem.  v_conflict_position::TEXT is the full contract: a decimal integer,
-- nothing else.
--
-- Backward compatibility:
-- Databases that have not yet run this script continue to raise the old-format
-- exception without HINT.  PostgresBackendHelpers.ToConflictException reads
-- Hint first and falls back to prose-parsing MessageText so both old and new
-- databases are handled correctly.
--
-- Multi-tenant variant: p_tenant_id is a parameter and tenant_id appears in
-- all WHERE clauses and INSERT column lists.  The function bodies are otherwise
-- identical to the single-tenant script of the same number (same base filename
-- required by MigrationUpgradeAndParityTests parity assertion).

-- ============================================================
-- alberto_append_events -- any-tag union boundary.
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
    v_conflict_position BIGINT;
    v_last_position     BIGINT;
    v_rows              $schema_prefix$alberto_events[];
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
                    USING ERRCODE = 'P0001', HINT = v_conflict_position::TEXT;
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
            (tenant_id, event_id, event_type, event_tags, event_data, event_metadata, created_at)
        SELECT p_tenant_id, ev_id, ev_type, ev_tags, ev_data, ev_meta, ev_created_at
        FROM parsed
        ORDER BY ord
        RETURNING $schema_prefix$alberto_events.*
    ),
    type_ins AS (
        INSERT INTO $schema_prefix$alberto_event_type_positions (tenant_id, event_type, global_position)
        SELECT p_tenant_id, i.event_type, i.global_position FROM inserted AS i
    ),
    tag_ins AS (
        INSERT INTO $schema_prefix$alberto_event_tag_positions (tenant_id, tag, global_position)
        SELECT p_tenant_id, UNNEST(i.event_tags) AS tag, i.global_position FROM inserted AS i
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
    v_conflict_position BIGINT;
    v_last_position     BIGINT;
    v_rows              $schema_prefix$alberto_events[];
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
                    USING ERRCODE = 'P0001', HINT = v_conflict_position::TEXT;
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
            (tenant_id, event_id, event_type, event_tags, event_data, event_metadata, created_at)
        SELECT p_tenant_id, ev_id, ev_type, ev_tags, ev_data, ev_meta, ev_created_at
        FROM parsed
        ORDER BY ord
        RETURNING $schema_prefix$alberto_events.*
    ),
    type_ins AS (
        INSERT INTO $schema_prefix$alberto_event_type_positions (tenant_id, event_type, global_position)
        SELECT p_tenant_id, i.event_type, i.global_position FROM inserted AS i
    ),
    tag_ins AS (
        INSERT INTO $schema_prefix$alberto_event_tag_positions (tenant_id, tag, global_position)
        SELECT p_tenant_id, UNNEST(i.event_tags) AS tag, i.global_position FROM inserted AS i
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
                    USING ERRCODE = 'P0001', HINT = v_conflict_position::TEXT;
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
                    USING ERRCODE = 'P0001', HINT = v_conflict_position::TEXT;
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
            (tenant_id, event_id, event_type, event_tags, event_data, event_metadata, created_at)
        SELECT p_tenant_id, ev_id, ev_type, ev_tags, ev_data, ev_meta, ev_created_at
        FROM parsed
        ORDER BY ord
        RETURNING $schema_prefix$alberto_events.*
    ),
    type_ins AS (
        INSERT INTO $schema_prefix$alberto_event_type_positions (tenant_id, event_type, global_position)
        SELECT p_tenant_id, i.event_type, i.global_position FROM inserted AS i
    ),
    tag_ins AS (
        INSERT INTO $schema_prefix$alberto_event_tag_positions (tenant_id, tag, global_position)
        SELECT p_tenant_id, UNNEST(i.event_tags) AS tag, i.global_position FROM inserted AS i
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
                    USING ERRCODE = 'P0001', HINT = v_conflict_position::TEXT;
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
                    USING ERRCODE = 'P0001', HINT = v_conflict_position::TEXT;
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
            (tenant_id, event_id, event_type, event_tags, event_data, event_metadata, created_at)
        SELECT p_tenant_id, ev_id, ev_type, ev_tags, ev_data, ev_meta, ev_created_at
        FROM parsed
        ORDER BY ord
        RETURNING $schema_prefix$alberto_events.*
    ),
    type_ins AS (
        INSERT INTO $schema_prefix$alberto_event_type_positions (tenant_id, event_type, global_position)
        SELECT p_tenant_id, i.event_type, i.global_position FROM inserted AS i
    ),
    tag_ins AS (
        INSERT INTO $schema_prefix$alberto_event_tag_positions (tenant_id, tag, global_position)
        SELECT p_tenant_id, UNNEST(i.event_tags) AS tag, i.global_position FROM inserted AS i
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
