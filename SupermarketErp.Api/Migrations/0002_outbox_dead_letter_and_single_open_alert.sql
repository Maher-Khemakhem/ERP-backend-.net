-- Outbox: retry limit with a dead-letter state, and per-event backoff.
--   failed_at       set when an event is given up on (permanent error or too many attempts);
--                   the processor never picks it up again. Re-queue with:
--                   UPDATE platform.dotnet_outbox SET failed_at = NULL, next_attempt_at = NULL, attempts = 0 WHERE id = ...
--   next_attempt_at earliest time a failed event may be retried; other events are not held up by it.
IF COL_LENGTH(N'platform.dotnet_outbox', N'failed_at') IS NULL
    ALTER TABLE [platform].[dotnet_outbox] ADD failed_at DATETIMEOFFSET NULL;

IF COL_LENGTH(N'platform.dotnet_outbox', N'next_attempt_at') IS NULL
    ALTER TABLE [platform].[dotnet_outbox] ADD next_attempt_at DATETIMEOFFSET NULL;

-- Low-stock alerts: at most one open (unacknowledged) alert per product.
-- Existing duplicates are kept but acknowledged, except the oldest open alert of each product.
WITH ranked AS (
    SELECT id,
           ROW_NUMBER() OVER (PARTITION BY product_id ORDER BY raised_at, id) AS position
    FROM [procurement].[low_stock_alert]
    WHERE acknowledged = 0
)
UPDATE alert
SET acknowledged = 1
FROM [procurement].[low_stock_alert] alert
JOIN ranked ON ranked.id = alert.id
WHERE ranked.position > 1;

IF NOT EXISTS (
    SELECT 1 FROM sys.indexes
    WHERE name = N'ux_low_stock_alert_open_product'
      AND object_id = OBJECT_ID(N'[procurement].[low_stock_alert]')
)
    EXEC(N'CREATE UNIQUE INDEX ux_low_stock_alert_open_product
           ON [procurement].[low_stock_alert](product_id)
           WHERE acknowledged = 0');

-- The processor's query also filters on the new columns, so rebuild its supporting index.
IF EXISTS (
    SELECT 1 FROM sys.indexes
    WHERE name = N'ix_dotnet_outbox_pending'
      AND object_id = OBJECT_ID(N'[platform].[dotnet_outbox]')
)
    DROP INDEX ix_dotnet_outbox_pending ON [platform].[dotnet_outbox];

EXEC(N'CREATE INDEX ix_dotnet_outbox_pending
       ON [platform].[dotnet_outbox](occurred_at, id)
       INCLUDE (next_attempt_at)
       WHERE processed_at IS NULL AND failed_at IS NULL');
