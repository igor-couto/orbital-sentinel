-- Full-catalog imports produce thousands of events. Claim only the pending slice,
-- and check partition ordering without rescanning already published history.
CREATE INDEX ix_outbox_pending_sequence ON outbox_messages(sequence)
    WHERE published_at IS NULL AND dead_lettered_at IS NULL;
CREATE INDEX ix_outbox_pending_partition ON outbox_messages(partition_key, sequence)
    WHERE published_at IS NULL AND dead_lettered_at IS NULL;
