# FundLedger — notes for AI assistants

Read these before changing anything:
- `docs/01-TRD.md` (technical rules)
- `docs/04-Backend-Schema.md` (data model)
- `docs/development.md` (how to build, test and change things)
- `docs/adr/`

Accepted ADRs are decisions. Do not reverse one silently: propose a new ADR that supersedes it.

## Non-negotiables

- **No hard deletes** of transactions, revisions, attachments or audit logs. Use cancel, hide or deactivate (BR-012). The DB app role has no DELETE grant on these tables. Never add one.
- **Money** is `numeric(18,2)` / C# `decimal` (`Money`) / JSON string. Never `float`/`double`/JS `number` arithmetic for amounts. Format via `web/src/lib/format` and `IndianNumberFormat`.
- **Tenant and fund isolation:**
  - The API connects as a login role in `fundledger_app` (no BYPASSRLS).
  - The tenant context is set **per transaction** by `TenantTransactionInterceptor` (ADR-0008). Tenant data access MUST run inside a transaction: `TenantTransactionFilter` for endpoints, explicit transactions in jobs. Never use session-level `SET` (Neon's PgBouncer runs in transaction mode).
  - Every new endpoint touching data needs a two-user IDOR integration test.
- **Balances are computed** from the `v_*` views (ADR-0004). Do not add stored balances without following TRD TR-034.
- **Every financial mutation** writes an audit event plus a `transaction_revisions` snapshot in the same DB transaction.
- **Secrets:**
  - Never commit secrets. The repo is **public**.
  - Never put secrets or DB credentials in the PWA.
  - Never log PINs, tokens or full mobile numbers.
- **Schema changes:**
  - Every change is a new EF migration **and** the same change in `database/schema.sql`. CI fails on drift.
  - Never edit an applied migration.
  - Migrations are expand-only; contract one release later.
- **Branching:** work on branches and open PRs. `main` is protected.

## Layout

| Path | Contents |
|---|---|
| `api/` | .NET 10 modular monolith (Api, Application, Domain, Infrastructure, Worker) + xUnit v3 tests |
| `web/` | React 19 + Vite PWA (TanStack Query, React Router, i18next, Tailwind v4) |
| `database/` | Canonical SQL, verification, dev and ops scripts |
| `infra/vps/` | Caddy edge, per-environment Compose, `deploy.sh` |
| `.github/workflows/` | `ci.yml` (PRs), `deploy.yml` (main → staging, `v*` → production) |
| `docs/` | PRD-derived docs, ADRs, ops guides |

## Gotchas

- The local folder name contains `&`. npm scripts call tools via `node node_modules/...`; don't switch them to bare binaries or `npx`.
- `api/openapi/fundledger-api.json` and `web/src/lib/api/schema.d.ts` are generated **and committed**. Regenerate both when the API surface changes.
- `dotnet test` uses Microsoft Testing Platform (`api/global.json`). DB tests skip without Docker unless `FUNDLEDGER_TEST_ADMIN_URL` is set.
