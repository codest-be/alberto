-- Alberto DCB Event Store - Zero-Downtime Projection Rebuild Support
-- Adds versioned rebuild capability for projection states

-- Metadata table for tracking rebuild operations
CREATE TABLE IF NOT EXISTS $schema_prefix$projection_rebuild_meta (
    projection_type       TEXT PRIMARY KEY,
    active_version        INT NOT NULL DEFAULT 1,
    rebuilding_version    INT,
    rebuild_status        TEXT,
    rebuild_started_at    TIMESTAMPTZ,
    rebuild_target_position BIGINT,
    rebuild_completed_at  TIMESTAMPTZ
);

-- Add version column to projection_states for versioned data
ALTER TABLE $schema_prefix$projection_states
ADD COLUMN IF NOT EXISTS rebuild_version INT NOT NULL DEFAULT 1;

-- Update primary key to include rebuild_version for versioned data isolation
-- First drop the existing primary key
ALTER TABLE $schema_prefix$projection_states
DROP CONSTRAINT IF EXISTS projection_states_pkey;

-- Create new primary key including rebuild_version
ALTER TABLE $schema_prefix$projection_states
ADD PRIMARY KEY (projection_type, document_id, rebuild_version);

-- Create index for efficient version-aware queries
CREATE INDEX IF NOT EXISTS idx_projection_states_version
ON $schema_prefix$projection_states(projection_type, rebuild_version);

-- Create index for efficient cleanup of old versions
CREATE INDEX IF NOT EXISTS idx_projection_states_type_version_cleanup
ON $schema_prefix$projection_states(projection_type, rebuild_version)
WHERE rebuild_version > 0;

-- Add comment explaining the versioning scheme
COMMENT ON COLUMN $schema_prefix$projection_states.rebuild_version IS 'Version number for zero-downtime rebuilds. Active version is tracked in projection_rebuild_meta.';
COMMENT ON TABLE $schema_prefix$projection_rebuild_meta IS 'Tracks active and rebuilding versions for zero-downtime projection rebuilds.';
