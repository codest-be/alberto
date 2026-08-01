-- Alberto DCB Event Store - Initial Schema (Single-Tenant)
--
-- This file represents the complete final schema as of the 34th migration script.
-- It is run once on a fresh database, so:
--   - CREATE TABLE (not IF NOT EXISTS) -- idempotency of this file is achieved by
--     the DbUp journal, which runs it at most once per database.
--   - CREATE INDEX IF NOT EXISTS -- safe on subsequent runs by existing databases
--     that execute this file via the DbUp journal catching up (they journaled the old
--     002-034 names and never journaled 001_InitialSchema.sql before).
--   - Inline check constraints (not NOT VALID) -- fresh tables have no rows to validate.
--
-- Supersession chains absorbed into this schema:
--   002     : legacy no-op (skipped on fresh installs, no net change)
--   006     : dead_letter claim columns
--   008     : pg_xact_id column
--   011     : fillfactor=70 on checkpoints and processor_leases
--   013     : outbox processing status
--   014/015 : projection_rebuild_meta
--   016     : outbox claim columns
--   017     : dead_letter claim fence (claim_id)
--   018     : requested_action, last_allocated_version
--   019     : deferred CHECK constraints (not needed on fresh tables)
--   020     : ix_alberto_events_inflight
--   021     : fence_token on checkpoints and leases
--   022/023/024 : concept tag index (net: no index -- 024 dropped it)
--   026     : event_type VARCHAR(500) in dead_letter_events
--   027     : outbox destination, routing_hint
--   032     : drops trigger-based notifiers (net: no triggers in this file)
--   034     : idx_alberto_outbox_delivered
--
-- Note: 012 (tenants table and tenant isolation) is MT-only and absent here.

-- ============================================================
-- SEQUENCES (must precede tables that reference them)
-- ============================================================

CREATE SEQUENCE $schema_prefix$alberto_events_global_position_seq
    START WITH 1 INCREMENT BY 1 NO MINVALUE NO MAXVALUE CACHE 1;

CREATE SEQUENCE $schema_prefix$alberto_fence_tokens
    START WITH 1 INCREMENT BY 1 NO MINVALUE NO MAXVALUE CACHE 1;

COMMENT ON SEQUENCE $schema_prefix$alberto_fence_tokens IS
    'Issues one strictly increasing generation number per lease acquisition. Shared across processors: tokens are only ever compared within a single checkpoint row.';

-- ============================================================
-- TABLES
-- ============================================================

CREATE TABLE $schema_prefix$alberto_events (
    global_position     BIGINT                      NOT NULL DEFAULT nextval('$schema_prefix$alberto_events_global_position_seq'),
    event_id            UUID                        NOT NULL DEFAULT gen_random_uuid(),
    event_type          VARCHAR(500)                NOT NULL,
    event_tags          VARCHAR(500)[]              NOT NULL DEFAULT '{}',
    event_data          JSONB                       NOT NULL DEFAULT '{}',
    event_metadata      JSONB                       NOT NULL DEFAULT '{}',
    created_at          TIMESTAMPTZ                 NOT NULL DEFAULT now(),
    pg_xact_id          BIGINT                      DEFAULT ((pg_current_xact_id())::text)::bigint,
    CONSTRAINT alberto_events_pkey               PRIMARY KEY (global_position),
    CONSTRAINT alberto_events_event_id_key       UNIQUE      (event_id)
);

ALTER SEQUENCE $schema_prefix$alberto_events_global_position_seq
    OWNED BY $schema_prefix$alberto_events.global_position;

CREATE TABLE $schema_prefix$alberto_event_type_positions (
    event_type          VARCHAR(500)                NOT NULL,
    global_position     BIGINT                      NOT NULL,
    CONSTRAINT alberto_event_type_positions_pkey   PRIMARY KEY (event_type, global_position),
    CONSTRAINT alberto_event_type_positions_global_position_fkey
        FOREIGN KEY (global_position) REFERENCES $schema_prefix$alberto_events(global_position) ON DELETE CASCADE
);

CREATE TABLE $schema_prefix$alberto_event_tag_positions (
    tag                 VARCHAR(500)                NOT NULL,
    global_position     BIGINT                      NOT NULL,
    CONSTRAINT alberto_event_tag_positions_pkey   PRIMARY KEY (tag, global_position),
    CONSTRAINT alberto_event_tag_positions_global_position_fkey
        FOREIGN KEY (global_position) REFERENCES $schema_prefix$alberto_events(global_position) ON DELETE CASCADE
);

CREATE TABLE $schema_prefix$alberto_processor_checkpoints (
    processor_id        VARCHAR(200)                NOT NULL,
    last_position       BIGINT                      NOT NULL,
    updated_at          TIMESTAMPTZ                 NOT NULL DEFAULT now(),
    fence_token         BIGINT                      NOT NULL DEFAULT 0,
    CONSTRAINT alberto_processor_checkpoints_pkey  PRIMARY KEY (processor_id)
) WITH (fillfactor = 70);

COMMENT ON COLUMN $schema_prefix$alberto_processor_checkpoints.fence_token IS
    'Highest lease generation that has written this checkpoint. Writes from an earlier generation are refused.';

CREATE TABLE $schema_prefix$alberto_processor_leases (
    consumer_id         TEXT                        NOT NULL,
    processor_id        TEXT                        NOT NULL,
    replica_id          TEXT                        NOT NULL,
    acquired_at         TIMESTAMPTZ                 NOT NULL DEFAULT now(),
    expires_at          TIMESTAMPTZ                 NOT NULL,
    fence_token         BIGINT                      NOT NULL DEFAULT 0,
    CONSTRAINT alberto_processor_leases_pkey  PRIMARY KEY (consumer_id, processor_id)
) WITH (fillfactor = 70);

CREATE TABLE $schema_prefix$alberto_dead_letter_events (
    id                  UUID                        NOT NULL,
    processor_id        VARCHAR(200)                NOT NULL,
    event_id            UUID                        NOT NULL,
    event_type          VARCHAR(500)                NOT NULL,
    event_data          JSONB                       NOT NULL,
    error_message       TEXT                        NOT NULL,
    stack_trace         TEXT,
    attempt_count       INTEGER                     NOT NULL,
    failed_at           TIMESTAMPTZ                 NOT NULL,
    global_position     BIGINT                      NOT NULL DEFAULT 0,
    retry_requested     BOOLEAN                     NOT NULL DEFAULT false,
    claimed_at          TIMESTAMPTZ,
    claim_expires_at    TIMESTAMPTZ,
    claimed_by          VARCHAR(200),
    claim_id            UUID,
    CONSTRAINT alberto_dead_letter_events_pkey      PRIMARY KEY (id)
);

CREATE TABLE $schema_prefix$alberto_outbox_entries (
    id                  UUID                        NOT NULL DEFAULT gen_random_uuid(),
    source_event_id     UUID                        NOT NULL,
    message_type        TEXT                        NOT NULL,
    version             TEXT                        NOT NULL DEFAULT '1',
    payload             JSONB                       NOT NULL,
    metadata            JSONB                       NOT NULL DEFAULT '{}',
    status              TEXT                        NOT NULL DEFAULT 'pending',
    retry_count         INTEGER                     NOT NULL DEFAULT 0,
    last_error          TEXT,
    created_at          TIMESTAMPTZ                 NOT NULL DEFAULT now(),
    delivered_at        TIMESTAMPTZ,
    claim_id            UUID,
    claimed_by          TEXT,
    claim_expires_at    TIMESTAMPTZ,
    destination         TEXT,
    routing_hint        TEXT,
    CONSTRAINT alberto_outbox_entries_pkey              PRIMARY KEY (id),
    CONSTRAINT alberto_outbox_entries_source_event_id_key UNIQUE (source_event_id),
    CONSTRAINT alberto_outbox_entries_status_check      CHECK (status = ANY (ARRAY['pending','processing','delivered','failed']))
);

-- Projection rebuild tables.
--
-- alberto_projection_rebuild_meta drives zero-downtime rebuilds.
-- The CHECK constraints are sufficient on a fresh table (no NOT VALID needed).
-- On existing databases the constraints were added as NOT VALID by migration 019 and
-- validated later, so they are already in force before this file runs.
CREATE TABLE $schema_prefix$alberto_projection_rebuild_meta (
    projection_type         TEXT            NOT NULL,
    active_version          INTEGER         NOT NULL DEFAULT 1,
    rebuilding_version      INTEGER,
    rebuild_status          TEXT            NOT NULL DEFAULT 'idle',
    rebuild_started_at      TIMESTAMPTZ,
    rebuild_target_position BIGINT,
    rebuild_completed_at    TIMESTAMPTZ,
    processor_id            TEXT            NOT NULL,
    updated_at              TIMESTAMPTZ     NOT NULL DEFAULT now(),
    last_allocated_version  INTEGER         NOT NULL DEFAULT 1,
    requested_action        TEXT,
    CONSTRAINT alberto_projection_rebuild_meta_pkey             PRIMARY KEY (processor_id),
    CONSTRAINT alberto_projection_rebuild_meta_status_check     CHECK (rebuild_status = ANY (ARRAY['idle','rebuilding','ready','completed','aborted'])),
    CONSTRAINT alberto_projection_rebuild_meta_version_check    CHECK (
        ((rebuild_status = ANY (ARRAY['rebuilding','ready'])) AND (rebuilding_version IS NOT NULL))
        OR ((rebuild_status = ANY (ARRAY['idle','completed','aborted'])) AND (rebuilding_version IS NULL))
    ),
    CONSTRAINT alberto_projection_rebuild_meta_high_water_check CHECK (
        (last_allocated_version >= active_version)
        AND (last_allocated_version >= COALESCE(rebuilding_version, 1))
    ),
    CONSTRAINT alberto_projection_rebuild_meta_requested_action_check CHECK (
        (requested_action IS NULL) OR (requested_action = ANY (ARRAY['promote','force-promote','abort']))
    )
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

CREATE TABLE $schema_prefix$alberto_projection_states (
    projection_type TEXT        NOT NULL,
    document_id     TEXT        NOT NULL,
    rebuild_version INTEGER     NOT NULL DEFAULT 1,
    state           JSONB       NOT NULL,
    updated_at      TIMESTAMPTZ NOT NULL DEFAULT now(),
    CONSTRAINT alberto_projection_states_pkey PRIMARY KEY (projection_type, document_id, rebuild_version)
);

COMMENT ON COLUMN $schema_prefix$alberto_projection_states.rebuild_version IS
    'Version number for zero-downtime rebuilds. Active version is tracked in alberto_projection_rebuild_meta.';

-- ============================================================
-- INDEXES
-- ============================================================

-- Dead-letter indexes
CREATE INDEX IF NOT EXISTS idx_alberto_dead_letter_failed_at
    ON $schema_prefix$alberto_dead_letter_events (failed_at DESC);

CREATE INDEX IF NOT EXISTS idx_alberto_dead_letter_processor
    ON $schema_prefix$alberto_dead_letter_events (processor_id);

CREATE INDEX IF NOT EXISTS idx_alberto_dead_letter_retry_pending
    ON $schema_prefix$alberto_dead_letter_events (processor_id, failed_at)
    WHERE retry_requested = true;

-- Outbox indexes
CREATE INDEX IF NOT EXISTS idx_alberto_outbox_pending
    ON $schema_prefix$alberto_outbox_entries (created_at)
    WHERE status = 'pending';

CREATE INDEX IF NOT EXISTS idx_alberto_outbox_expired_claims
    ON $schema_prefix$alberto_outbox_entries (claim_expires_at, created_at)
    WHERE status = 'processing';

CREATE INDEX IF NOT EXISTS idx_alberto_outbox_delivered
    ON $schema_prefix$alberto_outbox_entries (delivered_at)
    WHERE status = 'delivered';

-- Projection state indexes
CREATE INDEX IF NOT EXISTS idx_alberto_projection_states_type
    ON $schema_prefix$alberto_projection_states (projection_type);

CREATE INDEX IF NOT EXISTS idx_alberto_projection_states_version
    ON $schema_prefix$alberto_projection_states (projection_type, rebuild_version);

CREATE INDEX IF NOT EXISTS idx_alberto_projection_states_type_version_cleanup
    ON $schema_prefix$alberto_projection_states (projection_type, rebuild_version)
    WHERE rebuild_version > 0;

-- In-flight event visibility (migration 020).
-- The pg_xact_id column is set by the INSERT default; once the inserting transaction commits
-- the value is nulled out by the visibility cleanup pass. This partial index covers only rows
-- with a non-null pg_xact_id (i.e. potentially inflight).
CREATE INDEX IF NOT EXISTS ix_alberto_events_inflight
    ON $schema_prefix$alberto_events (global_position)
    WHERE pg_xact_id IS NOT NULL;

-- Processor lease secondary index for replica-ID lookups.
CREATE INDEX IF NOT EXISTS ix_alberto_processor_leases_replica
    ON $schema_prefix$alberto_processor_leases (replica_id);
