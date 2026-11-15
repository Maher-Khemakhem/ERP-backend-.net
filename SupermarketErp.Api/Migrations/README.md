# Database migrations

The API applies embedded SQL migrations at startup and records each applied version in
`platform.schema_migrations`. Migration filenames use `NNNN_name.sql`, for example
`0002_add_product_barcode.sql`. Add a new file for each schema change; do not edit a
migration that has already been applied to a shared database.

Each migration runs as one SQL Server batch inside a transaction. Do not include `GO`
batch separators. Keep migrations compatible with existing installations and include
data-preserving changes when a column or table needs to be replaced.

`0001_initial.sql` is the baseline copied from the former startup initializer. Its
guarded create statements allow it to establish a fresh database or record the current
schema on an installation that already has those tables.
