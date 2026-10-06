# ADR-0006 — Hosting and managed PostgreSQL

- **Status:** **Proposed.** Needs the product owner's decision (TRD Q-02).
- **Date:** 06-Oct-2026

## Context

PRD §26.1 says "VPS / cloud hosting".

The engineering standard requires:
- separate staging and production environments
- automated deploys
- rollback in under 2 minutes
- off-site, tested backups
- migration testing against a copy of production (database branching)

Users are mostly in India, so latency matters for phones on 3G/4G.

## Recommended option

| Component | Recommendation | Why |
|---|---|---|
| PostgreSQL | **Neon** (managed Postgres), in a region close to India (verify current region list) | PITR built in; **database branching** for staging and migration tests (Standard §6.15); RLS supported; low tiers suit 50 users. Use the pooled connection string in transaction mode (Standard §6.4). |
| API + Worker | Container on a small VPS (e.g. an existing Hostinger VPS) with Docker Compose + Caddy (auto-TLS), **or** a managed container host (Railway / Render / Fly.io) | Persistent process for the worker (Standard §7.8). The managed host gives one-click rollback. A VPS is cheapest, but rollback must be scripted (`docker compose` with the previous image tag). |
| PWA static assets | Cloudflare Pages / Netlify / the same VPS behind Caddy | CDN, preview deploys per PR |
| Object storage | Cloudflare R2 or Backblaze B2 (private bucket) | S3-compatible, low egress cost |
| Off-site backups | Nightly encrypted `pg_dump` to a bucket at a **different provider** than the DB | Standard §6.10 |
| Monitoring | Sentry, Better Stack (uptime + status page) | Standard §8.1, §8.9 |

## Alternatives

- **All on one VPS** (Postgres in Docker). Cheapest, but backups, PITR, upgrades and failover all become our job, and a VPS loss can lose data. Acceptable only with WAL archiving (e.g. pgBackRest/WAL-G) to off-site storage plus restore drills.
- **Azure App Service + Azure Database for PostgreSQL.** Natural for .NET, but costs more. Reasonable if the organization already has Azure credits.

## Decision needed

1. The DB host.
2. The API host.
3. The domain name.

Once these are chosen, update this ADR's status to Accepted and fill in the regions and plan tiers.
