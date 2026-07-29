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
