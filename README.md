# Supermarket ERP API

The Supermarket ERP API is the backend for a supermarket operations platform. It provides the shared services used by the staff ERP application and the customer storefront, keeping products, stock, sales, purchasing, finance, and online orders in one system.

## What the API does

- Manages products, inventory, prices, and promotions.
- Supports in-store sales, tills, and cash sessions.
- Tracks suppliers, purchase orders, goods receiving, invoices, and financial records.
- Provides a customer catalog, shopping carts, and online orders.
- Integrates with Stripe test checkout for online payments.
- Protects staff and customer actions with JWT authentication and role-based permissions.

The backend is a .NET 10 ASP.NET Core modular monolith: one API process organized into feature areas such as Auth, Inventory, POS, Procurement, Finance, and Ecommerce. SQL Server stores application data. Background workers process financial events and expire unpaid online orders.

## Run locally

### Requirements

- .NET 10 SDK
- Docker Desktop
- A SQL Server client such as SQL Server Management Studio or Azure Data Studio

From the repository root, start the local SQL Server container:

```powershell
$env:MSSQL_SA_PASSWORD = "Choose-a-strong-local-password-123!"
docker compose -f docker-compose.sqlserver.yml up -d
```

Connect to `localhost,1433` as `sa` with that password and create the application database if it does not already exist:

```sql
CREATE DATABASE supermarket_erp_saas_dev;
```

Create the API's local environment file:

```powershell
Copy-Item backend-.net/SupermarketErp.Api/.env.example backend-.net/SupermarketErp.Api/.env
```

Edit `.env` and set `JWT_SECRET` to at least 32 random characters. Set `DB_PASSWORD` to the same password used for the SQL Server container. The `.env` file is ignored by Git; do not commit secrets.

Run the API from the repository root:

```powershell
$env:ASPNETCORE_ENVIRONMENT = "Development"
dotnet run --project backend-.net/SupermarketErp.Api
```

If Windows blocks the generated application executable, run it through the .NET host:

```powershell
dotnet run --project backend-.net/SupermarketErp.Api -p:UseAppHost=false
```

The API listens on `http://localhost:8080`.

- Interactive API explorer: [http://localhost:8080/swagger](http://localhost:8080/swagger) (Development only)
- OpenAPI document: [http://localhost:8080/openapi/v1.json](http://localhost:8080/openapi/v1.json) (Development only)
- Liveness check: [http://localhost:8080/health/live](http://localhost:8080/health/live)
- Database readiness check: [http://localhost:8080/health/ready](http://localhost:8080/health/ready)

On a fresh database, create the first administrator with `POST /api/auth/register` before allowing customer registration. Protected endpoints require a bearer token; the registration and login responses include one.

## Database changes

The API applies pending SQL migrations at startup and records them in `platform.schema_migrations`. The initial schema is in `SupermarketErp.Api/Migrations/0001_initial.sql`. Add a new numbered file for each future schema change, such as `0002_add_product_barcode.sql`; do not edit a migration already applied to a shared database. Migration scripts run as a single SQL Server batch inside a transaction, so do not use `GO` separators. See [the migration guide](SupermarketErp.Api/Migrations/README.md).

## Configuration

The API reads settings from environment variables or the local `.env` file. See [`SupermarketErp.Api/.env.example`](SupermarketErp.Api/.env.example) for the full template.

| Setting | Required | Purpose |
|---|---:|---|
| `JWT_SECRET` | Yes | Signs authentication tokens; must be at least 32 bytes. |
| `DB_CONNECTION_STRING` | No | Complete SQL Server connection string; takes precedence over the individual database settings. |
| `DB_HOST`, `DB_PORT`, `DB_NAME` | No | SQL Server address and database name. |
| `DB_USERNAME`, `DB_PASSWORD` | No | SQL Server credentials. |
| `APP_UPLOAD_DIR` | No | Persistent directory for product images. |
| `STRIPE_SECRET_KEY`, `STRIPE_WEBHOOK_SECRET`, `STRIPE_TND_TO_EUR_RATE` | No | Stripe test checkout settings. |
| `STRIPE_STOREFRONT_RETURN_BASE_URL` | No | Storefront URL used after checkout. |

## Build

From the repository root:

```powershell
dotnet build backend-.net/SupermarketErp.Api/SupermarketErp.Api.csproj
```

## Related project

The staff web application is available at [ERP_frontend](https://github.com/Maher-Khemakhem/ERP_frontend).