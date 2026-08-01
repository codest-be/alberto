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
