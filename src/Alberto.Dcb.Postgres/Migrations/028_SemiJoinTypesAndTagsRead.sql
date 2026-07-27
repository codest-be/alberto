-- Alberto DCB Event Store - Migration 028 (Multi-Tenant)
--
-- Rewrite alberto_read_by_types_and_tags from INTERSECT to a correlated semi-join.
--
-- The INTERSECT form introduced in 009 plans as `HashSetOp Intersect` beneath a
-- `Sort` beneath the `Limit`.  A LIMIT cannot push through a set operation, so both
-- branches are read and hashed in full before a single row is discarded.  That makes
-- the query a function of total store size rather than of its own selectivity: the
-- type axis matches a roughly constant fraction of the log, so its branch grows with
-- the store while the result set stays pinned at p_limit.  Measured on a seeded store
-- (BenchmarkDotNet StreamByTypeAndTag), 10k/100k/1M events cost 1.2ms/4.1ms/34.8ms —
-- an 8.6x jump for a 10x growth, while every other read stayed flat.
--
-- EXISTS lets the LIMIT stop the scan early.  Measured against 1M events, minimum of
-- five warm runs, comparing this form against the INTERSECT it replaces:
--
--   1 type (333k rows) / 1 tag (10k rows)        32.8ms -> 9.0ms     3.6x
--   2 types (667k)     / 2 tags (11k)            84.7ms -> 12.4ms    6.8x
--   rare type (10)     / broad tag (1M)          83.4ms -> 0.08ms    1042x
--   broad type (333k)  / broad tag (1M)         183.4ms -> 157.2ms   1.2x
--
-- The last row is the honest floor: when neither axis is selective there is no plan
-- that helps.  The semi-join was not slower than the INTERSECT in any shape measured.
--
-- Written tag-side-outer, but that does not pin the drive side.  PostgreSQL flattens
-- the EXISTS into a semi-join and is free to reorder, which is what produces the
-- third row above: with a rare type and a universal tag the planner aggregates the
-- ten type rows and probes tags, rather than scanning a million tag rows.  So this is
-- one query rather than a pair templated on which axis is expected to be narrower.
--
-- DISTINCT is load-bearing, not cosmetic.  INTERSECT deduplicates; a tag-driven scan
-- emits one row per matching tag, so an event carrying two of p_tags would otherwise
-- appear twice and consume two slots of p_limit.

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
BEGIN
    IF p_types IS NULL OR array_length(p_types, 1) IS NULL
       OR p_tags IS NULL OR array_length(p_tags, 1) IS NULL THEN
        RETURN;
    END IF;

    RETURN QUERY
    SELECT e.global_position, e.tenant_id, e.event_id, e.event_type, e.event_tags, e.event_data, e.event_metadata, e.created_at
    FROM (
        SELECT DISTINCT etagp.global_position
        FROM $schema_prefix$alberto_event_tag_positions etagp
        WHERE etagp.tenant_id = p_tenant_id
          AND etagp.tag = ANY(p_tags)
          AND etagp.global_position > p_after_position
          AND EXISTS (
              SELECT 1
              FROM $schema_prefix$alberto_event_type_positions etp
              WHERE etp.tenant_id = p_tenant_id
                AND etp.global_position = etagp.global_position
                AND etp.event_type = ANY(p_types)
          )
        ORDER BY 1
        LIMIT p_limit
    ) mp
    INNER JOIN $schema_prefix$alberto_events e ON e.global_position = mp.global_position
    ORDER BY mp.global_position;
END;
$$ LANGUAGE plpgsql;
