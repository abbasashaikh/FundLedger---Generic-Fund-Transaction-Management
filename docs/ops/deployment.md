# Deployment — one-time setup and day-to-day operation

Architecture (ADR-0006):
- **Neon** for PostgreSQL. This is already set up; see [database-setup.md](database-setup.md).
- **API + worker** run on the VPS with Docker Compose behind Caddy.
- **PWA** is served from Cloudflare Pages.
- **Images** are stored in GitHub Container Registry (GHCR).

Deploys are run by `.github/workflows/deploy.yml`:
- a push to `main` deploys to **staging**
- a `v*` tag deploys to **production**, after approval

Every deploy runs the full CI first.

`deploy.yml` stays **switched off** until the repository variable `DEPLOY_ENABLED` is set to `true`, after the checklist below is complete.

---

## One-time setup checklist

### A. Domain and DNS (owner)

1. Register the domain.
2. Use Cloudflare DNS.
3. Create these records:

| Record | Points to |
|---|---|
| `api-staging.<domain>` | A → VPS IP (DNS only, grey cloud, so Caddy can obtain certificates) |
| `api.<domain>` | A → VPS IP |
| `staging.<domain>` | Cloudflare Pages project `fundledger-staging` (custom domain) |
| `app.<domain>` | Cloudflare Pages project `fundledger-prod` (custom domain) |

### B. VPS (owner gives SSH access; then the steps below)

1. Install Docker Engine and the Compose plugin.
2. Create a `deploy` user, add it to the `docker` group, and install the CI public key in `~deploy/.ssh/authorized_keys`.
3. Create the folders and the shared network:

```bash
sudo mkdir -p /opt/fundledger/{edge,staging,production} && sudo chown -R deploy: /opt/fundledger
```

```bash
docker network create fundledger_edge
```

4. Set up the edge (Caddy):
   - Copy `infra/vps/edge/docker-compose.yml`, `Caddyfile` and `.env.example` to `/opt/fundledger/edge/`.
   - Rename `.env.example` to `.env` and fill in the domains and ACME email.
   - Start it:

```bash
cd /opt/fundledger/edge && docker compose up -d
```

5. Create the environment files:
   - For each of `staging` and `production`, create `/opt/fundledger/<env>/.env` from `infra/vps/app/.env.example`.
   - Use the runtime database URL from the password manager (`fl_api_staging` / `fl_api_prod`, **pooled** host) and the exact PWA origin.
   - Then lock it down: `chmod 600 .env`.
6. Firewall: allow only 22, 80 and 443. Disable SSH password login.

### C. GitHub repository (owner, in Settings)

| Setting | Value |
|---|---|
| Branch protection on `main` | Require PR + passing CI checks (`API`, `Database`, `Web`, `Docker image`, `Secret scan`); no force-push |
| Environment `staging` | Secret `DB_MIGRATIONS_URL` = staging **owner** URL (direct host)<br>Secrets `VPS_SSH_KEY`, `VPS_KNOWN_HOSTS`, `CLOUDFLARE_API_TOKEN`<br>Variables `VPS_HOST`, `VPS_USER=deploy`, `API_BASE_URL=https://api-staging.<domain>`, `PAGES_PROJECT=fundledger-staging`, `CLOUDFLARE_ACCOUNT_ID` |
| Environment `production` | Same keys with production values. **Required reviewers: owner** (manual approval). |
| Repository variable | `DEPLOY_ENABLED=true`. Set this last. |
| GHCR package `fundledger-api` | After the first push, make it **public** so the VPS can pull without a token. It contains no secrets. Alternatively, keep it private and `docker login ghcr.io` on the VPS with a read-only token. |

### D. Cloudflare (owner)

1. Create the Pages projects `fundledger-staging` and `fundledger-prod` (Direct Upload).
2. Create an API token with the *Cloudflare Pages: Edit* permission and store it as `CLOUDFLARE_API_TOKEN`.
3. Create the R2 buckets `fundledger-staging` and `fundledger-prod`. These are used from Phase 3 for attachments.

### E. Monitoring (owner creates accounts; DSNs/keys go into env files)

- **Sentry:** create projects `fundledger-api` and `fundledger-web`. Put the DSN in `Sentry__Dsn` in the VPS `.env`.
- **Better Stack:** monitor `https://api-staging.<domain>/health/ready` and the production equivalent every minute from 2+ regions. Use the status page on their domain.

---

## Day to day

| Task | How |
|---|---|
| Deploy to staging | Merge a PR to `main` |
| Release to production | `git tag v1.2.3 && git push origin v1.2.3`, then approve in Actions |
| Roll back | On the VPS: `/opt/fundledger/<env>/deploy.sh <env> --rollback` (< 1 min). The previous image works with the current schema because migrations are expand-only. |
| See what's running | `curl https://api.<domain>/api/v1/version` (shows the git commit) |
| Logs | `docker logs fundledger-<env>-api --since 1h` (JSON lines) |

### What happens if a deploy fails health checks

`deploy.sh` waits up to about 60 s for `/health/ready`. If the new version isn't healthy, it automatically restores the previous image tag and the workflow fails.

Migrations already applied in that run are additive, so they don't need reverting.
