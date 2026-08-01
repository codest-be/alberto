-- Alberto DCB Event Store - Migration 032 (Multi-Tenant)
--
-- Drop the checkpoint and dead-letter notify triggers. Nothing has ever listened on the
-- channels they raise, and a NOTIFY is not free to the transaction that raises it.
--
-- Migration 001 opened three notification channels: $schema$_events, $schema$_checkpoints
-- and $schema$_dead_letters. Only the first grew a consumer. PostgresEventListener is the
-- single LISTEN in the codebase and it subscribes to $schema$_events alone -- no other C#,
-- no CLI command, and no test opens the other two. These triggers have been writing into a
-- queue nobody reads since the schema was created.
--
-- Left alone that would be untidy rather than costly, except that the cost of NOTIFY falls
-- on the writer, not the reader. A transaction raising one takes an exclusive lock on the
-- cluster-wide notification queue at commit, serialising its commit step against every other
-- notifying transaction -- across every database on the instance, not only this one.
-- Migration 025 went to some trouble to pay that once per append rather than once per event.
-- These triggers keep paying it on paths that are not quieter than the append path:
--
--   alberto_trg_notify_checkpoint is FOR EACH ROW on AFTER INSERT OR UPDATE of
--   alberto_processor_checkpoints, and every control loop on every node writes a checkpoint
--   after every batch it processes. With several processors running, that channel can carry
--   more traffic than the events channel it sits beside.
--
--   The dead-letter pair is lower volume but worse per operation: both are FOR EACH ROW, so
--   an operator clearing a processor's dead letters fans out one notification per row
--   deleted, each one taking the queue lock behind the same commit.
--
-- Nothing a client can observe changes. The three trigger functions go with their triggers;
-- no other object references them. If a checkpoint or dead-letter subscription is ever
-- wanted, it should be built the way 025 built the events one -- raised once from the code
-- path that knows the operation finished, not once per row from a trigger.
--
-- Every statement is IF EXISTS, so the script is safe to re-run and safe against a schema
-- where 002's legacy path created the same objects. DROP TRIGGER takes an ACCESS EXCLUSIVE
-- lock on each table for the length of this transaction; both tables are small and the drops
-- are catalog-only, but the lock still queues behind any open transaction holding the table.

DROP TRIGGER IF EXISTS alberto_trg_notify_checkpoint ON $schema_prefix$alberto_processor_checkpoints;
DROP TRIGGER IF EXISTS alberto_trg_dead_letter_insert_notify ON $schema_prefix$alberto_dead_letter_events;
DROP TRIGGER IF EXISTS alberto_trg_dead_letter_delete_notify ON $schema_prefix$alberto_dead_letter_events;

DROP FUNCTION IF EXISTS $schema_prefix$alberto_notify_checkpoint();
DROP FUNCTION IF EXISTS $schema_prefix$alberto_notify_dead_letter_insert();
DROP FUNCTION IF EXISTS $schema_prefix$alberto_notify_dead_letter_delete();
