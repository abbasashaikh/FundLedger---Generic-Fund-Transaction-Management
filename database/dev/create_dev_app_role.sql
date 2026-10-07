-- LOCAL DEVELOPMENT ONLY. Creates the runtime login the API uses against the
-- local docker-compose Postgres. Run AFTER migrations, as the local superuser,
-- choosing your own local password (it is never committed):
--   psql ... -v app_password='<your-local-password>' -f database/dev/create_dev_app_role.sql
SELECT format('CREATE ROLE fl_api_dev LOGIN PASSWORD %L NOBYPASSRLS IN ROLE fundledger_app', :'app_password')
WHERE NOT EXISTS (SELECT 1 FROM pg_roles WHERE rolname = 'fl_api_dev') \gexec
