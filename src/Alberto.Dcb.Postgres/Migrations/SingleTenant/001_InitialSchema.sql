-- Alberto DCB Event Store - Initial Schema (Single-Tenant)
-- Complete final-state schema with alberto_ prefix on all tables and functions.
-- No tenant_id columns - single tenant per schema.

-- Create schema if not using public
CREATE SCHEMA IF NOT EXISTS $schema$;

-- ============================================================
-- TABLES
-- ============================================================

-- Events table (no tenant_id)
CREATE TABLE IF NOT EXISTS $schema_prefix$alberto_events (
    global_position   BIGSERIAL PRIMARY KEY,
    event_id          UUID NOT NULL DEFAULT gen_random_uuid(),
    event_type        VARCHAR(500) NOT NULL,
    event_tags        VARCHAR(500)[] NOT NULL DEFAULT '{}',
    event_data        JSONB NOT NULL DEFAULT '{}',
    event_metadata    JSONB NOT NULL DEFAULT '{}',
    created_at        TIMESTAMPTZ NOT NULL DEFAULT now(),
    UNIQUE (event_id)
);

-- Inverted index for event types
CREATE TABLE IF NOT EXISTS $schema_prefix$alberto_event_type_positions (
    event_type        VARCHAR(500) NOT NULL,
    global_position   BIGINT NOT NULL REFERENCES $schema_prefix$alberto_events(global_position) ON DELETE CASCADE,
    PRIMARY KEY (event_type, global_position)
);

-- Inverted index for event tags
CREATE TABLE IF NOT EXISTS $schema_prefix$alberto_event_tag_positions (
    tag               VARCHAR(500) NOT NULL,
    global_position   BIGINT NOT NULL REFERENCES $schema_prefix$alberto_events(global_position) ON DELETE CASCADE,
    PRIMARY KEY (tag, global_position)
);

-- Processor checkpoints
CREATE TABLE IF NOT EXISTS $schema_prefix$alberto_processor_checkpoints (
    processor_id      VARCHAR(200) PRIMARY KEY,
    last_position     BIGINT NOT NULL,
    updated_at        TIMESTAMPTZ NOT NULL DEFAULT now()
);

-- Projection states (with rebuild_version support)
CREATE TABLE IF NOT EXISTS $schema_prefix$alberto_projection_states (
    projection_type   TEXT NOT NULL,
    document_id       TEXT NOT NULL,
    rebuild_version   INT NOT NULL DEFAULT 1,
    state             JSONB NOT NULL,
    updated_at        TIMESTAMPTZ NOT NULL DEFAULT now(),
    PRIMARY KEY (projection_type, document_id, rebuild_version)
);

CREATE INDEX IF NOT EXISTS idx_alberto_projection_states_type
    ON $schema_prefix$alberto_projection_states(projection_type);

CREATE INDEX IF NOT EXISTS idx_alberto_projection_states_version
    ON $schema_prefix$alberto_projection_states(projection_type, rebuild_version);

CREATE INDEX IF NOT EXISTS idx_alberto_projection_states_type_version_cleanup
    ON $schema_prefix$alberto_projection_states(projection_type, rebuild_version)
    WHERE rebuild_version > 0;

COMMENT ON COLUMN $schema_prefix$alberto_projection_states.rebuild_version IS 'Version number for zero-downtime rebuilds. Active version is tracked in alberto_projection_rebuild_meta.';

-- Projection rebuild metadata for zero-downtime rebuilds
CREATE TABLE IF NOT EXISTS $schema_prefix$alberto_projection_rebuild_meta (
    projection_type       TEXT PRIMARY KEY,
    active_version        INT NOT NULL DEFAULT 1,
    rebuilding_version    INT,
    rebuild_status        TEXT,
    rebuild_started_at    TIMESTAMPTZ,
    rebuild_target_position BIGINT,
    rebuild_completed_at  TIMESTAMPTZ
);

COMMENT ON TABLE $schema_prefix$alberto_projection_rebuild_meta IS 'Tracks active and rebuilding versions for zero-downtime projection rebuilds.';

-- Dead letter events (with global_position and retry_requested)
CREATE TABLE IF NOT EXISTS $schema_prefix$alberto_dead_letter_events (
    id                UUID PRIMARY KEY,
    processor_id      VARCHAR(200) NOT NULL,
    event_id          UUID NOT NULL,
    event_type        VARCHAR(200) NOT NULL,
    event_data        JSONB NOT NULL,
    error_message     TEXT NOT NULL,
    stack_trace       TEXT,
    attempt_count     INT NOT NULL,
    failed_at         TIMESTAMPTZ NOT NULL,
    global_position   BIGINT NOT NULL DEFAULT 0,
    retry_requested   BOOLEAN NOT NULL DEFAULT FALSE
);

CREATE INDEX IF NOT EXISTS idx_alberto_dead_letter_processor ON $schema_prefix$alberto_dead_letter_events(processor_id);
CREATE INDEX IF NOT EXISTS idx_alberto_dead_letter_failed_at ON $schema_prefix$alberto_dead_letter_events(failed_at DESC);

-- Transactional outbox
CREATE TABLE IF NOT EXISTS $schema_prefix$alberto_outbox_entries (
    id UUID PRIMARY KEY DEFAULT gen_random_uuid(),
    source_event_id UUID NOT NULL,
    message_type TEXT NOT NULL,
    version TEXT NOT NULL DEFAULT '1',
    payload JSONB NOT NULL,
    metadata JSONB NOT NULL DEFAULT '{}',
    status TEXT NOT NULL DEFAULT 'pending' CHECK (status IN ('pending', 'processing', 'delivered', 'failed')),
    retry_count INT NOT NULL DEFAULT 0,
    last_error TEXT,
    created_at TIMESTAMPTZ NOT NULL DEFAULT now(),
    delivered_at TIMESTAMPTZ,
    UNIQUE (source_event_id)
);

CREATE INDEX IF NOT EXISTS idx_alberto_outbox_pending
    ON $schema_prefix$alberto_outbox_entries (created_at) WHERE status = 'pending';

-- Processor leases (single-tenant only)
CREATE TABLE IF NOT EXISTS $schema_prefix$alberto_processor_leases (
    consumer_id   TEXT        NOT NULL,
    processor_id  TEXT        NOT NULL,
    replica_id    TEXT        NOT NULL,
    acquired_at   TIMESTAMPTZ NOT NULL DEFAULT now(),
    expires_at    TIMESTAMPTZ NOT NULL,
    PRIMARY KEY (consumer_id, processor_id)
);

CREATE INDEX IF NOT EXISTS ix_alberto_processor_leases_replica
    ON $schema_prefix$alberto_processor_leases (replica_id);

-- ============================================================
-- FUNCTIONS
-- ============================================================

-- Append events with optional DCB conflict check (v1)
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
    -- Perform DCB conflict check if expected position is provided
    IF p_expected_position IS NOT NULL THEN
        -- Check for conflicts by types
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

        -- Check for conflicts by tags
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

    -- Insert each event
    FOR v_event IN SELECT * FROM jsonb_array_elements(p_events)
    LOOP
        v_event_id := COALESCE((v_event->>'event_id')::UUID, gen_random_uuid());
        v_event_type := v_event->>'event_type';
        v_event_tags := ARRAY(SELECT jsonb_array_elements_text(COALESCE(v_event->'event_tags', '[]'::JSONB)));
        v_event_data := COALESCE(v_event->'event_data', '{}'::JSONB);
        v_event_metadata := COALESCE(v_event->'event_metadata', '{}'::JSONB);
        v_created_at := now();

        -- Insert into events table
        INSERT INTO $schema_prefix$alberto_events (event_id, event_type, event_tags, event_data, event_metadata, created_at)
        VALUES (v_event_id, v_event_type, v_event_tags, v_event_data, v_event_metadata, v_created_at)
        RETURNING $schema_prefix$alberto_events.global_position INTO v_new_position;

        -- Update type inverted index
        INSERT INTO $schema_prefix$alberto_event_type_positions (event_type, global_position)
        VALUES (v_event_type, v_new_position);

        -- Update tag inverted index
        FOREACH v_tag IN ARRAY v_event_tags
        LOOP
            INSERT INTO $schema_prefix$alberto_event_tag_positions (tag, global_position)
            VALUES (v_tag, v_new_position);
        END LOOP;

        -- Return the inserted event
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

-- Append events with DCB conflict check supporting wildcard patterns (v2)
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

-- Append events with DCB conflict check for all-tags queries (v3)
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
END;
$$ LANGUAGE plpgsql;

-- Read all events
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

-- Read events by types (OR logic)
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
    FROM $schema_prefix$alberto_events e
    INNER JOIN $schema_prefix$alberto_event_type_positions etp ON e.global_position = etp.global_position
    WHERE etp.event_type = ANY(p_types)
      AND e.global_position > p_after_position
    ORDER BY e.global_position
    LIMIT p_limit;
END;
$$ LANGUAGE plpgsql;

-- Read events by exact tags (OR logic)
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

    RETURN QUERY
    SELECT e.global_position, e.event_id, e.event_type, e.event_tags, e.event_data, e.event_metadata, e.created_at
    FROM (
        SELECT DISTINCT etagp.global_position
        FROM $schema_prefix$alberto_event_tag_positions etagp
        WHERE etagp.tag = ANY(p_tags)
          AND etagp.global_position > p_after_position
        ORDER BY etagp.global_position
        LIMIT p_limit
    ) matching_positions
    INNER JOIN $schema_prefix$alberto_events e ON e.global_position = matching_positions.global_position
    ORDER BY matching_positions.global_position;
END;
$$ LANGUAGE plpgsql;

-- Read events by types OR tags (DCB query)
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
BEGIN
    RETURN QUERY
    SELECT DISTINCT e.global_position, e.event_id, e.event_type, e.event_tags, e.event_data, e.event_metadata, e.created_at
    FROM $schema_prefix$alberto_events e
    LEFT JOIN $schema_prefix$alberto_event_type_positions etp ON e.global_position = etp.global_position
    LEFT JOIN $schema_prefix$alberto_event_tag_positions etagp ON e.global_position = etagp.global_position
    WHERE e.global_position > p_after_position
      AND (
          (p_types IS NOT NULL AND array_length(p_types, 1) > 0 AND etp.event_type = ANY(p_types))
          OR (p_tags IS NOT NULL AND array_length(p_tags, 1) > 0 AND etagp.tag = ANY(p_tags))
      )
    ORDER BY e.global_position
    LIMIT p_limit;
END;
$$ LANGUAGE plpgsql;

-- Read events by tag patterns (exact and/or prefix wildcards)
CREATE OR REPLACE FUNCTION $schema_prefix$alberto_read_by_tag_patterns(
    p_exact_tags VARCHAR(500)[] DEFAULT NULL,
    p_tag_prefixes VARCHAR(500)[] DEFAULT NULL,
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
    v_has_exact BOOLEAN := p_exact_tags IS NOT NULL AND array_length(p_exact_tags, 1) > 0;
    v_has_prefix BOOLEAN := p_tag_prefixes IS NOT NULL AND array_length(p_tag_prefixes, 1) > 0;
BEGIN
    IF NOT v_has_exact AND NOT v_has_prefix THEN
        RETURN;
    END IF;

    RETURN QUERY
    SELECT DISTINCT e.global_position, e.event_id, e.event_type, e.event_tags, e.event_data, e.event_metadata, e.created_at
    FROM $schema_prefix$alberto_events e
    INNER JOIN $schema_prefix$alberto_event_tag_positions etagp ON e.global_position = etagp.global_position
    WHERE e.global_position > p_after_position
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

-- Read events by types OR tag patterns (wildcards)
CREATE OR REPLACE FUNCTION $schema_prefix$alberto_read_by_types_or_tag_patterns(
    p_types VARCHAR(500)[] DEFAULT NULL,
    p_exact_tags VARCHAR(500)[] DEFAULT NULL,
    p_tag_prefixes VARCHAR(500)[] DEFAULT NULL,
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
    v_has_exact BOOLEAN := p_exact_tags IS NOT NULL AND array_length(p_exact_tags, 1) > 0;
    v_has_prefix BOOLEAN := p_tag_prefixes IS NOT NULL AND array_length(p_tag_prefixes, 1) > 0;
BEGIN
    RETURN QUERY
    SELECT DISTINCT e.global_position, e.event_id, e.event_type, e.event_tags, e.event_data, e.event_metadata, e.created_at
    FROM $schema_prefix$alberto_events e
    LEFT JOIN $schema_prefix$alberto_event_type_positions etp ON e.global_position = etp.global_position
    LEFT JOIN $schema_prefix$alberto_event_tag_positions etagp ON e.global_position = etagp.global_position
    WHERE e.global_position > p_after_position
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

-- Read events matching all specified tags (AND logic)
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
BEGIN
    RETURN QUERY
    SELECT e.global_position, e.event_id, e.event_type, e.event_tags, e.event_data, e.event_metadata, e.created_at
    FROM $schema_prefix$alberto_events e
    INNER JOIN (
        SELECT etagp.global_position
        FROM $schema_prefix$alberto_event_tag_positions etagp
        WHERE etagp.tag = ANY(p_tags)
          AND etagp.global_position > p_after_position
        GROUP BY etagp.global_position
        HAVING COUNT(DISTINCT etagp.tag) = array_length(p_tags, 1)
    ) matching_positions ON e.global_position = matching_positions.global_position
    ORDER BY e.global_position
    LIMIT p_limit;
END;
$$ LANGUAGE plpgsql;

-- Read events by types OR all-tags match
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
BEGIN
    RETURN QUERY
    SELECT DISTINCT e.global_position, e.event_id, e.event_type, e.event_tags, e.event_data, e.event_metadata, e.created_at
    FROM $schema_prefix$alberto_events e
    LEFT JOIN $schema_prefix$alberto_event_type_positions etp ON e.global_position = etp.global_position
    LEFT JOIN (
        SELECT etagp.global_position
        FROM $schema_prefix$alberto_event_tag_positions etagp
        WHERE p_tags IS NOT NULL
          AND array_length(p_tags, 1) > 0
          AND etagp.tag = ANY(p_tags)
          AND etagp.global_position > p_after_position
        GROUP BY etagp.global_position
        HAVING COUNT(DISTINCT etagp.tag) = array_length(p_tags, 1)
    ) matching_positions ON e.global_position = matching_positions.global_position
    WHERE e.global_position > p_after_position
      AND (
          (p_types IS NOT NULL AND array_length(p_types, 1) > 0 AND etp.event_type = ANY(p_types))
          OR matching_positions.global_position IS NOT NULL
      )
    ORDER BY e.global_position
    LIMIT p_limit;
END;
$$ LANGUAGE plpgsql;

-- Get last position
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

-- ============================================================
-- NOTIFICATION FUNCTIONS
-- ============================================================

-- Trigger function to notify on event append
CREATE OR REPLACE FUNCTION $schema_prefix$alberto_notify_events()
RETURNS TRIGGER AS $$
BEGIN
    PERFORM pg_notify('$schema$_events', NEW.global_position::TEXT);
    RETURN NEW;
END;
$$ LANGUAGE plpgsql;

-- Trigger function to notify on checkpoint update
CREATE OR REPLACE FUNCTION $schema_prefix$alberto_notify_checkpoint()
RETURNS TRIGGER AS $$
BEGIN
    PERFORM pg_notify('$schema$_checkpoints', NEW.processor_id || ':' || NEW.last_position::TEXT);
    RETURN NEW;
END;
$$ LANGUAGE plpgsql;

-- Trigger function to notify on dead letter insert
CREATE OR REPLACE FUNCTION $schema_prefix$alberto_notify_dead_letter_insert()
RETURNS TRIGGER AS $$
BEGIN
    PERFORM pg_notify('$schema$_dead_letters', 'added:' || NEW.processor_id);
    RETURN NEW;
END;
$$ LANGUAGE plpgsql;

-- Trigger function to notify on dead letter delete
CREATE OR REPLACE FUNCTION $schema_prefix$alberto_notify_dead_letter_delete()
RETURNS TRIGGER AS $$
BEGIN
    PERFORM pg_notify('$schema$_dead_letters', 'removed:' || OLD.processor_id);
    RETURN OLD;
END;
$$ LANGUAGE plpgsql;

-- ============================================================
-- FENCED CHECKPOINT FUNCTION
-- ============================================================

-- Save checkpoint only if the processor lease is still held (prevents zombie writes)
-- Uses UPSERT so the first checkpoint write for a new processor creates the row.
CREATE OR REPLACE FUNCTION $schema_prefix$alberto_save_checkpoint_if_processor_lease_held(
    p_processor_id TEXT,
    p_consumer_id TEXT,
    p_replica_id TEXT,
    p_position BIGINT
) RETURNS BOOLEAN AS $$
DECLARE
    v_lease_held BOOLEAN;
BEGIN
    SELECT EXISTS (
        SELECT 1 FROM $schema_prefix$alberto_processor_leases
        WHERE consumer_id = p_consumer_id
        AND processor_id = p_processor_id
        AND replica_id = p_replica_id
        AND expires_at > now()
    ) INTO v_lease_held;

    IF NOT v_lease_held THEN
        RETURN FALSE;
    END IF;

    INSERT INTO $schema_prefix$alberto_processor_checkpoints (processor_id, last_position, updated_at)
    VALUES (p_processor_id, p_position, now())
    ON CONFLICT (processor_id) DO UPDATE
    SET last_position = GREATEST($schema_prefix$alberto_processor_checkpoints.last_position, p_position),
        updated_at = now();

    RETURN TRUE;
END;
$$ LANGUAGE plpgsql;

-- ============================================================
-- TRIGGERS
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
