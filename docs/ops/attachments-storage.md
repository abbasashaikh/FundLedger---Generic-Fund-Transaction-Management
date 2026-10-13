# Attachment storage (Cloudflare R2) — one-time setup

Photos and PDFs attached to entries live in a **private** bucket (ADR-0006, TRD §9). The database holds only their details. Until this is set up the API answers `503 STORAGE_NOT_CONFIGURED` for uploads, and the app hides the attach buttons. Nothing else is affected.

## What the owner does (about 10 minutes, per environment)

1. In Cloudflare, create an **R2 bucket**: `fundledger-staging` and later `fundledger-prod`. Keep **public access off**.
2. Create an **R2 API token** with **Object Read & Write**, limited to that one bucket. Copy the Access Key ID, the Secret Access Key and the bucket's S3 endpoint (`https://<account-id>.r2.cloudflarestorage.com`). The secret is shown once.
3. Add a **CORS rule** on the bucket so the PWA can show files after the 60-second redirect:

   ```json
   [{ "AllowedOrigins": ["https://<your PWA domain>"], "AllowedMethods": ["GET"], "AllowedHeaders": ["*"], "MaxAgeSeconds": 3600 }]
   ```

4. On the VPS, add these to `/opt/fundledger/<env>/.env` (the file is not in git; never commit these):

   ```
   Storage__Provider=S3
   Storage__S3__ServiceUrl=https://<account-id>.r2.cloudflarestorage.com
   Storage__S3__Bucket=fundledger-<env>
   Storage__S3__AccessKeyId=<access key id>
   Storage__S3__SecretAccessKey=<secret access key>
   ```

5. Redeploy. Attach a photo to an entry; open it from the entry page.

## How it behaves

- Files are stored as `org/{org}/fund/{fund}/txn/{txn}/{uuid}.{ext}`. Downloads go through `GET /api/v1/attachments/{id}`, which checks access and then redirects to a link that expires after 60 seconds.
- Photos are decoded and saved again on the server, which removes EXIF data (including GPS location). PDFs are stored as uploaded. The type is judged by the file's first bytes, never by its name.
- Files are never deleted. An Admin can hide one with a reason; Members stop seeing it.
- The largest file is a setting (Settings → Photos and files, default 5 MB, up to 20). Up to 5 files per entry.

## Local development

`appsettings.Development.json` uses `Storage:Provider=Local` and a git-ignored folder (`api/.local-storage`). Tests use a temporary folder. Nothing to set up.

## If it stops working

- `503 STORAGE_NOT_CONFIGURED`: the `Storage__*` values are missing in that environment.
- Uploads work but photos don't show in the app: check the bucket's CORS rule (step 3).
- `Storage:Provider is S3 but Storage:S3 ... is incomplete` in the log: one of the four values is empty. The API refuses to start with a half-configured bucket.
