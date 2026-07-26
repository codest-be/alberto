-- Alberto DCB Event Store - Migration 025 (Multi-Tenant)
--
-- Add optional routing metadata to the outbox.
--
-- destination  — transport address (exchange, topic, queue, …) the relay should publish
--               the message to. NULL means "use the transport's default routing for this
--               message type". Purely advisory: relays that do not understand the field
--               ignore it.
--
-- routing_hint — transport-specific hint for partition keys, message-group ids, routing
--               keys, etc. NULL means "no hint". Like destination, this is advisory
--               and ignored by relays that do not implement it.
--
-- Both columns are nullable TEXT: existing rows and new rows produced by publishers that
-- have not yet been updated both receive NULL, which preserves today's routing behaviour.
-- Adding a nullable column in PostgreSQL is a metadata-only operation — no rows are
-- rewritten and the AccessExclusive lock is released as soon as the catalog update
-- commits, measured in microseconds on any live table.

ALTER TABLE $schema_prefix$alberto_outbox_entries
    ADD COLUMN IF NOT EXISTS destination TEXT,
    ADD COLUMN IF NOT EXISTS routing_hint TEXT;
