# ADR-0003 — Tenant isolation with shared schema + PostgreSQL RLS

- **Status:** Accepted
- **Date:** 06-Oct-2026

## Context

- PRD §6.1: all major records must be tenant-aware through `OrganizationId`.
- PRD §30: multi-organization SaaS is a future enhancement.
- Standard §2.4 requires RLS on user-data tables, and §2.11 requires an explicit choice of isolation model.

## Decision

- **Isolation model:** shared database, shared schema, with an `organization_id` column on every tenant table.
- **Enforcement:** PostgreSQL RLS (`ENABLE` + `FORCE`). The policies compare `organization_id` against `current_setting('app.org_id')`, which the API sets for every connection.
- **Fund-level RESTRICTIVE policies** on funds, transactions, opening balances, revisions and attachments. Members only see rows for funds they are assigned to.
- **Database roles:**
  - The API connects as `fundledger_app`: not the owner, and no `BYPASSRLS`.
  - Pre-authentication lookups use two `SECURITY DEFINER` functions owned by a NOLOGIN `BYPASSRLS` role.
- **Roles:**
  - The `ADMIN` / `MEMBER` enum, plus per-fund permission flags in `user_fund_access`.
  - There is no generic Roles/Permissions table. Two fixed roles plus fund flags cover every PRD requirement with less to misconfigure.
- **Defence in depth:** the API also applies EF Core global query filters and explicit permission checks.

## Consequences

Benefits:
- Cross-tenant and cross-fund leaks are blocked even if the API has a bug. This is verified in `database/verify_schema.sql`.

Costs and obligations:
- Every connection must have its context set and reset. Npgsql's reset-on-close plus the connection interceptor handle this, and an integration test asserts "no context → no rows".
- Reporting queries run under RLS. Indexes all lead with `fund_id` or `organization_id`.

## Alternatives considered

- **Schema-per-tenant or DB-per-tenant.** Stronger isolation, but too much operational overhead for small community organizations. It can be revisited if an enterprise or regulated tenant appears.
