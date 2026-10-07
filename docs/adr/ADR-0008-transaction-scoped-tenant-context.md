# ADR-0008 — Tenant context is set per transaction, not per connection

- **Status:** Accepted, 07-Oct-2026 (Phase 0). Refines TRD TR-002.
- **Date:** 07-Oct-2026

## Context

Row-Level Security (ADR-0003) reads `app.org_id`, `app.user_id` and `app.is_admin` from the PostgreSQL session. TRD TR-002 originally said to set these "on every pooled connection open", at **session** level.

In production, the API connects through Neon's PgBouncer in **transaction** pooling mode (ADR-0006). In that mode:
- one client connection can be served by different server connections from one transaction to the next
- a server connection is handed to other clients between transactions

A session-level setting can therefore apply to the wrong queries, or stay behind on a server connection that another request then uses. That would be a cross-tenant data leak, which is exactly what RLS exists to prevent.

## Decision

1. The API sets the context with `set_config(name, value, is_local => true)` **at the start of every database transaction**, through an EF Core `DbTransactionInterceptor` (`TenantTransactionInterceptor`).
2. Transaction-local settings disappear at COMMIT or ROLLBACK, and PgBouncer pins one server connection for the life of a transaction. The context is therefore always on the same connection as the queries it protects, and never outlives them.
3. All tenant data access runs inside a transaction:
   - **API endpoints:** authenticated route groups use `TenantTransactionFilter`, one transaction per request, committed on success and rolled back on error results or exceptions.
   - **Jobs:** the worker opens explicit transactions.
4. **Fail-closed.** A query that runs outside a transaction has no tenant context, so RLS returns **no rows**. A coding mistake shows up as missing data, never as another tenant's data.

## Verification

Integration tests in `TenantIsolationTests` passed against Neon **through the pooler** on 07-Oct-2026:

| Test | Result |
|---|---|
| Tenant A sees only A | ✓ |
| Tenant B sees only B | ✓ |
| No transaction → zero rows | ✓ |
| After COMMIT on the same physical connection → zero rows (no leak) | ✓ |
| Runtime role cannot DELETE transactions | ✓ |

CI runs the same tests against PostgreSQL 17 on every PR.

## Consequences

- Every request that touches tenant data holds one transaction for its duration. At V1 scale (50 users) this is negligible, and it also gives each request a consistent view of the data.
- Developers must not query tenant tables outside the filter or an explicit transaction. If they do, they get empty results, which tests catch quickly.
- Reports that need long reads still run within one transaction. That is fine at V1 volumes; revisit if a report ever needs more than a few seconds.

## Alternatives considered

- **Session-level `SET` on connection open.** Unsafe with transaction pooling, as described above. Rejected.
- **Session pooling or a direct (unpooled) connection.** Session-level context would be safe there, but it gives up the pooler that serverless Postgres needs (Standard §6.4). Rejected.
- **Prefixing every SQL command with `SET LOCAL`.** Fragile across Npgsql batching and EF query generation. Rejected.
