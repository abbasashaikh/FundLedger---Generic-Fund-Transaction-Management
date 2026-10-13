# Go-live guide for the owner — step by step

This is the order to do things in. Each step says what you do, what to send me (never a password in chat; see "What to share"), and how we know it worked. Do the steps in order; each takes minutes, not hours. Staging comes first, production later, with the same steps.

**Rough monthly cost:** VPS about US$5–10, domain about US$10–15 a year, Cloudflare (Pages, R2 small usage, DNS) free to a few dollars, Neon Launch plan about US$19, Sentry and Better Stack free tiers.

## What to share with me, and what never to share

| Share | Never share in chat |
|---|---|
| The domain name, the VPS IP address, Cloudflare account ID, bucket names, the R2 endpoint address | Passwords, PINs, API tokens, secret keys, the Neon owner password |
| Screenshots with secrets covered | Any `.env` file contents |

Secrets go straight into the GitHub **Settings → Environments** page, or onto the VPS in a file, by you. I never need to see them.

---

## Step 1 — Neon: protect the database (15 min)

1. Open the Neon console → your project `fundledger` → **Settings → Billing** and move to the **Launch** plan (the free plan sleeps the database and cannot keep point-in-time recovery long enough).
2. **Reset the owner password** (it appeared in earlier chat): *Roles → fundledger_owner → Reset password*. Save the new connection string in your password manager. Do the same for `fl_api_staging` and `fl_api_prod` if they exist (they do not yet; I create them in step 8 with you).
3. Check **Settings → Restore window**: set it to at least 7 days.

Done when: you can see Launch on the billing page.

## Step 2 — Domain (20 min)

1. Buy a domain (Hostinger or Cloudflare Registrar). Example below: `yourorg.org`.
2. Move DNS to **Cloudflare** (free): add the site in Cloudflare, copy the two nameservers it gives you into your registrar.

Send me: the domain name.

## Step 3 — VPS (30 min)

1. Create a VPS: Ubuntu 24.04, 2 vCPU / 2 GB RAM is enough (Hostinger KVM 2, Hetzner CX22, DigitalOcean 2 GB). Choose a region close to your users (Mumbai or Singapore).
2. Add your SSH key when creating it (or note the root password and tell me you did; we turn passwords off in step 4).
3. In Cloudflare DNS add (grey cloud, "DNS only"):
   - `api-staging` → A → the VPS IP
   - `api` → A → the VPS IP

Send me: the VPS IP address.

## Step 4 — Prepare the VPS (I give you the exact commands; 20 min)

I will give you a short copy-paste block that installs Docker, creates the `deploy` user, makes `/opt/fundledger/...`, creates the Docker network, starts Caddy (the web front door that gets HTTPS certificates automatically) and turns on the firewall (ports 22, 80, 443 only). The commands are in [deployment.md](deployment.md) section B. You paste them into the VPS console (Hostinger has a browser terminal). Say "VPS ready" when `docker ps` shows Caddy running.

## Step 5 — Cloudflare Pages and R2 (20 min)

1. **Pages:** Workers & Pages → Create → Pages → *Direct Upload* → project name `fundledger-staging`. Add custom domain `staging.yourorg.org`.
2. **R2:** R2 → Create bucket `fundledger-staging`, public access **off**. Follow [attachments-storage.md](attachments-storage.md) for the token and the CORS rule (the CORS origin is `https://staging.yourorg.org`).
3. **API token for deploys:** My Profile → API Tokens → Create → template *Edit Cloudflare Workers*, or a custom token with **Cloudflare Pages: Edit** on your account. Copy it once.
4. Note your **Account ID** (right side of the Cloudflare dashboard home).

Send me: the account ID, the Pages project name, the bucket name, the R2 endpoint address (not the keys).

## Step 6 — Monitoring accounts (15 min)

1. **Sentry:** create an account, projects `fundledger-api` (platform .NET) and `fundledger-web` (React). Copy each project's **DSN** (a web address, not secret enough to matter, but keep it in the env files).
2. **Better Stack:** create an account, add a monitor for `https://api-staging.yourorg.org/health/ready` (every minute) and later `https://api.yourorg.org/health/ready`.

## Step 7 — GitHub secrets and variables (20 min)

GitHub → repository → **Settings → Environments → New environment → `staging`**. Add:

| Kind | Name | Value |
|---|---|---|
| Secret | `DB_MIGRATIONS_URL` | the staging **owner** connection string (direct host, not pooled) |
| Secret | `VPS_SSH_KEY` | the private key for the `deploy` user (I tell you how to generate it in step 4) |
| Secret | `VPS_KNOWN_HOSTS` | output of `ssh-keyscan <VPS IP>` |
| Secret | `CLOUDFLARE_API_TOKEN` | the token from step 5 |
| Variable | `VPS_HOST` | the VPS IP |
| Variable | `VPS_USER` | `deploy` |
| Variable | `API_BASE_URL` | `https://api-staging.yourorg.org` |
| Variable | `PAGES_PROJECT` | `fundledger-staging` |
| Variable | `CLOUDFLARE_ACCOUNT_ID` | the account ID |

Also: set the package `fundledger-api` (Packages on your GitHub profile) to **public** after the first image is pushed, or tell me and I will give you the token route.

## Step 8 — Database roles and the signing key (I guide you live; 15 min)

1. Create the runtime role `fl_api_staging` in Neon (a login role in the `fundledger_app` group). I give you the exact SQL; you run it in the Neon SQL editor and put the generated password in your password manager.
2. On any computer: `dotnet run --project api/src/FundLedger.Api -- generate-jwt-key` and keep the single `Auth__Jwt__SigningKeyPem=...` line.
3. On the VPS create `/opt/fundledger/staging/.env` from `infra/vps/app/.env.example` and fill it: the runtime connection string (pooled host), `Cors__AllowedOrigins__0=https://staging.yourorg.org`, the signing key line, the Storage values from step 5, the Sentry DSN. Then `chmod 600 .env`.

## Step 9 — Switch deployment on (5 min)

1. GitHub → Settings → **Secrets and variables → Actions → Variables** → add `DEPLOY_ENABLED` = `true`. (Do this last.)
2. Merge any PR, or re-run the *Deploy* workflow. Watch it go green.
3. Open `https://api-staging.yourorg.org/health/ready` (should say healthy) and `https://staging.yourorg.org`.

## Step 10 — First staging sign-in (10 min)

I run the one-time command that creates your organization and first Admin on staging; it prints a temporary PIN once. You sign in at `https://staging.yourorg.org`, choose your own PIN, create the fund, accounts and users.

## Then: I run the rest (no action from you except sign-offs)

- Load test, backup and restore drill, rollback rehearsal, security scan against staging.
- UAT with 5–8 real users on staging, the timed entry test, installing the app on phones.
- Production: repeat steps 3, 5, 7, 8 and 9 with `api`, `app`, `fundledger-prod`, and a **separate Neon branch**; then the production bootstrap.

## Quick check-list to tick

- [ ] Neon on Launch plan; owner password reset
- [ ] Domain on Cloudflare DNS
- [ ] VPS running; DNS records `api-staging` and `api` pointing at it
- [ ] Docker + Caddy + firewall set up
- [ ] Pages project, R2 bucket + token + CORS, deploy token
- [ ] Sentry and Better Stack accounts
- [ ] GitHub `staging` environment secrets and variables
- [ ] `fl_api_staging` role, signing key, VPS `.env`
- [ ] `DEPLOY_ENABLED=true`, first deploy green
- [ ] Staging organization created and Admin signed in
