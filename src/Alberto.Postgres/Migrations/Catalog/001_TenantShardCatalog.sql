-- Alberto DCB Event Store - Tenant shard catalog
-- Records which database each tenant of a module lives in.
--
-- This is the only table in the control database.  It is deliberately not one of
-- the shards: every request to a sharded module reads this mapping, so putting it
-- in a shard would make that shard load-bearing for routing to all the others --
-- exactly the coupling sharding exists to remove.
--
-- It is also deliberately not alberto_tenants, which is maintained by a trigger on
-- alberto_events and therefore lives *inside* a shard, one copy per shard, and only
-- knows about tenants that already have events.  A tenant needs a shard before its
-- first event, not after.
--
-- No connection string appears here.  A row names a shard id; what that id resolves
-- to is a deployment decision that stays in configuration, so a database dump can
-- never leak credentials and an operator can move a shard without a data migration.

CREATE TABLE IF NOT EXISTS $schema_prefix$alberto_tenant_shards (
    module_key  VARCHAR(100) NOT NULL,
    tenant_id   VARCHAR(100) NOT NULL,
    shard_id    VARCHAR(63)  NOT NULL,
    assigned_at TIMESTAMPTZ  NOT NULL DEFAULT now(),
    CONSTRAINT pk_alberto_tenant_shards PRIMARY KEY (module_key, tenant_id)
);

-- Answers "what is in this shard?" for the CLI and for a shard being drained.
CREATE INDEX IF NOT EXISTS ix_alberto_tenant_shards_shard
    ON $schema_prefix$alberto_tenant_shards (module_key, shard_id);
