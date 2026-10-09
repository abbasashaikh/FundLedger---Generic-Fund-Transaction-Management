-- One-off (07-Oct-2026): the Neon production and staging branches received
-- database/schema.sql by hand before EF migrations existed. This records the
-- equivalent baseline migration as applied so `FundLedger.Api migrate` starts
-- from the right place. Idempotent; run as fundledger_owner. Do NOT run on a
-- database that lacks the schema — use `migrate` there instead.
CREATE TABLE IF NOT EXISTS fl.__ef_migrations_history (
    migration_id    character varying(150) NOT NULL,
    product_version character varying(32)  NOT NULL,
    CONSTRAINT pk___ef_migrations_history PRIMARY KEY (migration_id)
);

INSERT INTO fl.__ef_migrations_history (migration_id, product_version)
SELECT '20261007033113_InitialSchema', '10.0.12'
WHERE EXISTS (SELECT 1 FROM pg_namespace WHERE nspname = 'fl')
ON CONFLICT (migration_id) DO NOTHING;

SELECT migration_id, product_version FROM fl.__ef_migrations_history ORDER BY migration_id;
