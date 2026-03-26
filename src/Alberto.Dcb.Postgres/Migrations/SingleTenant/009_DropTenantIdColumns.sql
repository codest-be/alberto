-- Alberto DCB Event Store - Drop tenant_id columns (single-tenant upgrade)
-- Removes tenant_id from event tables when upgrading from a multi-tenant schema.
-- All operations use IF EXISTS guards so this is safe for fresh single-tenant installs.

-- events: drop the tenant index and the column itself
DROP INDEX IF EXISTS $schema_prefix$ix_events_tenant;
ALTER TABLE $schema_prefix$events DROP COLUMN IF EXISTS tenant_id;

-- event_type_positions: rebuild primary key without tenant_id
DO $$
BEGIN
    IF EXISTS (
        SELECT 1 FROM information_schema.columns
        WHERE table_schema = '$schema$'
          AND table_name = 'event_type_positions'
          AND column_name = 'tenant_id'
    ) THEN
        ALTER TABLE $schema_prefix$event_type_positions DROP CONSTRAINT IF EXISTS event_type_positions_pkey;
        ALTER TABLE $schema_prefix$event_type_positions DROP COLUMN IF EXISTS tenant_id;
        ALTER TABLE $schema_prefix$event_type_positions ADD PRIMARY KEY (event_type, global_position);
    END IF;
END $$;

-- event_tag_positions: rebuild primary key without tenant_id
DO $$
BEGIN
    IF EXISTS (
        SELECT 1 FROM information_schema.columns
        WHERE table_schema = '$schema$'
          AND table_name = 'event_tag_positions'
          AND column_name = 'tenant_id'
    ) THEN
        ALTER TABLE $schema_prefix$event_tag_positions DROP CONSTRAINT IF EXISTS event_tag_positions_pkey;
        ALTER TABLE $schema_prefix$event_tag_positions DROP COLUMN IF EXISTS tenant_id;
        ALTER TABLE $schema_prefix$event_tag_positions ADD PRIMARY KEY (tag, global_position);
    END IF;
END $$;

-- projection_states: rebuild primary key without tenant_id (if upgrading from multi-tenant schema)
DO $$
BEGIN
    IF EXISTS (
        SELECT 1 FROM information_schema.columns
        WHERE table_schema = '$schema$'
          AND table_name = 'projection_states'
          AND column_name = 'tenant_id'
    ) THEN
        DROP INDEX IF EXISTS $schema_prefix$idx_projection_states_tenant_type;
        ALTER TABLE $schema_prefix$projection_states DROP CONSTRAINT IF EXISTS projection_states_pkey;
        ALTER TABLE $schema_prefix$projection_states DROP COLUMN IF EXISTS tenant_id;
        ALTER TABLE $schema_prefix$projection_states ADD PRIMARY KEY (projection_type, document_id, rebuild_version);
    END IF;
END $$;
