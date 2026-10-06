# FundLedger — Generic Fund & Transaction Management

FundLedger is a lightweight PWA for small organizations (community groups, charities, masjids, societies, events, projects) that need a transparent record of:
- money received
- money spent
- transfers between accounts
- balances
- **who did what**

The first deployment is Ijtema fund management. The product itself is deliberately generic.

**Stack:** React + TypeScript PWA · ASP.NET Core (.NET 10) Web API · PostgreSQL with Row-Level Security.

**Scale:** up to 50 active users per organization.

## Documentation

| # | Document | What it covers |
|---|---|---|
| 0 | [PRD v1.1 (docx)](FundLedger_Complete_PRD_v1.1.docx) | Product requirements (baseline) |
| 1 | [Technical Requirements (TRD)](docs/01-TRD.md) | Architecture, stack, auth, ledger engine, offline sync, API, security, NFRs, environments, testing |
| 2 | [App Flow](docs/02-App-Flow.md) | Screen inventory, navigation, user flows, state machines, permission-driven UI, audit events |
| 3 | [UI/UX Design Brief](docs/03-UI-UX-Design-Brief.md) | Design goals, tokens, typography, components, screen specs, accessibility, microcopy |
| 4 | [Backend Schema](docs/04-Backend-Schema.md) | ER diagram, data dictionary, balance model, RLS, indexes, roles, retention |
| 5 | [Implementation Plan](docs/05-Implementation-Plan.md) | Phases, tasks, exit criteria, Definition of Done, risks, decisions needed |
| — | [Architecture Decision Records](docs/adr/) | Why key technical choices were made |
| — | [`database/schema.sql`](database/schema.sql) | PostgreSQL DDL (source of truth) |
| — | [`database/verify_schema.sql`](database/verify_schema.sql) | Executable checks for balances, business rules and RLS |

## Verify the schema locally

You need Docker. Run these commands from the repo root in Git Bash.

The first command makes Git Bash pass container paths to Docker unchanged. Without it, `/schema.sql` would be rewritten to a Windows path.

```bash
export MSYS_NO_PATHCONV=1
```

```bash
docker run --rm -d --name fl-pg -e POSTGRES_PASSWORD=dev postgres:16
```

```bash
docker cp database/schema.sql fl-pg:/schema.sql && docker cp database/verify_schema.sql fl-pg:/verify.sql
```

```bash
docker exec fl-pg psql -U postgres -v ON_ERROR_STOP=1 -q -f /schema.sql && docker exec fl-pg psql -U postgres -v ON_ERROR_STOP=1 -q -f /verify.sql
```

The last line of output should be `ALL CHECKS PASSED`. To clean up afterwards:

```bash
docker rm -f fl-pg
```

## Status

Documentation baseline. Implementation starts with Phase 0 of the [Implementation Plan](docs/05-Implementation-Plan.md).

Two decisions are still open:
- the OTP provider ([ADR-0002](docs/adr/ADR-0002-authentication.md))
- hosting ([ADR-0006](docs/adr/ADR-0006-hosting.md))
