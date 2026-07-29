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
