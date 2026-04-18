-- Alberto DCB Event Store - Processor Leases
-- Database-backed expiring leases for processor-level leadership distribution.
-- Each processor can be claimed by exactly one replica at a time.

CREATE TABLE IF NOT EXISTS $schema_prefix$processor_leases (
    consumer_id   TEXT        NOT NULL,
    processor_id  TEXT        NOT NULL,
    replica_id    TEXT        NOT NULL,
    acquired_at   TIMESTAMPTZ NOT NULL DEFAULT now(),
    expires_at    TIMESTAMPTZ NOT NULL,
    PRIMARY KEY (consumer_id, processor_id)
);

CREATE INDEX IF NOT EXISTS ix_processor_leases_replica
    ON $schema_prefix$processor_leases (replica_id);
