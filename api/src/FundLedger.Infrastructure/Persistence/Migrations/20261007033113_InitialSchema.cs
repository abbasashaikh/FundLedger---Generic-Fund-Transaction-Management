using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace FundLedger.Infrastructure.Persistence.Migrations
{
    /// <summary>
    /// V1 baseline: applies the verified DDL in <c>Sql/20261007033113_InitialSchema.sql</c>
    /// (a frozen copy of <c>database/schema.sql</c> as of 07-Oct-2026, without its
    /// BEGIN/COMMIT because EF already runs each migration in a transaction).
    ///
    /// Rule for later changes: write a NEW migration (SQL via <c>migrationBuilder.Sql</c>
    /// where EF can't express it) and update <c>database/schema.sql</c> to match.
    /// CI's schema-drift job fails if the two diverge. Never edit this file or its SQL.
    ///
    /// One documented exception (07-Oct-2026, Phase 1): the function-grant statement in the
    /// SQL was corrected because it failed on fresh databases on non-superuser hosts. No
    /// environment had executed this migration's SQL — Neon production and staging were
    /// baselined (database/ops/2026-10-07_baseline_ef_history.sql) — and the resulting
    /// privileges are identical.
    /// </summary>
    public partial class InitialSchema : Migration
    {
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(SqlResource.Read("20261007033113_InitialSchema.sql"));
        }

        /// <summary>
        /// Rollback of the baseline drops everything. Only meaningful on an empty
        /// database (local/CI). It is never run against staging or production;
        /// cluster-level roles are intentionally left in place.
        /// </summary>
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("DROP SCHEMA IF EXISTS fl CASCADE;");
        }
    }
}
