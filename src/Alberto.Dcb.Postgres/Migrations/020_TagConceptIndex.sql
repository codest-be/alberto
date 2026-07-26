-- Alberto DCB Event Store - Migration 020 (Multi-Tenant)
-- alberto:no-transaction
--
-- Index tags by their concept, so a wildcard tag boundary can be resolved by lookup instead
-- of by scanning every tag ever written.
--
-- A wildcard boundary ("order:*") reaches SQL as the prefix 'order:', and the append functions
-- match it with
--
--     EXISTS (SELECT 1 FROM unnest(p_dcb_tag_prefixes) AS prefix
--             WHERE etagp.tag LIKE prefix || '%')
--
-- PostgreSQL can turn a LIKE into an index range only when the pattern is a constant it can
-- see at planning time. Here the pattern is a row from an unnested array, so there is nothing
-- to derive a range from and the only available plan is a full scan of alberto_event_tag_positions.
-- That scan runs inside the append transaction, and it is worst on the append that succeeds:
-- with no conflicting row to stop early on it reads the whole table, so append latency grows
-- with total history rather than staying flat. Measured on 300k tagged events: 2096 buffers and
-- ~30 ms per append for the scan, against 6 buffers and ~0.06 ms through this index.
--
-- The indexed expression is the tag's concept including its separator -- left(tag, position of
-- the first ':') -- which is exactly what a prefix carries. Matching becomes equality, which is
-- collation-independent; a range predicate (tag >= prefix AND tag < ...) would not be, because
-- the primary key is ordered in the database's collation and a non-C collation does not order
-- punctuation the way a byte-wise prefix comparison assumes.
--
-- The expression is equivalent to the LIKE for every tag: a tag with no ':' yields the empty
-- string and matches no prefix, one with several yields the part before the first, and the LIKE
-- agrees in both cases. It is equivalent to the *predicate* only for prefixes that end at a
-- concept boundary, which is all Alberto produces -- TagPattern.ConceptPrefix is always
-- concept || ':', and concepts and ids are restricted to [A-Za-z0-9_-] and so contain no ':'.
-- Calling these functions directly with a partial prefix ('ord') used to match 'order:1' and
-- will not after migration 021.
--
-- CONCURRENTLY, and therefore outside a transaction: a plain CREATE INDEX holds a lock that
-- blocks inserts for its duration, which on this table is the append path itself.

CREATE INDEX CONCURRENTLY IF NOT EXISTS ix_alberto_event_tag_positions_concept
    ON $schema_prefix$alberto_event_tag_positions
    (tenant_id, (left(tag::TEXT, position(':' IN tag::TEXT))), global_position);

COMMENT ON INDEX $schema_prefix$ix_alberto_event_tag_positions_concept IS
    'Resolves wildcard tag boundaries (concept:*) by concept lookup. See migration 020.';
