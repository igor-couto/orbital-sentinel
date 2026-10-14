-- Expired-retry cleanup runs before every claim, even when nothing has failed.
-- Avoid scanning every assessment event just to discover an empty retry set.
CREATE INDEX ix_outbox_pending_retries ON outbox_messages(retry_count, lease_expires_at)
    WHERE published_at IS NULL AND dead_lettered_at IS NULL;
