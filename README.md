# Java to .NET Backend Migration Guide

This guide describes the Java-to-.NET backend migration. The ASP.NET Core API in `backend-.net/SupermarketErp.Api` now covers the documented authentication, inventory, pricing, POS, procurement, finance, storefront, and Stripe test-checkout route families. It has not yet completed contract-level parity review or production cutover work.

## Run the .NET API

Install the **.NET 10 SDK** for Windows from the [official .NET download page](https://dotnet.microsoft.com/en-us/download/dotnet/10.0). The SDK is required to build and run the API. After installation, open a new PowerShell window and check `dotnet --version`.

Copy `backend-.net/SupermarketErp.Api/.env.example` to `backend-.net/SupermarketErp.Api/.env`, then edit the database password and local signing key. The API reads that `.env` file on startup; process-level environment variables take precedence. The file is ignored by Git.

For local SQL Server, start the container from the repository root:

```powershell
$env:MSSQL_SA_PASSWORD = "Use-a-local-development-password-123!"
docker compose -f docker-compose.sqlserver.yml up -d
```

The password must satisfy SQL Server complexity requirements. Do not commit it or use it in a shared deployment.

From the repository root, start the API:

```powershell
Copy-Item backend-.net/SupermarketErp.Api/.env.example backend-.net/SupermarketErp.Api/.env
# Edit the .env file before starting the API.
dotnet run --project backend-.net/SupermarketErp.Api
```

The API listens on port `8080` and connects to SQL Server using `Microsoft.Data.SqlClient`. The .NET-owned identity and outbox tables are created at startup. Business tables must be provisioned in the SQL Server database before use; the Java Flyway migrations are PostgreSQL-specific and must not be applied directly to SQL Server. OpenAPI is available at `/openapi/v1.json` in Development, and readiness is available at `/health/ready`.

## Build a .NET container

The existing `Dockerfile.backend` still packages the Java service. Build the .NET image separately while migration and parity work is in progress:

```powershell
docker build -f Dockerfile.backend-dotnet -t supermarket-erp-api-dotnet .
```

Pass the same SQL Server and JWT environment variables shown above when running the image. Mount a persistent directory at `/data/uploads/products` so uploaded product images survive container replacement. This image does not run database migrations. Keep production traffic on the current deployment until the .NET service has passed contract, database snapshot, and staging checks.

The .NET implementation includes those route groups plus `/api/store/*`, `/api/online-orders/*`, and `/api/store/stripe/*`. It adds a PostgreSQL outbox for sales, cash-session closes, low-stock alerts, and goods receipts, plus a worker that expires unpaid online orders and releases reservations. Stripe checkout remains test-mode only. Runtime/database behavior still needs review against a migrated PostgreSQL snapshot and the Java OpenAPI contract.

## Current system

- `ERP/` is a Java 21, Spring Boot modular monolith exposing the REST API on port `8080` under `/api`.
- The API serves both `supermarket-erp/` (staff) and `supermarket-storefront/` (customers). The phone scanner also uses the API.
- The Java backend uses PostgreSQL and Flyway. The .NET backend uses SQL Server and `Microsoft.Data.SqlClient`; its SQL schema requires a separate SQL Server migration set.
- Business areas include `auth`, `inventory`, `pricing`, `pos`, `procurement`, `finance`, `ecommerce`, and `shared`.
- POS, online checkout, inventory, and procurement coordinate transactional operations. Finance and procurement also consume domain events.
- The current backend API and implementation details are documented in [ERP/README.md](../ERP/README.md). Read that endpoint map and the migration scripts before porting a feature.

## Recommended architecture

Keep a **modular monolith** in ASP.NET Core, matching the current system's overall shape. Run one backend application and use one SQL Server database, while keeping business areas such as inventory, POS, and finance behind clear module boundaries. A direct framework replacement should preserve those boundaries and externally visible behavior before introducing new architecture.

Within each module, use a simple layered structure:

```text
HTTP/API -> application services -> domain model
                 |                     |
                 +---- interfaces ------+
                           |
                    infrastructure (EF Core, PostgreSQL, external services)
```

Controllers handle HTTP concerns. Application services coordinate a use case and its transaction. Domain objects enforce business rules and state transitions. Infrastructure implements persistence and external integrations. Keep interfaces at module boundaries so, for example, POS calls the inventory module's public stock service instead of its repositories.

### Where CQRS fits

CQRS means separating the model used to change state (commands) from the model used to read state (queries). It is a design pattern that can be used inside a modular monolith; it does not mean the application must use microservices, separate databases, or an event stream.

For this migration, **do not adopt full CQRS as a prerequisite**. Keep ordinary reads and writes straightforward, and keep each business mutation in an application service with the database transaction it needs. Add a separate query model only where it solves a real problem, such as complex finance reports or a dashboard assembled from several modules. The existing domain events and replacement outbox are for reliable cross-module reactions; they do not by themselves mean the whole application follows CQRS.

This choice keeps critical operations such as completing a sale, reserving stock, and receiving a purchase order synchronous and atomic in SQL Server. It also avoids introducing read-model synchronization and eventual consistency across ordinary screens before there is a demonstrated need.

| Current | .NET equivalent to evaluate |
|---|---|
| Spring Boot application | ASP.NET Core Web API, using a supported .NET LTS release |
| Spring MVC controllers | ASP.NET Core controllers or Minimal APIs; use one consistent style |
| Spring Security resource server | ASP.NET Core authentication/authorization with JWT bearer validation |
| Spring Data JPA / Hibernate | Explicit SQL with `Microsoft.Data.SqlClient`; EF Core can be introduced after the SQL Server schema is established |
| Jakarta validation | ASP.NET Core model validation and explicit domain validation |
| `@Transactional` | EF Core/database transactions around the same business operations |
| Spring Modulith modules | Separate .NET projects or feature folders with enforced dependency rules |
| Modulith persisted event publications | A SQL Server transactional outbox and idempotent consumers |
| Flyway | A separate SQL Server migration set; PostgreSQL Flyway scripts are not portable without conversion |
| Springdoc OpenAPI | ASP.NET Core OpenAPI generation, preserving the API contract |

These are starting points, not one-to-one substitutions. Keep business rules in domain/application services instead of moving them into controllers or EF entities.

## Compatibility requirements

Treat the current backend as the behavioral specification. Before replacing an endpoint, record its route, HTTP method, request and response JSON, status codes, authorization roles, and side effects. Preserve these contracts so neither React application needs a coordinated rewrite.

- Keep `/api` routes and the development API port `8080` initially. Preserve the frontend's bearer-token flow and Vite proxy behavior.
- Preserve role names and route access: `ADMIN`, `MANAGER`, `CASHIER`, `INVENTORY_MANAGER`, and `ACCOUNTANT`.
- Match JSON field names, UUID formats, decimal precision, date/time formats, null handling, error responses, and pagination conventions.
- Preserve `Money` in TND at three decimal places, and `Quantity` rules (`EACH` versus kilograms with three decimal places).
- Keep stock allocation FEFO, reservation accounting, optimistic concurrency behavior, and rollback guarantees for sale, checkout, and receipt operations.
- Preserve event idempotency and journal/payment unique-key behavior. Event delivery is not a reason to post duplicate finance records.
- Preserve product image uploads, scanner pairing, Stripe test checkout and webhook signature checks, scheduled order expiry, and their configuration.
- Preserve first-admin setup, BCrypt password verification, JWT signing configuration, token claims, and expiration behavior. Do not reset or silently invalidate user accounts during cutover.

Current caveats and behaviors to verify are called out in [AGENTS.md](../AGENTS.md) and [ERP/README.md](../ERP/README.md). Confirm their current status before treating them as requirements or fixing them as part of the port.

## Database and data migration

The first .NET version should connect to the existing PostgreSQL database and existing schemas. Do not recreate tables from EF models or drop Spring infrastructure tables during the application rewrite.

1. Inventory every migration under `ERP/src/main/resources/db/migration`, including compatibility and data-backfill scripts. Record the applied Flyway version in each target environment.
2. Map each entity/value object to its existing table, schema, constraints, indexes, enum representation, timestamp convention, and numeric precision.
3. Keep Flyway as the only schema-change authority during the initial port. The .NET service can use the existing database without using JPA. If moving to EF migrations later, define a deliberate baseline and ownership handoff; do not let Flyway and EF independently mutate the same schema.
4. Retain all existing records and IDs. Confirm that EF does not accidentally treat logical cross-module UUIDs as new foreign keys or rename schemas/tables based on conventions.
5. Identify Spring Modulith publication-registry tables separately from business data. Build and operate the replacement outbox before retiring the old event consumers or registry; do not assume the old registry is a compatible .NET queue.
6. Test upgrade behavior against a copy of a production-like database, including startup with `ddl-auto: validate`-equivalent checks and all supported Flyway histories.

## Suggested solution layout

The implementation starts as one ASP.NET Core project with feature folders. It can be split into module projects later if that improves dependency enforcement without slowing the port:

```text
backend-.net/
  SupermarketErp.Api/
    Features/Auth/
    Features/Inventory/             Catalog, product families, stock, receiving
    Features/Pricing/                Product prices and promotions
    Features/Procurement/            Suppliers, purchase orders, alerts
    Features/Pos/                    Tills, sessions, sales, scanner pairing
    Features/Finance/                Journals, reports, invoices, payments
    Infrastructure/
```

Keep module dependencies explicit. For example, POS and ecommerce may call the public inventory and pricing application interfaces; finance should react to business events through the outbox rather than being called directly from every workflow. Avoid references to another module's persistence implementation.

## Migration sequence

### 1. Establish the contract

- Export or capture the OpenAPI document from the running Java API and retain it as the baseline.
- Build an endpoint inventory from [ERP/README.md](../ERP/README.md), controllers, security configuration, and the two frontend API clients.
- Record important behavior and failure cases for sale completion, online checkout/payment/cancellation, goods receipt, cash-session close, supplier payment, and user setup.
- Decide which known caveats are intentionally preserved and which are separate product changes.

### 2. Create the .NET foundation

- Add the solution, API host, module projects, PostgreSQL provider, structured logging, configuration binding, health/readiness endpoints, and OpenAPI generation.
- Read secrets from environment variables or the deployment secret store. Never copy local Java defaults into shared environments.
- Configure the service to listen on port `8080` and to use the existing `/api` prefix.
- Add architecture checks that prevent forbidden module-to-module persistence references.

### 3. Port cross-cutting behavior first

- Implement consistent API errors and validation, UTC/date handling, decimal precision, request logging, and database transaction handling.
- Port JWT validation and authorization policies, preserving claim names and role mappings.
- Verify BCrypt hashes already stored in PostgreSQL can be checked by the chosen .NET password-hashing implementation. Keep compatibility with existing hashes or plan an explicit, secure rehash-on-login approach.
- Implement outbox persistence, retry/observability policy, and idempotency keys before moving event-driven finance or alert flows.

### 4. Port modules in dependency order

Suggested order: `shared` and `auth`; `inventory`; `pricing`; `procurement`; `pos`; `finance`; `ecommerce`. Adjust based on dependencies and deployment needs. Port read-only/query paths first when useful, then mutations and their side effects. Keep business transitions and invariants inside domain/application services.

### 5. Compare behavior continuously

- Run the Java and .NET APIs against separate clones of the same sanitized database snapshot.
- Replay representative requests and compare status codes, JSON, database effects, emitted events, journal entries, stock balances, and reservation balances.
- Exercise concurrency and failure paths, not only successful requests: insufficient stock, duplicate webhook/event delivery, stale versions, invalid tender, incomplete receipt, and expired orders.
- Add contract, unit, and PostgreSQL integration tests for each ported behavior. Keep the existing Java test suite passing until Java is retired.

### 6. Deploy and cut over safely

- Update `Dockerfile.backend`, CI/build workflows, local developer setup, installer/runtime packaging, and operational runbooks for the chosen .NET runtime.
- Keep the same reverse-proxy route, TLS boundary, port, environment-variable semantics where practical, upload volume, and health checks.
- Use a staging database clone and verify backup/restore before production cutover.
- Do not send writes to both implementations against the same database. Choose one active writer; use a controlled traffic switch with a rollback plan.
- Watch API errors, auth failures, database conflicts, outbox backlog, journal totals, stock/reservation balances, Stripe webhook results, and scheduled expiry after cutover.
- Retire Java deployment artifacts and Spring event infrastructure only after the .NET service is stable and rollback requirements are satisfied.

## Repository work checklist

- [ ] Capture the Java OpenAPI contract and inventory frontend API usage.
- [x] Choose .NET 10 and pin the project package versions.
- [x] Add the .NET API project, auth, inventory, pricing, procurement, POS, and finance endpoints.
- [x] Add a transactional outbox for POS, receipt, cash-close, and low-stock events.
- [x] Implement the endpoint families documented in `ERP/README.md`.
- [ ] Enforce module dependency boundaries inside the .NET codebase.
- [x] Configure the API to use the existing PostgreSQL schemas while Flyway remains the migration owner.
- [x] Implement authentication, roles, BCrypt password verification, and JWT behavior.
- [x] Implement inventory/pricing and transactional stock operations.
- [x] Port POS, procurement, finance, and ecommerce workflows.
- [x] Implement the transactional outbox, retries, and idempotent event consumers.
- [x] Port file uploads, scanner pairing, Stripe test checkout, and scheduled expiry.
- [x] Add a separate .NET Dockerfile and document container startup.
- [ ] Update CI, installer, and operational scripts after runtime parity is confirmed.
- [ ] Compare API and database behavior on a sanitized database snapshot.
- [ ] Complete staging, backup/restore, production cutover, monitoring, and rollback rehearsal.
- [ ] Remove Java only after all consumers and deployment paths use .NET.

## Related documentation

- [Backend architecture and REST endpoint map](../ERP/README.md)
- [Repository overview and local setup](../README.md)
- [Contributor architecture rules](../AGENTS.md)
- [Operations and backup/restore](../ops/windows/README.md)
