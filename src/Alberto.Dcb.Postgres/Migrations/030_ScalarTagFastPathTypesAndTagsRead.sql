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
