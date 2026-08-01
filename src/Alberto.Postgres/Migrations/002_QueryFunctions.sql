-- Alberto DCB Event Store – 002 Query Functions (Multi-Tenant)
--
-- All PostgreSQL functions for the multi-tenant event store, in their final form.
-- DDL (tables, sequences, indexes, trigger) lives in 001_InitialSchema.sql.
-- Every statement is CREATE OR REPLACE FUNCTION — this script is idempotent and
-- runs on upgrade from any earlier journal state.
--
-- Rationale comments are carried forward from the migration that last changed
-- each function.

-- Alberto DCB Event Store - Migrate from Legacy (Multi-Tenant)
-- Renames old un-prefixed tables to alberto_-prefixed names on existing databases.
-- Also recreates all functions with the new names and drops old function names.
-- Safe to run on fresh installs (all guards use IF EXISTS).

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

-- Alberto DCB Event Store - Migration 009 (Multi-Tenant)
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
--        Drives from the events heap with two optional index lookups, requiring
--        PostgreSQL to scan/filter the events table before deduplication.
--   New: UNION of per-axis index-driven subqueries (each limited), then join
--        events.  Types-only and tags-only branches short-cut to a single index
--        scan; the tags-only branch re-uses the already-optimised read_by_tags.
--
-- SQL-16: alberto_read_by_types_and_tags
--   Old: drives from alberto_events with a correlated EXISTS subquery into
--        alberto_event_tag_positions — one subquery execution per outer row.
--   New: INTERSECT of two index-driven subqueries (types index ∩ tags index),
--        then join events for the full row.  Both inputs are sorted by position
--        so PostgreSQL can satisfy INTERSECT with a merge join, reading each
--        index once.

-- ============================================================
-- SQL-1: alberto_read_by_tags  (multi-tag LATERAL rewrite)
-- ============================================================

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
    IF p_tags IS NULL OR array_length(p_tags, 1) IS NULL THEN
        RETURN;
    END IF;

    -- Single-tag fast path: one index seek, no deduplication needed.
    IF array_length(p_tags, 1) = 1 THEN
        RETURN QUERY
        SELECT e.global_position, e.tenant_id, e.event_id, e.event_type, e.event_tags, e.event_data, e.event_metadata, e.created_at
        FROM (
            SELECT etagp.global_position
            FROM $schema_prefix$alberto_event_tag_positions etagp
            WHERE etagp.tenant_id = p_tenant_id
              AND etagp.tag = p_tags[1]
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
    -- is at most array_length(p_tags) * p_limit — versus an unbounded scan in the
    -- old DISTINCT ... tag = ANY(p_tags) form.
    RETURN QUERY
    SELECT e.global_position, e.tenant_id, e.event_id, e.event_type, e.event_tags, e.event_data, e.event_metadata, e.created_at
    FROM (
        SELECT DISTINCT sq.global_position AS gp
        FROM unnest(p_tags) AS t(tag)
        CROSS JOIN LATERAL (
            SELECT etagp.global_position
            FROM $schema_prefix$alberto_event_tag_positions etagp
            WHERE etagp.tenant_id = p_tenant_id
              AND etagp.tag = t.tag
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

-- Alberto DCB Event Store - Migration 012 (Multi-Tenant)
-- SQL-2: Add alberto_tenants catalog table maintained by a trigger on
--         alberto_events, so GetKnownTenantsAsync can query the catalog instead
--         of doing a full-table SELECT DISTINCT tenant_id.
--
-- P1.2 (TEN-8): Add tenant_id column to alberto_dead_letter_events and
--               alberto_outbox_entries so dead letters and outbox entries can be
--               filtered and isolated per tenant.

-- ============================================================
-- SQL-2: alberto_tenants catalog table
-- ============================================================

-- Catalog table: one row per known tenant, appended on first event for that
-- tenant and updated (last_seen_at) on every subsequent event batch.

-- Trigger function: upsert each distinct tenant_id seen in the new batch.
-- Statement-level so it fires once per INSERT statement, not once per row.

CREATE OR REPLACE FUNCTION $schema_prefix$alberto_maintain_tenants_batch()
RETURNS TRIGGER AS $$
BEGIN
    INSERT INTO $schema_prefix$alberto_tenants (tenant_id, first_seen_at, last_seen_at)
    SELECT DISTINCT tenant_id, now(), now()
    FROM new_table
    ON CONFLICT (tenant_id) DO UPDATE
        SET last_seen_at = now();
    RETURN NULL;
END;
$$ LANGUAGE plpgsql;

-- Alberto DCB Event Store - Migration 021 (Multi-Tenant)
--
-- Make the fenced checkpoint write atomic, and fence it on lease generation rather than on
-- replica identity.
--
-- Two defects, both in alberto_save_checkpoint_if_lease_held and its processor-lease twin.
--
-- 1. The lease check and the checkpoint write were separate statements. plpgsql runs them in
--    the caller's transaction, and under READ COMMITTED each statement takes its own snapshot,
--    so a lease that changes hands between the two is not seen by the second. The write lands
--    on behalf of a replica that no longer owns the processor.
--
-- 2. Even without that window, verifying the replica identity does not establish ownership over
--    time. A replica can lose its lease, watch another replica take over and advance the
--    checkpoint, and then re-acquire the same lease. A write it had already prepared under its
--    earlier ownership matches on identity, is accepted, and — because the upsert is monotonic
--    (GREATEST) — drags the checkpoint past everything the intervening owner processed. Those
--    events are then never delivered, and nothing recovers automatically: the checkpoint only
--    moves backwards through the operator's rewind.
--
-- The fix gives every acquisition of a lease a generation number drawn from a sequence, records
-- it on the lease, and has the writer present the generation it acquired. The checkpoint row
-- remembers the highest generation that has written to it, so a superseded generation is locked
-- out permanently rather than only until it happens to re-acquire. Check and write become one
-- statement, which closes the window in (1) and makes the checkpoint row itself the arbiter:
-- ON CONFLICT DO UPDATE re-reads the live row, so of two writers racing, the second sees the
-- first's generation.
--
-- The generation comes from a sequence rather than a per-row counter because releasing a lease
-- deletes its row. A counter would restart, and a write held over from before the release would
-- match again.
--
-- Also creates alberto_processor_leases here. It existed only in the single-tenant migration
-- set, while the control loop enables processor-lease fencing in both modes and the lease
-- manager is registered for every module regardless of tenancy — so a multi-tenant deployment
-- with leases enabled was failing on "relation alberto_processor_leases does not exist".
--
-- BREAKING. The old four-argument functions are dropped rather than left alongside the new
-- five-argument ones, so a replica still running the previous release fails its checkpoint
-- flushes instead of silently writing unfenced. That is the safe direction — such a replica
-- stops rather than advancing a checkpoint it no longer owns — but it does mean the two
-- releases cannot both write during a rolling upgrade.

-- ============================================================
-- PROCESSOR LEASES (mirrors the single-tenant schema)
-- ============================================================

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

-- The tenant-lease variant becomes a single statement too, but it takes no generation. Tenant
-- leases are keyed on (consumer_id, tenant_id), so a replica holds many of them and none of
-- them names an owner of this processor -- the check asks only whether the replica holds some
-- tenant under this consumer, which two replicas can satisfy at once. It fences against a
-- replica that has stopped renewing entirely; it does not decide between live replicas.
-- Processor-lease fencing is what the control loop uses and is the one to prefer.

CREATE OR REPLACE FUNCTION $schema_prefix$alberto_save_checkpoint_if_lease_held(
    p_processor_id TEXT,
    p_consumer_id  TEXT,
    p_replica_id   TEXT,
    p_position     BIGINT
) RETURNS BOOLEAN AS $$
DECLARE
    v_written INTEGER;
BEGIN
    INSERT INTO $schema_prefix$alberto_processor_checkpoints
        (processor_id, last_position, updated_at)
    SELECT p_processor_id, p_position, now()
    WHERE EXISTS (
        SELECT 1 FROM $schema_prefix$alberto_tenant_leases
        WHERE consumer_id = p_consumer_id
          AND replica_id  = p_replica_id
          AND expires_at  > now()
    )
    ON CONFLICT (processor_id) DO UPDATE
    SET last_position = GREATEST(
            $schema_prefix$alberto_processor_checkpoints.last_position,
            EXCLUDED.last_position),
        updated_at    = now();

    GET DIAGNOSTICS v_written = ROW_COUNT;
    RETURN v_written > 0;
END;
$$ LANGUAGE plpgsql;

-- Alberto DCB Event Store - Migration 025 (Multi-Tenant)
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
    v_has_types BOOLEAN := p_dcb_types IS NOT NULL AND array_length(p_dcb_types, 1) > 0;
    v_has_tags BOOLEAN := p_dcb_tags IS NOT NULL AND array_length(p_dcb_tags, 1) > 0;
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
            WHERE etp.tenant_id = p_tenant_id
              AND etp.event_type = ANY(p_dcb_types)
              AND etp.global_position > p_expected_position
            LIMIT 1;

            IF v_conflict_position IS NOT NULL THEN
                RAISE EXCEPTION 'DCB conflict: event type found at position %', v_conflict_position
                    USING ERRCODE = 'P0001';
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

-- Alberto DCB Event Store - Migration 030 (Multi-Tenant)
--
-- Widens migration 029's fast path from one tag AND one type to one tag and any
-- number of types, by adding a second branch with a different plan.
--
-- 029 shipped the narrow guard on evidence that could not have supported the wider
-- one.  The benchmark corpus it was measured against held three event types, so the
-- multi-element case it compared against — one tag, three types — named every type in
-- the store.  A predicate that matches everything cannot show what filtering costs.
-- The corpus now holds twenty types, which makes the named type count a selectivity
-- knob, and one tag against three of twenty is the shape this migration rests on.
--
-- What 029 got right is that the blocking Sort the general path carries sits above the
-- *tag* scan, and is there because `tag = ANY($3)` is opaque to the planner: it cannot
-- see that the array holds one element, so it cannot infer the scan is already ordered
-- by global_position.  A scalar `tag = $v_tag` matches the PK prefix
-- (tenant_id, tag, global_position) exactly, the Sort disappears, and the LIMIT
-- terminates the scan early.  That mechanism does not care how many types are named,
-- so a single tag is the right guard.
--
-- What it did not anticipate is that the type axis wants a different plan once more
-- than one type is named.  Simply relaxing 029's join to `etp.event_type = ANY(p_types)`
-- barely beats the general path, and it makes the one-type case nearly three times
-- worse: the scalar probe into (tenant_id, event_type, global_position) is a single
-- descent, while the array form costs one per element on every outer row and estimates
-- badly.  The cheaper multi-type shape drops the type-position index entirely and tests
-- event_type on the events row, which the query has to fetch anyway.  That trades an
-- index probe per candidate for a heap fetch per candidate, which loses at one type and
-- wins from two upward.
--
-- Measured as plpgsql functions under SET plan_cache_mode = force_generic_plan (what a
-- pooled connection gets), min of 12 warm calls, postgres:16-alpine seeded with the
-- benchmark corpus — 20 types uniform, 100 order tags, one tag per event — reading one
-- tag with limit 500:
--
--                          1M / 1 type   1M / 3 types   1M / 10 types   100k / 3 types
--   028 general path          8.975         9.213           7.845           2.219
--   029 relaxed to = ANY      8.850         8.866             —             2.185
--   type on the events row    7.648         2.561           0.799           0.636
--   029 scalar type           3.202           n/a             n/a             n/a
--
-- Hence two branches under one guard rather than one widened branch.
--
-- Dedup still needs no DISTINCT on either branch.  An event carries exactly one
-- event_type, so testing it — on the type-position PK or on the events row — cannot make
-- a position match twice, and a single scalar tag yields at most one tag-position row
-- per position.  No result can consume two slots of p_limit.  Two or more tags is the
-- case that genuinely can duplicate, and it still falls through to the general path.
--
-- The multi-tag general path is unchanged from 028 and still carries the Sort.  It is
-- left alone deliberately: the scalar rewrite is not available there, because more
-- than one tag is exactly the situation the array parameter exists to express.

CREATE OR REPLACE FUNCTION $schema_prefix$alberto_read_by_types_and_tags(
    p_tenant_id VARCHAR(100),
    p_types VARCHAR(500)[],
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
        SELECT e.global_position, e.tenant_id, e.event_id, e.event_type, e.event_tags, e.event_data, e.event_metadata, e.created_at
        FROM (
            SELECT tp.global_position
            FROM $schema_prefix$alberto_event_tag_positions tp
            INNER JOIN $schema_prefix$alberto_event_type_positions etp
                ON etp.global_position = tp.global_position
            WHERE tp.tenant_id = p_tenant_id
              AND tp.tag = v_tag
              AND tp.global_position > p_after_position
              AND etp.tenant_id = p_tenant_id
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
        -- range scan, so the LIMIT still stops it early; e.tenant_id needs no predicate
        -- because global_position is unique across tenants and tp is already scoped.
        RETURN QUERY
        SELECT e.global_position, e.tenant_id, e.event_id, e.event_type, e.event_tags, e.event_data, e.event_metadata, e.created_at
        FROM $schema_prefix$alberto_event_tag_positions tp
        INNER JOIN $schema_prefix$alberto_events e ON e.global_position = tp.global_position
        WHERE tp.tenant_id = p_tenant_id
          AND tp.tag = v_tag
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
    SELECT e.global_position, e.tenant_id, e.event_id, e.event_type, e.event_tags, e.event_data, e.event_metadata, e.created_at
    FROM (
        SELECT tagged.global_position
        FROM (
            SELECT DISTINCT etagp.global_position
            FROM $schema_prefix$alberto_event_tag_positions etagp
            WHERE etagp.tenant_id = p_tenant_id
              AND etagp.tag = ANY(p_tags)
              AND etagp.global_position > p_after_position
        ) tagged
        WHERE tagged.global_position IN (
            SELECT etp.global_position
            FROM $schema_prefix$alberto_event_type_positions etp
            WHERE etp.tenant_id = p_tenant_id
              AND etp.event_type = ANY(p_types)
        )
        ORDER BY 1
        LIMIT p_limit
    ) mp
    INNER JOIN $schema_prefix$alberto_events e ON e.global_position = mp.global_position
    ORDER BY mp.global_position;
END;
$$ LANGUAGE plpgsql;

-- Alberto DCB Event Store - Migration 031 (Multi-Tenant)
--
-- Removes the `= ANY` planner opacity from alberto_read_by_types, the last read function
-- still carrying it and, until this script, the slowest read in the benchmark suite.
--
-- The body this replaces is the one from 001/002:
--
--     SELECT e.* FROM alberto_events e
--     INNER JOIN alberto_event_type_positions etp ON e.global_position = etp.global_position
--     WHERE etp.tenant_id = p_tenant_id AND etp.event_type = ANY(p_types)
--       AND e.global_position > p_after_position
--     ORDER BY e.global_position LIMIT p_limit;
--
-- and it fails the same way migrations 029 and 030 diagnosed on the tag axis.  The planner
-- cannot see how many elements `event_type = ANY($2)` holds, so it cannot infer that the
-- scan is already ordered by global_position, so it cannot use
-- (tenant_id, event_type, global_position) as an ordered range scan.  It seq-scans the whole
-- type-position table, filters, sorts, and merge-joins.  The LIMIT then throws almost all of
-- that away: at 1M events the measured plan sorted 150k positions to return 500.
--
-- What is different here is that 030's remedy does not transfer.  030's second branch drops
-- the type-position index and tests event_type on the events row instead, which works there
-- because the *tag* scan bounds how many events rows are ever considered.  This function has
-- no tag axis.  Nothing bounds the events scan, so that shape degrades from "cheap" to
-- "reads the whole log" exactly when the query is most selective — a named type that is rare,
-- absent, or (multi-tenant) belongs to a tenant whose rows sit late in the log.  It is the
-- fastest shape on a corpus of uniformly frequent types and the worst one on any other, which
-- makes it the wrong thing to ship.  The measurements below are the reason this migration
-- does not simply copy 030.
--
-- The shape that does transfer is 029's scalar probe, applied once per named type.  Each
-- probe is a scalar `event_type = t` against the PK prefix, so it is an ordered index-only
-- range scan that the per-probe LIMIT terminates early; the results are merged by a top-N
-- sort over at most k x p_limit positions and re-limited.  It never touches a row it does not
-- need, whatever the type's frequency, and it needs no guard: at one type it degenerates to
-- exactly 029's scalar probe and measures the same.
--
-- Measured as plpgsql functions under SET plan_cache_mode = force_generic_plan (what a pooled
-- connection gets), min of 25 warm calls, postgres:16-alpine seeded with the benchmark corpus
-- -- 20 uniform event types, 100 order tags -- reading from position 0 with limit 500.
-- "k" is how many of the twenty types the query names; "absent" names one type that no event
-- carries, standing in for the general rare-type case.
--
-- Single-tenant, 1M events:
--
--                             k=1      k=3     k=10     k=20    absent
--   shipped body            23.561   28.233   44.631   60.021   17.221
--   event_type on the row    0.764    0.306    0.186    0.149   66.183
--   probe per type (this)    0.319    0.410    0.643    0.926    0.026
--   029's scalar probe       0.312      n/a      n/a      n/a      n/a
--
-- Multi-tenant, two tenants of 1M events each, t1 occupying the first half of the log and t2
-- the second:
--
--                            t1 k=1   t1 k=3  t1 k=10   t2 k=3   absent
--   shipped body            50.191    8.242   29.816   70.958    2.184
--   event_type on the row    0.783    0.359    0.208   53.234  183.814
--   probe per type (this)    0.329    0.461    0.733    0.454    0.032
--
-- The t2 and absent columns are the argument.  Testing event_type on the events row wins the
-- middle of the table and loses catastrophically at both edges, because both edges are the
-- same thing: an ordered walk of alberto_events from p_after_position that has to travel a
-- long way before the LIMIT is satisfied.  Probing per type is within noise of the best shape
-- at k=1, costs about 30us per additional named type, and has no edge case -- naming all
-- twenty types still beats the shipped body by 65x, and naming a type that does not exist
-- costs 26us instead of 66ms.
--
-- Dedup.  An event carries exactly one event_type, so one type's probe cannot return a
-- position twice, and two *distinct* types' probes cannot both return the same position.  The
-- position set is therefore already distinct and no DISTINCT over positions is needed -- but
-- only because the probe source is deduplicated first.  DcbQuery concatenates types without
-- deduplicating (WithTypes appends to Types, and neither it nor ByTypes filters), so
-- ByTypes("a").WithTypes("a") reaches this function as {a,a}.  Without the DISTINCT that array
-- runs the same probe twice and the merge returns each position twice: measured at 500 rows
-- holding 327 distinct positions, silently consuming a third of the caller's page with
-- duplicates.  The old `= ANY` form was immune to this by accident -- a row either satisfies
-- the predicate or does not, however many times the value appears in the array -- so the
-- DISTINCT is what preserves the previous behaviour rather than an optimisation.
--
-- e.tenant_id needs no predicate: global_position is unique across tenants and etp is already
-- scoped, the same reasoning 030 records.  NULL and empty p_types return no rows, as before --
-- `= ANY(NULL)` was never true and unnest(NULL) yields no rows, so both reach zero probes.
--
-- Out of scope, recorded so the next reader does not re-derive it: the three wildcard readers
-- (alberto_read_by_tag_patterns, _types_or_tag_patterns, _types_and_tag_patterns) were checked
-- for this same pattern and have no live body to fix -- migration 024 dropped all three, and
-- MigrationUpgradeAndParityTests asserts they stay dropped.  alberto_read_by_tags,
-- _types_or_tags and _by_all_tags still carry `= ANY` on the tag axis; they are a separate
-- question because a tag axis can genuinely duplicate positions, and they are not touched here.

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
    FROM (
        SELECT probe.global_position
        FROM (SELECT DISTINCT u.event_type FROM unnest(p_types) AS u(event_type)) t
        CROSS JOIN LATERAL (
            SELECT etp.global_position
            FROM $schema_prefix$alberto_event_type_positions etp
            WHERE etp.tenant_id = p_tenant_id
              AND etp.event_type = t.event_type
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

-- Alberto DCB Event Store - Migration 033 (Multi-Tenant)
--
-- Removes the unbounded tag-axis scans from the four read functions that still carried them:
-- alberto_read_by_all_tags, alberto_read_by_types_and_all_tags, alberto_read_by_types_or_all_tags
-- and alberto_read_by_types_or_tags.  Migrations 028-031 worked through the type axis and the
-- any-tag axis; these four are what was left, and 031's closing note flagged them as a separate
-- question because a tag axis can genuinely duplicate positions where a type axis cannot.  It
-- turns out to be a separate question with three separate answers, one per shape below.
--
-- The common failure is the one 029 named: `tag = ANY($N)` is opaque to the planner, so the scan
-- it drives cannot be recognised as already ordered by global_position, so the LIMIT cannot stop
-- it early.  Each of these four then puts a *blocking* node above that scan — a GROUP BY, a
-- UNION, or a DISTINCT — which removes even the accidental early exit.  Measured plan for
-- alberto_read_by_all_tags on one tag carried by half the log:
--
--     Limit (actual rows=500)
--       -> GroupAggregate (actual rows=500)
--            Filter: (count(DISTINCT tag) = array_length($1, 1))
--            -> Sort  Sort Method: external merge  Disk: 13720kB
--                 -> Index Only Scan (cost rows=47357) (actual rows=500000)
--
-- Half a million index rows read and spilled to disk to return five hundred, on an estimate off
-- by a factor of ten.  Cost is set by how many events carry the tag, not by the limit — which is
-- why the benchmark corpus never showed it.  That corpus puts one order tag on each event, so
-- every tag reaches about 1% of the log and the whole family measures in the low milliseconds.
-- The `order:hot` column below is a synthetic tag added to half the corpus to expose the slope;
-- it stands in for the tag shapes a real store has and the benchmark corpus does not — a status
-- or category tag, a tenant-wide marker, a busy customer.
--
-- ---------------------------------------------------------------------------------------------
-- Answer 1, for the union shapes (_types_or_tags, _types_or_all_tags): bound each arm.
--
-- This is 031's remedy applied to both axes at once.  Each named type gets its own scalar probe
-- into (tenant_id, event_type, global_position) with its own LIMIT, each named tag the same into
-- (tenant_id, tag, global_position); the arms are merged, top-N sorted, and re-limited.  Bounding
-- an arm at p_limit is safe because if a position belongs in the true first p_limit of the union
-- then at most p_limit-1 positions precede it, so it is within the first p_limit of whichever arm
-- produced it.  The UNION's dedup still runs, but now over at most (k+m) x p_limit positions
-- rather than every matching row in the log.
--
-- ---------------------------------------------------------------------------------------------
-- Answer 2, for the all-tags shapes: drive off one tag, test the rest with EXISTS.
--
-- AND semantics cannot bound a per-tag probe the way OR can — an event matching every named tag
-- must appear in every tag's list, so no single probe's first p_limit rows are enough.  But that
-- same fact gives the rewrite: every match carries the driving tag, so a scalar probe on one tag
-- is a complete candidate source.  It is an ordered PK-prefix range scan, the LIMIT terminates it
-- as soon as p_limit matches are found, and each candidate tests the remaining tags with one
-- index probe apiece.  The GROUP BY, the external sort, and the `= ANY` all disappear.
--
-- ---------------------------------------------------------------------------------------------
-- Answer 3, for choosing that driving tag: probe at runtime.
--
-- Which tag drives matters enormously, and the choice cannot be made when the query is planned.
-- A pooled connection gets a generic plan, so at plan time the tag values are unknown; the
-- planner cannot know which of them is rare.  Taking p_tags[1] and hoping costs 20x on the shape
-- this is most likely to meet in practice — one aggregate tag AND one category tag, written in
-- whichever order the caller happened to write them.
--
-- alberto_pick_all_tags_driver therefore measures.  For each tag it reads at most p_limit
-- positions above p_after_position, index-only, and keeps two facts: whether the tag ran out
-- before the cap, and how far along the log its p_limit-th position sits.  A tag that ran out has
-- fewer than p_limit rows in range at all, which caps the whole conjunction, so it wins outright
-- and the loop exits.  Otherwise the tag whose p_limit-th position is furthest along is the
-- sparsest over the stretch the driving scan will actually walk, which is the quantity that
-- matters rather than the tag's total frequency.  The probe costs one bounded index-only scan per
-- tag — about 50us for two tags — and is skipped entirely when only one tag is named.
--
-- ---------------------------------------------------------------------------------------------
-- Measured as plpgsql functions under SET plan_cache_mode = force_generic_plan (what a pooled
-- connection gets), min of 4-8 warm calls, postgres:16-alpine, single-tenant schema seeded with
-- the benchmark corpus -- 1M events, 20 uniform types, 100 order tags -- plus `order:hot` on half
-- of it.  Reading from position 0 with limit 500.  All rewrites were checked to return exactly
-- the same positions as the bodies they replace before being timed.
--
--                                                        shipped    this
--   by_all_tags        1 tag  (1% of the log)              1.067    0.509
--   by_all_tags        1 tag  (50%)                       55.544    0.557
--   by_all_tags        2 tags (hot first, then rare)      53.121    1.033
--   by_all_tags        2 tags (rare first, then hot)      53.121    1.035
--   types_and_all_tags 1 type  + 1 tag (50%)              51.424    0.948
--   types_and_all_tags 3 types + 1 tag (50%)              55.942    1.847
--   types_and_all_tags 1 type  + 2 tags                       --    3.996
--   types_or_tags      3 types + 1 tag                    84.435    0.768
--   types_or_all_tags  3 types + 2 tags                  390.934    1.512
--
-- The two design choices, isolated:
--
--   driver chosen by probe        1.033      driver = p_tags[1]           20.053
--   1 type: scalar type probe     0.948      1 type: type on events row    4.598
--   3 types: type on events row   1.847      3 types: EXISTS on type idx   7.755
--
-- The second pair is 030's finding reproduced on this shape, and this migration keeps 030's
-- branch for the same reason: at one type the scalar probe into the type-position PK is a single
-- descent, and from two types upward testing event_type on the events row the query must fetch
-- anyway is cheaper than probing an index per candidate.  030's own caveat still holds and is why
-- the branch is not widened further -- testing event_type on the events row is only safe while
-- something else bounds how many events rows are considered, which here is the driving tag scan.
--
-- ---------------------------------------------------------------------------------------------
-- Two behaviour changes, both deliberate.
--
-- Duplicate tags.  DcbQuery concatenates tags without deduplicating, so ByAllTags("a").WithTags("a")
-- reaches these functions as {a,a}.  The shipped all-tags bodies compare COUNT(DISTINCT tag)
-- against array_length(p_tags, 1), which is 1 against 2, so they return *nothing* for a query
-- that plainly should match every event tagged "a".  array_remove drops every occurrence of the
-- driver, so the rewrite matches those events.  This is a bug fix, not a plan change, and it is
-- the one place these rewrites are not row-for-row identical to what they replace.
--
-- Empty and NULL arrays are unchanged.  A NULL or empty p_tags returns no rows from the all-tags
-- functions, as before.  In the two union functions a missing axis contributes no arm rather than
-- a false disjunct, which is the same result: unnest(NULL) yields no rows, and `tag = NULL` is
-- never true.
--
-- The types-only branch of alberto_read_by_types_or_tags now delegates to alberto_read_by_types
-- rather than carrying its own copy of the `= ANY` scan.  That branch is unreachable from the
-- .NET backend -- BuildStreamQuery routes HasTypesOnly to alberto_read_by_types before it ever
-- reaches the union function -- but it was still 62.7ms of live SQL that 031 did not cover, and
-- these functions are part of the store's public surface whether or not this repository's client
-- calls them that way.

-- ---------------------------------------------------------------------------------------------
-- Driver selection for the all-tags shapes.
-- ---------------------------------------------------------------------------------------------

CREATE OR REPLACE FUNCTION $schema_prefix$alberto_pick_all_tags_driver(
    p_tenant_id VARCHAR(100),
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
            WHERE tp.tenant_id = p_tenant_id
              AND tp.tag = v_tag
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
DECLARE
    v_driver VARCHAR(500);
    v_rest   VARCHAR(500)[];
BEGIN
    IF p_tags IS NULL OR array_length(p_tags, 1) IS NULL THEN
        RETURN;
    END IF;

    v_driver := $schema_prefix$alberto_pick_all_tags_driver(p_tenant_id, p_tags, p_after_position, p_limit);
    v_rest := array_remove(p_tags, v_driver);

    RETURN QUERY
    SELECT e.global_position, e.tenant_id, e.event_id, e.event_type, e.event_tags, e.event_data, e.event_metadata, e.created_at
    FROM (
        SELECT tp.global_position
        FROM $schema_prefix$alberto_event_tag_positions tp
        WHERE tp.tenant_id = p_tenant_id
          AND tp.tag = v_driver
          AND tp.global_position > p_after_position
          AND NOT EXISTS (
              SELECT 1
              FROM (SELECT DISTINCT u.tag FROM unnest(v_rest) AS u(tag)) rest
              WHERE NOT EXISTS (
                  SELECT 1
                  FROM $schema_prefix$alberto_event_tag_positions x
                  WHERE x.tenant_id = p_tenant_id
                    AND x.tag = rest.tag
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
    p_tenant_id VARCHAR(100),
    p_types VARCHAR(500)[],
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
DECLARE
    v_driver VARCHAR(500);
    v_rest   VARCHAR(500)[];
    v_type   VARCHAR(500);
BEGIN
    IF p_types IS NULL OR array_length(p_types, 1) IS NULL
       OR p_tags IS NULL OR array_length(p_tags, 1) IS NULL THEN
        RETURN;
    END IF;

    v_driver := $schema_prefix$alberto_pick_all_tags_driver(p_tenant_id, p_tags, p_after_position, p_limit);
    v_rest := array_remove(p_tags, v_driver);

    IF array_length(p_types, 1) = 1 THEN
        v_type := p_types[1];

        -- One type: probe the type-position PK with a scalar, as 029 and 030 do.
        RETURN QUERY
        SELECT e.global_position, e.tenant_id, e.event_id, e.event_type, e.event_tags, e.event_data, e.event_metadata, e.created_at
        FROM (
            SELECT tp.global_position
            FROM $schema_prefix$alberto_event_tag_positions tp
            INNER JOIN $schema_prefix$alberto_event_type_positions etp
                ON etp.global_position = tp.global_position
               AND etp.tenant_id = p_tenant_id
               AND etp.event_type = v_type
            WHERE tp.tenant_id = p_tenant_id
              AND tp.tag = v_driver
              AND tp.global_position > p_after_position
              AND NOT EXISTS (
                  SELECT 1
                  FROM (SELECT DISTINCT u.tag FROM unnest(v_rest) AS u(tag)) rest
                  WHERE NOT EXISTS (
                      SELECT 1
                      FROM $schema_prefix$alberto_event_tag_positions x
                      WHERE x.tenant_id = p_tenant_id
                        AND x.tag = rest.tag
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
    -- because the driving tag scan bounds how many rows are ever considered; e.tenant_id needs
    -- no predicate because global_position is unique across tenants and tp is already scoped.
    RETURN QUERY
    SELECT e.global_position, e.tenant_id, e.event_id, e.event_type, e.event_tags, e.event_data, e.event_metadata, e.created_at
    FROM $schema_prefix$alberto_event_tag_positions tp
    INNER JOIN $schema_prefix$alberto_events e ON e.global_position = tp.global_position
    WHERE tp.tenant_id = p_tenant_id
      AND tp.tag = v_driver
      AND tp.global_position > p_after_position
      AND e.event_type = ANY(p_types)
      AND NOT EXISTS (
          SELECT 1
          FROM (SELECT DISTINCT u.tag FROM unnest(v_rest) AS u(tag)) rest
          WHERE NOT EXISTS (
              SELECT 1
              FROM $schema_prefix$alberto_event_tag_positions x
              WHERE x.tenant_id = p_tenant_id
                AND x.tag = rest.tag
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
DECLARE
    v_driver VARCHAR(500);
    v_rest   VARCHAR(500)[];
BEGIN
    v_driver := $schema_prefix$alberto_pick_all_tags_driver(p_tenant_id, p_tags, p_after_position, p_limit);
    v_rest := array_remove(p_tags, v_driver);

    RETURN QUERY
    SELECT e.global_position, e.tenant_id, e.event_id, e.event_type, e.event_tags, e.event_data, e.event_metadata, e.created_at
    FROM (
        SELECT arms.gp
        FROM (
            (
                SELECT probe.global_position AS gp
                FROM (SELECT DISTINCT u.event_type FROM unnest(p_types) AS u(event_type)) t
                CROSS JOIN LATERAL (
                    SELECT etp.global_position
                    FROM $schema_prefix$alberto_event_type_positions etp
                    WHERE etp.tenant_id = p_tenant_id
                      AND etp.event_type = t.event_type
                      AND etp.global_position > p_after_position
                    ORDER BY 1
                    LIMIT p_limit
                ) probe
            )
            UNION
            (
                SELECT tp.global_position
                FROM $schema_prefix$alberto_event_tag_positions tp
                WHERE tp.tenant_id = p_tenant_id
                  AND tp.tag = v_driver
                  AND tp.global_position > p_after_position
                  AND NOT EXISTS (
                      SELECT 1
                      FROM (SELECT DISTINCT u.tag FROM unnest(v_rest) AS u(tag)) rest
                      WHERE NOT EXISTS (
                          SELECT 1
                          FROM $schema_prefix$alberto_event_tag_positions x
                          WHERE x.tenant_id = p_tenant_id
                            AND x.tag = rest.tag
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
        SELECT * FROM $schema_prefix$alberto_read_by_types(p_tenant_id, p_types, p_after_position, p_limit);
        RETURN;
    END IF;

    IF NOT v_has_types AND v_has_tags THEN
        RETURN QUERY
        SELECT * FROM $schema_prefix$alberto_read_by_tags(p_tenant_id, p_tags, p_after_position, p_limit);
        RETURN;
    END IF;

    RETURN QUERY
    SELECT e.global_position, e.tenant_id, e.event_id, e.event_type, e.event_tags, e.event_data, e.event_metadata, e.created_at
    FROM (
        SELECT arms.gp
        FROM (
            (
                SELECT probe.global_position AS gp
                FROM (SELECT DISTINCT u.event_type FROM unnest(p_types) AS u(event_type)) t
                CROSS JOIN LATERAL (
                    SELECT etp.global_position
                    FROM $schema_prefix$alberto_event_type_positions etp
                    WHERE etp.tenant_id = p_tenant_id
                      AND etp.event_type = t.event_type
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
                    WHERE tp.tenant_id = p_tenant_id
                      AND tp.tag = g.tag
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
