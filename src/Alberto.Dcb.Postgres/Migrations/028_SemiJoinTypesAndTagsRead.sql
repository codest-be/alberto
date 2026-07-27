-- Alberto DCB Event Store - Migration 028 (Multi-Tenant)
--
-- Rewrite alberto_read_by_types_and_tags from INTERSECT to a semi-join over a
-- pre-deduplicated tag stream.
--
-- The INTERSECT form introduced in 009 plans as `HashSetOp Intersect` beneath a
-- `Sort` beneath the `Limit`.  A LIMIT cannot push through a set operation, so both
-- branches are read and hashed in full before a single row is discarded.  That makes
-- the query a function of total store size rather than of its own selectivity: the
-- type axis matches a roughly constant fraction of the log, so its branch grows with
-- the store while the result set stays pinned at p_limit.  Measured end-to-end
-- (BenchmarkDotNet StreamByTypeAndTag), 10k/100k/1M events cost 1.3ms/4.5ms/32.5ms —
-- a 7.2x jump for a 10x growth, while every other read stayed flat.
--
-- The shape below deduplicates the tag axis in its own subquery and then applies the
-- type axis as an IN semi-join, so the LIMIT sits on top of an already-unique stream
-- and can stop the scan early.  Measured end-to-end against the harness, back to back
-- on an otherwise idle machine (mean of 10 iterations, RunStrategy.Monitoring):
--
--            INTERSECT (009)   this form
--     10k        1.280ms        1.099ms
--    100k        4.512ms        3.732ms   (median 2.532ms; see the plan-cache note)
--      1M       32.536ms        5.123ms   6.4x
--
-- An earlier draft of this migration drove the scan from the tag side with a
-- correlated EXISTS and an outer DISTINCT.  It won just as decisively at 1M but was
-- ~37% SLOWER than the INTERSECT at 100k.  Pricing both halves of the plan cache with
-- `SET LOCAL plan_cache_mode` at 100k explains why, and is worth recording because it
-- is invisible in a single EXPLAIN (min over 12 calls, ms):
--
--                             custom plan   generic plan
--     INTERSECT (009)            2.883         2.662
--     DISTINCT over EXISTS       2.881         6.083
--     this form                  2.684         1.131
--
-- plpgsql plans a statement against actual parameter values for roughly five calls and
-- may then switch to a value-agnostic generic plan for the rest of the session.  The
-- EXISTS draft's custom plan was fine; its GENERIC plan was 2.1x worse, and under
-- connection pooling that is the plan almost every call gets — which is exactly the
-- 6.171ms the harness measured.  So do not "simplify" this back into a single DISTINCT
-- over a correlated EXISTS: the shape below is the one whose generic plan is good, and
-- a form that only looks fast with literal values in psql will not survive pooling.
-- The same asymmetry running the other way is why this form's 100k mean sits above its
-- median: its first few calls per connection use the slower custom plan.
--
-- The inner DISTINCT is load-bearing, not cosmetic.  INTERSECT deduplicates; a
-- tag-driven scan emits one row per matching tag, so an event carrying two of p_tags
-- would otherwise appear twice and consume two slots of p_limit.
--
-- Note this is not written to pin a drive side.  PostgreSQL flattens the IN into a
-- semi-join and is free to reorder, so a rare type against a broad tag is planned by
-- probing from the type side instead.  That keeps this one query rather than a pair
-- templated on which axis is expected to be narrower.

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
