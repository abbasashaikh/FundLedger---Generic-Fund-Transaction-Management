-- LOCAL DEVELOPMENT ONLY (docker-compose Postgres on localhost:5433).
-- Creates the runtime login the API uses in Development (appsettings.Development.json).
-- Run AFTER migrations, as the local superuser. The password is a local placeholder.
DO $$
BEGIN
  IF NOT EXISTS (SELECT 1 FROM pg_roles WHERE rolname = 'fl_api_dev') THEN
    CREATE ROLE fl_api_dev LOGIN PASSWORD 'fl_api_dev_local_only' NOBYPASSRLS IN ROLE fundledger_app;
  END IF;
END $$;
