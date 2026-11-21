-- SQL Server treats NULL as a value in UNIQUE constraints, so a nullable UNIQUE column accepts only ONE row
-- with NULL (PostgreSQL, which the original schema came from, accepts many). Consequences until now:
--   * online_order.payment_reference is NULL until an order is paid, so only one unpaid online order could
--     exist at a time (and a cancelled or expired order kept the slot forever): every other checkout failed;
--   * stripe_checkout.checkout_session_id is NULL while a Stripe session is being created: same problem.
-- Replace each constraint with a unique index that ignores NULLs.
-- The constraints are system-named and the names differ per database, so they are looked up.

DECLARE @drop nvarchar(max) = N'';

SELECT @drop += N'ALTER TABLE ' + QUOTENAME(OBJECT_SCHEMA_NAME(kc.parent_object_id)) + N'.' + QUOTENAME(OBJECT_NAME(kc.parent_object_id))
              + N' DROP CONSTRAINT ' + QUOTENAME(kc.name) + N';'
FROM sys.key_constraints kc
JOIN sys.index_columns ic ON ic.object_id = kc.parent_object_id AND ic.index_id = kc.unique_index_id
JOIN sys.columns c ON c.object_id = ic.object_id AND c.column_id = ic.column_id
WHERE kc.type = 'UQ'
  AND (   (kc.parent_object_id = OBJECT_ID(N'[ecommerce].[online_order]')    AND c.name = N'payment_reference')
       OR (kc.parent_object_id = OBJECT_ID(N'[ecommerce].[stripe_checkout]') AND c.name = N'checkout_session_id'))
  AND (SELECT COUNT(*) FROM sys.index_columns x WHERE x.object_id = kc.parent_object_id AND x.index_id = kc.unique_index_id) = 1;

IF LEN(@drop) > 0 EXEC sp_executesql @drop;

IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = N'ux_online_order_payment_reference'
               AND object_id = OBJECT_ID(N'[ecommerce].[online_order]'))
    EXEC(N'CREATE UNIQUE INDEX ux_online_order_payment_reference
           ON [ecommerce].[online_order](payment_reference)
           WHERE payment_reference IS NOT NULL');

IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = N'ux_stripe_checkout_session_id'
               AND object_id = OBJECT_ID(N'[ecommerce].[stripe_checkout]'))
    EXEC(N'CREATE UNIQUE INDEX ux_stripe_checkout_session_id
           ON [ecommerce].[stripe_checkout](checkout_session_id)
           WHERE checkout_session_id IS NOT NULL');
