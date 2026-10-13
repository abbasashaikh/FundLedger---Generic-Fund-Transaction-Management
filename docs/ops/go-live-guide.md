# Go-live guide for the owner — free path, step by step

Everything here costs **nothing**. No domain, no server to rent. Each step says what you do, what to send me, and how we know it worked. Do them in order; each takes 10–30 minutes. If a screen looks different from what I describe (these sites change their menus), tell me what you see and I'll adjust.

## The free stack

| Job | Service | Free allowance (check at sign-up) |
|---|---|---|
| Database | **Neon** (already set up) | 0.5 GB, sleeps when idle, only a few hours of history |
| API | **Render** web service (runs our Docker image) | 512 MB RAM, 750 hours a month (one service awake all month), sleeps after 15 min idle |
| App (the PWA) | **Cloudflare Pages** | unlimited bandwidth |
| Photos and backups | **Backblaze B2** (S3-compatible) | 10 GB |
| Build and deploy | **GitHub Actions** + GitHub container registry | free for a public repository |
| Keeps the API awake and alerts you | **UptimeRobot** | 50 monitors, checks every 5 minutes |
| Addresses | `*.pages.dev` and `*.onrender.com` | free, with HTTPS |

**Honest limits.** Free plans have no uptime promise and can change their rules. The API shares 512 MB of memory, so very large PDF exports can be slow. Neon keeps only about 6 hours of history, so we take a **nightly encrypted backup** ourselves (step 7). With one free Render service you run **one live environment**: it is used for user testing first and becomes the real one at go-live (step 9). Nothing in the code changes if you later pay for more.

If you do have a credit card and want something sturdier at no cost, Oracle Cloud "Always Free" gives a real server. It is more work to set up; tell me and I'll write that variant.

## What to share with me, and what never to share

| Share | Never share in chat |
|---|---|
| Project names and public web addresses (`...pages.dev`, `...onrender.com`), bucket names, the B2 endpoint address, your Cloudflare account ID | Passwords, PINs, API tokens, secret keys, the Neon owner password, the contents of any `.env` file |

Secrets go straight into the GitHub or Render settings page by you. I never need to see them.

---

## Step 1 — Neon: reset the owner password (5 min)

1. Neon console → project `fundledger` → **Roles** → `fundledger_owner` → **Reset password**. Keep the new connection string in a password manager. (The old one appeared in chat.)
2. Stay on the free plan. In **Settings** note the "restore window" (a few hours): that is why step 7 exists.

## Step 2 — Cloudflare account and Pages (15 min)

1. Create a free Cloudflare account (no card needed). Open **Workers & Pages → Create → Pages → Upload assets**, name the project `fundledger` (the address becomes `fundledger.pages.dev`; if taken, add letters and tell me the final name). Upload any small file so it is created.
2. **My Profile → API Tokens → Create Token → Create Custom Token.** Permission: *Account → Cloudflare Pages → Edit*. Create it and copy it once (this is `CLOUDFLARE_API_TOKEN`).
3. Note your **Account ID** (right side of the dashboard home page).

Send me: the Pages project name and the account ID.

## Step 3 — Backblaze B2 account and buckets (20 min)

1. Create a free Backblaze account (you may be asked to verify a phone number).
2. **Buckets → Create a Bucket**: `fundledger-files` and `fundledger-backups`, both **Private**. Leave encryption on.
3. Open each bucket's page and note the **S3 endpoint** (looks like `s3.us-west-004.backblazeb2.com`); the region is the middle part (`us-west-004`).
4. **Application Keys → Add a New Application Key**: name `fundledger`, allow access to both buckets, **Read and Write**. Copy the **keyID** and **applicationKey** once.
5. On `fundledger-files` open **Bucket Settings → CORS Rules** and add a rule that allows the origin `https://fundledger.pages.dev` (your Pages address) to download (GET). This lets the app show photos.

Send me: the endpoint address and the two bucket names (not the keys).

## Step 4 — Render: the API (20 min)

1. Create a free Render account (GitHub sign-in is fine). Check at sign-up that a free web service does not ask for a card; if it does, stop and tell me.
2. First, one image has to exist. In GitHub open **Actions → Deploy → Run workflow**, tick **Only build and publish the image**, and run it (about 5 minutes; it needs none of the later settings). Then make the package public: GitHub → your profile → **Packages → fundledger-api → Package settings → Change visibility → Public**.
3. Render → **New → Web Service → Existing image**, image URL `ghcr.io/abbasashaikh/fundledger-api:staging`, **Region: Singapore**, **Instance type: Free**, name `fundledger-api`.
4. Under **Environment**, add these variables (the values in angle brackets are yours; secrets only here):

   | Name | Value |
   |---|---|
   | `PORT` | `8080` |
   | `ASPNETCORE_ENVIRONMENT` | `Staging` |
   | `ForwardedHeaders__TrustAllProxies` | `true` (Render's proxy is the only way in) |
   | `ConnectionStrings__FundLedger` | the runtime database URL from step 5 (the **pooled** host) |
   | `Cors__AllowedOrigins__0` | `https://fundledger.pages.dev` |
   | `Auth__Jwt__SigningKeyPem` | the one line from step 5 |
   | `Storage__Provider` | `S3` |
   | `Storage__S3__ServiceUrl` | `https://<your B2 endpoint>` |
   | `Storage__S3__Region` | `<your region, for example us-west-004>` |
   | `Storage__S3__Bucket` | `fundledger-files` |
   | `Storage__S3__AccessKeyId` | the keyID |
   | `Storage__S3__SecretAccessKey` | the applicationKey |

5. **Settings → Health Check Path**: `/health/live`. Save and let it deploy. When it is live, open `https://<your service>.onrender.com/api/v1/version`: you should see a small JSON.
6. Render → your service → **Settings → Deploy Hook**: copy the URL (this is `RENDER_DEPLOY_HOOK`, a secret).

Send me: the service address (`...onrender.com`).

## Step 5 — Database role and signing key (I guide you live; 15 min)

1. I give you a short SQL block; you run it in the Neon **SQL Editor**. It creates the login role `fl_api_staging` (runtime only, no owner rights). Put the password it asks you to choose in your password manager, and build the **pooled** connection URL from Neon's connection dialog.
2. On your computer, with the .NET SDK installed: `dotnet run --project api/src/FundLedger.Api -- generate-jwt-key` prints one `Auth__Jwt__SigningKeyPem=...` line. Copy that line (not the public key) into Render (step 4). Do not save it in any file in the project.

## Step 6 — GitHub secrets and the switch (15 min)

GitHub → repository → **Settings → Environments → New environment `staging`**. Add:

| Kind | Name | Value |
|---|---|---|
| Secret | `DB_MIGRATIONS_URL` | the **owner** connection string (direct host, not pooled) |
| Secret | `RENDER_DEPLOY_HOOK` | from step 4 |
| Secret | `CLOUDFLARE_API_TOKEN` | from step 2 |
| Variable | `API_BASE_URL` | `https://<your service>.onrender.com` |
| Variable | `PAGES_PROJECT` | `fundledger` |
| Variable | `CLOUDFLARE_ACCOUNT_ID` | from step 2 |

Then **Settings → Secrets and variables → Actions → Variables** (repository level): `HOSTING` = `render`. **Last of all:** `DEPLOY_ENABLED` = `true`.

Merge any change (or run the *Deploy* workflow by hand): it runs the tests, applies database changes, tells Render to run the new image, publishes the app, and waits until the running version matches. Open `https://fundledger.pages.dev`.

## Step 7 — Keep it awake, alerts, and nightly backup (25 min)

1. **UptimeRobot** (free account): add a monitor of type HTTP(s), URL `https://<your service>.onrender.com/health/live`, every 5 minutes, with your email for alerts. This also stops the free API from sleeping.
2. **Backup key:** download `age` from <https://github.com/FiloSottile/age/releases> and run `age-keygen -o backup-key.txt`. It prints a line starting `Public key: age1...`. **Keep `backup-key.txt` offline in two places** (the file is the only way to open a backup; if you lose it the backups are useless). Send me only the `age1...` public key.
3. GitHub → repository settings. Secrets: `BACKUP_DATABASE_URL` (the owner connection string, direct host), `BACKUP_S3_KEY_ID`, `BACKUP_S3_SECRET` (the B2 key from step 3). Variables: `BACKUP_S3_ENDPOINT` (`https://s3.<region>.backblazeb2.com`), `BACKUP_S3_REGION`, `BACKUP_S3_BUCKET` (`fundledger-backups`), `BACKUP_AGE_PUBLIC_KEY` (the `age1...` line), and last `BACKUP_ENABLED` = `true`.
4. Actions → *Nightly backup* → **Run workflow** once and check it is green. How to restore: [backup-restore.md](backup-restore.md).

## Step 8 — First sign-in (10 min)

I run the one-time command that creates your organization and first Admin and prints a temporary PIN once; you sign in, choose your own PIN, then create the fund, accounts and users. Then user testing starts: 5–8 real people, a script that covers every acceptance row, a timed entry test, installing the app on their phones.

## Step 9 — Go-live (when testing is signed off)

1. Neon: create a **new database branch** for real data (for example `production`), run the migration against it, create its runtime role.
2. Render: change `ConnectionStrings__FundLedger` to the new branch's runtime URL and redeploy.
3. I run the production bootstrap; Admins hand out temporary PINs in person.
4. The test data stays in the old branch (delete it later).

## Checklist

- [ ] Neon owner password reset
- [ ] Cloudflare account, Pages project, deploy token, account ID
- [ ] Backblaze account, two private buckets, application key, CORS rule
- [ ] Render service running from the image; version address answers
- [ ] `fl_api_staging` role and signing key set in Render
- [ ] GitHub environment `staging` secrets and variables; `HOSTING=render`; `DEPLOY_ENABLED=true`; first deploy green
- [ ] UptimeRobot monitor
- [ ] Backup key kept offline in two places; nightly backup run once, green
- [ ] First Admin signed in on the live address
