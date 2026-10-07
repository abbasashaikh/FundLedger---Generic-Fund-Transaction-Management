# Database setup — Neon (done 07-Oct-2026)

This file records what exists and how it was created. **It contains no secrets.** Connection strings and passwords live in the owner's password manager and in GitHub Environment secrets, never in this repo.

## What exists

| Item | Value |
|---|---|
| Neon organization | `abstechsol` (Free plan) |
| Project | `fundledger` (id `damp-meadow-79622001`) |
| Region | AWS Asia Pacific 1 (Singapore), `aws-ap-southeast-1`. This is the closest Neon region to India; there is no Mumbai region. |
| PostgreSQL | 17 |
| Database | `fundledger` (schema `fl`) |
| Owner / migration role | `fundledger_owner`. Used only for schema changes, never by the API. |

| Branch | Id | Purpose | API login role |
|---|---|---|---|
| `production` (default) | `br-raspy-sky-b3n8cv5p` | Live data | `fl_api_prod` |
| `staging` | `br-holy-cake-b3ufd6hr` | Staging / UAT | `fl_api_staging` |

How the API roles are configured:
- Each API role exists **only on its own branch**.
- Each is `LOGIN`, `NOBYPASSRLS`, and a member of `fundledger_app` only. This was verified after creation.
- Each connects through the **pooled** host (`-pooler`) as required by ADR-0006.
- Schema changes use the **direct** host as `fundledger_owner`.

## Verification performed

1. `database/schema.sql` was applied to `production`.
2. `staging` was branched from `production`, so it has an identical, empty schema.
3. `database/verify_schema.sql` was run on a temporary branch and on a fresh empty database. **All 16 checks passed on Neon.** The temporary branch had an expiry time, so Neon deleted it automatically.
4. Each API role was tested by connecting to its branch:

| Check | Result |
|---|---|
| Connection | Works |
| Rows visible without tenant context | 0 |
| `DELETE` on transactions | Denied |

## Neon-specific findings (fixed in `schema.sql`)

Neon's owner role is not a superuser, which surfaced three issues:

1. **Changing a function's owner** requires the current role to be able to `SET ROLE` to the new owner, and the new owner needs `CREATE` on the schema. Both are now granted temporarily and revoked straight after.
2. **Execute permission on the pre-auth lookup functions** is now revoked from `PUBLIC` and granted only to `fundledger_app`. These functions return PIN hashes.
3. **Neon rejects weak role passwords.** The test role in `verify_schema.sql` is now `NOLOGIN`, so it needs no password.

## Free-plan limits — resolve before go-live

| Limit on Free plan | Our requirement | Action |
|---|---|---|
| Point-in-time restore window: 6 hours | 7 days (TRD §15.3, RPO ≤ 15 min) | Upgrade to a paid plan before production go-live (P6-01), **or** rely on nightly off-site dumps (RPO 24 h) until then |
| Protected branches: limit already used by other projects in the org | `production` must be protected | Upgrade, or free a protected-branch slot from another project |
| Compute scales to zero when idle | First request after idle is slower (cold start) | Acceptable for staging. For production, consider disabling auto-suspend on a paid plan. |
| Storage 0.5 GB per branch (verify current limit) | V1 needs far less | Monitor |

## How to run SQL against these branches

**Option 1 — Neon console.** Use the SQL Editor and select the branch and database `fundledger`.

**Option 2 — Python with `psycopg`.** This is how the setup was done, because Docker was unavailable at the time.

```bash
pip install "psycopg[binary]"
```

The owner's direct URL is kept in a local file outside the repo. A tiny runner script executes a `.sql` file against it with simple-query protocol, so multi-statement files work. Strip `psql` meta-commands (lines starting with `\`) first.

**Option 3 — psql in Docker.** Use the commands in the README with the branch's direct URL.

## Never

- Run `verify_schema.sql` against `production` or `staging`. It inserts test organizations and users. Use a temporary branch with an expiry time.
- Give the API the `fundledger_owner` credentials.
- Create API roles from the Neon console's Roles page. Console-created roles join `neon_superuser`. Always use SQL with `NOBYPASSRLS ... IN ROLE fundledger_app`.
