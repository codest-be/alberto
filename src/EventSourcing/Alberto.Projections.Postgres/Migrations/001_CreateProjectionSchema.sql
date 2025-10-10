-- =============================================================================
-- ALBERTO PROJECTIONS SCHEMA FOR POSTGRESQL
-- =============================================================================
-- This script creates the base projection infrastructure in the specified schema.
-- The $schema$ variable is replaced by DbUp at runtime.
-- Note: Individual projection tables are still created dynamically by the repository.
-- =============================================================================

-- Create schema if it doesn't exist
CREATE SCHEMA IF NOT EXISTS $schema$;

-- Migration tracking comment (DbUp tracks migrations automatically)
-- This file establishes the schema and can be extended with shared infrastructure

