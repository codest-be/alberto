-- Alberto DCB Event Store - Complete Schema (Multi-Tenant)
--
-- This single script establishes the entire final schema for a fresh multi-tenant
-- installation. It replaces the 34-script history that accreted before the 1.0
-- release. Every object here reflects the final state of those scripts:
--
--   Tables        : final column sets (all ALTER TABLE ADD COLUMN applied)
--   Indexes       : final surviving set (022→024 cancellation omitted; inflight plain)
--   Functions     : final bodies (025 notify-in-function, 032 trigger drops absorbed)
--   Triggers      : only alberto_trg_maintain_tenants survives (032 dropped the others)
--
-- Existing databases already have this file's embedded-resource name journaled
-- (Alberto.Postgres.Migrations.001_InitialSchema.sql) and skip it entirely.
-- 002_QueryFunctions.sql carries the CREATE OR REPLACE FUNCTION definitions that
-- bring an existing database's functions up to the latest versions.
--
-- Supersession chain summary carried forward from the history:
--   002 : legacy rename bridge — no-op on fresh installs, omitted
--   004/005/007/009/028/029/030/031/033 : progressive read-function rewrites
--                                         — only final bodies survive (in 002)
--   010 : batch notify trigger for alberto_events
--   025 : drops that trigger, inlines pg_notify in append functions
--   032 : drops checkpoint and dead-letter notify triggers (no consumer ever listened)
--   022 : concept index for wildcard tag boundaries
--   023 : functions to serve wildcard boundaries
--   024 : drops 022 and 023 entirely — net result is no concept index, no wildcards
--   013/016 : outbox status constraint + claim columns (already in 001 on fresh install)
--   019 : VALIDATE CONSTRAINT (NOT VALID / VALIDATE only needed for live rows; fresh
--         tables create validated constraints from the start)

-- ============================================================
-- SCHEMA
-- ============================================================

CREATE SCHEMA IF NOT EXISTS $schema$;

-- ============================================================
-- TABLES AND SEQUENCES
-- ============================================================

-- Event log. global_position drives all ordering and is the universal join key.
-- pg_xact_id tracks the inserting transaction so GetStableHeadAsync can find the
-- first event whose writer has committed — required for the inflight-visibility index.
-- Migration 008 added pg_xact_id; cast through text because pg_current_xact_id()
-- returns xid8 and BIGINT cannot receive xid8 directly.
CREATE TABLE IF NOT EXISTS $schema_prefix$alberto_events (
    global_position   BIGSERIAL PRIMARY KEY,
    tenant_id         VARCHAR(100) NOT NULL,
    event_id          UUID NOT NULL DEFAULT gen_random_uuid(),
    event_type        VARCHAR(500) NOT NULL,
    event_tags        VARCHAR(500)[] NOT NULL DEFAULT '{}',
    event_data        JSONB NOT NULL DEFAULT '{}',
    event_metadata    JSONB NOT NULL DEFAULT '{}',
    created_at        TIMESTAMPTZ NOT NULL DEFAULT now(),
    pg_xact_id        BIGINT DEFAULT ((pg_current_xact_id())::text)::bigint,
    UNIQUE (event_id)
);

-- Inverted index for event types (tenant-scoped). PK supplies the ordered range scan
-- that alberto_read_by_types uses: (tenant_id, event_type, global_position).
CREATE TABLE IF NOT EXISTS $schema_prefix$alberto_event_type_positions (
    tenant_id         VARCHAR(100) NOT NULL,
    event_type        VARCHAR(500) NOT NULL,
    global_position   BIGINT NOT NULL REFERENCES $schema_prefix$alberto_events(global_position) ON DELETE CASCADE,
    PRIMARY KEY (tenant_id, event_type, global_position)
);

-- Inverted index for event tags (tenant-scoped). PK supplies the ordered range scan
-- that the tag-axis read functions use: (tenant_id, tag, global_position).
CREATE TABLE IF NOT EXISTS $schema_prefix$alberto_event_tag_positions (
    tenant_id         VARCHAR(100) NOT NULL,
    tag               VARCHAR(500) NOT NULL,
    global_position   BIGINT NOT NULL REFERENCES $schema_prefix$alberto_events(global_position) ON DELETE CASCADE,
    PRIMARY KEY (tenant_id, tag, global_position)
);

-- Processor checkpoints. fillfactor=70 leaves room for HOT updates: the control loop
-- writes a checkpoint after every batch, so the same row is updated frequently and
-- HOT avoids writing a new heap version (and index entry) for each update.
CREATE TABLE IF NOT EXISTS $schema_prefix$alberto_processor_checkpoints (
    processor_id      VARCHAR(200) PRIMARY KEY,
    last_position     BIGINT NOT NULL,
    updated_at        TIMESTAMPTZ NOT NULL DEFAULT now(),
    fence_token       BIGINT NOT NULL DEFAULT 0
) WITH (fillfactor = 70);

COMMENT ON COLUMN $schema_prefix$alberto_processor_checkpoints.fence_token IS
    'Highest lease generation that has written this checkpoint. Writes from an earlier generation are refused.';

-- Per-processor leases for the fenced checkpoint path. fillfactor=70 for same HOT
-- reason as processor_checkpoints.
CREATE TABLE IF NOT EXISTS $schema_prefix$alberto_processor_leases (
    consumer_id       TEXT NOT NULL,
    processor_id      TEXT NOT NULL,
    replica_id        TEXT NOT NULL,
    acquired_at       TIMESTAMPTZ NOT NULL DEFAULT now(),
    expires_at        TIMESTAMPTZ NOT NULL,
    fence_token       BIGINT NOT NULL DEFAULT 0,
    PRIMARY KEY (consumer_id, processor_id)
) WITH (fillfactor = 70);

-- Strictly-increasing fence-token sequence. Shared across processors; tokens are
-- only ever compared within a single checkpoint row.
CREATE SEQUENCE IF NOT EXISTS $schema_prefix$alberto_fence_tokens
    START WITH 1 INCREMENT BY 1 NO MINVALUE NO MAXVALUE CACHE 1;

COMMENT ON SEQUENCE $schema_prefix$alberto_fence_tokens IS
    'Issues one strictly increasing generation number per lease acquisition. Shared across processors: tokens are only ever compared within a single checkpoint row.';

-- Projection states. tenant_id is 'public.*' for the cross-tenant sentinel or an
-- actual tenant. rebuild_version distinguishes the shadow copy during a rebuild from
-- the live copy.
CREATE TABLE IF NOT EXISTS $schema_prefix$alberto_projection_states (
    tenant_id         TEXT NOT NULL,
    projection_type   TEXT NOT NULL,
    document_id       TEXT NOT NULL,
    rebuild_version   INT NOT NULL DEFAULT 1,
    state             JSONB NOT NULL,
    updated_at        TIMESTAMPTZ NOT NULL DEFAULT now(),
    PRIMARY KEY (tenant_id, projection_type, document_id, rebuild_version)
);

COMMENT ON COLUMN $schema_prefix$alberto_projection_states.rebuild_version IS
    'Version number for zero-downtime rebuilds. Active version is tracked in alberto_projection_rebuild_meta.';

-- Projection rebuild coordinator state. Keyed by processor (not projection_type) so
-- one processor can manage multiple projections without a separate row per type. The
-- CHECK constraints on status and version cross-validate: a rebuilding_version must
-- exist iff status is rebuilding|ready, and the high-water mark must never drop below
-- the version it records.
CREATE TABLE IF NOT EXISTS $schema_prefix$alberto_projection_rebuild_meta (
    projection_type           TEXT NOT NULL,
    active_version            INT NOT NULL DEFAULT 1,
    rebuilding_version        INT,
    rebuild_status            TEXT NOT NULL DEFAULT 'idle',
    rebuild_started_at        TIMESTAMPTZ,
    rebuild_target_position   BIGINT,
    rebuild_completed_at      TIMESTAMPTZ,
    processor_id              TEXT NOT NULL,
    updated_at                TIMESTAMPTZ NOT NULL DEFAULT now(),
    last_allocated_version    INT NOT NULL DEFAULT 1,
    requested_action          TEXT,
    PRIMARY KEY (processor_id),
    CONSTRAINT alberto_projection_rebuild_meta_status_check
        CHECK (rebuild_status = ANY (ARRAY['idle','rebuilding','ready','completed','aborted'])),
    CONSTRAINT alberto_projection_rebuild_meta_version_check
        CHECK (
            ((rebuild_status = ANY (ARRAY['rebuilding','ready'])) AND rebuilding_version IS NOT NULL)
            OR
            ((rebuild_status = ANY (ARRAY['idle','completed','aborted'])) AND rebuilding_version IS NULL)
        ),
    CONSTRAINT alberto_projection_rebuild_meta_high_water_check
        CHECK (last_allocated_version >= active_version AND last_allocated_version >= COALESCE(rebuilding_version, 1)),
    CONSTRAINT alberto_projection_rebuild_meta_requested_action_check
        CHECK (requested_action IS NULL OR requested_action = ANY (ARRAY['promote','force-promote','abort']))
);

COMMENT ON TABLE $schema_prefix$alberto_projection_rebuild_meta IS
    'Drives zero-downtime projection rebuilds. Keyed by processor. active_version is what readers see; rebuilding_version is what a shadow control loop is replaying into.';
COMMENT ON COLUMN $schema_prefix$alberto_projection_rebuild_meta.projection_type IS
    'Key into alberto_projection_states. Needed to drop superseded rows after promotion.';
COMMENT ON COLUMN $schema_prefix$alberto_projection_rebuild_meta.rebuild_target_position IS
    'Global position the shadow loop must reach before the rebuild can be promoted.';
COMMENT ON COLUMN $schema_prefix$alberto_projection_rebuild_meta.last_allocated_version IS
    'High-water mark of every version ever allocated to this processor. The next rebuild takes this + 1, so an aborted rebuild never donates its number to the rebuild that follows it.';
COMMENT ON COLUMN $schema_prefix$alberto_projection_rebuild_meta.requested_action IS
    'Operator intent waiting for the rebuild coordinator. Operators never perform completion transitions directly.';

-- Dead-letter quarantine for poison events. event_type is VARCHAR(500) to match
-- alberto_events — a narrower column caused silent truncation errors when dead-lettering
-- events whose type name exceeded the old VARCHAR(200) limit (migration 026).
-- claim_id / claimed_by / claim_expires_at: claim fence so a relay can re-claim
-- a processing entry whose claim expired (migration 017).
-- tenant_id nullable: added in migration 012 for multi-tenant isolation.
CREATE TABLE IF NOT EXISTS $schema_prefix$alberto_dead_letter_events (
    id                UUID PRIMARY KEY,
    processor_id      VARCHAR(200) NOT NULL,
    event_id          UUID NOT NULL,
    event_type        VARCHAR(500) NOT NULL,
    event_data        JSONB NOT NULL,
    error_message     TEXT NOT NULL,
    stack_trace       TEXT,
    attempt_count     INT NOT NULL,
    failed_at         TIMESTAMPTZ NOT NULL,
    global_position   BIGINT NOT NULL DEFAULT 0,
    retry_requested   BOOLEAN NOT NULL DEFAULT FALSE,
    claimed_at        TIMESTAMPTZ,
    claim_expires_at  TIMESTAMPTZ,
    claimed_by        VARCHAR(200),
    tenant_id         VARCHAR(100),
    claim_id          UUID
);

-- Transactional outbox for at-least-once message delivery. status transitions:
-- pending → processing (claimed by relay) → delivered | failed.
-- A claim that expires without delivery transitions back to pending on the next
-- ClaimPendingAsync call (claim_expires_at < now()).
-- destination / routing_hint: advisory routing metadata for the transport relay
-- (migration 027); relays that do not understand them ignore the columns.
CREATE TABLE IF NOT EXISTS $schema_prefix$alberto_outbox_entries (
    id                UUID PRIMARY KEY DEFAULT gen_random_uuid(),
    source_event_id   UUID NOT NULL UNIQUE,
    message_type      TEXT NOT NULL,
    version           TEXT NOT NULL DEFAULT '1',
    payload           JSONB NOT NULL,
    metadata          JSONB NOT NULL DEFAULT '{}',
    status            TEXT NOT NULL DEFAULT 'pending',
    retry_count       INT NOT NULL DEFAULT 0,
    last_error        TEXT,
    created_at        TIMESTAMPTZ NOT NULL DEFAULT now(),
    delivered_at      TIMESTAMPTZ,
    claim_id          UUID,
    claimed_by        TEXT,
    claim_expires_at  TIMESTAMPTZ,
    tenant_id         VARCHAR(100),
    destination       TEXT,
    routing_hint      TEXT,
    CONSTRAINT alberto_outbox_entries_status_check
        CHECK (status = ANY (ARRAY['pending','processing','delivered','failed']))
);

-- Tenant-scoped leases for fair distribution across replicas. fillfactor=70 for HOT.
CREATE TABLE IF NOT EXISTS $schema_prefix$alberto_tenant_leases (
    consumer_id       TEXT NOT NULL,
    tenant_id         TEXT NOT NULL,
    replica_id        TEXT NOT NULL,
    acquired_at       TIMESTAMPTZ NOT NULL DEFAULT now(),
    expires_at        TIMESTAMPTZ NOT NULL,
    PRIMARY KEY (consumer_id, tenant_id)
) WITH (fillfactor = 70);

COMMENT ON TABLE $schema_prefix$alberto_tenant_leases IS
    'Tracks tenant ownership with expiring leases for fair distribution across replicas.';
COMMENT ON COLUMN $schema_prefix$alberto_tenant_leases.consumer_id IS 'Unique identifier for the consumer group.';
COMMENT ON COLUMN $schema_prefix$alberto_tenant_leases.tenant_id IS 'The tenant ID being claimed.';
COMMENT ON COLUMN $schema_prefix$alberto_tenant_leases.replica_id IS 'Unique identifier for the replica holding the lease.';
COMMENT ON COLUMN $schema_prefix$alberto_tenant_leases.acquired_at IS 'When the lease was first acquired.';
COMMENT ON COLUMN $schema_prefix$alberto_tenant_leases.expires_at IS 'When the lease expires if not renewed.';

-- Consistent-hashing ring assignments for tenant-to-node routing.
CREATE TABLE IF NOT EXISTS $schema_prefix$alberto_tenant_assignments (
    tenant_id         TEXT PRIMARY KEY,
    node_id           TEXT NOT NULL,
    assigned_at       TIMESTAMPTZ NOT NULL DEFAULT now(),
    ring_version      BIGINT NOT NULL DEFAULT 1
);

-- Tenant catalog maintained by trigger. Avoids full-scan SELECT DISTINCT tenant_id
-- queries in GetKnownTenantsAsync — created in migration 012 for multi-tenant stores.
CREATE TABLE IF NOT EXISTS $schema_prefix$alberto_tenants (
    tenant_id         VARCHAR(100) NOT NULL,
    first_seen_at     TIMESTAMPTZ NOT NULL DEFAULT now(),
    last_seen_at      TIMESTAMPTZ NOT NULL DEFAULT now(),
    CONSTRAINT pk_alberto_tenants PRIMARY KEY (tenant_id)
);

COMMENT ON TABLE $schema_prefix$alberto_tenants IS
    'Catalog of known tenant IDs, maintained by trigger on alberto_events. Avoids full-scan SELECT DISTINCT tenant_id queries in GetKnownTenantsAsync.';

-- ============================================================
-- INDEXES
-- ============================================================

-- Partial index for GetStableHeadAsync. That query scans events looking for the first
-- position whose inserting transaction is committed (pg_xact_id IS NULL after commit).
-- Without a partial index the planner sees near-zero selectivity (almost every row has
-- pg_xact_id NULL once committed) and chooses a sequential scan. Indexing only the
-- in-flight tail keeps the scan O(concurrent writers), not O(store size).
-- Migration 020 built this CONCURRENTLY; a fresh table needs no CONCURRENTLY.
CREATE INDEX IF NOT EXISTS ix_alberto_events_inflight
    ON $schema_prefix$alberto_events (global_position)
    WHERE pg_xact_id IS NOT NULL;

-- Tenant scan: list all events for a tenant in order.
CREATE INDEX IF NOT EXISTS ix_alberto_events_tenant
    ON $schema_prefix$alberto_events (tenant_id, global_position);

-- Replica index for processor leases (lease renewal looks up by replica).
CREATE INDEX IF NOT EXISTS ix_alberto_processor_leases_replica
    ON $schema_prefix$alberto_processor_leases (replica_id);

-- Dead-letter retrieval indexes.
CREATE INDEX IF NOT EXISTS idx_alberto_dead_letter_processor
    ON $schema_prefix$alberto_dead_letter_events (processor_id);
CREATE INDEX IF NOT EXISTS idx_alberto_dead_letter_failed_at
    ON $schema_prefix$alberto_dead_letter_events (failed_at DESC);
CREATE INDEX IF NOT EXISTS idx_alberto_dead_letter_retry_pending
    ON $schema_prefix$alberto_dead_letter_events (processor_id, failed_at)
    WHERE retry_requested = TRUE;
CREATE INDEX IF NOT EXISTS idx_alberto_dead_letter_tenant_processor
    ON $schema_prefix$alberto_dead_letter_events (tenant_id, processor_id)
    WHERE tenant_id IS NOT NULL;

-- Outbox claim and delivery indexes.
CREATE INDEX IF NOT EXISTS idx_alberto_outbox_pending
    ON $schema_prefix$alberto_outbox_entries (created_at)
    WHERE status = 'pending';
CREATE INDEX IF NOT EXISTS idx_alberto_outbox_expired_claims
    ON $schema_prefix$alberto_outbox_entries (claim_expires_at, created_at)
    WHERE status = 'processing';
CREATE INDEX IF NOT EXISTS idx_alberto_outbox_tenant_pending
    ON $schema_prefix$alberto_outbox_entries (tenant_id, created_at)
    WHERE tenant_id IS NOT NULL AND status = 'pending';
-- Retention purge index. PurgeDeliveredAsync deletes WHERE status='delivered' AND
-- delivered_at < $1; a partial index on delivered_at WHERE status='delivered' turns
-- the sweep into a range scan over only the rows being removed (migration 034).
CREATE INDEX IF NOT EXISTS idx_alberto_outbox_delivered
    ON $schema_prefix$alberto_outbox_entries (delivered_at)
    WHERE status = 'delivered';

-- Projection state lookup indexes.
CREATE INDEX IF NOT EXISTS idx_alberto_projection_states_tenant_type
    ON $schema_prefix$alberto_projection_states (tenant_id, projection_type);
CREATE INDEX IF NOT EXISTS idx_alberto_projection_states_version
    ON $schema_prefix$alberto_projection_states (projection_type, rebuild_version);
CREATE INDEX IF NOT EXISTS idx_alberto_projection_states_type_version_cleanup
    ON $schema_prefix$alberto_projection_states (projection_type, rebuild_version)
    WHERE rebuild_version > 0;

-- Tenant lease and assignment indexes.
CREATE INDEX IF NOT EXISTS ix_alberto_tenant_leases_expires
    ON $schema_prefix$alberto_tenant_leases (expires_at);
CREATE INDEX IF NOT EXISTS ix_alberto_tenant_leases_replica
    ON $schema_prefix$alberto_tenant_leases (replica_id);
CREATE INDEX IF NOT EXISTS ix_alberto_tenant_assignments_node_id
    ON $schema_prefix$alberto_tenant_assignments (node_id);

-- ============================================================
-- TRIGGER FUNCTIONS
-- ============================================================

-- Maintains the alberto_tenants catalog in bulk. Runs once per INSERT statement
-- (statement-level, REFERENCING NEW TABLE) rather than once per row, so a batch
-- append pays one INSERT … ON CONFLICT regardless of how many distinct tenants it
-- carries. Created in migration 012 for multi-tenant stores.
CREATE OR REPLACE FUNCTION $schema_prefix$alberto_maintain_tenants_batch()
RETURNS TRIGGER LANGUAGE plpgsql AS $$
BEGIN
    INSERT INTO $schema_prefix$alberto_tenants (tenant_id, first_seen_at, last_seen_at)
    SELECT DISTINCT tenant_id, now(), now()
    FROM new_table
    ON CONFLICT (tenant_id) DO UPDATE
        SET last_seen_at = now();
    RETURN NULL;
END;
$$;

-- ============================================================
-- TRIGGERS
-- ============================================================

-- Maintains the tenant catalog after every INSERT on alberto_events.
-- Migration 032 dropped the three notify triggers (checkpoint, dead-letter insert,
-- dead-letter delete) because nothing in the codebase LISTENed on those channels
-- and each NOTIFY locked the cluster-wide notification queue on commit.
-- The events notify trigger was also dropped by 025, which moved pg_notify into
-- the append functions to fire exactly once per call instead of once per event.
CREATE TRIGGER alberto_trg_maintain_tenants
    AFTER INSERT ON $schema_prefix$alberto_events
    REFERENCING NEW TABLE AS new_table
    FOR EACH STATEMENT
    EXECUTE FUNCTION $schema_prefix$alberto_maintain_tenants_batch();
