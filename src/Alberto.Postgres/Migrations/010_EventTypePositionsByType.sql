-- Alberto DCB Event Store – 010 Event Type Positions By Type (Multi-Tenant)
--
-- A processor reads the events of its types across all tenants, in position order. The
-- primary key of alberto_event_type_positions leads with tenant_id, so it cannot serve that
-- read in order, and the read fell back to scanning alberto_events from the checkpoint: its
-- cost grew with every event after the checkpoint, matching or not. This index gives each
-- type an ordered range scan, so the read touches only rows of the types it asks for.
--
-- The single-tenant set needs no counterpart: its primary key is already
-- (event_type, global_position).
--
-- Cost: one more index entry per event appended.

CREATE INDEX IF NOT EXISTS ix_alberto_event_type_positions_type
    ON $schema_prefix$alberto_event_type_positions (event_type, global_position);
