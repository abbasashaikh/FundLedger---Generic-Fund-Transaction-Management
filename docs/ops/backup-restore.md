# Backups and restore (free path)

## What is backed up

Every night at 02:00 India time the *Nightly backup* workflow (`.github/workflows/backup.yml`):
1. copies the whole `fl` schema of the database (`pg_dump`, compressed),
2. checks the copy is a readable archive that contains the transactions table,
3. **encrypts it with your public key** (age), so nothing readable is ever stored or logged,
4. uploads it to the private bucket `fundledger-backups` under `db/`, and
5. deletes copies older than 30 days.

Neon on the free plan keeps only a few hours of history, so these nightly copies are the real safety net. The most you can lose is about a day of entries; people recording entries after a restore re-enter them (the app's own queue protects entries made while the server was down).

Photos live in `fundledger-files`. They are not copied nightly (they are never deleted or changed, and Backblaze keeps them). If you want a second copy, ask me to add a weekly bucket-to-bucket copy.

## The key

Only the **private key** (`backup-key.txt`) can open a backup. Keep it offline in two places (for example a USB stick and a printed copy in a safe place). **If it is lost, every backup is unreadable.** Never put it in GitHub, chat, or the project folder.

## Restore (also the yearly drill)

You need: the private key, the `age` program, PostgreSQL's `pg_restore` (or Docker), and a place to restore into (a **new** Neon branch or database, never over live data).

1. Download the newest file from the bucket `fundledger-backups/db/` (Backblaze website → Browse Files → Download).
2. Decrypt it:
   ```bash
   age -d -i backup-key.txt -o restored.pgdump fundledger-YYYY-MM-DD-HHMM.pgdump.age
   ```
3. Create an empty database in a new Neon branch and run the schema creation roles once (ask me; they are in `database/`).
4. Restore:
   ```bash
   pg_restore --no-owner --no-privileges --dbname "<new database connection string>" restored.pgdump
   ```
5. Check it: `database/verify_schema.sql` must print `ALL CHECKS PASSED`, and the fund balances must match the dashboard of the live app for the same day.
6. To go live on the restored copy, point Render's `ConnectionStrings__FundLedger` at it and redeploy.

**Do a drill once before go-live** and write the date and result at the bottom of this file. A backup that has never been restored is only a hope.

## Drill log

| Date | Who | Result |
|---|---|---|
| | | |
