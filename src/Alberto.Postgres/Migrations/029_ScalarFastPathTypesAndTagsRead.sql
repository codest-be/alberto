-- Alberto DCB Event Store - Migration 029 (Multi-Tenant)
--
-- Single-element fast path for alberto_read_by_types_and_tags.
--
-- Migration 028 removed the INTERSECT and with it the cliff that made this read a
-- function of total store size.  What remained still grew: 2.7 ms at 100k against
-- 6.0 ms at 1M, while every other paged read in the suite stayed flat.  This is the
-- rest of that story.
--
-- The 028 generic plan reads the tag axis as an Index Only Scan on the tag PK, then
-- puts a blocking Sort and Unique above it before the semi-join and the LIMIT:
--
--   Limit → Nested Loop Semi Join
--             → Unique → Sort → Index Only Scan (tag = ANY($3) AND pos > $4)
--             → Index Only Scan on type PK (event_type = ANY($2) AND pos = tag.pos)
--
-- A Sort is blocking, so the LIMIT cannot stop the tag scan early: every matching tag
-- row is read and sorted before the first result row can be emitted.  At 1M that is
-- ~10,000 tag rows sorted to find the ~1,500 positions the semi-join must probe to
-- return 500.  The scan is O(store_size / n_distinct_tags), not O(limit).
--
-- The planner inserts that Sort because `tag = ANY($3)` tells it nothing.  An array
-- parameter is opaque: it cannot know the array holds one element, so it cannot infer
-- that the scan is already ordered by global_position, and it falls back to a default
-- selectivity that overestimates the tag axis by ~3x.
--
-- Remedy: when both arrays hold exactly one element, extract them into scalar plpgsql
-- variables.  The predicate becomes `tag = $v_tag`, which matches the PK prefix
-- (tenant_id, tag, global_position) exactly — so the scan is ordered by construction,
-- the Sort disappears, and the LIMIT terminates it after ~1,500 rows instead of 10,000.
-- Scalar parameters also get per-column MCV statistics, so the row estimates are right.
--
--   Limit → Nested Loop
--             → Index Only Scan on tag PK  (tag = $v_tag AND pos > $after)  ← ordered
--             → Index Only Scan on type PK (event_type = $v_type AND pos = tag.pos)
--
-- Measured in a Docker postgres:16-alpine container seeded with the benchmark's shape
-- (3 types uniform, 100 order tags, one tag per event), min-of-30 warm calls under
-- SET LOCAL plan_cache_mode.  Judge the generic column: that is what a pooled
-- connection gets after plpgsql stops replanning, and it is what the suite measures.
--
--                      100k custom   100k generic   1M custom   1M generic
--   028 baseline          2.402         1.048         3.009       2.060
--   this form             1.553         0.935         2.309       1.504
--
-- The multi-element path below is unchanged and measured within noise of 028
-- (3 tags + 1 type at 1M: 3.411 ms against 3.374 ms generic).
--
-- Rejected: a type-first correlated EXISTS on the scalar path.  Its generic plan drives
-- from the type axis (~333k rows at 1M) and reads ~50k type positions before the LIMIT
-- fires — 2.525 ms generic at 1M against 028's own 2.060 ms, and 47% worse on a paging
-- query.  This is the same trap 028's header warns about, and it is still a trap.
--
-- The INNER JOIN is deliberate: like 028 it pins no drive side, leaving the planner free
-- to lead with the type axis when a query inverts the selectivity (rare type, broad tag).
--
-- Every predicate names tenant_id in the WHERE clause rather than the JOIN ON clause, so
-- each scan sees the whole compound key (tenant_id, tag|event_type, global_position) and
-- can use the PK prefix for an ordered range scan.

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

        -- No DISTINCT here, and it is not an oversight.  The general path needs one
        -- because an event carrying two of p_tags is emitted twice by a tag-driven scan
        -- and would consume two slots of p_limit.  With a single tag that cannot happen:
        -- (tenant_id, tag, global_position) is the tag PK and (tenant_id, event_type,
        -- global_position) is the type PK, so each side yields at most one row per
        -- position and their join yields at most one.
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
