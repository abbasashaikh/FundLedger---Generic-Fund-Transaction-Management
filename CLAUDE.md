# FundLedger — notes for AI assistants

Read these before changing anything:
- `docs/01-TRD.md` (technical rules)
- `docs/04-Backend-Schema.md` (data model)
- `docs/adr/`

Accepted ADRs are decisions. Do not reverse one silently: propose a new ADR that supersedes it.

## Non-negotiables

- **No hard deletes** of transactions, revisions, attachments or audit logs. Use cancel, hide or deactivate (BR-012). The DB app role has no DELETE grant on these tables. Never add one.
- **Money** is `numeric(18,2)` / C# `decimal` / JSON string. Never `float`/`double`/JS `number` arithmetic for amounts.
- **Tenant and fund isolation:**
  - The API connects as `fundledger_app` (no BYPASSRLS) and sets `app.org_id`, `app.user_id` and `app.is_admin` per connection.
  - Every new endpoint touching data needs a two-user IDOR integration test.
- **Balances are computed** from the `v_*` views (ADR-0004). Do not add stored balances without following TRD TR-034.
- **Every financial mutation** writes an audit event plus a `transaction_revisions` snapshot in the same DB transaction.
- **Secrets:**
  - Never commit secrets.
  - Never put secrets or DB credentials in the PWA.
  - Never log OTPs, tokens, PINs or full mobile numbers.
- **`database/schema.sql` is the source of truth.** EF migrations must keep `database/verify_schema.sql` passing. Update both together.
- **Branching:** work on branches and open PRs. `main` is protected.

## Layout (once code exists)

| Path | Contents |
|---|---|
| `api/` | .NET 10 solution |
| `web/` | Vite React TS PWA |
| `database/` | Canonical SQL + verification |
| `docs/` | PRD-derived docs + ADRs |
| `infra/` | Deployment |
