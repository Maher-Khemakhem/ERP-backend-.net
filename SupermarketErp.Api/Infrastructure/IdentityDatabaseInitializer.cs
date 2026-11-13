using Microsoft.Data.SqlClient;

namespace SupermarketErp.Api.Infrastructure;

public static class IdentityDatabaseInitializer
{
    // Creates the .NET-owned tables without requiring a separate migration runner.
    public static async Task EnsureCreated(SqlServerDataSource dataSource, CancellationToken ct = default)
    {
        await using var connection = await dataSource.OpenConnectionAsync(ct);
        await using var command = new SqlCommand("""
            IF SCHEMA_ID(N'platform') IS NULL EXEC(N'CREATE SCHEMA [platform]');

            IF OBJECT_ID(N'[dbo].[app_user]', N'U') IS NULL
            CREATE TABLE [dbo].[app_user] (
                id UNIQUEIDENTIFIER NOT NULL PRIMARY KEY,
                username VARCHAR(80) NOT NULL UNIQUE,
                display_name VARCHAR(120) NOT NULL,
                password_hash VARCHAR(255) NOT NULL,
                enabled BIT NOT NULL,
                created_at DATETIMEOFFSET NOT NULL,
                version BIGINT NOT NULL
            );

            IF OBJECT_ID(N'[dbo].[app_user_roles]', N'U') IS NULL
            CREATE TABLE [dbo].[app_user_roles] (
                user_id UNIQUEIDENTIFIER NOT NULL REFERENCES [dbo].[app_user](id),
                role VARCHAR(40) NOT NULL,
                PRIMARY KEY (user_id, role),
                CONSTRAINT app_user_roles_role_check CHECK (role IN (
                    'ADMIN', 'MANAGER', 'CASHIER', 'INVENTORY_MANAGER', 'ACCOUNTANT', 'CUSTOMER'
                ))
            );

            IF OBJECT_ID(N'[platform].[dotnet_outbox]', N'U') IS NULL
            CREATE TABLE [platform].[dotnet_outbox] (
                id UNIQUEIDENTIFIER NOT NULL PRIMARY KEY,
                event_type VARCHAR(160) NOT NULL,
                payload NVARCHAR(MAX) NOT NULL,
                occurred_at DATETIMEOFFSET NOT NULL,
                processed_at DATETIMEOFFSET NULL,
                attempts INTEGER NOT NULL DEFAULT 0,
                last_error NVARCHAR(MAX) NULL
            );

            IF NOT EXISTS (
                SELECT 1 FROM sys.indexes
                WHERE name = N'ix_dotnet_outbox_pending'
                  AND object_id = OBJECT_ID(N'[platform].[dotnet_outbox]')
            )
            CREATE INDEX ix_dotnet_outbox_pending
                ON [platform].[dotnet_outbox](occurred_at)
                WHERE processed_at IS NULL;

            IF SCHEMA_ID(N'inventory') IS NULL EXEC(N'CREATE SCHEMA [inventory]');
            IF SCHEMA_ID(N'pricing') IS NULL EXEC(N'CREATE SCHEMA [pricing]');
            IF SCHEMA_ID(N'pos') IS NULL EXEC(N'CREATE SCHEMA [pos]');
            IF SCHEMA_ID(N'procurement') IS NULL EXEC(N'CREATE SCHEMA [procurement]');
            IF SCHEMA_ID(N'finance') IS NULL EXEC(N'CREATE SCHEMA [finance]');
            IF SCHEMA_ID(N'ecommerce') IS NULL EXEC(N'CREATE SCHEMA [ecommerce]');

            IF OBJECT_ID(N'[inventory].[product_family]', N'U') IS NULL
            CREATE TABLE [inventory].[product_family] (
                id UNIQUEIDENTIFIER NOT NULL PRIMARY KEY,
                name VARCHAR(160) NOT NULL UNIQUE,
                image_url VARCHAR(500) NULL
            );

            IF OBJECT_ID(N'[inventory].[product]', N'U') IS NULL
            CREATE TABLE [inventory].[product] (
                id UNIQUEIDENTIFIER NOT NULL PRIMARY KEY,
                sku VARCHAR(255) NOT NULL UNIQUE,
                name VARCHAR(255) NOT NULL,
                family_id UNIQUEIDENTIFIER NULL,
                image_url VARCHAR(500) NULL,
                online_visible BIT NOT NULL DEFAULT 1,
                unit_type VARCHAR(255) NOT NULL,
                perishable BIT NOT NULL,
                reorder_point INT NULL,
                vat_rate DECIMAL(5,2) NULL,
                CONSTRAINT product_unit_type_check CHECK (unit_type IN ('EACH', 'WEIGHT'))
            );

            IF OBJECT_ID(N'[inventory].[stock_level]', N'U') IS NULL
            CREATE TABLE [inventory].[stock_level] (
                id UNIQUEIDENTIFIER NOT NULL PRIMARY KEY,
                product_id UNIQUEIDENTIFIER NOT NULL UNIQUE,
                version BIGINT NOT NULL
            );

            IF OBJECT_ID(N'[inventory].[lot]', N'U') IS NULL
            CREATE TABLE [inventory].[lot] (
                id UNIQUEIDENTIFIER NOT NULL PRIMARY KEY,
                lot_number VARCHAR(255) NOT NULL,
                product_id UNIQUEIDENTIFIER NOT NULL,
                expiry_date DATE NULL,
                received_date DATE NOT NULL,
                supplier_reference VARCHAR(255) NULL,
                unit_cost DECIMAL(19,3) NULL
            );

            IF OBJECT_ID(N'[inventory].[lot_entry]', N'U') IS NULL
            CREATE TABLE [inventory].[lot_entry] (
                id UNIQUEIDENTIFIER NOT NULL PRIMARY KEY,
                stock_level_id UNIQUEIDENTIFIER NULL,
                lot_id UNIQUEIDENTIFIER NOT NULL UNIQUE,
                on_hand_value DECIMAL(38,2) NULL,
                on_hand_unit_type SMALLINT NULL,
                reserved_value DECIMAL(38,2) NULL,
                reserved_unit_type SMALLINT NULL
            );

            IF OBJECT_ID(N'[pricing].[product_price]', N'U') IS NULL
            CREATE TABLE [pricing].[product_price] (
                id UNIQUEIDENTIFIER NOT NULL PRIMARY KEY,
                product_id UNIQUEIDENTIFIER NOT NULL UNIQUE,
                unit_price DECIMAL(38,2) NULL
            );

            IF OBJECT_ID(N'[pricing].[promotion]', N'U') IS NULL
            CREATE TABLE [pricing].[promotion] (
                id UNIQUEIDENTIFIER NOT NULL PRIMARY KEY,
                product_id UNIQUEIDENTIFIER NOT NULL,
                type VARCHAR(255) NOT NULL,
                discount_percent DECIMAL(38,2) NOT NULL,
                valid_from DATETIMEOFFSET NOT NULL,
                valid_until DATETIMEOFFSET NOT NULL,
                active BIT NOT NULL
            );

            IF OBJECT_ID(N'[pos].[till]', N'U') IS NULL
            CREATE TABLE [pos].[till] (
                id UNIQUEIDENTIFIER NOT NULL PRIMARY KEY,
                till_number VARCHAR(255) NOT NULL UNIQUE,
                status VARCHAR(255) NOT NULL,
                version BIGINT NOT NULL
            );

            IF OBJECT_ID(N'[pos].[cash_session]', N'U') IS NULL
            CREATE TABLE [pos].[cash_session] (
                id UNIQUEIDENTIFIER NOT NULL PRIMARY KEY,
                till_id UNIQUEIDENTIFIER NOT NULL,
                cashier_name VARCHAR(255) NULL,
                opening_float DECIMAL(38,2) NULL,
                expected_cash DECIMAL(38,2) NULL,
                counted_cash DECIMAL(38,2) NULL,
                status VARCHAR(255) NOT NULL,
                opened_at DATETIMEOFFSET NOT NULL,
                closed_at DATETIMEOFFSET NULL,
                version BIGINT NOT NULL
            );

            IF OBJECT_ID(N'[pos].[pos_transaction]', N'U') IS NULL
            CREATE TABLE [pos].[pos_transaction] (
                id UNIQUEIDENTIFIER NOT NULL PRIMARY KEY,
                cash_session_id UNIQUEIDENTIFIER NOT NULL,
                status VARCHAR(255) NOT NULL,
                payment_method VARCHAR(255) NULL,
                started_at DATETIMEOFFSET NOT NULL,
                completed_at DATETIMEOFFSET NULL,
                version BIGINT NOT NULL
            );

            IF OBJECT_ID(N'[pos].[transaction_line]', N'U') IS NULL
            CREATE TABLE [pos].[transaction_line] (
                id UNIQUEIDENTIFIER NOT NULL PRIMARY KEY,
                transaction_id UNIQUEIDENTIFIER NULL,
                product_id UNIQUEIDENTIFIER NOT NULL,
                value DECIMAL(38,2) NULL,
                unit_type SMALLINT NULL,
                line_total DECIMAL(38,2) NULL,
                vat_amount DECIMAL(19,3) NULL
            );

            IF OBJECT_ID(N'[procurement].[supplier]', N'U') IS NULL
            CREATE TABLE [procurement].[supplier] (
                id UNIQUEIDENTIFIER NOT NULL PRIMARY KEY,
                name VARCHAR(255) NOT NULL,
                contact_phone VARCHAR(255) NULL,
                contact_email VARCHAR(255) NULL,
                address VARCHAR(500) NULL
            );

            IF OBJECT_ID(N'[procurement].[purchase_order]', N'U') IS NULL
            CREATE TABLE [procurement].[purchase_order] (
                id UNIQUEIDENTIFIER NOT NULL PRIMARY KEY,
                supplier_id UNIQUEIDENTIFIER NOT NULL,
                status VARCHAR(255) NOT NULL,
                placed_at DATETIMEOFFSET NOT NULL
            );

            IF OBJECT_ID(N'[procurement].[purchase_order_line]', N'U') IS NULL
            CREATE TABLE [procurement].[purchase_order_line] (
                id UNIQUEIDENTIFIER NOT NULL PRIMARY KEY,
                purchase_order_id UNIQUEIDENTIFIER NULL,
                product_id UNIQUEIDENTIFIER NOT NULL,
                value DECIMAL(38,2) NULL,
                unit_type SMALLINT NULL,
                unit_cost DECIMAL(38,2) NULL
            );

            IF OBJECT_ID(N'[procurement].[low_stock_alert]', N'U') IS NULL
            CREATE TABLE [procurement].[low_stock_alert] (
                id UNIQUEIDENTIFIER NOT NULL PRIMARY KEY,
                product_id UNIQUEIDENTIFIER NOT NULL,
                raised_at DATETIMEOFFSET NOT NULL,
                acknowledged BIT NOT NULL
            );

            IF OBJECT_ID(N'[finance].[journal_entry]', N'U') IS NULL
            CREATE TABLE [finance].[journal_entry] (
                id UNIQUEIDENTIFIER NOT NULL PRIMARY KEY,
                event_key VARCHAR(255) NOT NULL UNIQUE,
                description VARCHAR(255) NOT NULL,
                reference_id UNIQUEIDENTIFIER NULL,
                posted_at DATETIMEOFFSET NOT NULL
            );

            IF OBJECT_ID(N'[finance].[journal_line]', N'U') IS NULL
            CREATE TABLE [finance].[journal_line] (
                id UNIQUEIDENTIFIER NOT NULL PRIMARY KEY,
                journal_entry_id UNIQUEIDENTIFIER NOT NULL,
                account_code VARCHAR(255) NOT NULL,
                debit_amount DECIMAL(19,3) NOT NULL,
                credit_amount DECIMAL(19,3) NOT NULL,
                description VARCHAR(255) NOT NULL
            );

            IF OBJECT_ID(N'[finance].[ledger_entry]', N'U') IS NULL
            CREATE TABLE [finance].[ledger_entry] (
                id UNIQUEIDENTIFIER NOT NULL PRIMARY KEY,
                type VARCHAR(255) NOT NULL,
                amount DECIMAL(38,2) NULL,
                description VARCHAR(255) NOT NULL,
                reference_id UNIQUEIDENTIFIER NULL,
                recorded_at DATETIMEOFFSET NOT NULL
            );

            IF OBJECT_ID(N'[finance].[supplier_invoice]', N'U') IS NULL
            CREATE TABLE [finance].[supplier_invoice] (
                id UNIQUEIDENTIFIER NOT NULL PRIMARY KEY,
                supplier_id UNIQUEIDENTIFIER NOT NULL,
                purchase_order_id UNIQUEIDENTIFIER NULL,
                invoice_number VARCHAR(255) NOT NULL UNIQUE,
                subtotal DECIMAL(19,3) NOT NULL,
                tax_amount DECIMAL(19,3) NOT NULL,
                total_amount DECIMAL(19,3) NOT NULL,
                paid_amount DECIMAL(19,3) NOT NULL,
                status VARCHAR(255) NOT NULL,
                issue_date DATE NOT NULL,
                due_date DATE NULL,
                created_at DATETIMEOFFSET NOT NULL,
                version BIGINT NOT NULL
            );

            IF OBJECT_ID(N'[finance].[supplier_payment]', N'U') IS NULL
            CREATE TABLE [finance].[supplier_payment] (
                id UNIQUEIDENTIFIER NOT NULL PRIMARY KEY,
                payment_reference VARCHAR(255) NOT NULL UNIQUE,
                invoice_id UNIQUEIDENTIFIER NOT NULL,
                amount DECIMAL(19,3) NOT NULL,
                payment_method VARCHAR(255) NOT NULL,
                paid_date DATE NOT NULL
            );

            IF OBJECT_ID(N'[ecommerce].[cart]', N'U') IS NULL
            CREATE TABLE [ecommerce].[cart] (
                id UNIQUEIDENTIFIER NOT NULL PRIMARY KEY,
                customer_id UNIQUEIDENTIFIER NOT NULL UNIQUE,
                version BIGINT NOT NULL,
                created_at DATETIMEOFFSET NOT NULL,
                updated_at DATETIMEOFFSET NOT NULL
            );

            IF OBJECT_ID(N'[ecommerce].[cart_item]', N'U') IS NULL
            CREATE TABLE [ecommerce].[cart_item] (
                id UNIQUEIDENTIFIER NOT NULL PRIMARY KEY,
                cart_id UNIQUEIDENTIFIER NOT NULL,
                product_id UNIQUEIDENTIFIER NOT NULL,
                quantity_value DECIMAL(19,3) NOT NULL,
                quantity_unit_type VARCHAR(20) NOT NULL,
                CONSTRAINT cart_item_cart_product_unique UNIQUE (cart_id, product_id)
            );

            IF OBJECT_ID(N'[ecommerce].[online_order]', N'U') IS NULL
            CREATE TABLE [ecommerce].[online_order] (
                id UNIQUEIDENTIFIER NOT NULL PRIMARY KEY,
                order_number VARCHAR(50) NOT NULL UNIQUE,
                customer_id UNIQUEIDENTIFIER NOT NULL,
                status VARCHAR(30) NOT NULL,
                net_amount DECIMAL(19,3) NOT NULL,
                vat_amount DECIMAL(19,3) NOT NULL,
                total_amount DECIMAL(19,3) NOT NULL,
                payment_reference VARCHAR(120) NULL UNIQUE,
                created_at DATETIMEOFFSET NOT NULL,
                confirmed_at DATETIMEOFFSET NULL,
                version BIGINT NOT NULL,
                expires_at DATETIMEOFFSET NULL,
                cancelled_at DATETIMEOFFSET NULL,
                expired_at DATETIMEOFFSET NULL,
                pickup_code VARCHAR(12) NULL,
                picked_up_at DATETIMEOFFSET NULL,
                picked_up_by VARCHAR(80) NULL
            );

            IF OBJECT_ID(N'[ecommerce].[online_order_line]', N'U') IS NULL
            CREATE TABLE [ecommerce].[online_order_line] (
                id UNIQUEIDENTIFIER NOT NULL PRIMARY KEY,
                online_order_id UNIQUEIDENTIFIER NOT NULL,
                product_id UNIQUEIDENTIFIER NOT NULL,
                product_name VARCHAR(255) NOT NULL,
                quantity_value DECIMAL(19,3) NOT NULL,
                quantity_unit_type VARCHAR(20) NOT NULL,
                unit_price DECIMAL(19,3) NOT NULL,
                line_total DECIMAL(19,3) NOT NULL,
                vat_amount DECIMAL(19,3) NOT NULL
            );

            IF OBJECT_ID(N'[ecommerce].[stripe_checkout]', N'U') IS NULL
            CREATE TABLE [ecommerce].[stripe_checkout] (
                id UNIQUEIDENTIFIER NOT NULL PRIMARY KEY,
                order_id UNIQUEIDENTIFIER NOT NULL UNIQUE,
                checkout_session_id VARCHAR(255) NULL UNIQUE,
                checkout_url VARCHAR(MAX) NULL,
                tnd_amount DECIMAL(19,3) NOT NULL,
                eur_cents BIGINT NOT NULL,
                tnd_to_eur_rate DECIMAL(18,8) NOT NULL,
                status VARCHAR(20) NOT NULL,
                version BIGINT NOT NULL,
                created_at DATETIMEOFFSET NOT NULL
            );
            """, connection);
        await command.ExecuteNonQueryAsync(ct);
    }
}

