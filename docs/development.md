# Local development

## Prerequisites

| Tool | Version | Notes |
|---|---|---|
| .NET SDK | 10.0.203+ | Pinned by `api/global.json` |
| Node.js | 22 LTS | |
| Docker Desktop | current | For local Postgres + MinIO, and for the database integration tests |
| Python 3 + Pillow | optional | Only to regenerate PWA icons (`web/scripts/generate-icons.py`) |

> **Windows note.** This repo's local folder name contains `&`. That breaks npm's `.cmd` shims, because `cmd.exe` treats `&` as a command separator. The npm scripts in `web/package.json` therefore call each tool through `node node_modules/...` directly. Use `npm run <script>`, not `npx <tool>`.

## Repository layout

| Path | Contents |
|---|---|
| `api/` | .NET 10 solution: Api, Application, Domain, Infrastructure, Worker + tests |
| `api/openapi/fundledger-api.json` | OpenAPI document, generated on build and committed |
| `web/` | React 19 + Vite PWA |
| `database/schema.sql` | Canonical schema (must equal the result of all EF migrations; CI checks) |
| `database/verify_schema.sql` | Business-rule and RLS assertions |
| `database/dev/` | Local-only helpers |
| `database/ops/` | One-off operational scripts that were run against real environments |
| `infra/vps/` | Caddy edge + per-environment Compose + `deploy.sh` |
| `.github/workflows/` | CI (`ci.yml`) and deploy (`deploy.yml`) |
| `docker-compose.yml` | Local Postgres 17 (port 5433) + MinIO |

## First-time setup

1. Start the local services:

```bash
docker compose up -d
```

2. Apply migrations to the local database as its superuser:

```bash
cd api && ConnectionStrings__Migrations="Host=localhost;Port=5433;Database=fundledger;Username=postgres;Password=fundledger_local_only" dotnet run --project src/FundLedger.Api -- migrate
```

3. Create the local API login role:

```bash
docker compose exec -T postgres psql -U postgres -d fundledger -f - < database/dev/create_dev_app_role.sql
```

4. Install the web dependencies:

```bash
cd web && npm ci
```

### Without Docker

You can point the API at the Neon **staging** branch for local work. The setting is stored in your user profile, outside the repo, and overrides `appsettings.Development.json`:

```bash
cd api && dotnet user-secrets set "ConnectionStrings:FundLedger" "<fl_api_staging pooled URL>" --project src/FundLedger.Api
```

Remove it later with `dotnet user-secrets clear --project src/FundLedger.Api`.

## Run

| What | Command | URL |
|---|---|---|
| API | `cd api && dotnet run --project src/FundLedger.Api` | http://localhost:5087 (`/health/ready`, `/api/v1/openapi.json`) |
| Worker | `cd api && dotnet run --project src/FundLedger.Worker` | logs only |
| PWA | `cd web && npm run dev` | http://localhost:5173 |

The Claude desktop preview uses `.claude/launch.json`, which defines the `fundledger-api` and `fundledger-web` configurations.

## Test and check (same as CI)

**API:**

```bash
cd api && dotnet build && dotnet test
```

**Web:**

```bash
cd web && npm run lint && npm run typecheck && npm test && npm run check:api
```

Notes on the API tests:
- Database tests use Testcontainers (PostgreSQL 17) when Docker is running.
- Without Docker, set `FUNDLEDGER_TEST_ADMIN_URL` to an owner URL of a **temporary** Neon branch. The tests create and drop their own database there.
- Otherwise the database tests are reported as skipped. CI sets `FUNDLEDGER_REQUIRE_DB_TESTS=true`, so they can never be skipped there.

## Changing the database

1. Write a new EF migration:

```bash
cd api && dotnet ef migrations add <Name> --project src/FundLedger.Infrastructure --startup-project src/FundLedger.Infrastructure --output-dir Persistence/Migrations
```

   Put anything EF can't express (RLS policies, triggers, grants, views) into the migration with `migrationBuilder.Sql(...)`.

2. Make the **same** change in `database/schema.sql`. If business rules change, extend `database/verify_schema.sql` too.

3. Follow expand-and-contract (TRD TR-091): additive changes first, removals one release later. Add a rollback note to the PR.

CI rebuilds the schema both ways, fails on any difference, and runs `verify_schema.sql` on both.

**Never edit an applied migration** (for example `20261007033113_InitialSchema` and its SQL).

## Changing the API contract

1. Building the API regenerates `api/openapi/fundledger-api.json`. Commit it.
2. Regenerate the typed client and commit `web/src/lib/api/schema.d.ts`:

```bash
cd web && npm run gen:api
```

CI fails if either file is stale.
